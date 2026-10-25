using FluentAssertions;
using FluentAssertions.Execution;
using Kestrel.Transport.IoUring.Transport;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

public class ListenerCloseFinalizationTests
{
    [Fact]
    public void Run_WhenCloseSocketFdThrows_StillLogsWarning_ReleasesSlotExactlyOnce_AndDoesNotEscape()
    {
        List<string> trace = [];
        List<string> warnings = [];
        var finalizationAdapter = new RecordingFinalizationAdapter(
            trace,
            warnings,
            closeException: new InvalidOperationException("simulated close failure"));

        Exception? failure = Record.Exception(() =>
            ListenerCloseFinalization.Run(connectionId: 17, hasRecvInFlight: false, ref finalizationAdapter));

        using var assertions = new AssertionScope();

        failure.Should().BeNull(
            because: "listener close finalization should contain socket-close failures so slot release can still run");
        trace.Should().Equal(
            [
                "remove-closing:17",
                "close",
                "release:17",
            ]);
        warnings.Should().Equal(
            [
                "log-close-finalization:17:simulated close failure",
            ]);
    }

    [Fact]
    public void Run_WhenCloseSocketFdSucceeds_RemovesClosingEntry_ClosesAndReleasesWithoutWarning()
    {
        List<string> trace = [];
        List<string> warnings = [];
        var finalizationAdapter = new RecordingFinalizationAdapter(trace, warnings);

        ListenerCloseFinalization.Run(connectionId: 18, hasRecvInFlight: false, ref finalizationAdapter);

        trace.Should().Equal(
            [
                "remove-closing:18",
                "close",
                "release:18",
            ]);
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void Run_WhenRecvIsStillInFlight_ShutsDownSocketWithoutReleasingSlot()
    {
        List<string> trace = [];
        List<string> warnings = [];
        var finalizationAdapter = new RecordingFinalizationAdapter(trace, warnings);

        ListenerCloseFinalization.Run(connectionId: 19, hasRecvInFlight: true, ref finalizationAdapter);

        trace.Should().Equal(
            [
                "shutdown",
            ]);
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void Run_WhenCloseFailureLoggerThrows_StillReleasesSlotExactlyOnce_AndDoesNotEscape()
    {
        List<string> trace = [];
        List<string> warnings = [];
        var finalizationAdapter = new RecordingFinalizationAdapter(
            trace,
            warnings,
            closeException: new InvalidOperationException("simulated close failure"),
            logException: new InvalidOperationException("simulated finalization logger failure"));

        Exception? failure = Record.Exception(() =>
            ListenerCloseFinalization.Run(connectionId: 21, hasRecvInFlight: false, ref finalizationAdapter));

        using var assertions = new AssertionScope();

        failure.Should().BeNull(
            because: "a throwing close-finalization logger must not stop slot release");
        trace.Should().Equal(
            [
                "remove-closing:21",
                "close",
                "release:21",
            ]);
        warnings.Should().Equal(
            [
                "log-close-finalization:21:simulated close failure",
            ]);
    }

    private struct RecordingFinalizationAdapter(
        List<string> trace,
        List<string> warnings,
        Exception? closeException = null,
        Exception? logException = null)
        : ListenerCloseFinalization.IFinalizationAdapter<RecordingFinalizationAdapter>
    {
        private readonly List<string> _trace = trace;
        private readonly List<string> _warnings = warnings;
        private readonly Exception? _closeException = closeException;
        private readonly Exception? _logException = logException;

        public static void ShutdownSocket(ref RecordingFinalizationAdapter self) =>
            self._trace.Add("shutdown");

        public static void RemoveClosingConnection(
            ref RecordingFinalizationAdapter self,
            long connectionId) =>
            self._trace.Add($"remove-closing:{connectionId}");

        public static void CloseSocketFd(ref RecordingFinalizationAdapter self)
        {
            self._trace.Add("close");
            if (self._closeException is not null)
                throw self._closeException;
        }

        public static void ReleaseConnection(
            ref RecordingFinalizationAdapter self,
            long connectionId) =>
            self._trace.Add($"release:{connectionId}");

        public static void LogCloseFinalizationFailure(
            ref RecordingFinalizationAdapter self,
            long connectionId,
            Exception exception)
        {
            self._warnings.Add($"log-close-finalization:{connectionId}:{exception.GetBaseException().Message}");
            if (self._logException is not null)
                throw self._logException;
        }
    }
}
