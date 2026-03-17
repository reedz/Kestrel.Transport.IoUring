using System.Runtime.InteropServices;

namespace AspNetCoreUring.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct IoUringCqRingOffsets
{
    public uint Head;
    public uint Tail;
    public uint RingMask;
    public uint RingEntries;
    public uint Overflow;
    public uint Cqes;
    public uint Flags;
    public uint Resv1;
    public ulong Resv2;
}
