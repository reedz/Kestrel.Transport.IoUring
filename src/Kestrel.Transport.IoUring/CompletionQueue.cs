using System.Runtime.CompilerServices;
using System.Threading;
using Kestrel.Transport.IoUring.Native;

namespace Kestrel.Transport.IoUring;

/// <summary>
/// View over the mmap'd completion queue ring.
/// Memory ownership is held by <see cref="Ring"/> — this type does not mmap or munmap.
/// </summary>
internal sealed unsafe class CompletionQueue
{
    private readonly uint* _head;
    private readonly uint* _tail;
    private readonly uint* _ringMask;
    private readonly uint* _ringEntries;
    private readonly uint* _overflow;
    private readonly IoUringCqe* _cqes;

    public CompletionQueue(nint cqRingPtr, in IoUringParams p)
    {
        byte* ringBase = (byte*)cqRingPtr;
        _head = (uint*)(ringBase + p.CqOff.Head);
        _tail = (uint*)(ringBase + p.CqOff.Tail);
        _ringMask = (uint*)(ringBase + p.CqOff.RingMask);
        _ringEntries = (uint*)(ringBase + p.CqOff.RingEntries);
        _overflow = (uint*)(ringBase + p.CqOff.Overflow);
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

    /// <summary>
    /// Consumes the entry at the head. Returns true when it was the request's last completion
    /// (<c>IORING_CQE_F_MORE</c> clear): the kernel holds nothing of that request any more.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AdvanceHead()
    {
        uint head = *_head;
        bool terminal = (_cqes[head & *_ringMask].Flags & IoUringConstants.IORING_CQE_F_MORE) == 0;
        Volatile.Write(ref *_head, head + 1);
        return terminal;
    }

    public uint Available => Volatile.Read(ref *_tail) - Volatile.Read(ref *_head);

    /// <summary>Number of CQ entries the kernel allocated.</summary>
    public uint RingEntries => *_ringEntries;

    /// <summary>Returns the kernel's CQ overflow counter.</summary>
    public uint OverflowCount => Volatile.Read(ref *_overflow);
}
