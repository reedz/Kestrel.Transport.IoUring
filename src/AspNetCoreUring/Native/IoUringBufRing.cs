using System.Runtime.InteropServices;

namespace AspNetCoreUring.Native;

/// <summary>
/// Registration argument for <c>IORING_REGISTER_PBUF_RING</c>.
/// Matches the kernel's <c>struct io_uring_buf_reg</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IoUringBufReg
{
    public ulong RingAddr;      // page-aligned address of the buffer ring
    public uint RingEntries;    // must be power of two, max 32768
    public ushort Bgid;         // buffer group ID
    public ushort Pad;
    public ulong Resv0;
    public ulong Resv1;
    public ulong Resv2;
}

/// <summary>
/// A single buffer entry in the ring. Matches <c>struct io_uring_buf</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IoUringBuf
{
    public ulong Addr;
    public uint Len;
    public ushort Bid;     // buffer ID — returned in CQE flags
    public ushort Resv;
}
