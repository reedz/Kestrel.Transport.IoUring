using System.Runtime.InteropServices;
using AspNetCoreUring.Native;

namespace AspNetCoreUring;

public sealed class Ring : IDisposable
{
    private readonly int _ringFd;
    private readonly SubmissionQueue _sq;
    private readonly CompletionQueue _cq;

    // mmap regions owned by Ring — disposed in Dispose().
    private readonly nint _sqRingPtr;
    private readonly nuint _sqRingMapSize;
    private readonly nint _sqesPtr;
    private readonly nuint _sqesSize;
    private readonly nint _cqRingPtr;   // == _sqRingPtr when singleMmap
    private readonly nuint _cqMapSize;  // 0 when singleMmap
    private readonly bool _singleMmap;

    /// <summary>
    /// Lock protecting SQ ring access (TryGetSqe, Flush).
    /// Callers must hold this lock while calling <see cref="TryGetSqe"/> and filling the SQE.
    /// <see cref="Submit"/> and <see cref="SubmitAndWait"/> acquire it internally for Flush.
    /// io_uring_enter is called OUTSIDE this lock — the kernel serializes concurrent Enter calls.
    /// </summary>
    internal readonly object SubmitLock = new();

    private bool _disposed;

    public static bool IsSupported { get; private set; }

    static Ring()
    {
        IsSupported = CheckSupport();
    }

    private static bool CheckSupport()
    {
        try
        {
            unsafe
            {
                IoUringParams p = default;
                int fd = IoUringNative.IoUringSetup(2, &p);
                if (fd >= 0)
                {
                    Libc.close(fd);
                    return true;
                }
                int err = Marshal.GetLastPInvokeError();
                // ENOSYS = syscall not available; EPERM = blocked by seccomp/permissions.
                return err != IoUringConstants.ENOSYS && err != IoUringConstants.EPERM;
            }
        }
        catch
        {
            return false;
        }
    }

    public unsafe Ring(uint entries)
    {
        IoUringParams p = default;
        int fd = IoUringNative.IoUringSetup(entries, &p);
        if (fd < 0)
        {
            int err = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException($"io_uring_setup failed with errno {err}");
        }

        _ringFd = fd;

        try
        {
            _singleMmap = (p.Features & IoUringConstants.IORING_FEAT_SINGLE_MMAP) != 0;

            nuint sqRingSize = p.SqOff.Array + p.SqEntries * (nuint)sizeof(uint);
            nuint cqRingSize = p.CqOff.Cqes + p.CqEntries * (nuint)sizeof(IoUringCqe);
            nuint sqesSize = p.SqEntries * (nuint)sizeof(IoUringSqe);

            // When SINGLE_MMAP is supported, SQ and CQ share one mmap region at IORING_OFF_SQ_RING.
            nuint ringMapSize = _singleMmap ? Math.Max(sqRingSize, cqRingSize) : sqRingSize;

            nint sqRingPtr = Libc.mmap(
                nint.Zero, ringMapSize,
                IoUringConstants.PROT_READ | IoUringConstants.PROT_WRITE,
                IoUringConstants.MAP_SHARED | IoUringConstants.MAP_POPULATE,
                fd, (long)IoUringConstants.IORING_OFF_SQ_RING);

            if (sqRingPtr == IoUringConstants.MAP_FAILED)
                throw new InvalidOperationException($"mmap SQ ring failed: {Marshal.GetLastPInvokeError()}");

            nint sqesPtr = Libc.mmap(
                nint.Zero, sqesSize,
                IoUringConstants.PROT_READ | IoUringConstants.PROT_WRITE,
                IoUringConstants.MAP_SHARED | IoUringConstants.MAP_POPULATE,
                fd, (long)IoUringConstants.IORING_OFF_SQES);

            if (sqesPtr == IoUringConstants.MAP_FAILED)
            {
                Libc.munmap(sqRingPtr, ringMapSize);
                throw new InvalidOperationException($"mmap SQEs failed: {Marshal.GetLastPInvokeError()}");
            }

            nint cqRingPtr;
            nuint cqMapSize;

            if (_singleMmap)
            {
                cqRingPtr = sqRingPtr; // shared region
                cqMapSize = 0;
            }
            else
            {
                cqMapSize = cqRingSize;
                cqRingPtr = Libc.mmap(
                    nint.Zero, cqMapSize,
                    IoUringConstants.PROT_READ | IoUringConstants.PROT_WRITE,
                    IoUringConstants.MAP_SHARED | IoUringConstants.MAP_POPULATE,
                    fd, (long)IoUringConstants.IORING_OFF_CQ_RING);

                if (cqRingPtr == IoUringConstants.MAP_FAILED)
                {
                    Libc.munmap(sqesPtr, sqesSize);
                    Libc.munmap(sqRingPtr, ringMapSize);
                    throw new InvalidOperationException($"mmap CQ ring failed: {Marshal.GetLastPInvokeError()}");
                }
            }

            _sqRingPtr = sqRingPtr;
            _sqRingMapSize = ringMapSize;
            _sqesPtr = sqesPtr;
            _sqesSize = sqesSize;
            _cqRingPtr = cqRingPtr;
            _cqMapSize = cqMapSize;

            _sq = new SubmissionQueue(sqRingPtr, sqesPtr, in p);
            _cq = new CompletionQueue(cqRingPtr, in p);

            // Register the ring fd to avoid kernel fd lookup on every io_uring_enter.
            TryRegisterRingFd();
        }
        catch
        {
            Libc.close(fd);
            throw;
        }
    }

