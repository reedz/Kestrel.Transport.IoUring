using System.Runtime.InteropServices;
using AspNetCoreUring.Native;

namespace AspNetCoreUring;

public sealed class Ring : IDisposable
{
    private readonly int _ringFd;
    private readonly SubmissionQueue _sq;
    private readonly CompletionQueue _cq;
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
            _sq = new SubmissionQueue(fd, in p);
            _cq = new CompletionQueue(fd, in p);
        }
        catch
        {
            Libc.close(fd);
            throw;
        }
    }

    internal unsafe bool TryGetSqe(out IoUringSqe* sqe) => _sq.TryGetSqe(out sqe);

    public int Submit()
    {
        uint toSubmit = _sq.Flush();
        if (toSubmit == 0)
            return 0;

        int ret;
        while (true)
        {
            ret = IoUringNative.IoUringEnter(_ringFd, toSubmit, 0, 0);
            if (ret >= 0)
                break;
            int err = Marshal.GetLastPInvokeError();
            if (err == IoUringConstants.EINTR)
                continue;
            if (err == IoUringConstants.EAGAIN)
                return 0; // SQ ring full; caller should retry later.
            throw new InvalidOperationException($"io_uring_enter (submit) failed with errno {err}");
        }
        return ret;
    }

    public int SubmitAndWait(uint minComplete)
    {
        uint toSubmit = _sq.Flush();
        uint flags = minComplete > 0 ? IoUringConstants.IORING_ENTER_GETEVENTS : 0u;
        if (_sq.NeedsWakeup)
            flags |= IoUringConstants.IORING_ENTER_SQ_WAKEUP;

        int ret;
        while (true)
        {
            ret = IoUringNative.IoUringEnter(_ringFd, toSubmit, minComplete, flags);
            if (ret >= 0)
                break;
            int err = Marshal.GetLastPInvokeError();
            if (err == IoUringConstants.EINTR)
            {
                // After the first attempt, don't resubmit already-flushed SQEs.
                toSubmit = 0;
                continue;
            }
            throw new InvalidOperationException($"io_uring_enter (wait) failed with errno {err}");
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
        _sq.Dispose();
        _cq.Dispose();
        Libc.close(_ringFd);
    }
}
