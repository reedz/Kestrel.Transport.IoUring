using System.Runtime.InteropServices;
using Kestrel.Transport.IoUring.Native;

namespace Kestrel.Transport.IoUring;

/// <summary>Managed wrapper around a Linux io_uring instance.</summary>
public sealed class Ring : IDisposable
{
    internal const int TeardownEnterEintrRetryLimit = 4;
    internal const int TeardownOverflowDrainRoundLimit = 4;

    internal readonly struct EnterInvocationResult
    {
        public EnterInvocationResult(int returnValue, int errno)
        {
            ReturnValue = returnValue;
            Errno = errno;
        }

        public int ReturnValue { get; }

        public int Errno { get; }

        public static EnterInvocationResult Success(int returnValue) => new(returnValue, 0);

        public static EnterInvocationResult Failure(int errno) => new(-1, errno);
    }

    internal readonly record struct TeardownCompletionSnapshot(
        uint AvailableCompletions,
        int MaterializationErrno,
        bool HasNodropOverflowBacklog);

    internal delegate EnterInvocationResult IoUringEnterInvoker(int fd, uint toSubmit, uint minComplete, uint flags);

    private readonly int _ringFd;
    private readonly SubmissionQueue _sq;
    private readonly CompletionQueue _cq;
    private readonly IoUringEnterInvoker? _enterInvoker;
    private readonly uint _features;

    // mmap regions owned by Ring — disposed in Dispose().
    private readonly nint _sqRingPtr;
    private readonly nuint _sqRingMapSize;
    private readonly nint _sqesPtr;
    private readonly nuint _sqesSize;
    private readonly nint _cqRingPtr;   // == _sqRingPtr when singleMmap
    private readonly nuint _cqMapSize;  // 0 when singleMmap
    private readonly bool _singleMmap;

    /// <summary>The actual setup flags accepted by the kernel after fallbacks.</summary>
    public uint SetupFlags { get; }

    /// <summary>
    /// Lock protecting SQ ring access (TryGetSqe, Flush).
    /// Callers must hold this lock while calling <see cref="TryGetSqe"/> and filling the SQE.
    /// <see cref="Submit"/> and <see cref="SubmitAndWait"/> acquire it internally for Flush.
    /// io_uring_enter is called OUTSIDE this lock — the kernel serializes concurrent Enter calls.
    /// </summary>
    internal readonly object SubmitLock = new();

    private int _disposed;

    /// <summary>Gets a value indicating whether io_uring is supported on this system.</summary>
    public static bool IsSupported { get; private set; }

    static Ring()
    {
        IsSupported = CheckSupport();
    }