    private unsafe void TryRegisterRingFd()
    {
        // Best-effort: register ring fd for slightly faster io_uring_enter.
        // Uses a {s32 offset, u32 data} pair. offset=-1 lets kernel pick slot.
        var regData = stackalloc int[2];
        regData[0] = -1;
        regData[1] = _ringFd;
        IoUringNative.IoUringRegister(
            _ringFd,
            IoUringConstants.IORING_REGISTER_RING_FDS,
            (nint)regData,
            1);
    }

    // ── Registered files (IOSQE_FIXED_FILE) ──
    private int[]? _registeredFiles;
    private int _nextFileSlot;
    private bool _fileTableRegistered;
    private readonly Stack<int> _freeFileSlots = new();

    /// <summary>Whether fixed-file descriptors are available.</summary>
    internal bool HasRegisteredFiles => _fileTableRegistered;

    /// <summary>
    /// Initializes the registered file table. Call once before accepting connections.
    /// </summary>
    internal unsafe bool InitFileTable(int maxFiles)
    {
        _registeredFiles = new int[maxFiles];
        Array.Fill(_registeredFiles, -1);
        _nextFileSlot = 0;

        fixed (int* p = _registeredFiles)
        {
            int ret = IoUringNative.IoUringRegister(
                _ringFd, IoUringConstants.IORING_REGISTER_FILES, (nint)p, (uint)maxFiles);
            if (ret < 0)
            {
                _registeredFiles = null;
                return false;
            }
        }
        _fileTableRegistered = true;
        return true;
    }

    /// <summary>
    /// Registers an fd in the next available slot and returns the fixed-file index.
    /// Returns -1 if full.
    /// </summary>
    internal unsafe int RegisterFd(int fd)
    {
        if (_registeredFiles == null)
            return -1;

        int slot;
        if (_freeFileSlots.Count > 0)
            slot = _freeFileSlots.Pop();
        else if (_nextFileSlot < _registeredFiles.Length)
            slot = _nextFileSlot++;
        else
            return -1;

        _registeredFiles[slot] = fd;

        // Update the kernel's table for this single slot.
        var update = new IoUringFilesUpdate
        {
            Offset = (uint)slot,
            Fds = (ulong)(nint)(&fd),
        };
        int ret = IoUringNative.IoUringRegister(
            _ringFd, 6 /* IORING_REGISTER_FILES_UPDATE */, (nint)(&update), 1);
        if (ret < 0)
        {
            _registeredFiles[slot] = -1;
            _freeFileSlots.Push(slot);
            return -1;
        }
        return slot;
    }

    /// <summary>
    /// Unregisters an fd from its slot (sets to -1).
    /// </summary>
    internal unsafe void UnregisterFd(int slot)
    {
        if (_registeredFiles == null || slot < 0 || slot >= _registeredFiles.Length)
            return;

        _registeredFiles[slot] = -1;
        _freeFileSlots.Push(slot);

        int emptyFd = -1;
        var update = new IoUringFilesUpdate
        {
            Offset = (uint)slot,
            Fds = (ulong)(nint)(&emptyFd),
        };
        IoUringNative.IoUringRegister(
            _ringFd, 6 /* IORING_REGISTER_FILES_UPDATE */, (nint)(&update), 1);
    }

    internal unsafe bool TryGetSqe(out IoUringSqe* sqe) => _sq.TryGetSqe(out sqe);

    /// <summary>The io_uring file descriptor — needed for buffer ring registration.</summary>
    internal int Fd => _ringFd;

    public int Submit()
    {
        uint toSubmit;
        lock (SubmitLock) { toSubmit = _sq.Flush(); }
        if (toSubmit == 0)
            return 0;
        return Enter(toSubmit, 0, 0);
    }

    public int SubmitAndWait(uint minComplete)
    {
        uint toSubmit;
        lock (SubmitLock) { toSubmit = _sq.Flush(); }
        uint flags = minComplete > 0 ? IoUringConstants.IORING_ENTER_GETEVENTS : 0u;
        if (_sq.NeedsWakeup)
            flags |= IoUringConstants.IORING_ENTER_SQ_WAKEUP;
        return Enter(toSubmit, minComplete, flags);
    }

    /// <summary>
    /// Calls io_uring_enter. Thread-safe — the kernel serializes concurrent calls.
    /// Called OUTSIDE SubmitLock.
    /// </summary>
    private int Enter(uint toSubmit, uint minComplete, uint flags)
    {
        int ret;
        while (true)
        {
            ret = IoUringNative.IoUringEnter(_ringFd, toSubmit, minComplete, flags);
            if (ret >= 0)
                break;
            int err = Marshal.GetLastPInvokeError();
            if (err == IoUringConstants.EINTR)
            {
                toSubmit = 0;
                continue;
            }
            if (err == IoUringConstants.EAGAIN)
                return 0;
            throw new InvalidOperationException($"io_uring_enter failed with errno {err}");
        }
        return ret;
    }

    internal bool TryPeekCompletion(out IoUringCqe cqe) => _cq.TryPeekCompletion(out cqe);

    public void AdvanceCompletion() => _cq.AdvanceHead();

    public uint AvailableCompletions => _cq.Available;

    /// <summary>Returns the kernel's CQ overflow counter. Non-zero means completions were dropped.</summary>
    internal uint CqOverflowCount => _cq.OverflowCount;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (!_singleMmap && _cqMapSize > 0)
            Libc.munmap(_cqRingPtr, _cqMapSize);
        Libc.munmap(_sqesPtr, _sqesSize);
        Libc.munmap(_sqRingPtr, _sqRingMapSize);
        Libc.close(_ringFd);
    }
}
