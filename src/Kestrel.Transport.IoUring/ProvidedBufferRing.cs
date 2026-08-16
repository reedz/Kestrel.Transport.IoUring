using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kestrel.Transport.IoUring.Native;
using Kestrel.Transport.IoUring.Transport;

namespace Kestrel.Transport.IoUring;

/// <summary>
/// Manages a provided buffer ring for io_uring multishot recv.
/// Layout matches the kernel's <c>io_uring_buf_ring</c> union:
/// the header (with tail at offset 14) overlaps with bufs[0].
/// </summary>
internal sealed unsafe class ProvidedBufferRing : IDisposable
{
    private readonly int _ringFd;
    private readonly ushort _bgid;
    private readonly int _bufferSize;
    private readonly int _ringEntries;
    private readonly int _mask;
    private readonly byte[] _backingMemory;
    private readonly nint _ringMem;
    private readonly nuint _ringMemSize;
    private readonly IoUringBuf* _bufs;
    private readonly ushort* _tailPtr;
    private ushort _tail;
    private int _disposed;

    public ushort GroupId => _bgid;
    public int BufferSize => _bufferSize;

    public ProvidedBufferRing(int ringFd, ushort bgid, int ringEntries, int bufferSize)
    {
        if (ringEntries < 1 ||
            ringEntries > IoUringTransportOptions.MaxRingEntries ||
            (ringEntries & (ringEntries - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ringEntries),
                $"ringEntries must be a power of two between 1 and {IoUringTransportOptions.MaxRingEntries}.");
        }
        if (bufferSize < 1 || (long)ringEntries * bufferSize > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(bufferSize));

        _ringFd = ringFd;
        _bgid = bgid;
        _bufferSize = bufferSize;
        _ringEntries = ringEntries;
        _mask = ringEntries - 1;

        // The ring is an array of io_uring_buf (16 bytes each), with the header
        // (tail field at offset 14) overlapping bufs[0].
        nuint ringSize = (nuint)(ringEntries * sizeof(IoUringBuf));
        nuint pageSize = 4096;
        _ringMemSize = (ringSize + pageSize - 1) & ~(pageSize - 1);
        _backingMemory = GC.AllocateArray<byte>(ringEntries * bufferSize, pinned: true);

        _ringMem = Libc.mmap(
            nint.Zero, _ringMemSize,
            IoUringConstants.PROT_READ | IoUringConstants.PROT_WRITE,
            IoUringConstants.MAP_PRIVATE | IoUringConstants.MAP_ANONYMOUS, -1, 0);
        if (_ringMem == IoUringConstants.MAP_FAILED)
            throw new InvalidOperationException($"mmap for buffer ring failed: {Marshal.GetLastPInvokeError()}");

        _bufs = (IoUringBuf*)_ringMem;
        _tailPtr = (ushort*)(_ringMem + 14);

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
            Libc.munmap(_ringMem, _ringMemSize);
            throw new InvalidOperationException(
                $"IORING_REGISTER_PBUF_RING failed: {Marshal.GetLastPInvokeError()}");
        }

        for (int i = 0; i < ringEntries; i++)
            AddBuffer((ushort)i);
        CommitTail();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetBuffer(ushort bufferId) =>
        _backingMemory.AsSpan(bufferId * _bufferSize, _bufferSize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecycleBuffer(ushort bufferId)
    {
        AddBuffer(bufferId);
        CommitTail();
    }

    private void AddBuffer(ushort bid)
    {
        int idx = _tail & _mask;
        _bufs[idx].Addr = (ulong)GetBufferPointer(bid);
        _bufs[idx].Len = (uint)_bufferSize;
        _bufs[idx].Bid = bid;
        _tail++;
    }

    private nint GetBufferPointer(ushort bufferId)
    {
        fixed (byte* p = &_backingMemory[bufferId * _bufferSize])
            return (nint)p;
    }

    private void CommitTail()
    {
        Volatile.Write(ref *_tailPtr, _tail);
    }

    public void Dispose()
        => DisposeCore(unregister: true);

    internal void DisposeAfterRingClosed()
        => DisposeCore(unregister: false);

    private void DisposeCore(bool unregister)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (unregister)
        {
            var reg = new IoUringBufReg { Bgid = _bgid };
            IoUringNative.IoUringRegister(
                _ringFd,
                IoUringConstants.IORING_UNREGISTER_PBUF_RING,
                (nint)Unsafe.AsPointer(ref reg),
                1);
        }
        Libc.munmap(_ringMem, _ringMemSize);
    }
}
