using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using AspNetCoreUring.Native;

namespace AspNetCoreUring;

internal sealed unsafe class CompletionQueue : IDisposable
{
    private readonly nint _cqRingPtr;
    private readonly nuint _cqRingSize;

    private readonly uint* _head;
    private readonly uint* _tail;
    private readonly uint* _ringMask;
    private readonly uint* _ringEntries;
    private readonly IoUringCqe* _cqes;

    public CompletionQueue(int ringFd, in IoUringParams p)
    {
        nuint cqRingSize = p.CqOff.Cqes + p.CqEntries * (nuint)sizeof(IoUringCqe);

        nint cqRingPtr = Libc.mmap(
            nint.Zero,
            cqRingSize,
            IoUringConstants.PROT_READ | IoUringConstants.PROT_WRITE,
            IoUringConstants.MAP_SHARED | IoUringConstants.MAP_POPULATE,
            ringFd,
            (long)IoUringConstants.IORING_OFF_CQ_RING);

        if (cqRingPtr == IoUringConstants.MAP_FAILED)
            throw new InvalidOperationException($"mmap CQ ring failed: {Marshal.GetLastPInvokeError()}");

        _cqRingPtr = cqRingPtr;
        _cqRingSize = cqRingSize;

        byte* ringBase = (byte*)cqRingPtr;
        _head = (uint*)(ringBase + p.CqOff.Head);
        _tail = (uint*)(ringBase + p.CqOff.Tail);
        _ringMask = (uint*)(ringBase + p.CqOff.RingMask);
        _ringEntries = (uint*)(ringBase + p.CqOff.RingEntries);
        _cqes = (IoUringCqe*)(ringBase + p.CqOff.Cqes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPeekCompletion(out IoUringCqe cqe)
    {
        uint head = Volatile.Read(ref *_head);
        uint tail = Volatile.Read(ref *_tail);

        if (head == tail)
        {
            cqe = default;
            return false;
        }

        cqe = _cqes[head & *_ringMask];
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AdvanceHead()
    {
        Volatile.Write(ref *_head, *_head + 1);
    }

    public uint Available => Volatile.Read(ref *_tail) - Volatile.Read(ref *_head);

    public void Dispose()
    {
        Libc.munmap(_cqRingPtr, _cqRingSize);
    }
}
