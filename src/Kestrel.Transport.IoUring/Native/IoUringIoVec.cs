using System.Runtime.InteropServices;

namespace Kestrel.Transport.IoUring.Native;

/// <summary>Linux <c>struct iovec</c> (64-bit): a buffer pointer and its length.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IoUringIoVec
{
    public nint Base;
    public nuint Len;
}
