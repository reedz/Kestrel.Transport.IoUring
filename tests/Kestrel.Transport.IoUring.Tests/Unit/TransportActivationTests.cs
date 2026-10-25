using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Kestrel.Transport.IoUring.Transport;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

/// <summary>
/// The factory must report which transport actually served a bind, and must be able to refuse a
/// silent fall back to SocketTransport. Benchmarks and test lanes depend on this to avoid certifying
/// io_uring results that were really produced by sockets.
/// </summary>
public class TransportActivationTests
{
    private static IoUringTransportFactory CreateFactory(
        IoUringTransportOptions options,
        Func<bool>? isSupported = null,
        Func<uint, uint, Ring>? createRing = null) =>
        new(
            Options.Create(options),
            Options.Create(new SocketTransportOptions()),
            NullLoggerFactory.Instance,
            isSupported,
            createRing);

    private static IPEndPoint LoopbackAnyPort() => new(IPAddress.Loopback, 0);

    [Fact]
    public async Task Bind_WhenIoUringUnavailable_AndRequired_Throws()
    {
        var factory = CreateFactory(
            new IoUringTransportOptions { RequireIoUring = true },
            isSupported: () => false);

        Func<Task> bind = async () => await factory.BindAsync(LoopbackAnyPort());

        await bind.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*RequireIoUring*");
        factory.Bindings.Should().BeEmpty("a refused bind must not be reported as active");
    }

    [Fact]
    public async Task Bind_WhenIoUringUnavailable_NotRequired_FallsBackAndRecordsReason()
    {
        var factory = CreateFactory(new IoUringTransportOptions(), isSupported: () => false);

        await using var listener = await factory.BindAsync(LoopbackAnyPort());

        listener.Should().NotBeOfType<IoUringConnectionListener>();
        listener.Should().NotBeOfType<IoUringMultiListener>();
        var binding = factory.Bindings.Should().ContainSingle().Subject;
        binding.Mode.Should().Be(IoUringBindingMode.SocketFallback);
        binding.RingCount.Should().Be(0);
        binding.FallbackReason.Should().Contain("not supported");
    }

    [Fact]
    public async Task Bind_WhenRingSetupFails_AndRequired_Throws()
    {
        var factory = CreateFactory(
            new IoUringTransportOptions { RequireIoUring = true },
            isSupported: () => true,
            createRing: (_, _) => throw new InvalidOperationException("io_uring_setup failed with errno 12"));

        Func<Task> bind = async () => await factory.BindAsync(LoopbackAnyPort());

        var thrown = await bind.Should().ThrowAsync<NotSupportedException>();
        thrown.Which.InnerException.Should().BeOfType<InvalidOperationException>();
        factory.Bindings.Should().BeEmpty();
    }

    [Fact]
    public async Task Bind_WhenRingSetupFails_NotRequired_FallsBackAndRecordsReason()
    {
        var factory = CreateFactory(
            new IoUringTransportOptions(),
            isSupported: () => true,
            createRing: (_, _) => throw new InvalidOperationException("io_uring_setup failed with errno 12"));

        await using var listener = await factory.BindAsync(LoopbackAnyPort());

        var binding = factory.Bindings.Should().ContainSingle().Subject;
        binding.Mode.Should().Be(IoUringBindingMode.SocketFallback);
        binding.FallbackReason.Should().Contain("errno 12");
    }

    [Fact]
    public async Task Bind_UnixDomainSocket_FallsBackEvenWhenRequired()
    {
        string path = Path.Combine(Path.GetTempPath(), $"iouring-activation-{Guid.NewGuid():N}.sock");
        var factory = CreateFactory(new IoUringTransportOptions { RequireIoUring = true });
        try
        {
            await using var listener = await factory.BindAsync(new UnixDomainSocketEndPoint(path));

            var binding = factory.Bindings.Should().ContainSingle().Subject;
            binding.Mode.Should().Be(IoUringBindingMode.SocketFallback);
            binding.FallbackReason.Should().Contain("endpoint");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Bind_RealRing_ReportsIoUringBinding()
    {
        if (!Ring.IsSupported) return;

        var factory = CreateFactory(new IoUringTransportOptions { RequireIoUring = true, RingSize = 64, BufferRingSize = 64 });

        await using var listener = await factory.BindAsync(LoopbackAnyPort());

        var binding = factory.Bindings.Should().ContainSingle().Subject;
        binding.Mode.Should().Be(IoUringBindingMode.IoUring);
        binding.RingCount.Should().Be(1);
        binding.FallbackReason.Should().BeNull();
        ((IPEndPoint)binding.EndPoint).Port.Should().BeGreaterThan(0, "the bound port must be reported, not the requested port 0");
    }

    [Fact]
    public async Task Bind_RealRings_MultiRing_ReportsRingCount()
    {
        if (!Ring.IsSupported) return;

        var factory = CreateFactory(new IoUringTransportOptions
        {
            RequireIoUring = true,
            ThreadCount = 2,
            RingSize = 64,
            BufferRingSize = 64,
        });

        await using var listener = await factory.BindAsync(LoopbackAnyPort());

        var binding = factory.Bindings.Should().ContainSingle().Subject;
        binding.Mode.Should().Be(IoUringBindingMode.IoUring);
        binding.RingCount.Should().Be(2);
    }

    [Fact]
    public void IsUsingIoUring_ReportsCapability_NotActivation()
    {
        // Documented contract: this is the host capability probe only. Activation is in Bindings.
        IoUringTransportFactory.IsUsingIoUring.Should().Be(Ring.IsSupported);
    }
}