    private static bool CheckSupport()
    {
        if (Environment.GetEnvironmentVariable("IOURING_FORCE_FALLBACK") == "1")
            return false;
        if (!OperatingSystem.IsLinux())
            return false;
        if (!IoUringKernelFeatures.MeetsCoreMinimum(IoUringKernelFeatures.CurrentKernel()))
            return false;

        try
        {
            unsafe
            {
                IoUringParams p = default;
                int fd = IoUringNative.IoUringSetup(2, &p);
                if (fd >= 0)
                {
                    bool supported = IoUringKernelFeatures.RingSupportsRequiredOpcodes(fd);
                    Libc.close(fd);
                    return supported;
                }
                return false;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Initializes a new io_uring instance with the specified queue depth and optional setup flags.
    /// On EINVAL (typically older kernels lacking newer flag support), drops the optional flags
    /// in the order DEFER_TASKRUN → SINGLE_ISSUER → COOP_TASKRUN → SQPOLL → none, and retries.</summary>
    public Ring(uint entries, uint setupFlags = 0)
        : this(entries, setupFlags, enterInvoker: null)
    {
    }

    internal unsafe Ring(uint entries, uint setupFlags, IoUringEnterInvoker? enterInvoker)
    {
        _enterInvoker = enterInvoker;

        // Order matters: drop newest/most-restrictive flags first so older kernels still benefit
        // from older flags (e.g. COOP_TASKRUN on a 5.19 kernel that doesn't support DEFER_TASKRUN).
        // Each entry is a flag-mask removed from setupFlags on retry.
        ReadOnlySpan<uint> dropOrder = stackalloc uint[]
        {
            IoUringConstants.IORING_SETUP_DEFER_TASKRUN,
            IoUringConstants.IORING_SETUP_SINGLE_ISSUER,
            IoUringConstants.IORING_SETUP_COOP_TASKRUN,
            IoUringConstants.IORING_SETUP_SQPOLL,
        };

        IoUringParams p = default;
        p.Flags = setupFlags;
        int fd = IoUringNative.IoUringSetup(entries, &p);
        int dropIdx = 0;
        while (fd < 0)
        {
            int err = Marshal.GetLastPInvokeError();
            bool canDropSqPollForPermissions =
                err == IoUringConstants.EPERM &&
                (setupFlags & IoUringConstants.IORING_SETUP_SQPOLL) != 0;
            if (canDropSqPollForPermissions)
            {
                setupFlags &= ~IoUringConstants.IORING_SETUP_SQPOLL;
            }
            else if (err != IoUringConstants.EINVAL || setupFlags == 0)
            {
                throw new InvalidOperationException($"io_uring_setup failed with errno {err} (flags=0x{setupFlags:x})");
            }
            else
            {
                // Try dropping the next flag in the drop order; if none left, fall back to flags=0.
                bool dropped = false;
                while (dropIdx < dropOrder.Length)
                {
                    uint flag = dropOrder[dropIdx++];
                    if ((setupFlags & flag) != 0)
                    {
                        setupFlags &= ~flag;
                        dropped = true;
                        break;
                    }
                }
                if (!dropped)
                {
                    setupFlags = 0;
                }
            }
            p = default;
            p.Flags = setupFlags;
            fd = IoUringNative.IoUringSetup(entries, &p);
        }
        SetupFlags = setupFlags;
        _features = p.Features;
        _ringFd = fd;

        bool singleMmap = (p.Features & IoUringConstants.IORING_FEAT_SINGLE_MMAP) != 0;
        nint sqRingPtr = nint.Zero;
        nint sqesPtr = nint.Zero;
        nint cqRingPtr = nint.Zero;
        nuint ringMapSize = 0;
        nuint sqesSize = 0;
        nuint cqMapSize = 0;
        try
        {
            _singleMmap = singleMmap;

            nuint sqRingSize = p.SqOff.Array + p.SqEntries * (nuint)sizeof(uint);
            nuint cqRingSize = p.CqOff.Cqes + p.CqEntries * (nuint)sizeof(IoUringCqe);
            sqesSize = p.SqEntries * (nuint)sizeof(IoUringSqe);

            // When SINGLE_MMAP is supported, SQ and CQ share one mmap region at IORING_OFF_SQ_RING.
            ringMapSize = singleMmap ? Math.Max(sqRingSize, cqRingSize) : sqRingSize;

            sqRingPtr = Libc.mmap(
                nint.Zero, ringMapSize,
                IoUringConstants.PROT_READ | IoUringConstants.PROT_WRITE,
                IoUringConstants.MAP_SHARED | IoUringConstants.MAP_POPULATE,
                fd, (long)IoUringConstants.IORING_OFF_SQ_RING);

            if (sqRingPtr == IoUringConstants.MAP_FAILED)
            {
                int errno = Marshal.GetLastPInvokeError();
                sqRingPtr = nint.Zero;
                throw new InvalidOperationException($"mmap SQ ring failed: {errno}");
            }

            sqesPtr = Libc.mmap(
                nint.Zero, sqesSize,
                IoUringConstants.PROT_READ | IoUringConstants.PROT_WRITE,
                IoUringConstants.MAP_SHARED | IoUringConstants.MAP_POPULATE,
                fd, (long)IoUringConstants.IORING_OFF_SQES);

            if (sqesPtr == IoUringConstants.MAP_FAILED)
            {
                int errno = Marshal.GetLastPInvokeError();
                sqesPtr = nint.Zero;
                throw new InvalidOperationException($"mmap SQEs failed: {errno}");
            }

            if (singleMmap)
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
                    int errno = Marshal.GetLastPInvokeError();
                    cqRingPtr = nint.Zero;
                    throw new InvalidOperationException($"mmap CQ ring failed: {errno}");
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
        }
        catch
        {
            if (!singleMmap && cqRingPtr != nint.Zero)
                Libc.munmap(cqRingPtr, cqMapSize);
            if (sqesPtr != nint.Zero)
                Libc.munmap(sqesPtr, sqesSize);
            if (sqRingPtr != nint.Zero)
                Libc.munmap(sqRingPtr, ringMapSize);
            Libc.close(fd);
            throw;
        }
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
            _ringFd, IoUringConstants.IORING_REGISTER_FILES_UPDATE, (nint)(&update), 1);
        if (ret < 0)
        {
            _registeredFiles[slot] = -1;
            _freeFileSlots.Push(slot);
            return -1;
        }
        return slot;
    }

    /// <summary>
    /// Unregisters an fd from its slot (sets to -1). On failure the kernel still holds a reference to the file, so
    /// the slot is not reused: it is remembered for <see cref="RetryFailedUnregisters"/> instead.
    /// </summary>
    internal bool UnregisterFd(int slot)
    {
        if (_registeredFiles == null || slot < 0 || slot >= _registeredFiles.Length)
            return false;
        if (_registeredFiles[slot] == -1)
            return true;

        if (TryRemoveRegisteredFile(slot))
            return true;

        if (!_failedUnregisters.Contains(slot))
            _failedUnregisters.Add(slot);
        return false;
    }

    // Fixed-file slots whose removal failed (owner thread only).
    private readonly List<int> _failedUnregisters = [];

    /// <summary>Fixed-file slots still registered because their removal failed.</summary>
    internal int FailedUnregisterCount => _failedUnregisters.Count;

    // Test seam: while it returns true, removing a registered file fails as if FILES_UPDATE had.
    internal Func<bool>? UnregisterFailureInjector { get; set; }

    /// <summary>Retries removing the slots whose unregister failed. Returns how many are still registered.</summary>
    internal int RetryFailedUnregisters()
    {
        for (int i = _failedUnregisters.Count - 1; i >= 0; i--)
        {
            if (TryRemoveRegisteredFile(_failedUnregisters[i]))
                _failedUnregisters.RemoveAt(i);
        }

        return _failedUnregisters.Count;
    }

    private unsafe bool TryRemoveRegisteredFile(int slot)
    {
        if (UnregisterFailureInjector?.Invoke() == true)
            return false;

        int emptyFd = -1;
        var update = new IoUringFilesUpdate
        {
            Offset = (uint)slot,
            Fds = (ulong)(nint)(&emptyFd),
        };
        int ret = IoUringNative.IoUringRegister(
            _ringFd, IoUringConstants.IORING_REGISTER_FILES_UPDATE, (nint)(&update), 1);
        if (ret < 0)
            return false;

        _registeredFiles![slot] = -1;
        _freeFileSlots.Push(slot);
        return true;
    }

    internal unsafe bool TryGetSqe(out IoUringSqe* sqe)
    {
        if (_submissionsSealed || !_sq.TryGetSqe(out sqe))
        {
            sqe = null;
            return false;
        }

        _acquiredSqes++;
        return true;
    }

    // Operation ledger. Every SQE handed out is submitted eventually (Flush publishes it, and the kernel posts a
    // completion even for one it rejects), and every request ends with exactly one completion that has
    // IORING_CQE_F_MORE clear. Their difference is the number of requests the kernel may still be working on,
    // and therefore whether it may still read or write memory they reference. Closing the ring fd does not end
    // them synchronously: the kernel cancels them asynchronously after close() returns. Owner thread only.
    private long _acquiredSqes;
    private long _terminalCompletions;
    private bool _submissionsSealed;

    /// <summary>Submitted requests that have not yet posted their final completion.</summary>
    /// <remarks>A ring that was never enabled cannot have handed anything to the kernel.</remarks>
    internal long OutstandingOperations => IsDisabled ? 0 : _acquiredSqes - _terminalCompletions;

    /// <summary>
    /// Stops handing out SQEs (<see cref="TryGetSqe"/> reports a full queue from now on). Used once teardown has
    /// established that the kernel holds nothing, so that no late caller can start a new request on memory that
    /// is about to be released.
    /// </summary>
    internal void SealSubmissions() => _submissionsSealed = true;

    /// <summary>True when SQEs have been prepared but not yet submitted.</summary>
    internal bool HasStagedSqes => _sq.HasStaged;

    /// <summary>
    /// True when SQEs are staged locally or were published but not yet consumed by the kernel (a short or EAGAIN
    /// enter leaves them in the SQ ring). Either way a <see cref="Submit"/> is still owed.
    /// </summary>
    internal bool HasUnsubmittedSqes => _sq.HasStaged || _sq.PendingSubmit != 0;

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private int _enabled;

    /// <summary>
    /// True while a ring created with IORING_SETUP_R_DISABLED has not been enabled: the kernel rejects
    /// io_uring_enter (EBADFD), so SQEs can be prepared but nothing is submitted.
    /// </summary>
    internal bool IsDisabled =>
        (SetupFlags & IoUringConstants.IORING_SETUP_R_DISABLED) != 0 && Volatile.Read(ref _enabled) == 0;

    /// <summary>
    /// Enables a ring created with IORING_SETUP_R_DISABLED. With IORING_SETUP_SINGLE_ISSUER the calling thread
    /// becomes the only thread allowed to submit to the ring (and, on newer kernels, to register with it).
    /// </summary>
    internal void EnableRings()
    {
        if (!IsDisabled)
            return;

        if (IoUringNative.IoUringRegister(_ringFd, IoUringConstants.IORING_REGISTER_ENABLE_RINGS, 0, 0) < 0)
        {
            throw new InvalidOperationException(
                $"IORING_REGISTER_ENABLE_RINGS failed with errno {Marshal.GetLastPInvokeError()}");
        }

        Volatile.Write(ref _enabled, 1);
    }

    /// <summary>The io_uring file descriptor — needed for buffer ring registration.</summary>
    internal int Fd => _ringFd;

    /// <summary>True iff DEFER_TASKRUN is enabled — callers must always pass GETEVENTS to drain completions.</summary>
    internal bool RequiresGetEventsToDrain => (SetupFlags & IoUringConstants.IORING_SETUP_DEFER_TASKRUN) != 0;

    private bool SupportsNodropOverflowMaterialization => (_features & IoUringConstants.IORING_FEAT_NODROP) != 0;

    // Live SQ flag reporting hidden NODROP overflow-list completions that still
    // exist outside the visible CQ ring snapshot.
    private bool HasNodropOverflowBacklog =>
        SupportsNodropOverflowMaterialization && _sq.HasOverflowBacklog;

    private bool UsesSqPollThread => (SetupFlags & IoUringConstants.IORING_SETUP_SQPOLL) != 0;

    /// <summary>
    /// SQPOLL: the tail store published by Flush must be visible before IORING_SQ_NEED_WAKEUP is read, or the poll
    /// thread can go to sleep without seeing the new entries and no wakeup is sent (liburing issues
    /// io_uring_smp_mb() here). A release store followed by an acquire load does not order that, so fence fully
    /// instead of relying on the SubmitLock release.
    /// </summary>
    private bool NeedsSqPollWakeup()
    {
        if (!UsesSqPollThread)
            return false;

        Interlocked.MemoryBarrier();
        return _sq.NeedsWakeup;
    }

    // Non-SQPOLL callers re-account kernel-visible pending SQEs on each enter/retry.
    // SQPOLL intentionally stays flush-gated so retries preserve the poll-thread behavior.
    private uint GetSubmitBudget(uint flushed) => UsesSqPollThread ? flushed : _sq.PendingSubmit;

    private uint GetRetryBudget() => UsesSqPollThread ? 0u : _sq.PendingSubmit;

    /// <summary>Flushes pending submission queue entries and submits them to the kernel.</summary>
    /// <returns>
    /// The number of SQEs accepted by the kernel for this enter call. On non-SQPOLL rings, this can
    /// include earlier or concurrently flushed kernel-visible SQEs that were still pending at enter time.
    /// </returns>
    public int Submit()
    {
        uint flushed;
        lock (SubmitLock) { flushed = _sq.Flush(); }
        // With DEFER_TASKRUN, completions only run when we enter with GETEVENTS — pass it
        // even on a "submit-only" call so the kernel processes any queued task work.
        uint flags = RequiresGetEventsToDrain ? IoUringConstants.IORING_ENTER_GETEVENTS : 0u;
        if (NeedsSqPollWakeup())
            flags |= IoUringConstants.IORING_ENTER_SQ_WAKEUP;
        uint toSubmit = GetSubmitBudget(flushed);
        if (toSubmit == 0 && flags == 0)
            return 0;
        return Enter(toSubmit, 0, flags);
    }

    /// <summary>Submits pending entries and waits for at least <paramref name="minComplete"/> completions.</summary>
    /// <returns>
    /// The number of SQEs accepted by the kernel for this enter call. On non-SQPOLL rings, this can
    /// include earlier or concurrently flushed kernel-visible SQEs that were still pending at enter time.
    /// </returns>
    public int SubmitAndWait(uint minComplete)
    {
        uint flushed;
        lock (SubmitLock) { flushed = _sq.Flush(); }
        // With DEFER_TASKRUN, ALWAYS pass GETEVENTS — even when minComplete is 0 — so the
        // kernel actually drains task work into CQEs.
        uint flags = (minComplete > 0 || RequiresGetEventsToDrain)
            ? IoUringConstants.IORING_ENTER_GETEVENTS
            : 0u;
        if (NeedsSqPollWakeup())
            flags |= IoUringConstants.IORING_ENTER_SQ_WAKEUP;
        uint toSubmit = GetSubmitBudget(flushed);
        return Enter(toSubmit, minComplete, flags);
    }

    /// <summary>
    /// Calls io_uring_enter. Thread-safe — the kernel serializes concurrent calls.
    /// Called OUTSIDE SubmitLock.
    /// </summary>
    private int InvokeEnter(uint toSubmit, uint minComplete, uint flags, out int errno)
    {
        if (_enterInvoker is null)
        {
            int ret = IoUringNative.IoUringEnter(_ringFd, toSubmit, minComplete, flags);
            errno = ret < 0 ? Marshal.GetLastPInvokeError() : 0;
            return ret;
        }

        EnterInvocationResult result = _enterInvoker(_ringFd, toSubmit, minComplete, flags);
        errno = result.Errno;
        return result.ReturnValue;
    }

    private int Enter(uint toSubmit, uint minComplete, uint flags)
    {
        int ret;
        while (true)
        {
            ret = InvokeEnter(toSubmit, minComplete, flags, out int err);
            if (ret >= 0)
                break;
            if (err == IoUringConstants.EINTR)
            {
                toSubmit = GetRetryBudget();
                continue;
            }
            if (err == IoUringConstants.EAGAIN)
                return 0;
            if (err == IoUringConstants.EBUSY)
                return 0;
            if (err == IoUringConstants.EEXIST)
            {
                // SINGLE_ISSUER: this thread is not the ring's issuer. That is a programming error, not a transient
                // condition; retrying would spin forever.
                throw new InvalidOperationException(
                    "io_uring_enter failed with EEXIST: the calling thread is not the ring's single issuer.");
            }
            throw new InvalidOperationException($"io_uring_enter failed with errno {err}");
        }
        return ret;
    }

    /// <summary>
    /// Performs the teardown-only nonblocking GETEVENTS enter used to materialize deferred
    /// task work or NODROP overflow-list CQEs without waiting for a not-yet-complete accept.
    /// This path never submits SQEs and bounds retry-on-EINTR so disposal cannot hang.
    /// </summary>
    internal EnterInvocationResult EnterForTeardown()
    {
        const uint flags = IoUringConstants.IORING_ENTER_GETEVENTS;
        int eintrRetriesRemaining = TeardownEnterEintrRetryLimit;

        while (true)
        {
            int ret = InvokeEnter(0, 0, flags, out int err);
            if (ret >= 0)
                return EnterInvocationResult.Success(ret);

            switch (err)
            {
                case IoUringConstants.EINTR when eintrRetriesRemaining-- > 0:
                    continue;
                case IoUringConstants.EAGAIN:
                case IoUringConstants.EBUSY:
                    return EnterInvocationResult.Success(0);
                default:
                    return EnterInvocationResult.Failure(err);
            }
        }
    }

    /// <summary>
    /// Returns a snapshot of CQEs available to teardown after at most one bounded
    /// nonblocking GETEVENTS step. The initial boundary capture uses this to
    /// materialize DEFER_TASKRUN work, while later overflow-drain rounds can opt in to
    /// one hidden NODROP overflow-list materialization step per round. The returned
    /// flag reports whether hidden overflow-list work still remains separate from the
    /// visible CQ entries in this snapshot.
    /// </summary>
    internal TeardownCompletionSnapshot CaptureCompletionSnapshotForTeardown(
        bool materializeNodropOverflowBacklog = false)
    {
        int materializationErrno = 0;
        if (RequiresGetEventsToDrain || (materializeNodropOverflowBacklog && HasNodropOverflowBacklog))
            materializationErrno = EnterForTeardown().Errno;

        return new(
            AvailableCompletions,
            materializationErrno,
            HasNodropOverflowBacklog);
    }

    internal bool TryPeekCompletion(out IoUringCqe cqe) => _cq.TryPeekCompletion(out cqe);

    /// <summary>Advances the completion queue head past the last peeked entry.</summary>
    public void AdvanceCompletion()
    {
        if (_cq.AdvanceHead())
            _terminalCompletions++;
    }

    /// <summary>Gets the number of completion queue entries ready to be consumed.</summary>
    public uint AvailableCompletions => _cq.Available;

    /// <summary>Number of completion queue entries the kernel allocated (by default twice the SQ size).</summary>
    internal uint CqEntries => _cq.RingEntries;

    /// <summary>Returns the kernel's CQ overflow counter.</summary>
    internal uint CqOverflowCount => _cq.OverflowCount;

    /// <summary>Releases all io_uring resources and unmaps shared memory regions.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (!_singleMmap && _cqMapSize > 0)
            Libc.munmap(_cqRingPtr, _cqMapSize);
        Libc.munmap(_sqesPtr, _sqesSize);
        Libc.munmap(_sqRingPtr, _sqRingMapSize);
        Libc.close(_ringFd);
    }
}
