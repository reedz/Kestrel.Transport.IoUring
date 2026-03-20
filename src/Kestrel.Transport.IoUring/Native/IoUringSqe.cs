using System.Runtime.InteropServices;

namespace Kestrel.Transport.IoUring.Native;

[StructLayout(LayoutKind.Sequential, Size = 64)]
internal unsafe struct IoUringSqe
{
    public byte Opcode;
    public byte Flags;
    public ushort IoPrio;
    public int Fd;
    public ulong OffOrAddr2;
    public ulong AddrOrSpliceOffIn;
    public uint Len;
    public uint OpFlags;
    public ulong UserData;
    public ushort BufIndexOrGroup;
    public ushort Personality;
    public uint SpliceFdOrFileIndex;
    public fixed ulong Pad2[2];
}
