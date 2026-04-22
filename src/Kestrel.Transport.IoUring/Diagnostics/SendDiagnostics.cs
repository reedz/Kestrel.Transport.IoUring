using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Kestrel.Transport.IoUring.Diagnostics;

/// <summary>
/// Lightweight, opt-in diagnostic counters for the io_uring send path.
///
/// All counters are static + lock-free; cost in the disabled state is one
/// branch on <see cref="Enabled"/>. Enable by setting
/// <c>IoUringTransportOptions.LogPoolStatsInterval</c> to a positive value.
///
/// Goal of this module (Round-3, Opt G): quantify per-send Pin() churn so we
/// can verify the upcoming NativeSendArena drives PinsOutstanding ≈ 0 and
/// surfaces any latent short-send / abort-with-inflight issues.
/// </summary>
internal static class SendDiagnostics
{
    public static bool Enabled;

    // Cumulative counters (monotonic).
    private static long _pinsStarted;
    private static long _pinsDisposed;
    private static long _shortSendResubmits;
    private static long _sendAbortsWithPinnedHandle;
    private static long _arenaSlotAcquisitions;
    private static long _arenaFallbackPins;
    private static long _bytesCopiedToArena;
    private static long _bytesFallbackPinned;

    // Gauges (current value).
    private static long _pinnedBytesOutstanding;
    private static long _arenaSlotsInUse;
    private static long _arenaSlotsHighWater;

    // Lifetime (avg + max).
    private static long _pinLifetimeNsTotal;
    private static long _pinLifetimeNsMax;

    /// <summary>Counts a Pin() call and adds the byte count to the outstanding gauge.</summary>
    public static long OnPinStart(int byteLen)
    {
        if (!Enabled) return 0;
        Interlocked.Increment(ref _pinsStarted);
        Interlocked.Add(ref _pinnedBytesOutstanding, byteLen);
        return Stopwatch.GetTimestamp();
    }

