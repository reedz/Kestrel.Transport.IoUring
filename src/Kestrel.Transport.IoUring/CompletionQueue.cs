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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AdvanceHead()
    {
        Volatile.Write(ref *_head, *_head + 1);
    }

    public uint Available => Volatile.Read(ref *_tail) - Volatile.Read(ref *_head);

    /// <summary>Returns the kernel's CQ overflow counter (non-zero means completions were lost).</summary>
    public uint OverflowCount => Volatile.Read(ref *_overflow);
}
