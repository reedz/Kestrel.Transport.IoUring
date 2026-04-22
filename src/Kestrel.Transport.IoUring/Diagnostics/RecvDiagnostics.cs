using System.Threading;
using Microsoft.Extensions.Logging;

namespace Kestrel.Transport.IoUring.Diagnostics;

/// <summary>
/// Lightweight, opt-in diagnostic counters for the io_uring recv / accept path.
///
/// Round-4 S2: classify the residual ~1,400 socket errors per trial we observe at
/// c=1024 dual-load — they may be ENOBUFS (buffer ring exhaustion), accept-channel
/// drops (S0.1 rescue path), SQ-full (multishot rearm could not be posted),
/// EPIPE/ECONNRESET (peer close), or lost recv (recv retry queue grew unbounded).
///
/// Disabled by default: cost when off is one volatile-read branch on
/// <see cref="Enabled"/>. Enable by setting
/// <c>IoUringTransportOptions.LogPoolStatsInterval</c> to a positive value
/// (the same flag SendDiagnostics uses).
/// </summary>
internal static class RecvDiagnostics
{
    public static bool Enabled;

    // ── Recv outcome counters (monotonic) ──
    private static long _recvEnobufs;             // CQE result == -ENOBUFS
    private static long _recvEpipe;               // CQE result == -EPIPE
    private static long _recvEconnreset;          // CQE result == -ECONNRESET
    private static long _recvOtherError;          // any other negative result
    private static long _recvCleanClose;          // result == 0 (peer FIN)

    // ── Submission failure counters (monotonic) ──
    private static long _multishotRearmSqFull;    // SubmitMultishotRecv returned false
    private static long _singleshotRearmSqFull;   // SubmitRecv returned false
    private static long _acceptChannelDrops;      // BoundedChannel TryWrite=false

    // ── Backpressure counters (monotonic) ──
    private static long _asyncFlushPending;       // OnRecvCompleteFromBuffer flush returned async

    // ── Gauges (current value) ──
    private static long _recvRetryDepth;          // size of _recvRetrySet
    private static long _recvRetryDepthHighWater;

    public static void OnRecvEnobufs()       { if (Enabled) Interlocked.Increment(ref _recvEnobufs); }
    public static void OnRecvEpipe()         { if (Enabled) Interlocked.Increment(ref _recvEpipe); }
    public static void OnRecvEconnreset()    { if (Enabled) Interlocked.Increment(ref _recvEconnreset); }
    public static void OnRecvOtherError()    { if (Enabled) Interlocked.Increment(ref _recvOtherError); }
    public static void OnRecvCleanClose()    { if (Enabled) Interlocked.Increment(ref _recvCleanClose); }
    public static void OnMultishotRearmSqFull()  { if (Enabled) Interlocked.Increment(ref _multishotRearmSqFull); }
    public static void OnSingleshotRearmSqFull() { if (Enabled) Interlocked.Increment(ref _singleshotRearmSqFull); }
    public static void OnAcceptChannelDrop()  { if (Enabled) Interlocked.Increment(ref _acceptChannelDrops); }
    public static void OnAsyncFlushPending() { if (Enabled) Interlocked.Increment(ref _asyncFlushPending); }

    public static void OnRecvRetryDepth(int depth)
    {
        if (!Enabled) return;
        Volatile.Write(ref _recvRetryDepth, depth);
        long prev;
        do { prev = Volatile.Read(ref _recvRetryDepthHighWater); if (depth <= prev) break; }
        while (Interlocked.CompareExchange(ref _recvRetryDepthHighWater, depth, prev) != prev);
    }

    public sealed class Snapshot
    {
        public long Enobufs, Epipe, Econnreset, OtherError, CleanClose;
        public long MultishotRearmSqFull, SingleshotRearmSqFull, AcceptChannelDrops;
        public long AsyncFlushPending;
        public long RecvRetryDepth, RecvRetryDepthHighWater;
    }

    private static Snapshot _last = new();

    public static Snapshot Sample() => new()
    {
        Enobufs                  = Volatile.Read(ref _recvEnobufs),
        Epipe                    = Volatile.Read(ref _recvEpipe),
        Econnreset               = Volatile.Read(ref _recvEconnreset),
        OtherError               = Volatile.Read(ref _recvOtherError),
        CleanClose               = Volatile.Read(ref _recvCleanClose),
        MultishotRearmSqFull     = Volatile.Read(ref _multishotRearmSqFull),
        SingleshotRearmSqFull    = Volatile.Read(ref _singleshotRearmSqFull),
        AcceptChannelDrops       = Volatile.Read(ref _acceptChannelDrops),
        AsyncFlushPending        = Volatile.Read(ref _asyncFlushPending),
        RecvRetryDepth           = Volatile.Read(ref _recvRetryDepth),
        RecvRetryDepthHighWater  = Volatile.Read(ref _recvRetryDepthHighWater),
    };

    /// <summary>
    /// Starts a background timer that logs delta-rates and gauges per
    /// <paramref name="intervalSeconds"/>. Caller owns the returned timer
    /// (dispose to stop). Returns null if interval &lt;= 0.
    /// </summary>
    public static Timer? StartPeriodicLogger(ILogger logger, int intervalSeconds)
    {
        if (intervalSeconds <= 0) return null;
        Enabled = true;
        var period = System.TimeSpan.FromSeconds(intervalSeconds);
        return new Timer(_ =>
        {
            try
            {
                var cur = Sample();
                var prev = _last;
                _last = cur;
                double inv = 1.0 / intervalSeconds;
                string line = string.Format(
                    "[io_uring recv-diag] enobufs/s={0:F0} epipe/s={1:F0} econnreset/s={2:F0} otherErr/s={3:F0} cleanClose/s={4:F0} msRearmSqFull/s={5:F0} ssRearmSqFull/s={6:F0} acceptDrop/s={7:F0} asyncFlush/s={8:F0} recvRetryQ={9} recvRetryHi={10}",
                    (cur.Enobufs - prev.Enobufs) * inv,
                    (cur.Epipe - prev.Epipe) * inv,
                    (cur.Econnreset - prev.Econnreset) * inv,
                    (cur.OtherError - prev.OtherError) * inv,
                    (cur.CleanClose - prev.CleanClose) * inv,
                    (cur.MultishotRearmSqFull - prev.MultishotRearmSqFull) * inv,
                    (cur.SingleshotRearmSqFull - prev.SingleshotRearmSqFull) * inv,
                    (cur.AcceptChannelDrops - prev.AcceptChannelDrops) * inv,
                    (cur.AsyncFlushPending - prev.AsyncFlushPending) * inv,
                    cur.RecvRetryDepth, cur.RecvRetryDepthHighWater);
                // Logger AND Console — benchmark scenarios often suppress info-level
                // logging but capture stdout, so emit both for visibility.
                logger.LogInformation(line);
                System.Console.WriteLine(line);
            }
            catch { /* never throw from a Timer callback */ }
        }, null, period, period);
    }
}
