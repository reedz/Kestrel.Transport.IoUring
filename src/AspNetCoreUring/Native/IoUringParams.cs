using System.Runtime.InteropServices;

namespace AspNetCoreUring.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct IoUringParams
{
    public uint SqEntries;
    public uint CqEntries;
    public uint Flags;
    public uint SqThreadCpu;
    public uint SqThreadIdle;
    public uint Features;
    public uint WqFd;
    public uint Resv0;
    public uint Resv1;
    public uint Resv2;
    public IoUringSqRingOffsets SqOff;
    public IoUringCqRingOffsets CqOff;
}
