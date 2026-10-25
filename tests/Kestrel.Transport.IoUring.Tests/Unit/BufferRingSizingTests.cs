using FluentAssertions;
using Kestrel.Transport.IoUring.Transport;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

/// <summary>
/// The provided buffer ring is consumed whenever a ready socket's receive completes, so a ring smaller than the
/// number of active connections makes receives fail with ENOBUFS and stalls connections under load. The default
/// therefore follows the connection limit.
/// </summary>
public class BufferRingSizingTests
{
    [Theory]
    [InlineData(32, 1024)]       // never below the historical default
    [InlineData(1024, 1024)]
    [InlineData(2000, 2048)]     // next power of two
    [InlineData(4000, 4096)]
    [InlineData(16384, 4096)]    // capped to bound pinned memory (4096 x 2 KiB = 8 MiB per ring)
    public void Auto_FollowsMaxConnections(int maxConnections, int expected)
    {
        var options = new IoUringTransportOptions { MaxConnections = maxConnections };

        options.BufferRingSize.Should().Be(0, "0 means auto");
        options.EffectiveBufferRingSize.Should().Be(expected);
    }

    [Fact]
    public void Explicit_IsHonoured()
    {
        var options = new IoUringTransportOptions { MaxConnections = 16384, BufferRingSize = 64 };

        options.EffectiveBufferRingSize.Should().Be(64);
    }

    [Fact]
    public void Auto_NeverExceedsManagedArrayLimit()
    {
        var options = new IoUringTransportOptions { MaxConnections = 16384, ReceiveBufferSize = 1 << 20 };

        ((long)options.EffectiveBufferRingSize * options.ReceiveBufferSize)
            .Should().BeLessThanOrEqualTo(int.MaxValue);
        options.Invoking(o => o.Validate()).Should().NotThrow();
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(4096, true)]
    [InlineData(3, false)]
    [InlineData(-1, false)]
    public void Validate_AcceptsAutoAndPowersOfTwoOnly(int bufferRingSize, bool valid)
    {
        var options = new IoUringTransportOptions { BufferRingSize = bufferRingSize };

        if (valid)
            options.Invoking(o => o.Validate()).Should().NotThrow();
        else
            options.Invoking(o => o.Validate()).Should().Throw<ArgumentOutOfRangeException>();
    }
}
