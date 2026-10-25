using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Kestrel.Transport.IoUring.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

/// <summary>
/// A CI lane is only evidence if it really ran what it claims. These checks fail the lane, instead of letting
/// io_uring tests skip themselves, when the host cannot provide it.
/// </summary>
public class TestLaneGuardTests
{
    [Fact]
    public async Task SingleShotLane_RunsNativeIoUringWithoutTheProvidedBufferRing()
    {
        if (!TestTransport.SingleShotLane) return;

        Ring.IsSupported.Should().BeTrue(
            "the single-shot lane needs native io_uring; on this host every io_uring test would skip itself");

        var options = TestTransport.Apply(new IoUringTransportOptions { ThreadCount = 1 });
        var listener = new IoUringConnectionListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new Ring((uint)options.EffectiveRingSize),
            options,
            NullLogger<IoUringConnectionListener>.Instance);
        listener.Bind(options.ListenBacklog);
        try
        {
            listener.UsesBufferRing.Should().BeFalse("the lane must exercise single-shot receive");

            using var client = new TcpClient();
            await client.ConnectAsync((IPEndPoint)listener.EndPoint);
            var connection = await listener.AcceptAsync();
            await client.GetStream().WriteAsync("single-shot"u8.ToArray());
            var read = await connection!.Transport.Input.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            read.Buffer.Length.Should().BeGreaterThan(0);
            connection.Transport.Input.AdvanceTo(read.Buffer.End);
            await connection.DisposeAsync();
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    /// <summary><c>EXPECT_DOTNET_MAJOR</c>: the runtime this test process actually runs on (roll-forward lanes).</summary>
    [Fact]
    public void RuntimeLane_RunsOnTheExpectedRuntimeMajor()
    {
        string? expected = Environment.GetEnvironmentVariable("EXPECT_DOTNET_MAJOR");
        if (string.IsNullOrEmpty(expected)) return;

        Environment.Version.Major.Should().Be(int.Parse(expected),
            "the lane must run the tests on .NET {0}, not on whichever runtime happened to be installed", expected);
    }

    /// <summary>
    /// <c>TEST_REQUIRE_NATIVE_IOURING=1</c>: io_uring tests skip themselves on hosts without io_uring, so a lane
    /// that is meant to cover io_uring must fail instead of passing vacuously.
    /// </summary>
    [Fact]
    public void NativeLane_HasIoUring()
    {
        if (Environment.GetEnvironmentVariable("TEST_REQUIRE_NATIVE_IOURING") != "1") return;

        Ring.IsSupported.Should().BeTrue("this lane is meant to exercise io_uring, not to skip it");
    }
}
