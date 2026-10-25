using FluentAssertions;
using Kestrel.Transport.IoUring.Native;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

/// <summary>
/// Multishot receive needs Linux 6.0; provided buffer rings alone (5.19) are not enough, because every multishot
/// receive then completes with EINVAL and closes its connection. Older or unidentifiable kernels must therefore use
/// single-shot receive.
/// </summary>
public class KernelFeatureGateTests
{
    [Theory]
    [InlineData(5, 15, false)]
    [InlineData(5, 19, false)]
    [InlineData(6, 0, true)]
    [InlineData(6, 17, true)]
    [InlineData(7, 0, true)]
    public void BufferRing_IsUsedOnlyWhenTheKernelHasMultishotRecv(int major, int minor, bool expected)
    {
        IoUringKernelFeatures.ShouldUseBufferRing(true, new Version(major, minor, 0, 0)).Should().Be(expected);
    }

    [Fact]
    public void BufferRing_IsNeverUsedWhenDisabledOrWhenTheKernelIsUnknown()
    {
        IoUringKernelFeatures.ShouldUseBufferRing(false, new Version(6, 17)).Should().BeFalse();
        IoUringKernelFeatures.ShouldUseBufferRing(true, null).Should().BeFalse();
    }

    [Fact]
    public void CoreMinimum_RejectsOnlyKernelsKnownToBeTooOld()
    {
        IoUringKernelFeatures.MeetsCoreMinimum(new Version(5, 5)).Should().BeFalse();
        IoUringKernelFeatures.MeetsCoreMinimum(new Version(5, 6)).Should().BeTrue();
        IoUringKernelFeatures.MeetsCoreMinimum(new Version(6, 17)).Should().BeTrue();
        // io_uring_setup itself still decides when the kernel cannot be identified.
        IoUringKernelFeatures.MeetsCoreMinimum(null).Should().BeTrue();
    }

    [Fact]
    public void CurrentKernel_IsReportedOnLinux()
    {
        if (!OperatingSystem.IsLinux())
            return;

        IoUringKernelFeatures.CurrentKernel().Should().NotBeNull();
    }
}
