namespace Kestrel.Transport.IoUring.Transport;

/// <summary>Configuration options for the io_uring transport.</summary>
public sealed class IoUringTransportOptions
{
    /// <summary>Depth of the io_uring submission and completion queues (must be a power of two).</summary>
    public int RingSize { get; set; } = 256;

    /// <summary>Maximum number of simultaneous connections. Excess connections are rejected.</summary>
    public int MaxConnections { get; set; } = 1024;

    /// <summary>TCP listen backlog passed to <c>listen(2)</c>.</summary>
    public int ListenBacklog { get; set; } = 512;

    /// <summary>Capacity of the internal accept channel (buffered accepted-connection queue).</summary>
    public int AcceptQueueCapacity { get; set; } = 128;

    /// <summary>Per-connection receive buffer size in bytes.</summary>
    public int ReceiveBufferSize { get; set; } = 4096;

    /// <summary>
    /// Number of IO threads (each with its own io_uring ring). Defaults to 1.
    /// When greater than 1, multiple listen sockets with SO_REUSEPORT are created
    /// and the kernel distributes incoming connections across them.
    /// On multi-core servers, set to <c>Environment.ProcessorCount</c> for best throughput.
    /// </summary>
    public int ThreadCount { get; set; } = 1;

    /// <summary>
    /// Returns the effective ring size, ensuring it is large enough for the configured
    /// <see cref="MaxConnections"/> (at least <c>2 * MaxConnections + 16</c>, rounded up to
    /// the next power of two).
    /// </summary>
    internal int EffectiveRingSize
    {
        get
        {
            // Each connection needs at least a RECV slot, plus headroom for ACCEPT, eventfd, SENDs.
            int minimum = 2 * MaxConnections + 16;
            int size = Math.Max(RingSize, minimum);
            return (int)RoundUpPowerOfTwo((uint)size);
        }
    }

    internal static uint RoundUpPowerOfTwo(uint v)
    {
        v--;
        v |= v >> 1;
        v |= v >> 2;
        v |= v >> 4;
        v |= v >> 8;
        v |= v >> 16;
        v++;
        return v;
    }
}