    /// <summary>Counts a Dispose() of a pin and updates the lifetime histogram.</summary>
    public static void OnPinDispose(long startTs, int byteLen)
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _pinsDisposed);
        Interlocked.Add(ref _pinnedBytesOutstanding, -byteLen);

        if (startTs > 0)
        {
            long ns = (long)((Stopwatch.GetTimestamp() - startTs) * (1_000_000_000.0 / Stopwatch.Frequency));
            Interlocked.Add(ref _pinLifetimeNsTotal, ns);
            long prev;
            do { prev = Volatile.Read(ref _pinLifetimeNsMax); if (ns <= prev) break; }
            while (Interlocked.CompareExchange(ref _pinLifetimeNsMax, ns, prev) != prev);
        }
    }

    public static void OnShortSendResubmit() { if (Enabled) Interlocked.Increment(ref _shortSendResubmits); }
    public static void OnSendAbortWithPinned() { if (Enabled) Interlocked.Increment(ref _sendAbortsWithPinnedHandle); }

    // Arena counters (used once Opt G lands).
    public static void OnArenaSlotAcquired(int byteLen)
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _arenaSlotAcquisitions);
        Interlocked.Add(ref _bytesCopiedToArena, byteLen);
        long inUse = Interlocked.Increment(ref _arenaSlotsInUse);
        long prev;
        do { prev = Volatile.Read(ref _arenaSlotsHighWater); if (inUse <= prev) break; }
        while (Interlocked.CompareExchange(ref _arenaSlotsHighWater, inUse, prev) != prev);
    }
    public static void OnArenaSlotReleased() { if (Enabled) Interlocked.Decrement(ref _arenaSlotsInUse); }
    public static void OnArenaFallbackPin(int byteLen)
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _arenaFallbackPins);
        Interlocked.Add(ref _bytesFallbackPinned, byteLen);
    }

    /// <summary>Snapshot of all counter values at a point in time.</summary>
    public sealed class Snapshot
    {
        public long PinsStarted, PinsDisposed, ShortSendResubmits, SendAbortsWithPinned;
        public long ArenaSlotAcquisitions, ArenaFallbackPins;
        public long BytesCopiedToArena, BytesFallbackPinned;
        public long PinnedBytesOutstanding, ArenaSlotsInUse, ArenaSlotsHighWater;
        public long PinLifetimeNsAvg, PinLifetimeNsMax;
    }

    private static Snapshot _last = new();

    public static Snapshot Sample()
    {
        long pinsDone = Volatile.Read(ref _pinsDisposed);
        long lifetimeTotal = Volatile.Read(ref _pinLifetimeNsTotal);
        return new Snapshot
        {
            PinsStarted              = Volatile.Read(ref _pinsStarted),
            PinsDisposed             = pinsDone,
            ShortSendResubmits       = Volatile.Read(ref _shortSendResubmits),
            SendAbortsWithPinned     = Volatile.Read(ref _sendAbortsWithPinnedHandle),
            ArenaSlotAcquisitions    = Volatile.Read(ref _arenaSlotAcquisitions),
            ArenaFallbackPins        = Volatile.Read(ref _arenaFallbackPins),
            BytesCopiedToArena       = Volatile.Read(ref _bytesCopiedToArena),
            BytesFallbackPinned      = Volatile.Read(ref _bytesFallbackPinned),
            PinnedBytesOutstanding   = Volatile.Read(ref _pinnedBytesOutstanding),
            ArenaSlotsInUse          = Volatile.Read(ref _arenaSlotsInUse),
            ArenaSlotsHighWater      = Volatile.Read(ref _arenaSlotsHighWater),
            PinLifetimeNsAvg         = pinsDone > 0 ? lifetimeTotal / pinsDone : 0,
            PinLifetimeNsMax         = Volatile.Read(ref _pinLifetimeNsMax),
        };
    }

    /// <summary>
    /// Starts a background timer that logs delta-rates per <paramref name="intervalSeconds"/>.
    /// Caller owns the returned timer (dispose to stop). Returns null if interval &lt;= 0.
    /// </summary>
    public static Timer? StartPeriodicLogger(ILogger logger, int intervalSeconds)
    {
        if (intervalSeconds <= 0) return null;
        Enabled = true;
        var period = TimeSpan.FromSeconds(intervalSeconds);
        return new Timer(_ =>
        {
            try
            {
                var cur = Sample();
                var prev = _last;
                _last = cur;
                double inv = 1.0 / intervalSeconds;
                logger.LogInformation(
                    "[io_uring diag] pin/s={PinRate:F0} dispose/s={DisRate:F0} outstandingPins(bytes)={Outstanding} " +
                    "arena: acq/s={ArenaAcqRate:F0} fallback/s={FbRate:F0} slotsInUse={SlotsInUse} hiWater={HiWater} " +
                    "bytesCopied/s={CopiedRate:F0} bytesFallbackPinned/s={FbBytesRate:F0} " +
                    "shortSend/s={ShortRate:F0} abortWithPinned={AbortWP} pinLifetimeNs(avg/max)={LifeAvg}/{LifeMax}",
                    (cur.PinsStarted - prev.PinsStarted) * inv,
                    (cur.PinsDisposed - prev.PinsDisposed) * inv,
                    cur.PinnedBytesOutstanding,
                    (cur.ArenaSlotAcquisitions - prev.ArenaSlotAcquisitions) * inv,
                    (cur.ArenaFallbackPins - prev.ArenaFallbackPins) * inv,
                    cur.ArenaSlotsInUse, cur.ArenaSlotsHighWater,
                    (cur.BytesCopiedToArena - prev.BytesCopiedToArena) * inv,
                    (cur.BytesFallbackPinned - prev.BytesFallbackPinned) * inv,
                    (cur.ShortSendResubmits - prev.ShortSendResubmits) * inv,
                    cur.SendAbortsWithPinned,
                    cur.PinLifetimeNsAvg, cur.PinLifetimeNsMax);
            }
            catch { /* never throw from a Timer callback */ }
        }, null, period, period);
    }
}
