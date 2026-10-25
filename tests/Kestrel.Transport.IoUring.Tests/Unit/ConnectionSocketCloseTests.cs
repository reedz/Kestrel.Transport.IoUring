using System.Runtime.InteropServices;
using FluentAssertions;
using FluentAssertions.Execution;
using Kestrel.Transport.IoUring.Transport;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

public class ConnectionSocketCloseTests
{
    [Fact]
    public void Run_WhenConnectionClosedCallbackThrows_StillUnregistersAndClosesExactlyOnceBeforeSurfacingFailure()
    {
        using var connectionCts = new CancellationTokenSource();
        List<string> trace = [];
        HashSet<string> callbacks = [];
        int socketClosed = 0;

        using var _ = connectionCts.Token.Register(() =>
        {
            callbacks.Add("ok");
            trace.Add("callback:ok");
        });
        using var __ = connectionCts.Token.Register(() =>
        {
            callbacks.Add("throw");
            trace.Add("callback:throw");
            throw new InvalidOperationException("simulated ConnectionClosed failure");
        });

        var closeAdapter = new RecordingCloseAdapter(trace);

        AggregateException failure = Assert.Throws<AggregateException>(() =>
            ConnectionSocketClose.Run(connectionCts, fileIndex: 7, socketFd: 42, ref socketClosed, ref closeAdapter));

        using var assertions = new AssertionScope();

        connectionCts.IsCancellationRequested.Should().BeTrue();
        callbacks.Should().BeEquivalentTo(["throw", "ok"]);
        failure.InnerExceptions.Should().ContainSingle()
            .Which.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("simulated ConnectionClosed failure");
        socketClosed.Should().Be(1);

        trace.Should().Equal(
            [
                "callback:throw",
                "callback:ok",
                "unregister:7",
                "close:42",
            ],
            because: "fd ownership must still be released during the first close attempt even when a ConnectionClosed callback throws");

        ConnectionSocketClose.Run(connectionCts, fileIndex: 7, socketFd: 42, ref socketClosed, ref closeAdapter);

        trace.Should().Equal(
            [
                "callback:throw",
                "callback:ok",
                "unregister:7",
                "close:42",
            ],
            because: "a retry after the first throwing close attempt must not issue duplicate unregister or close work");
    }

    [Fact]
    public void Run_WhenCancellationThrowsAndCleanupNeedsBothWarningBranches_CompletesCleanupBeforeSurfacingAggregateFailure()
    {
        using var connectionCts = new CancellationTokenSource();
        List<string> trace = [];
        HashSet<string> callbacks = [];
        int socketClosed = 0;

        using var _ = connectionCts.Token.Register(() =>
        {
            callbacks.Add("ok");
            trace.Add("callback:ok");
        });
        using var __ = connectionCts.Token.Register(() =>
        {
            callbacks.Add("throw");
            trace.Add("callback:throw");
            throw new InvalidOperationException("simulated ConnectionClosed failure");
        });

        var closeAdapter = new RecordingCloseAdapter(
            trace,
            unregisterResult: false,
            closeResult: -1,
            closeErrno: 9);

        AggregateException failure = Assert.Throws<AggregateException>(() =>
            ConnectionSocketClose.Run(connectionCts, fileIndex: 17, socketFd: 102, ref socketClosed, ref closeAdapter));

        using var assertions = new AssertionScope();

        connectionCts.IsCancellationRequested.Should().BeTrue();
        callbacks.Should().BeEquivalentTo(["throw", "ok"]);
        failure.InnerExceptions.Should().ContainSingle()
            .Which.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("simulated ConnectionClosed failure");

        trace.Should().Equal(
            [
                "callback:throw",
                "callback:ok",
                "unregister:17",
                "log-unregister:17",
                "close:102",
                "log-close:102:9",
            ],
            because: "callback failure must surface only after both cleanup warning branches have run");
    }

    [Fact]
    public void Run_WhenWarningLoggersThrow_StillCompletesCleanupAndAggregatesFailures()
    {
        using var connectionCts = new CancellationTokenSource();
        List<string> trace = [];
        HashSet<string> callbacks = [];
        int socketClosed = 0;

        using var _ = connectionCts.Token.Register(() =>
        {
            callbacks.Add("ok");
            trace.Add("callback:ok");
        });
        using var __ = connectionCts.Token.Register(() =>
        {
            callbacks.Add("throw");
            trace.Add("callback:throw");
            throw new InvalidOperationException("simulated ConnectionClosed failure");
        });

        var closeAdapter = new RecordingCloseAdapter(
            trace,
            unregisterResult: false,
            closeResult: -1,
            closeErrno: 9,
            unregisterLogException: new InvalidOperationException("simulated unregister logger failure"),
            closeLogException: new InvalidOperationException("simulated close logger failure"));

        AggregateException failure = Assert.Throws<AggregateException>(() =>
            ConnectionSocketClose.Run(connectionCts, fileIndex: 21, socketFd: 122, ref socketClosed, ref closeAdapter));

        using var assertions = new AssertionScope();

        connectionCts.IsCancellationRequested.Should().BeTrue();
        callbacks.Should().BeEquivalentTo(["throw", "ok"]);
        failure.InnerExceptions.Should().HaveCount(3);
        var callbackFailure = failure.InnerExceptions[0].Should().BeOfType<AggregateException>().Which;
        callbackFailure.InnerExceptions.Should().ContainSingle()
            .Which.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("simulated ConnectionClosed failure");
        failure.InnerExceptions[1].Message.Should().Be("simulated unregister logger failure");
        failure.InnerExceptions[2].Message.Should().Be("simulated close logger failure");

        trace.Should().Equal(
            [
                "callback:throw",
                "callback:ok",
                "unregister:21",
                "log-unregister:21",
                "close:122",
                "log-close:122:9",
            ],
            because: "logger failures must not skip the mandatory close cleanup path");
    }

