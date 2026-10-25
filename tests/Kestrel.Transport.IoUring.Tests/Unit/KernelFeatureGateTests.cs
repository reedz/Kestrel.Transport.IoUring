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
        IoUringKernelFeatures.MeetsCoreMinimum(new Version(5, 6)).Should().BeFalse("5.6-5.7 cannot park on the eventfd READ");
        IoUringKernelFeatures.MeetsCoreMinimum(new Version(5, 9)).Should().BeFalse();
        IoUringKernelFeatures.MeetsCoreMinimum(new Version(5, 10)).Should().BeTrue();
        IoUringKernelFeatures.MeetsCoreMinimum(new Version(6, 17)).Should().BeTrue();
        // The opcode probe still decides when the kernel cannot be identified.
        IoUringKernelFeatures.MeetsCoreMinimum(null).Should().BeTrue();
    }

    private static byte[] ProbeBuffer(int opsLength, params byte[] supported)
    {
        var probe = new byte[16 + 64 * 8];
        probe[1] = (byte)opsLength;
        foreach (byte opcode in supported)
            probe[16 + opcode * 8 + 2] = 1;
        return probe;
    }

    [Fact]
    public void Probe_AcceptsABufferWhereEveryRequiredOpcodeIsSupported()
    {
        var probe = ProbeBuffer(50, IoUringKernelFeatures.RequiredOpcodes);

        IoUringKernelFeatures.AllOpcodesSupported(probe, IoUringKernelFeatures.RequiredOpcodes).Should().BeTrue();
    }

    [Fact]
    public void Probe_RejectsAMissingOpcodeAnEmulatedRingAndATruncatedAnswer()
    {
        byte[] required = IoUringKernelFeatures.RequiredOpcodes;
        byte[] allButAccept = required.Where(o => o != IoUringConstants.IORING_OP_ACCEPT).ToArray();

        // gVisor-style: only a couple of opcodes implemented.
        IoUringKernelFeatures.AllOpcodesSupported(ProbeBuffer(50, allButAccept), required).Should().BeFalse();
        IoUringKernelFeatures.AllOpcodesSupported(ProbeBuffer(50, IoUringConstants.IORING_OP_READV), required).Should().BeFalse();
        // The kernel reports fewer opcodes than the transport needs.
        IoUringKernelFeatures.AllOpcodesSupported(ProbeBuffer(10, required), required).Should().BeFalse();
        IoUringKernelFeatures.AllOpcodesSupported(new byte[8], required).Should().BeFalse();
    }

    [Fact]
    public void Probe_AcceptsTheRealKernelsRing()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("IOURING_FORCE_FALLBACK") == "1")
            return;
        if (!IoUringKernelFeatures.MeetsCoreMinimum(IoUringKernelFeatures.CurrentKernel()))
            return;

        // Ring.IsSupported now includes the probe, so a host that supports io_uring at all passes it; this asserts the
        // probe call itself against the real kernel rather than only the parser.
        using var ring = new Ring(8);
        IoUringKernelFeatures.RingSupportsRequiredOpcodes(ring.Fd).Should().BeTrue();
    }

    [Fact]
    public void CurrentKernel_IsReportedOnLinux()
    {
        if (!OperatingSystem.IsLinux())
            return;

        IoUringKernelFeatures.CurrentKernel().Should().NotBeNull();
    }
}
