namespace Kestrel.Transport.IoUring.Native;

/// <summary>
/// Kernel feature gates for io_uring features that fail at runtime on older kernels instead of being probed.
/// </summary>
internal static class IoUringKernelFeatures
{
    /// <summary>
    /// The transport uses <c>IORING_OP_SEND</c>, <c>RECV</c> and <c>READ</c> (Linux 5.6), plus <c>ACCEPT</c> and
    /// <c>ASYNC_CANCEL</c> (5.5) and <c>SENDMSG</c> (5.3). On older kernels io_uring_setup succeeds but those
    /// operations fail with <c>EINVAL</c>, so the transport must not claim to be supported there.
    /// </summary>
    internal static readonly Version CoreMinimum = new(5, 6);

    /// <summary>True unless the kernel is known to be older than <see cref="CoreMinimum"/>.</summary>
    internal static bool MeetsCoreMinimum(Version? kernel) => kernel is null || kernel >= CoreMinimum;

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