    [Fact]
    public void Run_WhenCallbacksDoNotThrow_UnregistersFixedFileAndClosesSocketOnce()
    {
        using var connectionCts = new CancellationTokenSource();
        List<string> trace = [];
        int socketClosed = 0;

        using var _ = connectionCts.Token.Register(() => trace.Add("callback:ok"));

        var closeAdapter = new RecordingCloseAdapter(trace);

        ConnectionSocketClose.Run(connectionCts, fileIndex: 9, socketFd: 52, ref socketClosed, ref closeAdapter);

        trace.Should().Equal(
            [
                "callback:ok",
                "unregister:9",
                "close:52",
            ]);
    }

    [Fact]
    public void Run_WhenCalledRepeatedlyAfterSuccessfulCleanup_RemainsIdempotent()
    {
        using var connectionCts = new CancellationTokenSource();
        List<string> trace = [];
        int socketClosed = 0;

        using var _ = connectionCts.Token.Register(() => trace.Add("callback:ok"));

        var closeAdapter = new RecordingCloseAdapter(trace);

        ConnectionSocketClose.Run(connectionCts, fileIndex: 11, socketFd: 62, ref socketClosed, ref closeAdapter);
        ConnectionSocketClose.Run(connectionCts, fileIndex: 11, socketFd: 62, ref socketClosed, ref closeAdapter);

        trace.Should().Equal(
            [
                "callback:ok",
                "unregister:11",
                "close:62",
            ],
            because: "the socket-close guard should prevent duplicate unregister or close work after the first successful cleanup pass");
    }

    [Fact]
    public void Run_WhenFileIndexIsNegative_SkipsUnregisterAndClosesSocket()
    {
        using var connectionCts = new CancellationTokenSource();
        List<string> trace = [];
        int socketClosed = 0;

        using var _ = connectionCts.Token.Register(() => trace.Add("callback:ok"));

        var closeAdapter = new RecordingCloseAdapter(trace);

        ConnectionSocketClose.Run(connectionCts, fileIndex: -1, socketFd: 72, ref socketClosed, ref closeAdapter);

        trace.Should().Equal(
            [
                "callback:ok",
                "close:72",
            ]);
    }

    [Fact]
    public void Run_WhenUnregisterFixedFileFails_LogsAndStillClosesSocket()
    {
        using var connectionCts = new CancellationTokenSource();
        List<string> trace = [];
        int socketClosed = 0;

        using var _ = connectionCts.Token.Register(() => trace.Add("callback:ok"));

        var closeAdapter = new RecordingCloseAdapter(trace, unregisterResult: false);

        ConnectionSocketClose.Run(connectionCts, fileIndex: 13, socketFd: 82, ref socketClosed, ref closeAdapter);

        trace.Should().Equal(
            [
                "callback:ok",
                "unregister:13",
                "log-unregister:13",
                "close:82",
            ]);
    }

    [Fact]
    public void Run_WhenCloseSocketFdFails_LogsWarning()
    {
        using var connectionCts = new CancellationTokenSource();
        List<string> trace = [];
        int socketClosed = 0;

        using var _ = connectionCts.Token.Register(() => trace.Add("callback:ok"));

        var closeAdapter = new RecordingCloseAdapter(trace, closeResult: -1, closeErrno: 9);

        ConnectionSocketClose.Run(connectionCts, fileIndex: 15, socketFd: 92, ref socketClosed, ref closeAdapter);

        trace.Should().Equal(
            [
                "callback:ok",
                "unregister:15",
                "close:92",
                "log-close:92:9",
            ]);
    }

    private struct RecordingCloseAdapter(
        List<string> trace,
        bool unregisterResult = true,
        int closeResult = 0,
        int closeErrno = 0,
        Exception? unregisterLogException = null,
        Exception? closeLogException = null)
        : ConnectionSocketClose.ICloseAdapter<RecordingCloseAdapter>
    {
        private readonly List<string> _trace = trace;
        private readonly bool _unregisterResult = unregisterResult;
        private readonly int _closeResult = closeResult;
        private readonly int _closeErrno = closeErrno;
        private readonly Exception? _unregisterLogException = unregisterLogException;
        private readonly Exception? _closeLogException = closeLogException;

        public static bool UnregisterFixedFile(ref RecordingCloseAdapter self, int fileIndex)
        {
            self._trace.Add($"unregister:{fileIndex}");
            return self._unregisterResult;
        }

        public static int CloseSocketFd(ref RecordingCloseAdapter self, int socketFd)
        {
            self._trace.Add($"close:{socketFd}");
            if (self._closeResult < 0)
                Marshal.SetLastPInvokeError(self._closeErrno);

            return self._closeResult;
        }

        public static void LogFixedFileUnregisterFailure(ref RecordingCloseAdapter self, int fileIndex)
        {
            self._trace.Add($"log-unregister:{fileIndex}");
            if (self._unregisterLogException is not null)
                throw self._unregisterLogException;
        }

        public static void LogSocketCloseFailure(ref RecordingCloseAdapter self, int socketFd, int errno)
        {
            self._trace.Add($"log-close:{socketFd}:{errno}");
            if (self._closeLogException is not null)
                throw self._closeLogException;
        }
    }
}
