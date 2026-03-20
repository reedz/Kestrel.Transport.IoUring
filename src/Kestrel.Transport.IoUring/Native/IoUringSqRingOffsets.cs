using System.Runtime.InteropServices;

namespace Kestrel.Transport.IoUring.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct IoUringSqRingOffsets
{
    public uint Head;
    public uint Tail;
    public uint RingMask;
    public uint RingEntries;
    public uint Flags;
    public uint Dropped;
    public uint Array;
    public uint Resv1;
    public ulong Resv2;
}
