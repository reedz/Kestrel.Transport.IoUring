namespace Kestrel.Transport.IoUring.Transport;

/// <summary>Configuration options for the io_uring transport.</summary>
public sealed class IoUringTransportOptions
{
    internal const int MaxRingEntries = 32768;

    /// <summary>Depth of the io_uring submission and completion queues (must be a power of two).</summary>
    public int RingSize { get; set; } = 1024;

    /// <summary>Maximum number of simultaneous connections. Excess connections are rejected.</summary>
    public int MaxConnections { get; set; } = 16384;

    /// <summary>TCP listen backlog passed to <c>listen(2)</c>.</summary>
    public int ListenBacklog { get; set; } = 512;

    /// <summary>Capacity of the internal accept channel (buffered accepted-connection queue).</summary>
    public int AcceptQueueCapacity { get; set; } = 1024;

    /// <summary>
    /// When &gt; 0, opt-in periodic logging of send-path diagnostic counters
    /// (Pin/Unpin rate, outstanding pinned bytes, short-send resubmits, etc.).
    /// Used during Round-3 perf work to validate the NativeSendArena lands its
    /// POH reduction; default 0 (disabled, zero-cost).
    /// </summary>
    public int LogPoolStatsInterval { get; set; } = 0;

    /// <summary>Per-connection receive buffer size in bytes.
    /// Also controls per-buffer size in the provided buffer ring when EnableBufferRing=true.
    /// 2 KB is plenty for keep-alive HTTP/1.1 small-payload workloads (Plaintext/JSON);
    /// raise for upload-heavy workloads. Pinned heap cost = BufferRingSize × ReceiveBufferSize × ThreadCount.</summary>
    public int ReceiveBufferSize { get; set; } = 2048;

    /// <summary>
    /// Number of IO threads (each with its own io_uring ring). Defaults to 1.
    /// When greater than 1, multiple listen sockets with SO_REUSEPORT are created
    /// and the kernel distributes incoming connections across them.
    /// On multi-core servers, set to <c>Environment.ProcessorCount</c> for best throughput.
    /// </summary>
    public int ThreadCount { get; set; } = 1;

    /// <summary>
    /// Enable SQPOLL mode: the kernel polls the SQ ring in a dedicated kernel thread,
    /// eliminating io_uring_enter syscalls for submission. Requires CAP_SYS_NICE or root.
    /// Each ring consumes one kernel CPU thread.
    /// </summary>
    public bool EnableSqPoll { get; set; }

    /// <summary>
    /// Enable IORING_SETUP_COOP_TASKRUN (kernel 5.19+). Defers task-work to the next
    /// io_uring_enter from the issuer thread, eliminating IPI wakeups for completions.
    /// <para>
    /// WARNING: this defers task-work for cross-thread completions too — incompatible with
    /// our eventfd-based cross-thread wakeup pattern (the eventfd completion stays as task
    /// work and never wakes a blocked issuer). Defaults to <c>false</c>; opt in only after
    /// switching to MSG_RING-based wakeups.
    /// </para>
    /// </summary>
    public bool EnableCoopTaskRun { get; set; } = false;

    /// <summary>
    /// Enable IORING_SETUP_SINGLE_ISSUER (kernel 6.0+). Asserts a single submitter, allowing
    /// kernel-side optimisations and is required for DEFER_TASKRUN. Defaults to true
    /// (auto-fallback on older kernels).
    /// </summary>
    public bool EnableSingleIssuer { get; set; } = false;

    /// <summary>
    /// Enable IORING_SETUP_DEFER_TASKRUN (kernel 6.1+). All task-work is deferred until the
    /// issuer thread re-enters io_uring; biggest reduction in scheduling overhead.
    /// Requires SINGLE_ISSUER. Defaults to <c>false</c> — DEFER_TASKRUN can interact poorly
    /// with cross-thread eventfd wakeups in some scenarios; opt in explicitly after validating.
    /// (Auto-fallback on older kernels if enabled.)
    /// </summary>
    public bool EnableDeferTaskRun { get; set; } = false;

    /// <summary>
    /// Enable provided buffer rings for multishot recv. Eliminates per-recv memory pinning
    /// and SQE resubmission. Requires kernel 6.0+. Defaults to true.
    /// </summary>
    public bool EnableBufferRing { get; set; } = true;

    /// <summary>
    /// Enables multishot accept when supported by the kernel, with automatic fallback
    /// to single-shot accept on <c>EINVAL</c>.
    /// </summary>
    public bool EnableMultishotAccept { get; set; } = true;

    /// <summary>
    /// Enables registered file descriptors. This requires a synchronous kernel table update
    /// for every accepted and closed connection, so it is disabled by default for balanced
    /// persistent-connection and connection-churn performance.
    /// </summary>
    public bool EnableRegisteredFiles { get; set; }

    /// <summary>Number of buffers in the provided buffer ring (must be power of two).</summary>
    public int BufferRingSize { get; set; } = 1024;

    /// <summary>
    /// When true, Kestrel HTTP processing runs inline on the IO loop thread,
    /// eliminating cross-thread hops for maximum throughput (Seastar/ScyllaDB model).
    /// When false, HTTP processing runs on the ThreadPool (safer for blocking middleware).
    /// Defaults to true. Set to false if middleware performs blocking I/O.
    /// </summary>
    public bool UnsafeInlineScheduling { get; set; } = true;

    /// <summary>
    /// Returns the configured ring size.
    /// In-flight operations do not consume submission-ring entries after the kernel has
    /// accepted them, so queue depth must not scale with <see cref="MaxConnections"/>.
    /// </summary>
    internal int EffectiveRingSize => RingSize;

    internal void Validate()
    {
        if (RingSize < 2 || RingSize > MaxRingEntries || !IsPowerOfTwo(RingSize))
            throw new ArgumentOutOfRangeException(nameof(RingSize),
                $"RingSize must be a power of two between 2 and {MaxRingEntries}.");
        if (MaxConnections < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxConnections), "MaxConnections must be positive.");
        if (ListenBacklog < 1)
            throw new ArgumentOutOfRangeException(nameof(ListenBacklog), "ListenBacklog must be positive.");
        if (AcceptQueueCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(AcceptQueueCapacity), "AcceptQueueCapacity must be positive.");
        if (ReceiveBufferSize < 1)
            throw new ArgumentOutOfRangeException(nameof(ReceiveBufferSize), "ReceiveBufferSize must be positive.");
        if (ThreadCount < 1 || ThreadCount > MaxConnections)
            throw new ArgumentOutOfRangeException(nameof(ThreadCount),
                "ThreadCount must be positive and no greater than MaxConnections.");
        if (LogPoolStatsInterval < 0)
            throw new ArgumentOutOfRangeException(nameof(LogPoolStatsInterval),
                "LogPoolStatsInterval cannot be negative.");
        if (BufferRingSize < 1 || BufferRingSize > MaxRingEntries || !IsPowerOfTwo(BufferRingSize))
            throw new ArgumentOutOfRangeException(nameof(BufferRingSize),
                $"BufferRingSize must be a power of two between 1 and {MaxRingEntries}.");
        if ((long)BufferRingSize * ReceiveBufferSize > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(BufferRingSize),
                "BufferRingSize multiplied by ReceiveBufferSize must fit in a managed array.");
        if ((long)AcceptQueueCapacity * ThreadCount > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(AcceptQueueCapacity),
                "AcceptQueueCapacity multiplied by ThreadCount is too large.");
    }

    private static bool IsPowerOfTwo(int value) => (value & (value - 1)) == 0;
}
