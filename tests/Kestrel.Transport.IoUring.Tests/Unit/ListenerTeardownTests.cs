using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Kestrel.Transport.IoUring.Native;
using Kestrel.Transport.IoUring.Tests.Integration;
using Kestrel.Transport.IoUring.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

/// <summary>
/// Disposal may release memory the kernel was given (pins, receive buffers, the provided-buffer ring, the ring
/// mappings) only after every submitted request has posted its final completion; closing the ring fd is not a
/// barrier. When that cannot be shown before the deadline the ring is quarantined instead of released.
/// </summary>
[Collection(ProcessWideResourcesCollection.Name)]
public class ListenerTeardownTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    // Decodes to a connection id far beyond any slot, so the listener treats its completion as stale and ignores it.
    private const ulong ForeignUserData = 0x0000_7FFF_FF00_0000UL;

    private static (IoUringConnectionListener Listener, Ring Ring) CreateListener(
        Action? loopHook = null,
        ForeignEventFdRead? foreign = null)
    {
        var options = TestTransport.Apply(new IoUringTransportOptions { ThreadCount = 1 });
        var ring = new Ring((uint)options.EffectiveRingSize);
        foreign?.Attach(ring);
        var listener = new IoUringConnectionListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            ring,
            options,
            NullLogger<IoUringConnectionListener>.Instance)
        {
            IoLoopIterationHook = loopHook ?? (foreign is null ? null : foreign.InjectOnce),
        };
        listener.Bind(options.ListenBacklog);
        return (listener, ring);
    }

    [Fact]
    public async Task Dispose_WithLiveConnectionsAndTraffic_ReleasesOnlyAfterEveryRequestEnded()
    {
        if (!Ring.IsSupported) return;

        var (listener, ring) = CreateListener();
        var clients = new List<TcpClient>();
        try
        {
            for (int i = 0; i < 4; i++)
            {
                var client = new TcpClient();
                clients.Add(client);
                await client.ConnectAsync((IPEndPoint)listener.EndPoint);
                (await listener.AcceptAsync()).Should().NotBeNull();
                await client.GetStream().WriteAsync("GET / HTTP/1.1\r\n"u8.ToArray());
            }
        }
        finally
        {
            await listener.DisposeAsync().AsTask().WaitAsync(Limit);
            foreach (var client in clients)
                client.Dispose();
        }

        listener.Teardown.Should().Be(IoUringConnectionListener.TeardownOutcome.Quiesced);
        ring.OutstandingOperations.Should().Be(0);
        ring.IsDisposed.Should().BeTrue();
        IoUringQuarantine.Contains(listener).Should().BeFalse();
        ((PinnedBlockMemoryPool)listener.MemoryPoolForTests).IsDisposed.Should().BeTrue(
            "every connection is closed, so the pipe pool goes with the listener");
    }

    [Fact]
    public async Task Dispose_WhenThePeerClosedAConnectionTheAppNeverDisposed_DoesNotWaitOutTheDrainTimeout()
    {
        if (!Ring.IsSupported) return;

        var (listener, ring) = CreateListener();
        var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.EndPoint);
        var connection = await listener.AcceptAsync();
        connection.Should().NotBeNull();

        // The peer's FIN moves the connection to closing, but its send loop keeps waiting for the application.
        client.Dispose();
        await Task.Delay(100);

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await listener.DisposeAsync().AsTask().WaitAsync(Limit);

        elapsed.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(3),
            "disposal aborts every connection the application has not disposed instead of waiting 5 s for it");
        listener.Teardown.Should().Be(IoUringConnectionListener.TeardownOutcome.Quiesced);
        ring.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task Dispose_WhenTheLoopIsWedged_QuarantinesWithoutTouchingWhatTheLoopOwns()
    {
        if (!Ring.IsSupported) return;

        using var wedge = new ManualResetEventSlim(false);
        using var wedged = new ManualResetEventSlim(false);
        int wedging = 0;
        var (listener, ring) = CreateListener(() =>
        {
            if (Volatile.Read(ref wedging) == 1)
            {
                wedged.Set();
                wedge.Wait();
            }
        });
        listener.ShutdownStepTimeout = TimeSpan.FromMilliseconds(200);
        listener.TeardownDrainTimeout = TimeSpan.FromMilliseconds(200);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync((IPEndPoint)listener.EndPoint);
            var connection = await listener.AcceptAsync();
            connection.Should().NotBeNull();

            Volatile.Write(ref wedging, 1);
            await client.GetStream().WriteAsync(new byte[] { 1 }); // wakes the loop into the wedge
            wedged.Wait(Limit).Should().BeTrue();

            await listener.DisposeAsync().AsTask().WaitAsync(Limit);

            IoUringQuarantine.Contains(listener).Should().BeTrue();
            ring.IsDisposed.Should().BeFalse();
            ((PinnedBlockMemoryPool)listener.MemoryPoolForTests).IsDisposed.Should().BeFalse(
                "the wedged loop's connections may still use pipe memory");
            using (var late = new TcpClient())
            {
                Func<Task> connect = () => late.ConnectAsync((IPEndPoint)listener.EndPoint);
                (await connect.Should().ThrowAsync<SocketException>())
                    .Which.SocketErrorCode.Should().Be(
                        SocketError.ConnectionRefused,
                        "a listener nobody can serve stops listening instead of letting the kernel accept for it");
            }
            await connection!.DisposeAsync().AsTask().WaitAsync(Limit); // finalization was still signalled
        }
        finally
        {
            wedge.Set(); // let the loop run its own teardown
        }
    }

    [Fact]
    public async Task Dispose_WhenARequestNeverEnds_QuarantinesInsteadOfReleasing()
    {
        if (!Ring.IsSupported) return;

        using var foreign = new ForeignEventFdRead();
        var (listener, ring) = CreateListener(foreign: foreign);
        listener.SuppressTeardownCancels = true;
        listener.TeardownDrainTimeout = TimeSpan.FromMilliseconds(300);
        foreign.Injected.Wait(Limit).Should().BeTrue();
        long quarantinedBefore = IoUringQuarantine.QuarantinedRings;

        await listener.DisposeAsync().AsTask().WaitAsync(Limit);

        listener.Teardown.Should().Be(IoUringConnectionListener.TeardownOutcome.Quarantined);
        ring.OutstandingOperations.Should().BeGreaterThan(0);
        ring.IsDisposed.Should().BeFalse("the kernel may still write into memory the ring's requests reference");
        IoUringQuarantine.Contains(listener).Should().BeTrue();
        IoUringQuarantine.QuarantinedRings.Should().BeGreaterThan(quarantinedBefore);
        ((PinnedBlockMemoryPool)listener.MemoryPoolForTests).IsDisposed.Should().BeFalse(
            "a quarantined listener releases nothing");
    }

    [Fact]
    public async Task Dispose_CancelsRequestsTheListenerDoesNotTrack()
    {
        if (!Ring.IsSupported) return;
        // IORING_ASYNC_CANCEL_ANY, which reaches requests no targeted cancel names, arrived in Linux 5.19.
        if (IoUringKernelFeatures.CurrentKernel() is not { } kernel || kernel < new Version(5, 19)) return;

        using var foreign = new ForeignEventFdRead();
        var (listener, ring) = CreateListener(foreign: foreign);
        foreign.Injected.Wait(Limit).Should().BeTrue();

        await listener.DisposeAsync().AsTask().WaitAsync(Limit);

        listener.Teardown.Should().Be(IoUringConnectionListener.TeardownOutcome.Quiesced);
        ring.OutstandingOperations.Should().Be(0);
        ring.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task LoopDeath_WithAcceptedSocketsQueued_ClosesThemAndReleasesTheRing()
    {
        if (!Ring.IsSupported || !Directory.Exists("/proc/self/fd")) return;

        using var hold = new ManualResetEventSlim(false);
        using var held = new ManualResetEventSlim(false);
        int phase = 0; // 0 = run, 1 = park the loop, 2 = fail every iteration
        var (listener, ring) = CreateListener(() =>
        {
            switch (Volatile.Read(ref phase))
            {
                case 1:
                    held.Set();
                    hold.Wait();
                    // Fail before this iteration dispatches the queued accepts normally.
                    if (Volatile.Read(ref phase) == 2)
                        throw new InvalidOperationException("injected io loop fault");
                    break;
                case 2:
                    throw new InvalidOperationException("injected io loop fault");
            }
        });

        GC.Collect();
        GC.WaitForPendingFinalizers();
        int before = OpenTransportFds();
        var clients = new List<TcpClient>();
        try
        {
            // Park the loop, then connect: the multishot accept keeps posting accepted sockets to the CQ that the
            // dead loop will never dispatch normally.
            Volatile.Write(ref phase, 1);
            clients.Add(await ConnectAsync(listener));
            held.Wait(Limit).Should().BeTrue();
            for (int i = 0; i < 8; i++)
                clients.Add(await ConnectAsync(listener));
            await Task.Delay(100);

            Volatile.Write(ref phase, 2);
            hold.Set();
            await listener.IoLoopStopped.WaitAsync(Limit);

            listener.Teardown.Should().Be(IoUringConnectionListener.TeardownOutcome.Quiesced);
            ring.OutstandingOperations.Should().Be(0, "the accept request ended and every queued completion was consumed");

            // Only the connection accepted before the loop was parked became a connection; the queued accepts were
            // reclaimed by the drain, not dispatched.
            int delivered = 0;
            while (await listener.AcceptAsync().AsTask().WaitAsync(Limit) is not null)
                delivered++;
            delivered.Should().BeLessThanOrEqualTo(1);
        }
        finally
        {
            await listener.DisposeAsync().AsTask().WaitAsync(Limit);
            foreach (var client in clients)
                client.Dispose();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        OpenTransportFds().Should().BeLessThanOrEqualTo(before, "every socket the kernel accepted must be closed");
    }

    private static async Task<TcpClient> ConnectAsync(IoUringConnectionListener listener)
    {
        var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.EndPoint);
        return client;
    }

    private static int OpenTransportFds() => Directory.GetFileSystemEntries("/proc/self/fd")
        .Count(path =>
        {
            string? target = new FileInfo(path).LinkTarget;
            return target is not null &&
                (target.StartsWith("socket:", StringComparison.Ordinal) ||
                 target.Contains("io_uring", StringComparison.Ordinal) ||
                 target.Contains("eventfd", StringComparison.Ordinal));
        });

    /// <summary>
    /// A READ on a private eventfd, submitted on the IO loop with a user_data the listener does not know, that
    /// never completes on its own. Stands in for a request whose completion teardown cannot obtain.
    /// </summary>
    private sealed unsafe class ForeignEventFdRead : IDisposable
    {
        // The kernel may complete the read whenever the eventfd is signalled or the ring finally goes away, so its
        // buffer is never released.
        private static readonly List<ulong[]> s_buffers = [];

        private readonly int _eventFd = Libc.eventfd(0, 0);
        private readonly ulong[] _buffer = GC.AllocateArray<ulong>(1, pinned: true);
        private Ring? _ring;
        private int _injected;

        public ForeignEventFdRead()
        {
            lock (s_buffers)
                s_buffers.Add(_buffer);
        }

        public ManualResetEventSlim Injected { get; } = new(false);

        public void Attach(Ring ring) => _ring = ring;

        // Runs on the IO loop, the only thread that may touch the SQ.
        public void InjectOnce()
        {
            if (Interlocked.Exchange(ref _injected, 1) != 0)
                return;

            var ring = _ring ?? throw new InvalidOperationException("Attach the ring first.");
            if (!ring.TryGetSqe(out IoUringSqe* sqe))
                throw new InvalidOperationException("SQ full");
            sqe->Opcode = IoUringConstants.IORING_OP_READ;
            sqe->Fd = _eventFd;
            sqe->AddrOrSpliceOffIn = (ulong)(nint)Unsafe.AsPointer(ref _buffer[0]);
            sqe->Len = sizeof(ulong);
            sqe->UserData = ForeignUserData;
            ring.Submit();
            Injected.Set();
        }

        // A kernel request holds its own reference to the file, so closing the descriptor is safe.
        public void Dispose()
        {
            Libc.close(_eventFd);
            Injected.Dispose();
        }
    }
}
