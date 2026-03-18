namespace AspNetCoreUring.Transport;

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
}
