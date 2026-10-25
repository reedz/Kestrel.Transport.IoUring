using System.Threading;

namespace Kestrel.Transport.IoUring.Transport;

/// <summary>
/// Admission for <see cref="IoUringTransportOptions.MaxConnections"/>, shared by every ring of a listener: a
/// connection reserves a place with one compare-and-swap at accept, before any connection resource exists, and
/// releases it exactly once after it has been cleaned up. The same design as HAProxy's actconn, Envoy's global
/// downstream connection monitor and Kestrel's ResourceCounter. With SO_REUSEPORT the kernel hashes connections
/// to rings, so a per-ring split of the limit would refuse connections on one ring while another had room.
/// </summary>
internal sealed class ConnectionAdmission(int limit)
{
    private int _count;

    public int Limit { get; } = limit;

    public int Count => Volatile.Read(ref _count);

    public bool TryReserve()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _count);
            if (current >= Limit)
                return false;
        }
        while (Interlocked.CompareExchange(ref _count, current + 1, current) != current);

        return true;
    }

    public void Release(int count = 1) => Interlocked.Add(ref _count, -count);
}
