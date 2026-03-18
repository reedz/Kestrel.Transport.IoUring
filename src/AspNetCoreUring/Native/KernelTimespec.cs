using System.Runtime.InteropServices;

namespace AspNetCoreUring.Native;

/// <summary>
/// Matches the Linux <c>__kernel_timespec</c> struct used by <c>IORING_OP_TIMEOUT</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct KernelTimespec
{
    public long TvSec;
    public long TvNsec;

    public static KernelTimespec FromMilliseconds(long milliseconds) => new()
    {
        TvSec = milliseconds / 1000,
        TvNsec = (milliseconds % 1000) * 1_000_000,
    };
}
