using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using FluentAssertions;
using Kestrel.Transport.IoUring.Native;
using Kestrel.Transport.IoUring.Transport;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

public class TransportLifecycleTests
{
    [Fact]
    public async Task DisposeAsync_KeepsSendPinUntilTerminalCompletion()
    {
        using var ring = new Ring(8);
        using var memory = new TrackingMemoryManager(32);
        var connection = CreateConnection(ring);
        var closeRequests = new List<(long ConnectionId, ushort Generation)>();
        connection.Generation = 12;
        connection.StartSendLoop((_, _) => { }, (id, generation) => closeRequests.Add((id, generation)));

        connection.SubmitSend(memory.Memory);
        connection.HasSendInFlight.Should().BeTrue();
        memory.UnpinCount.Should().Be(0);

        await connection.DisposeAsync();

        memory.UnpinCount.Should().Be(0, "the kernel still owns an in-flight SEND buffer");
        closeRequests.Should().Equal((0L, (ushort)12));

        connection.CompleteSend(memory.Memory.Length, 0);

        connection.HasSendInFlight.Should().BeFalse();
        memory.UnpinCount.Should().Be(1, "the terminal CQE releases kernel buffer ownership");
    }

    [Fact]
    public async Task AbortAndDispose_RequestCloseExactlyOnceWithGeneration()
    {
        using var ring = new Ring(8);
        var connection = CreateConnection(ring);
        var closeRequest = new TaskCompletionSource<(long, ushort)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int requestCount = 0;
        connection.Generation = 37;
        connection.StartSendLoop(
            (_, _) => { },
            (id, generation) =>
            {
                Interlocked.Increment(ref requestCount);
                closeRequest.TrySetResult((id, generation));
            });

        await Task.Run(() => connection.Abort(new ConnectionAbortedException("test")));
        await connection.DisposeAsync();
        connection.Abort(new ConnectionAbortedException("duplicate"));

        var request = await closeRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        request.Should().Be((0L, (ushort)37));
        Volatile.Read(ref requestCount).Should().Be(1);
    }

    [Fact]
    public async Task RingShutdown_ReleasesSendPinWhenNoTerminalCqeCanArrive()
    {
        var ring = new Ring(8);
        using var memory = new TrackingMemoryManager(32);
        var connection = CreateConnection(ring);

        connection.SubmitSend(memory.Memory);
        await connection.DisposeAsync();
        memory.UnpinCount.Should().Be(0);

        ring.Dispose();
        connection.CleanupAfterRingShutdown();

        connection.HasSendInFlight.Should().BeFalse();
        memory.UnpinCount.Should().Be(1);
    }

    [Fact]
    public async Task ListenerCloseRequest_RejectsStaleGeneration_AndClosesDuplicateCollectionsOnce()
    {
        using var ring = new Ring(8);
        var options = new IoUringTransportOptions
        {
            MaxConnections = 1,
            EnableBufferRing = false,
        };
        var listener = new IoUringConnectionListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            ring,
            options,
            NullLogger.Instance);
        int socketFd = Libc.socket(2 /* AF_INET */, 1 /* SOCK_STREAM */, 0);
        socketFd.Should().BeGreaterThanOrEqualTo(0);
        var connection = CreateConnection(ring, socketFd);
        connection.HasRecvInFlight = true;
        listener.SetConnectionForTest(0, connection);

        listener.ProcessCloseRequestForTest(0, (ushort)(connection.Generation + 1))
            .Should().BeFalse();
        connection.IsClosing.Should().BeFalse();
        connection.SocketCloseCount.Should().Be(0);

        listener.ProcessCloseRequestForTest(0, connection.Generation).Should().BeTrue();
        connection.IsClosing.Should().BeTrue();
        connection.SocketCloseCount.Should().Be(0, "the recv CQE still owns the slot");

        await listener.DisposeAsync();

