using System.Threading;

namespace Kestrel.Transport.IoUring.Transport;

/// <summary>
/// Keeps listeners whose rings could not be shown to be idle reachable for the rest of the process.
/// </summary>
/// <remarks>
/// A request the kernel has not finished may still read or write the memory it was given (receive buffers, the
/// provided-buffer ring, pinned response segments, iovecs), and closing the ring fd does not stop it synchronously.
/// When disposal cannot observe every request's final completion before its deadline, releasing that memory could
/// let the kernel write into memory the GC or the pipe pools have handed to someone else. The listener, and through
/// it the ring, its mappings and every connection with its pins, is parked here instead: a bounded leak per failed
/// ring rather than memory corruption. It accumulates if failures repeat, which the counters make visible.
/// </remarks>
internal static class IoUringQuarantine
{
    private static readonly object s_lock = new();
    private static readonly List<object> s_owners = [];
    private static long s_rings;
    private static long s_connections;
    private static long s_outstandingOperations;

    /// <summary>Rings quarantined since the process started.</summary>
    internal static long QuarantinedRings => Interlocked.Read(ref s_rings);

    /// <summary>Connections whose memory is held by quarantined rings.</summary>
    internal static long QuarantinedConnections => Interlocked.Read(ref s_connections);

    /// <summary>Requests that were still outstanding when their rings were quarantined.</summary>
    internal static long QuarantinedOperations => Interlocked.Read(ref s_outstandingOperations);

    internal static void Add(object owner, int connections, long outstandingOperations)
    {
        lock (s_lock)
            s_owners.Add(owner);

        Interlocked.Increment(ref s_rings);
        Interlocked.Add(ref s_connections, connections);
        Interlocked.Add(ref s_outstandingOperations, outstandingOperations);
    }

    internal static bool Contains(object owner)
    {
        lock (s_lock)
            return s_owners.Contains(owner);
    }
}
