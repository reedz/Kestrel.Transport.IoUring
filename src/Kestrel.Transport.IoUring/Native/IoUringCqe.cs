using System.Runtime.InteropServices;

namespace Kestrel.Transport.IoUring.Native;

[StructLayout(LayoutKind.Sequential, Size = 16)]
internal struct IoUringCqe
{
    public ulong UserData;
    public int Res;
    public uint Flags;
}
