using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Kestrel.Transport.IoUring.Native;

namespace Kestrel.Transport.IoUring;

/// <summary>
/// View over the mmap'd submission queue ring and SQE array.
/// Memory ownership is held by <see cref="Ring"/> — this type does not mmap or munmap.
/// </summary>
internal sealed unsafe class SubmissionQueue
{
    private readonly uint* _head;
    private readonly uint* _tail;
    private readonly uint* _ringMask;
    private readonly uint* _ringEntries;
    private readonly uint* _flags;
    private readonly uint* _array;
    private readonly IoUringSqe* _sqes;

    private uint _sqeTail;
    private uint _sqeHead;

    public SubmissionQueue(nint sqRingPtr, nint sqesPtr, in IoUringParams p)
    {
        byte* ringBase = (byte*)sqRingPtr;
        _head = (uint*)(ringBase + p.SqOff.Head);
        _tail = (uint*)(ringBase + p.SqOff.Tail);
        _ringMask = (uint*)(ringBase + p.SqOff.RingMask);
        _ringEntries = (uint*)(ringBase + p.SqOff.RingEntries);
        _flags = (uint*)(ringBase + p.SqOff.Flags);
        _array = (uint*)(ringBase + p.SqOff.Array);
        _sqes = (IoUringSqe*)sqesPtr;

        _sqeTail = 0;
        _sqeHead = 0;
    }

    public uint RingEntries => *_ringEntries;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetSqe(out IoUringSqe* sqe)
    {
        uint head = Volatile.Read(ref *_head);
        uint mask = *_ringMask;
        if (_sqeTail - head >= *_ringEntries)
        {
            sqe = null;
            return false;
        }
        uint index = _sqeTail & mask;
        sqe = _sqes + index;
        // io_uring requires unused SQE fields to be zero; zeroing the full struct is the safest approach.
        *sqe = default;
        _sqeTail++;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Flush()
    {
        uint tail = _sqeTail;
        uint submitted = tail - _sqeHead;
        if (submitted == 0)
            return 0;

        uint mask = *_ringMask;
        uint ktail = Volatile.Read(ref *_tail);
        for (uint i = 0; i < submitted; i++)
        {
            _array[(ktail + i) & mask] = (_sqeHead + i) & mask;
        }
        _sqeHead = tail;
        Volatile.Write(ref *_tail, ktail + submitted);
        return submitted;
    }

    public bool NeedsWakeup =>
        (Volatile.Read(ref *_flags) & IoUringConstants.IORING_SQ_NEED_WAKEUP) != 0;
}
