using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AspNetCoreUring.Native;

namespace AspNetCoreUring;

/// <summary>
/// Manages a provided buffer ring for io_uring multishot recv.
/// The kernel selects buffers from this ring for incoming data;
/// after processing, the application recycles them back.
/// </summary>
internal sealed unsafe class ProvidedBufferRing : IDisposable
{
    private readonly int _ringFd;
    private readonly ushort _bgid;
    private readonly int _bufferSize;
    private readonly int _ringEntries;
    private readonly byte[] _backingMemory;  // pinned array for all buffers
    private readonly nint _ringMem;          // mmap'd ring header (page-aligned)
    private readonly nuint _ringMemSize;
    private IoUringBuf* _ring;               // pointer into _ringMem
    private ushort _tail;

    /// <summary>Buffer group ID used in SQE <c>BufIndexOrGroup</c> field.</summary>
    public ushort GroupId => _bgid;

    public int BufferSize => _bufferSize;

    public ProvidedBufferRing(int ringFd, ushort bgid, int ringEntries, int bufferSize)
    {
        _ringFd = ringFd;
        _bgid = bgid;
        _bufferSize = bufferSize;
        _ringEntries = ringEntries;

        // Allocate the ring header — must be page-aligned.
        // The ring layout: [16-byte header (with tail at offset 14)] followed by IoUringBuf[ringEntries].
        nuint headerSize = 16; // resv1(8) + resv2(4) + resv3(2) + tail(2)
        nuint entrySize = (nuint)(ringEntries * sizeof(IoUringBuf));
        _ringMemSize = headerSize + entrySize;
        // Round up to page size for mmap alignment.
        nuint pageSize = 4096;
        nuint allocSize = (_ringMemSize + pageSize - 1) & ~(pageSize - 1);

        _ringMem = Libc.mmap(
            nint.Zero, allocSize,
            IoUringConstants.PROT_READ | IoUringConstants.PROT_WRITE,
            0x22 /* MAP_PRIVATE | MAP_ANONYMOUS */, -1, 0);
        if (_ringMem == IoUringConstants.MAP_FAILED)
            throw new InvalidOperationException($"mmap for buffer ring failed: {Marshal.GetLastPInvokeError()}");

        // The ring entries start at offset 16 (after the header).
        _ring = (IoUringBuf*)(_ringMem + 16);

        // Allocate pinned backing memory for the actual data buffers.
        _backingMemory = GC.AllocateArray<byte>(ringEntries * bufferSize, pinned: true);

        // Register the buffer ring with the kernel.
        var reg = new IoUringBufReg
        {
            RingAddr = (ulong)_ringMem,
            RingEntries = (uint)ringEntries,
            Bgid = bgid,
        };

        int ret = IoUringNative.IoUringRegister(
            ringFd,
            IoUringConstants.IORING_REGISTER_PBUF_RING,
            (nint)Unsafe.AsPointer(ref reg),
            1);
        if (ret < 0)
        {
            Libc.munmap(_ringMem, allocSize);
            throw new InvalidOperationException(
                $"IORING_REGISTER_PBUF_RING failed: {Marshal.GetLastPInvokeError()}");
        }

        // Populate the ring with all buffers.
        for (int i = 0; i < ringEntries; i++)
            AddBuffer((ushort)i);

        // Commit by writing the tail.
        CommitTail();
    }

    /// <summary>Gets a <see cref="Span{T}"/> for the buffer at the given index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetBuffer(ushort bufferId) =>
        _backingMemory.AsSpan(bufferId * _bufferSize, _bufferSize);

    /// <summary>Gets a pinned pointer for the buffer at the given index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public nint GetBufferPointer(ushort bufferId)
    {
        fixed (byte* p = &_backingMemory[bufferId * _bufferSize])
            return (nint)p;
    }

    /// <summary>Recycles a buffer back into the ring after the application has processed it.</summary>
    public void RecycleBuffer(ushort bufferId)
    {
        AddBuffer(bufferId);
        CommitTail();
    }

    private void AddBuffer(ushort bid)
    {
        int idx = _tail & (_ringEntries - 1);
        _ring[idx].Addr = (ulong)GetBufferPointer(bid);
        _ring[idx].Len = (uint)_bufferSize;
        _ring[idx].Bid = bid;
        _tail++;
    }

    private void CommitTail()
    {
        // The tail field is at offset 14 in the ring header (resv1=8 + resv2=4 + resv3=2).
        ushort* tailPtr = (ushort*)(_ringMem + 14);
        Volatile.Write(ref *tailPtr, _tail);
    }

    public void Dispose()
    {
        // Unregister the buffer ring.
        var reg = new IoUringBufReg { Bgid = _bgid };
        IoUringNative.IoUringRegister(
            _ringFd,
            IoUringConstants.IORING_UNREGISTER_PBUF_RING,
            (nint)Unsafe.AsPointer(ref reg),
            1);

        nuint pageSize = 4096;
        nuint allocSize = (_ringMemSize + pageSize - 1) & ~(pageSize - 1);
        Libc.munmap(_ringMem, allocSize);
    }
}
