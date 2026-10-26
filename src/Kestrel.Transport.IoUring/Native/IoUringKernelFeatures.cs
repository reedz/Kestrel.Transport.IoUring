namespace Kestrel.Transport.IoUring.Native;

/// <summary>
/// Kernel feature gates for io_uring features that fail at runtime on older kernels instead of being probed.
/// </summary>
internal static class IoUringKernelFeatures
{
    /// <summary>
    /// The transport submits <c>SEND</c>, <c>RECV</c> and <c>READ</c> (Linux 5.6), <c>ACCEPT</c> and
    /// <c>ASYNC_CANCEL</c> (5.5) and <c>SENDMSG</c> (5.3). The floor is 5.10, the oldest maintained LTS kernel, rather
    /// than 5.6: its wake-up path reads a non-blocking eventfd with <c>READ</c>, which completes with <c>EAGAIN</c>
    /// instead of waiting on 5.6-5.7 (the request is marked no-wait there; fixed in 5.8), and the 5.8-5.9
    /// releases have not been evaluated. Older kernels use SocketTransport. The opcode probe below is the stronger
    /// check; this version test only avoids relying on it where the semantics, not the opcode, differ.
    /// </summary>
    internal static readonly Version CoreMinimum = new(5, 10);

    /// <summary>True unless the kernel is known to be older than <see cref="CoreMinimum"/>.</summary>
    internal static bool MeetsCoreMinimum(Version? kernel) => kernel is null || kernel >= CoreMinimum;

    /// <summary>Opcodes the default configuration submits; every one must be implemented by the running kernel.</summary>
    internal static readonly byte[] RequiredOpcodes =
    [
        IoUringConstants.IORING_OP_SENDMSG,
        IoUringConstants.IORING_OP_ACCEPT,
        IoUringConstants.IORING_OP_ASYNC_CANCEL,
        IoUringConstants.IORING_OP_READ,
        IoUringConstants.IORING_OP_SEND,
        IoUringConstants.IORING_OP_RECV,
    ];

    private const int ProbeHeaderSize = 16;
    private const int ProbeOpSize = 8;
    private const int ProbeOpCount = 64;

    /// <summary>
    /// Reads an <c>io_uring_probe</c> result: a 16-byte header (<c>last_op</c>, <c>ops_len</c>, reserved) followed by
    /// <c>ops_len</c> entries of 8 bytes (<c>op</c>, reserved, <c>flags</c>, reserved), indexed by opcode, where flag
    /// bit 0 means "supported". Anything that is not clearly supported counts as unsupported.
    /// </summary>
    internal static bool AllOpcodesSupported(ReadOnlySpan<byte> probe, ReadOnlySpan<byte> required)
    {
        if (probe.Length < ProbeHeaderSize)
            return false;

        int opsLength = probe[1];
        foreach (byte opcode in required)
        {
            int offset = ProbeHeaderSize + opcode * ProbeOpSize;
            if (opcode >= opsLength || offset + ProbeOpSize > probe.Length)
                return false;

            ushort flags = (ushort)(probe[offset + 2] | (probe[offset + 3] << 8));
            if ((flags & 1) == 0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Asks the ring itself which opcodes it implements (<c>IORING_REGISTER_PROBE</c>, Linux 5.6). A ring that cannot
    /// answer is not trusted: environments that merely emulate io_uring (for example gVisor's experimental support)
    /// create rings but implement only a few opcodes, and the transport would then accept connections it can never
    /// serve.
    /// </summary>
    internal static unsafe bool RingSupportsRequiredOpcodes(int ringFd)
    {
        Span<byte> probe = stackalloc byte[ProbeHeaderSize + ProbeOpCount * ProbeOpSize];
        probe.Clear();
        fixed (byte* pointer = probe)
        {
            if (IoUringNative.IoUringRegister(ringFd, IoUringConstants.IORING_REGISTER_PROBE, (nint)pointer, ProbeOpCount) < 0)
                return false;
        }

        return AllOpcodesSupported(probe, RequiredOpcodes);
    }

    /// <summary>
    /// Multishot receive (<c>IORING_RECV_MULTISHOT</c>) arrived in Linux 6.0. Provided buffer rings arrived in
    /// 5.19, so on 5.19 registering the ring succeeds but every multishot receive completes with <c>EINVAL</c>,
    /// which would close every connection. Kernels older than this use single-shot receive instead.
    /// </summary>
    internal static readonly Version MultishotRecvMinimum = new(6, 0);

    /// <summary>
    /// Decides whether the provided-buffer ring with multishot receive may be used. A kernel that cannot be
    /// identified is treated as too old: single-shot receive works everywhere, just slower.
    /// </summary>
    internal static bool ShouldUseBufferRing(bool enabled, Version? kernel) =>
        enabled && kernel is not null && kernel >= MultishotRecvMinimum;

    /// <summary>The running Linux kernel version, or null when it cannot be determined.</summary>
    internal static Version? CurrentKernel()
    {
        if (!OperatingSystem.IsLinux())
            return null;

        try
        {
            return Environment.OSVersion.Version;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