        connection.SocketCloseCount.Should().Be(1,
            "the same connection may be present in active and closing collections during shutdown");
    }

    [Fact]
    public async Task ControlSubmissions_RetryAfterSqFull_WithoutDuplicateArming()
    {
        using var ring = new Ring(2);
        FillSubmissionQueue(ring);
        var listener = new IoUringConnectionListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            ring,
            new IoUringTransportOptions { MaxConnections = 1, EnableBufferRing = false },
            NullLogger.Instance);

        listener.RequestControlSubmissionsForTest();

        listener.AcceptSubmissionPending.Should().BeTrue();
        listener.EventFdReadSubmissionPending.Should().BeTrue();
        listener.AcceptArmed.Should().BeFalse();
        listener.EventFdReadArmed.Should().BeFalse();

        listener.RetryControlSubmissionsForTest();

        listener.AcceptSubmissionPending.Should().BeFalse();
        listener.EventFdReadSubmissionPending.Should().BeFalse();
        listener.AcceptArmed.Should().BeTrue();
        listener.EventFdReadArmed.Should().BeTrue();

        listener.RequestControlSubmissionsForTest();
        listener.AcceptSubmissionPending.Should().BeFalse();
        listener.EventFdReadSubmissionPending.Should().BeFalse();

        await listener.DisposeAsync();
    }

    [Fact]
    public void SubmitSend_WhenSqFull_SubmitsPendingEntriesAndMakesProgress()
    {
        using var ring = new Ring(1);
        FillSubmissionQueue(ring);
        using var memory = new TrackingMemoryManager(16);
        var connection = CreateConnection(ring);

        connection.SubmitSend(memory.Memory);

        connection.HasSendInFlight.Should().BeTrue();
        memory.PinCount.Should().Be(2, "the first pin is released before the exceptional submit");
        memory.UnpinCount.Should().Be(1);

        connection.CompleteSend(memory.Memory.Length, 0);
        memory.UnpinCount.Should().Be(2);
    }

    [Fact]
    public async Task SendLoop_SendsFinalBufferedDataWhenWriterCompletes()
    {
        var ring = new Ring(8);
        var scheduler = new IoUringPipeScheduler(() => { });
        var connection = CreateConnection(ring, scheduler: scheduler);
        connection.StartSendLoop((_, _) => { }, (_, _) => { });

        await connection.Transport.Output.WriteAsync("final"u8.ToArray());
        await connection.Transport.Output.CompleteAsync();

        scheduler.DrainWorkItems();

        connection.HasSendInFlight.Should().BeTrue(
            "a completed PipeReader can still contain the response's final bytes");

        await connection.DisposeAsync();
        ring.Dispose();
        connection.CleanupAfterRingShutdown();
    }

    [Fact]
    public async Task MultishotRecv_BuffersAdditionalCqeUntilPendingFlushCompletes()
    {
        using var ring = new Ring(8);
        var scheduler = new IoUringPipeScheduler(() => { });
        var connection = CreateConnection(ring, useBufferRing: true, scheduler: scheduler);
        var rearmRequest = new TaskCompletionSource<(long, ushort)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Generation = 7;
        connection.StartSendLoop(
            (id, generation) => rearmRequest.TrySetResult((id, generation)),
            (_, _) => { });

        byte[] first = new byte[(1024 * 1024) + 1];
        Array.Fill(first, (byte)'A');
        byte[] second = "tail"u8.ToArray();

        connection.OnRecvCompleteFromBuffer(first)
            .Should().Be(IoUringConnection.RecvWriteResult.Pending);
        connection.OnRecvCompleteFromBuffer(second)
            .Should().Be(IoUringConnection.RecvWriteResult.Pending);
        connection.PendingRecvBytes.Should().Be(second.Length);

        ReadResult firstRead = await connection.Transport.Input.ReadAsync();
        firstRead.Buffer.Length.Should().Be(first.Length,
            "the second CQE must not write through PipeWriter while the first flush is pending");
        connection.Transport.Input.AdvanceTo(firstRead.Buffer.End);
        scheduler.DrainWorkItems();

        connection.Generation = 8;
        var request = await rearmRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        request.Should().Be((0L, (ushort)7),
            "deferred callbacks must retain the generation that started the flush");

        connection.ResumeRecvAfterFlush().Should().Be(IoUringConnection.RecvWriteResult.Ready);
        connection.PendingRecvBytes.Should().Be(0);

        ReadResult secondRead = await connection.Transport.Input.ReadAsync();
        secondRead.Buffer.ToArray().Should().Equal(second);
        connection.Transport.Input.AdvanceTo(secondRead.Buffer.End);

        await connection.DisposeAsync();
    }

    [Fact]
    public async Task MultishotRecv_ClosesWhenPendingCopyBoundIsExceeded()
    {
        using var ring = new Ring(8);
        var connection = CreateConnection(ring, useBufferRing: true);
        var closeRequest = new TaskCompletionSource<(long, ushort)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Generation = 19;
        connection.StartSendLoop(
            (_, _) => { },
            (id, generation) => closeRequest.TrySetResult((id, generation)));

        connection.OnRecvCompleteFromBuffer(new byte[(1024 * 1024) + 1])
            .Should().Be(IoUringConnection.RecvWriteResult.Pending);
        connection.OnRecvCompleteFromBuffer(new byte[IoUringConnection.MaxPendingRecvBytes + 1])
            .Should().Be(IoUringConnection.RecvWriteResult.Closed);

        connection.PendingRecvBytes.Should().Be(0);
        (await closeRequest.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be((0L, (ushort)19));

        await connection.DisposeAsync();
    }

    [Fact]
    public async Task MultishotRecv_TerminalCqeWaitsForBufferedDataInsteadOfDroppingIt()
    {
        using var ring = new Ring(8);
        var scheduler = new IoUringPipeScheduler(() => { });
        var connection = CreateConnection(ring, useBufferRing: true, scheduler: scheduler);
        var rearmRequest = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.StartSendLoop(
            (_, _) => rearmRequest.TrySetResult(),
            (_, _) => { });
        byte[] first = new byte[(1024 * 1024) + 1];
        byte[] last = "last-before-eof"u8.ToArray();

        connection.OnRecvCompleteFromBuffer(first)
            .Should().Be(IoUringConnection.RecvWriteResult.Pending);
        connection.OnRecvCompleteFromBuffer(last)
            .Should().Be(IoUringConnection.RecvWriteResult.Pending);
        connection.OnRecvEnd().Should().Be(IoUringConnection.RecvWriteResult.Pending);

        ReadResult firstRead = await connection.Transport.Input.ReadAsync();
        connection.Transport.Input.AdvanceTo(firstRead.Buffer.End);
        scheduler.DrainWorkItems();
        await rearmRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));

        connection.ResumeRecvAfterFlush().Should().Be(IoUringConnection.RecvWriteResult.InputCompleted);

        ReadResult lastRead = await connection.Transport.Input.ReadAsync();
        lastRead.Buffer.ToArray().Should().Equal(last);
        lastRead.IsCompleted.Should().BeTrue();
        connection.Transport.Input.AdvanceTo(lastRead.Buffer.End);

        await connection.DisposeAsync();
    }

    [Fact]
    public async Task ClosingConnection_FinalizesAfterPendingRecvFlushCompletes()
    {
        using var ring = new Ring(8);
        var options = new IoUringTransportOptions
        {
            MaxConnections = 1,
            EnableBufferRing = false,
        };
        var listener = new IoUringConnectionListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            ring,
            options,
            NullLogger.Instance);
        var scheduler = new IoUringPipeScheduler(() => { });
        int socketFd = Libc.socket(2 /* AF_INET */, 1 /* SOCK_STREAM */, 0);
        socketFd.Should().BeGreaterThanOrEqualTo(0);
        var connection = CreateConnection(ring, socketFd, useBufferRing: true, scheduler);
        listener.SetConnectionForTest(0, connection);

        connection.OnRecvCompleteFromBuffer(new byte[(1024 * 1024) + 1])
            .Should().Be(IoUringConnection.RecvWriteResult.Pending);
        connection.IsClosing = true;
        connection.HasRecvInFlight = false;
        connection.OnRecvEnd().Should().Be(IoUringConnection.RecvWriteResult.Pending);

        ReadResult read = await connection.Transport.Input.ReadAsync();
        connection.Transport.Input.AdvanceTo(read.Buffer.End);
        scheduler.DrainWorkItems();

        listener.EnqueueRecvResubmitForTest(0, connection.Generation);
        listener.DrainRecvResubmitQueueForTest();

        connection.SocketCloseCount.Should().Be(1);
        await listener.DisposeAsync();
    }

    [Fact]
    public async Task CloseRequest_WaitsUntilSendLoopCanNoLongerSubmit()
    {
        using var ring = new Ring(8);
        var listener = new IoUringConnectionListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            ring,
            new IoUringTransportOptions { MaxConnections = 1, EnableBufferRing = false },
            NullLogger.Instance);
        var scheduler = new IoUringPipeScheduler(() => { });
        int socketFd = Libc.socket(2 /* AF_INET */, 1 /* SOCK_STREAM */, 0);
        socketFd.Should().BeGreaterThanOrEqualTo(0);
        var connection = CreateConnection(ring, socketFd, scheduler: scheduler);
        listener.SetConnectionForTest(0, connection);
        connection.StartSendLoop(
            (_, _) => { },
            (_, _) => { },
            (id, generation) => listener.ProcessCloseRequestForTest(id, generation));

        listener.ProcessCloseRequestForTest(0, connection.Generation).Should().BeTrue();
        connection.SocketCloseCount.Should().Be(0,
            "the idle send loop can still submit data after the close request");

        await connection.Transport.Output.CompleteAsync();
        scheduler.DrainWorkItems();

        connection.SendLoopCompleted.Should().BeTrue();
        connection.SocketCloseCount.Should().Be(1);
        await listener.DisposeAsync();
    }

    [Fact]
    public async Task PeerHalfClose_CanStillReceiveDelayedTransportResponse()
    {
        using var ring = new Ring(32);
        var listener = new IoUringConnectionListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            ring,
            new IoUringTransportOptions
            {
                RingSize = 32,
                MaxConnections = 1,
                EnableBufferRing = false,
            },
            NullLogger.Instance);
        listener.Bind(listenBacklog: 16);

        using var client = new Socket(SocketType.Stream, ProtocolType.Tcp);
        Task<ConnectionContext?> acceptTask = listener.AcceptAsync().AsTask();
        await client.ConnectAsync((IPEndPoint)listener.EndPoint);
        var connection = (IoUringConnection)(await acceptTask.WaitAsync(TimeSpan.FromSeconds(5)))!;

        byte[] request = "request-before-fin"u8.ToArray();
        await client.SendAsync(request, SocketFlags.None);
        client.Shutdown(SocketShutdown.Send);

        var receivedRequest = new ArrayBufferWriter<byte>();
        while (true)
        {
            ReadResult read = await connection.Transport.Input.ReadAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var segment in read.Buffer)
                receivedRequest.Write(segment.Span);
            connection.Transport.Input.AdvanceTo(read.Buffer.End);
            if (read.IsCompleted)
                break;
        }
        receivedRequest.WrittenSpan.ToArray().Should().Equal(request);

        byte[] response = "response-after-fin"u8.ToArray();
        await connection.Transport.Output.WriteAsync(response);
        await connection.Transport.Output.CompleteAsync();

        byte[] receivedResponse = new byte[response.Length];
        int received = 0;
        while (received < receivedResponse.Length)
        {
            int count = await client.ReceiveAsync(
                    receivedResponse.AsMemory(received),
                    SocketFlags.None)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            if (count == 0)
                break;
            received += count;
        }

        received.Should().Be(response.Length);
        receivedResponse.Should().Equal(response);

        await connection.DisposeAsync();
        await listener.DisposeAsync();
    }

    [Fact]
    public async Task RepeatedConnectionChurn_ReturnsSlotsFilesAndBudgetToBaseline()
    {
        using var ring = new Ring(32);
        var listener = new IoUringConnectionListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            ring,
            new IoUringTransportOptions
            {
                RingSize = 32,
                MaxConnections = 8,
                EnableBufferRing = false,
            },
            NullLogger.Instance);
        listener.Bind(listenBacklog: 16);
        int baselineRegisteredFiles = ring.RegisteredFileCount;

        for (int i = 0; i < 64; i++)
        {
            using var client = new Socket(SocketType.Stream, ProtocolType.Tcp);
            Task<ConnectionContext?> accept = listener.AcceptAsync().AsTask();
            await client.ConnectAsync((IPEndPoint)listener.EndPoint);
            var connection = (IoUringConnection)(await accept.WaitAsync(TimeSpan.FromSeconds(5)))!;

            byte[] request = BitConverter.GetBytes(i);
            await client.SendAsync(request, SocketFlags.None);
            client.Shutdown(SocketShutdown.Send);

            ReadResult input = await connection.Transport.Input.ReadAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            input.Buffer.ToArray().Should().Equal(request);
            connection.Transport.Input.AdvanceTo(input.Buffer.End);

            byte[] response = BitConverter.GetBytes(~i);
            await connection.Transport.Output.WriteAsync(response);
            await connection.Transport.Output.CompleteAsync();

            byte[] received = new byte[response.Length];
            int count = await client.ReceiveAsync(received, SocketFlags.None);
            count.Should().Be(response.Length);
            received.Should().Equal(response);
            await connection.DisposeAsync();
            client.Dispose();

            for (int attempt = 0;
                 attempt < 100 && listener.FreeConnectionSlotCount != 8;
                 attempt++)
            {
                await Task.Delay(10);
            }
            listener.FreeConnectionSlotCount.Should().Be(8);
        }

        listener.OccupiedConnectionCount.Should().Be(0);
        listener.ClosingConnectionCount.Should().Be(0);
        listener.PendingReceiveBudgetBytes.Should().Be(0);
        ring.RegisteredFileCount.Should().Be(baselineRegisteredFiles);
        await listener.DisposeAsync();
    }

    [Fact]
    public async Task Bind_WithEphemeralPort_ReportsActualLocalEndpoint()
    {
        using var ring = new Ring(8);
        var listener = new IoUringConnectionListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            ring,
            new IoUringTransportOptions { MaxConnections = 1, EnableBufferRing = false },
            NullLogger.Instance);

        listener.Bind(listenBacklog: 16);

        listener.EndPoint.Should().BeOfType<IPEndPoint>()
            .Which.Port.Should().BeGreaterThan(0);

        await listener.DisposeAsync();
    }

    [Fact]
    public async Task IoLoopShutdownTimeout_DoesNotPretendTheLoopStopped()
    {
        var neverStops = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Func<Task> wait = () => IoUringConnectionListener.WaitForIoLoopShutdownAsync(
            neverStops.Task,
            TimeSpan.Zero);

        await wait.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task FatalListenerFailure_IsSurfacedByAcceptAsync()
    {
        using var ring = new Ring(8);
        var listener = new IoUringConnectionListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            ring,
            new IoUringTransportOptions { MaxConnections = 1, EnableBufferRing = false },
            NullLogger.Instance);
        var fatalError = new InvalidOperationException("fatal-ring-state");

        listener.FailListenerForTest(fatalError);

        Func<Task> accept = async () => await listener.AcceptAsync();
        IOException thrown = (await accept.Should().ThrowAsync<IOException>()).Which;
        thrown.InnerException.Should().BeSameAs(fatalError);
        await listener.DisposeAsync();
    }

    [Fact]
    public async Task PendingReceiveCopies_RespectSharedRingBudget()
    {
        using var ring = new Ring(8);
        var budget = new ReceiveBufferBudget(maxBytes: 4);
        var first = CreateConnection(ring, useBufferRing: true, receiveBufferBudget: budget);
        var second = CreateConnection(ring, useBufferRing: true, receiveBufferBudget: budget);
        var secondClose = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        second.StartSendLoop((_, _) => { }, (_, _) => secondClose.TrySetResult());

        first.OnRecvCompleteFromBuffer(new byte[(1024 * 1024) + 1])
            .Should().Be(IoUringConnection.RecvWriteResult.Pending);
        second.OnRecvCompleteFromBuffer(new byte[(1024 * 1024) + 1])
            .Should().Be(IoUringConnection.RecvWriteResult.Pending);

        first.OnRecvCompleteFromBuffer("1234"u8)
            .Should().Be(IoUringConnection.RecvWriteResult.Pending);
        budget.ReservedBytes.Should().Be(4);

        second.OnRecvCompleteFromBuffer("x"u8)
            .Should().Be(IoUringConnection.RecvWriteResult.Closed);
        await secondClose.Task.WaitAsync(TimeSpan.FromSeconds(5));
        budget.ReservedBytes.Should().Be(4);

        first.CleanupAfterRingShutdown();
        second.CleanupAfterRingShutdown();
        budget.ReservedBytes.Should().Be(0);
        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task ConnectionFinalization_ReleasesQueuedReceiveBudget()
    {
        using var ring = new Ring(8);
        var budget = new ReceiveBufferBudget(maxBytes: 4);
        var listener = new IoUringConnectionListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            ring,
            new IoUringTransportOptions { MaxConnections = 1, EnableBufferRing = false },
            NullLogger.Instance);
        int socketFd = Libc.socket(2 /* AF_INET */, 1 /* SOCK_STREAM */, 0);
        socketFd.Should().BeGreaterThanOrEqualTo(0);
        var connection = CreateConnection(
            ring,
            socketFd,
            useBufferRing: true,
            receiveBufferBudget: budget);
        listener.SetConnectionForTest(0, connection);

        connection.OnRecvCompleteFromBuffer(new byte[(1024 * 1024) + 1])
            .Should().Be(IoUringConnection.RecvWriteResult.Pending);
        connection.OnRecvCompleteFromBuffer("1234"u8)
            .Should().Be(IoUringConnection.RecvWriteResult.Pending);
        budget.ReservedBytes.Should().Be(4);

        listener.ProcessCloseRequestForTest(0, connection.Generation).Should().BeTrue();

        connection.SocketCloseCount.Should().Be(1);
        budget.ReservedBytes.Should().Be(0);
        await listener.DisposeAsync();
    }

    [Fact]
    public async Task MultiListener_WithEphemeralPort_BindsEveryWorkerToReportedPort()
    {
        using ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });
        var listener = new IoUringMultiListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new IoUringTransportOptions
            {
                RingSize = 8,
                MaxConnections = 2,
                ThreadCount = 2,
                EnableBufferRing = false,
            },
            loggerFactory);

        listener.EndPoint.Should().BeOfType<IPEndPoint>()
            .Which.Port.Should().BeGreaterThan(0);

        await listener.DisposeAsync();
    }

    [Fact]
    public async Task FatalMultiRingWorkerFailure_IsSurfacedByAcceptAsync()
    {
        using ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });
        var listener = new IoUringMultiListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new IoUringTransportOptions
            {
                RingSize = 8,
                MaxConnections = 2,
                ThreadCount = 2,
                EnableBufferRing = false,
            },
            loggerFactory);
        var fatalError = new InvalidOperationException("fatal-worker-state");

        listener.FailWorkerForTest(0, fatalError);

        Func<Task> accept = async () => await listener.AcceptAsync();
        IOException thrown = (await accept.Should().ThrowAsync<IOException>()).Which;
        thrown.InnerException.Should().BeOfType<IOException>()
            .Which.InnerException.Should().BeSameAs(fatalError);
        await listener.DisposeAsync();
    }

    [Fact]
    public void PublicConnectionIds_AreUniqueAcrossSlotReuse()
    {
        string first = IoUringConnectionListener.CreatePublicConnectionId();
        string second = IoUringConnectionListener.CreatePublicConnectionId();

        second.Should().NotBe(first);
    }

    [Fact]
    public async Task AcceptedSocket_ExposesRemoteAndLocalEndpoints()
    {
        var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        int port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
        using var client = new Socket(SocketType.Stream, ProtocolType.Tcp);
        Task connectTask = client.ConnectAsync(IPAddress.Loopback, port);
        using Socket server = await tcpListener.AcceptSocketAsync();
        await connectTask;
        tcpListener.Stop();

        int serverFd = (int)server.SafeHandle.DangerousGetHandle();
        var (remoteEndPoint, localEndPoint) =
            IoUringConnectionListener.GetSocketEndpoints(serverFd);

        remoteEndPoint.Should().BeOfType<IPEndPoint>()
            .Which.Port.Should().Be(((IPEndPoint)client.LocalEndPoint!).Port);
        localEndPoint.Should().BeOfType<IPEndPoint>()
            .Which.Port.Should().Be(port);
    }

    [Fact]
    public void CqOverflow_IsFatalWithoutNoDropFeature()
    {
        IoUringConnectionListener.IsCqOverflowFatal(0).Should().BeTrue();
    }

    [Fact]
    public void CqOverflow_IsDeferredWithNoDropFeature()
    {
        IoUringConnectionListener.IsCqOverflowFatal(IoUringConstants.IORING_FEAT_NODROP)
            .Should().BeFalse();
    }

    private static IoUringConnection CreateConnection(
        Ring ring,
        int socketFd = -1,
        bool useBufferRing = false,
        IoUringPipeScheduler? scheduler = null,
        ReceiveBufferBudget? receiveBufferBudget = null)
    {
        scheduler ??= new IoUringPipeScheduler(() => { });
        return new IoUringConnection(
            connectionId: 0,
            socketFd,
            fileIndex: -1,
            ring,
            remoteEndPoint: null,
            localEndPoint: null,
            receiveBufferSize: 2048,
            scheduler,
            unsafeInlineScheduling: false,
            NullLogger.Instance,
            useBufferRing,
            receiveBufferBudget: receiveBufferBudget);
    }

    private static unsafe void FillSubmissionQueue(Ring ring)
    {
        int count = 0;
        while (ring.TryGetSqe(out IoUringSqe* sqe))
        {
            sqe->Opcode = IoUringConstants.IORING_OP_NOP;
            sqe->UserData = IoUringConnection.EncodeUserData(
                0,
                0,
                IoUringConnection.OpType.Cancel);
            count++;
            count.Should().BeLessThan(65536);
        }
        count.Should().BeGreaterThan(0);
    }

    private sealed unsafe class TrackingMemoryManager : MemoryManager<byte>
    {
        private readonly byte[] _buffer;
        private GCHandle _pin;

        public TrackingMemoryManager(int length) => _buffer = new byte[length];

        public int PinCount { get; private set; }
        public int UnpinCount { get; private set; }

        public override Span<byte> GetSpan() => _buffer;

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            PinCount++;
            _pin = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
            return new MemoryHandle(
                (byte*)_pin.AddrOfPinnedObject() + elementIndex,
                default,
                this);
        }

        public override void Unpin()
        {
            UnpinCount++;
            if (_pin.IsAllocated)
                _pin.Free();
        }

        protected override void Dispose(bool disposing)
        {
            if (_pin.IsAllocated)
                _pin.Free();
        }
    }
}
