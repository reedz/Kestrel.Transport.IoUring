using FluentAssertions;
using FluentAssertions.Execution;
using Kestrel.Transport.IoUring.Transport;
using Microsoft.AspNetCore.Connections;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

public class ConnectionTransportAbortTests
{
    [Fact]
    public void Run_WhenConnectionClosedCallbackThrows_StillRunsAbortCleanup_LogsWarning_AndDoesNotEscape()
    {
        using var connectionCts = new CancellationTokenSource();
        List<string> trace = [];
        List<string> warnings = [];
        HashSet<string> callbacks = [];

        using var _ = connectionCts.Token.Register(() =>
        {
            callbacks.Add("ok");
            trace.Add("callback:ok");
        });
        using var __ = connectionCts.Token.Register(() =>
        {
            callbacks.Add("throw");
            trace.Add("callback:throw");
            throw new InvalidOperationException("simulated abort callback failure");
        });

        var abortAdapter = new RecordingAbortAdapter(trace, warnings);
        var reason = new ConnectionAbortedException("simulated abort");

        Exception? failure = Record.Exception(() =>
            ConnectionTransportAbort.Run(connectionCts, reason, ref abortAdapter));

        using var assertions = new AssertionScope();

        failure.Should().BeNull(
            because: "transport abort must contain ConnectionClosed callback failures and continue mandatory cleanup");
        connectionCts.IsCancellationRequested.Should().BeTrue();
        callbacks.Should().BeEquivalentTo(["throw", "ok"]);
        trace.Should().Equal(
            [
                "callback:throw",
                "callback:ok",
                "cancel-send-retry",
                "complete-input:simulated abort",
                "cancel-pending-read",
                "shutdown",
            ]);
        warnings.Should().Equal(
            [
                "log-abort-callback:simulated abort callback failure",
            ]);
    }

    [Fact]
    public void Run_WhenAbortCallbackLoggerThrows_StillRunsAbortCleanup_AndDoesNotEscape()
    {
        using var connectionCts = new CancellationTokenSource();
        List<string> trace = [];
        List<string> warnings = [];
        HashSet<string> callbacks = [];

        using var _ = connectionCts.Token.Register(() =>
        {
            callbacks.Add("ok");
            trace.Add("callback:ok");
        });
        using var __ = connectionCts.Token.Register(() =>
        {
            callbacks.Add("throw");
            trace.Add("callback:throw");
            throw new InvalidOperationException("simulated abort callback failure");
        });

        var abortAdapter = new RecordingAbortAdapter(
            trace,
            warnings,
            logException: new InvalidOperationException("simulated abort logger failure"));
        var reason = new ConnectionAbortedException("simulated abort");

        Exception? failure = Record.Exception(() =>
            ConnectionTransportAbort.Run(connectionCts, reason, ref abortAdapter));

        using var assertions = new AssertionScope();

        failure.Should().BeNull(
            because: "a throwing abort logger must not skip the remaining cleanup");
        connectionCts.IsCancellationRequested.Should().BeTrue();
        callbacks.Should().BeEquivalentTo(["throw", "ok"]);
        trace.Should().Equal(
            [
                "callback:throw",
                "callback:ok",
                "cancel-send-retry",
                "complete-input:simulated abort",
                "cancel-pending-read",
                "shutdown",
            ]);
        warnings.Should().Equal(
            [
                "log-abort-callback:simulated abort callback failure",
            ]);
    }

    [Fact]
    public void Run_WhenConnectionClosedCallbacksDoNotThrow_CompletesAbortCleanupInOrder()
    {
        using var connectionCts = new CancellationTokenSource();
        List<string> trace = [];
        List<string> warnings = [];

        using var _ = connectionCts.Token.Register(() => trace.Add("callback:ok"));

        var abortAdapter = new RecordingAbortAdapter(trace, warnings);
        var reason = new ConnectionAbortedException("nonthrowing abort");

        ConnectionTransportAbort.Run(connectionCts, reason, ref abortAdapter);

        trace.Should().Equal(
            [
                "callback:ok",
                "cancel-send-retry",
                "complete-input:nonthrowing abort",
                "cancel-pending-read",
                "shutdown",
            ]);
        warnings.Should().BeEmpty();
    }

    private struct RecordingAbortAdapter(
        List<string> trace,
        List<string> warnings,
        Exception? logException = null)
        : ConnectionTransportAbort.IAbortAdapter<RecordingAbortAdapter>
    {
        private readonly List<string> _trace = trace;
        private readonly List<string> _warnings = warnings;
        private readonly Exception? _logException = logException;

        public static void CancelSendRetry(ref RecordingAbortAdapter self) =>
            self._trace.Add("cancel-send-retry");

        public static void CompleteInput(
            ref RecordingAbortAdapter self,
            ConnectionAbortedException reason) =>
            self._trace.Add($"complete-input:{reason.Message}");

        public static void CancelOutputPendingRead(ref RecordingAbortAdapter self) =>
            self._trace.Add("cancel-pending-read");

        public static void ShutdownSocket(ref RecordingAbortAdapter self) =>
            self._trace.Add("shutdown");

        public static void LogConnectionClosedCallbackFailure(
            ref RecordingAbortAdapter self,
            Exception exception)
        {
            self._warnings.Add($"log-abort-callback:{exception.GetBaseException().Message}");
            if (self._logException is not null)
                throw self._logException;
        }
    }
}
