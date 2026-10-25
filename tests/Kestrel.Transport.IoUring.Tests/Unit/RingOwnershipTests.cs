using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Kestrel.Transport.IoUring.Native;
using Kestrel.Transport.IoUring.Transport;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

/// <summary>
/// NEW-05: with IORING_SETUP_SINGLE_ISSUER the ring binds to one thread. It used to bind to the thread that created
/// the ring, so every submission from the IO loop failed with EEXIST and was retried forever. Rings are now created
/// disabled and enabled by their IO loop. NEW-14: a fixed-file unregister that fails must not leave the connection
/// open for the peer, and is retried even when the loop is otherwise idle.
/// </summary>
public class RingOwnershipTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private static bool SupportsSingleIssuer =>
        Ring.IsSupported &&
        IoUringKernelFeatures.CurrentKernel() is { } kernel &&
        kernel >= new Version(6, 1);

    public static TheoryData<int, bool> SingleIssuerVariants => new()
    {
        { 1, false },
        { 1, true },
        { 2, false },
    };

    [Theory]
    [MemberData(nameof(SingleIssuerVariants))]
    public async Task SingleIssuer_ServesTrafficAndShutsDownCleanly(int threadCount, bool deferTaskRun)
    {
        if (!SupportsSingleIssuer) return;

        var factory = new IoUringTransportFactory(
            Options.Create(TestTransport.Apply(new IoUringTransportOptions
            {
                RequireIoUring = true,
                ThreadCount = threadCount,
                EnableSingleIssuer = true,
                EnableDeferTaskRun = deferTaskRun,
                EnableRegisteredFiles = true,
            })),
            Options.Create(new SocketTransportOptions()),
            NullLoggerFactory.Instance);

        var listener = await factory.BindAsync(new IPEndPoint(IPAddress.Loopback, 0)).AsTask().WaitAsync(Limit);
        try
        {
            var binding = factory.Bindings.Should().ContainSingle().Subject;
            (binding.SetupFlags & IoUringConstants.IORING_SETUP_SINGLE_ISSUER).Should().NotBe(0u,
                "the kernel accepted SINGLE_ISSUER, so this test exercises it");
            if (deferTaskRun)
                (binding.SetupFlags & IoUringConstants.IORING_SETUP_DEFER_TASKRUN).Should().NotBe(0u);

            for (int i = 0; i < 3; i++)
                await RoundTripAsync(listener);
        }
        finally
        {
            await listener.UnbindAsync().AsTask().WaitAsync(Limit);
            await listener.DisposeAsync().AsTask().WaitAsync(Limit);
        }

        if (listener is IoUringConnectionListener single)
            single.Teardown.Should().Be(IoUringConnectionListener.TeardownOutcome.Quiesced);
    }

    [Fact]
    public async Task FailedFixedFileUnregister_ShutsTheSocketDown_AndIsRetriedOnAnIdleLoop()
    {
        if (!Ring.IsSupported) return;

        var options = TestTransport.Apply(new IoUringTransportOptions { ThreadCount = 1, EnableRegisteredFiles = true });
        var ring = new Ring((uint)options.EffectiveRingSize);
        var listener = new IoUringConnectionListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            ring,
            options,
            NullLogger<IoUringConnectionListener>.Instance);
        listener.Bind(options.ListenBacklog);
        int failing = 1;
        ring.UnregisterFailureInjector = () => Volatile.Read(ref failing) != 0;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync((IPEndPoint)listener.EndPoint);
            var connection = await listener.AcceptAsync();
            connection.Should().NotBeNull();

            await connection!.DisposeAsync().AsTask().WaitAsync(Limit);

            ring.FailedUnregisterCount.Should().Be(1, "the slot stays reserved while the kernel still references it");
            var buffer = new byte[16];
            int read = await client.GetStream().ReadAsync(buffer).AsTask().WaitAsync(Limit);
            read.Should().Be(0, "the socket was shut down, so the peer sees the close despite the table reference");

            Volatile.Write(ref failing, 0);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (ring.FailedUnregisterCount != 0 && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            ring.FailedUnregisterCount.Should().Be(0, "the loop retries without any traffic to wake it");
        }
        finally
        {
            await listener.DisposeAsync().AsTask().WaitAsync(Limit);
        }
    }

    private static async Task RoundTripAsync(IConnectionListener listener)
    {
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.EndPoint);
        var connection = await listener.AcceptAsync().AsTask().WaitAsync(Limit);
        connection.Should().NotBeNull();

        await client.GetStream().WriteAsync("ping"u8.ToArray());
        var result = await connection!.Transport.Input.ReadAsync().AsTask().WaitAsync(Limit);
        result.Buffer.Length.Should().BeGreaterThan(0);
        connection.Transport.Input.AdvanceTo(result.Buffer.End);

        await connection.Transport.Output.WriteAsync("pong"u8.ToArray()).AsTask().WaitAsync(Limit);
        var reply = new byte[4];
        int got = 0;
        while (got < reply.Length)
        {
            int n = await client.GetStream().ReadAsync(reply.AsMemory(got)).AsTask().WaitAsync(Limit);
            n.Should().BeGreaterThan(0);
            got += n;
        }
        reply.Should().Equal("pong"u8.ToArray());

        await connection.DisposeAsync().AsTask().WaitAsync(Limit);
    }
}
