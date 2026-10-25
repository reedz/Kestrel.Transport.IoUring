using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using FluentAssertions;
using Kestrel.Transport.IoUring;
using Kestrel.Transport.IoUring.Native;
using Xunit;
using Xunit.Sdk;

namespace Kestrel.Transport.IoUring.Tests.Unit;

public class RingEnterBudgetTests
{
    [Fact]
    public Task Submit_Empty_DoesNotInvokeEnter()
        => RunWithTimeout(Submit_Empty_DoesNotInvokeEnterCore);

    private void Submit_Empty_DoesNotInvokeEnterCore()
    {
        if (!Ring.IsSupported) return;

        var enter = new ScriptedEnterInvoker();

        using var ring = CreateRing(enter);

        int submitted = ring.Submit();

        submitted.Should().Be(0);
        enter.Calls.Should().BeEmpty("empty Submit should return before invoking io_uring_enter when flags are zero");
    }

    [Fact]
    public Task Submit_AfterFullSuccess_ThenEmpty_DoesNotInvokeEnter()
        => RunWithTimeout(Submit_AfterFullSuccess_ThenEmpty_DoesNotInvokeEnterCore);

    private unsafe void Submit_AfterFullSuccess_ThenEmpty_DoesNotInvokeEnterCore()
    {
        if (!Ring.IsSupported) return;

        var enter = new ScriptedEnterInvoker(
            call => Ring.EnterInvocationResult.Success((int)call.ToSubmit));

        using var ring = CreateRing(enter);
        StageNops(ring, 2);

        int first = ring.Submit();
        int second = ring.Submit();

        first.Should().Be(2);
        second.Should().Be(0);
        enter.Calls.Select(static call => call.ToSubmit)
            .Should().Equal(new uint[] { 2u },
                "after a fully successful submit, another empty Submit should return before invoking io_uring_enter again");
    }

    [Fact]
    public Task Submit_AfterShortPositiveEnter_ResubmitsResidualBudget()
        => RunWithTimeout(Submit_AfterShortPositiveEnter_ResubmitsResidualBudgetCore);

    private unsafe void Submit_AfterShortPositiveEnter_ResubmitsResidualBudgetCore()
    {
        if (!Ring.IsSupported) return;

        var enter = new ScriptedEnterInvoker(
            _ => Ring.EnterInvocationResult.Success(1),
            call => Ring.EnterInvocationResult.Success((int)call.ToSubmit));

        using var ring = CreateRing(enter);
        StageNops(ring, 2);

        int first = ring.Submit();
        int second = ring.Submit();

        first.Should().Be(1);
        enter.Calls.Select(static call => call.ToSubmit)
            .Should().Equal(new uint[] { 2u, 1u },
                "a later submit should retry the residual SQE left kernel-visible after a short positive enter result");
        second.Should().Be(1);
    }

    [Fact]
    public Task Submit_RetryAfterEintr_PreservesPendingBudget()
        => RunWithTimeout(Submit_RetryAfterEintr_PreservesPendingBudgetCore);

    private unsafe void Submit_RetryAfterEintr_PreservesPendingBudgetCore()
    {
        if (!Ring.IsSupported) return;

        var enter = new ScriptedEnterInvoker(
            _ => Ring.EnterInvocationResult.Failure(IoUringConstants.EINTR),
            call => Ring.EnterInvocationResult.Success((int)call.ToSubmit));

        using var ring = CreateRing(enter);
        StageNops(ring, 2);

        int submitted = ring.Submit();

        enter.Calls.Select(static call => call.ToSubmit)
            .Should().Equal(new uint[] { 2u, 2u }, "retrying after EINTR should keep the pending submit budget");
        submitted.Should().Be(2);
    }

    [Fact]
    public Task Submit_SqPollRetryAfterEintr_PreservesFlushBudgetConditionally()
        => RunWithTimeout(Submit_SqPollRetryAfterEintr_PreservesFlushBudgetConditionallyCore);

    private unsafe void Submit_SqPollRetryAfterEintr_PreservesFlushBudgetConditionallyCore()
    {
        if (!Ring.IsSupported) return;

        var enter = new ScriptedEnterInvoker(
            _ => Ring.EnterInvocationResult.Failure(IoUringConstants.EINTR),
            _ => Ring.EnterInvocationResult.Success(0));

        using var ring = CreateRing(enter, IoUringConstants.IORING_SETUP_SQPOLL);
        if ((ring.SetupFlags & IoUringConstants.IORING_SETUP_SQPOLL) == 0)
            return;

        StageNops(ring, 2);

        int submitted = ring.Submit();

        enter.Calls.Select(static call => call.ToSubmit)
            .Should().Equal(new uint[] { 2u, 0u },
                "when SQPOLL survives setup fallback, Submit should stay flush-gated and retry EINTR with a zero budget");
        submitted.Should().Be(0);
    }

    [Fact]
    public Task Submit_RetryAfterEintr_ExcludesNewUnflushedSqeFromRetryBudget()
        => RunWithTimeout(Submit_RetryAfterEintr_ExcludesNewUnflushedSqeFromRetryBudgetCore);

    private unsafe void Submit_RetryAfterEintr_ExcludesNewUnflushedSqeFromRetryBudgetCore()
    {
        if (!Ring.IsSupported) return;

        Ring? callbackRing = null;
        var enter = new ScriptedEnterInvoker(
            _ =>
            {
                StageNops(callbackRing ?? throw new XunitException(
                    "Expected the callback ring to be assigned before the scripted EINTR step runs."), 1);
                return Ring.EnterInvocationResult.Failure(IoUringConstants.EINTR);
            },
            call => Ring.EnterInvocationResult.Success((int)call.ToSubmit),
            call => Ring.EnterInvocationResult.Success((int)call.ToSubmit));

        using var ring = CreateRing(enter);
        callbackRing = ring;
        StageNops(ring, 2);

        int first = ring.Submit();
        int second = ring.Submit();

        first.Should().Be(2);
        second.Should().Be(1);
        enter.Calls.Select(static call => call.ToSubmit)
            .Should().Equal(new uint[] { 2u, 2u, 1u },
                "retrying after EINTR should recompute only kernel-visible pending SQEs, so a staged-but-unflushed extra SQE must wait for the next Flush");
    }

    [Fact]
    public Task Submit_Eexist_IsFatalInsteadOfRetriedForever()
        => RunWithTimeout(Submit_Eexist_IsFatalInsteadOfRetriedForeverCore);

    // EEXIST from io_uring_enter means the calling thread is not the ring's SINGLE_ISSUER. It never clears by
    // itself, so retrying (as before) spun forever.
    private unsafe void Submit_Eexist_IsFatalInsteadOfRetriedForeverCore()
    {
        if (!Ring.IsSupported) return;

        var enter = new ScriptedEnterInvoker(
            _ => Ring.EnterInvocationResult.Failure(IoUringConstants.EEXIST),
            call => Ring.EnterInvocationResult.Success((int)call.ToSubmit));

        using var ring = CreateRing(enter);
        StageNops(ring, 2);

        FluentActions.Invoking(() => ring.Submit())
            .Should().Throw<InvalidOperationException>().WithMessage("*EEXIST*");
        enter.Calls.Should().ContainSingle("EEXIST must not be retried");
    }

    [Fact]
    public Task Submit_AfterEagainReturn_SeesKernelVisiblePendingBudget()
        => RunWithTimeout(Submit_AfterEagainReturn_SeesKernelVisiblePendingBudgetCore);

    private unsafe void Submit_AfterEagainReturn_SeesKernelVisiblePendingBudgetCore()
    {
        if (!Ring.IsSupported) return;

        var enter = new ScriptedEnterInvoker(
            _ => Ring.EnterInvocationResult.Failure(IoUringConstants.EAGAIN),
            call => Ring.EnterInvocationResult.Success((int)call.ToSubmit));

        using var ring = CreateRing(enter);
        StageNops(ring, 2);

        int first = ring.Submit();
        int second = ring.Submit();

        first.Should().Be(0);
        enter.Calls.Select(static call => call.ToSubmit)
            .Should().Equal(new uint[] { 2u, 2u },
                "after EAGAIN returns control, the next Submit should still enter the kernel-visible pending SQEs even when Flush() returns 0");
        second.Should().Be(2);
    }

    [Fact]
    public Task Submit_AfterOlderResidueAndNewFlush_ReentersAllKernelVisibleBudget()
        => RunWithTimeout(Submit_AfterOlderResidueAndNewFlush_ReentersAllKernelVisibleBudgetCore);

    private unsafe void Submit_AfterOlderResidueAndNewFlush_ReentersAllKernelVisibleBudgetCore()
    {
        if (!Ring.IsSupported) return;

        var enter = new ScriptedEnterInvoker(
            _ => Ring.EnterInvocationResult.Failure(IoUringConstants.EAGAIN),
            _ => Ring.EnterInvocationResult.Success(1));

        using var ring = CreateRing(enter);
        StageNops(ring, 2);

        int first = ring.Submit();

        StageNops(ring, 1);

        int second = ring.Submit();

        first.Should().Be(0);
        enter.Calls.Select(static call => call.ToSubmit)
            .Should().Equal(new uint[] { 2u, 3u },
                "after older kernel-visible residue and a later flush, the next Submit should re-enter all ready SQEs instead of only the newest flush");
        second.Should().Be(1);
    }

    [Fact]
    public Task SubmitAndWait_RetryAfterEintr_PreservesPendingBudgetAndWaitParameters()
        => RunWithTimeout(SubmitAndWait_RetryAfterEintr_PreservesPendingBudgetAndWaitParametersCore);

    private unsafe void SubmitAndWait_RetryAfterEintr_PreservesPendingBudgetAndWaitParametersCore()
    {
        if (!Ring.IsSupported) return;

        var enter = new ScriptedEnterInvoker(
            _ => Ring.EnterInvocationResult.Failure(IoUringConstants.EINTR),
            call => Ring.EnterInvocationResult.Success((int)call.ToSubmit));

        using var ring = CreateRing(enter);
        StageNops(ring, 2);

        int submitted = ring.SubmitAndWait(1);

        enter.Calls.Select(static call => call.ToSubmit)
            .Should().Equal(new uint[] { 2u, 2u }, "retrying SubmitAndWait after EINTR should keep the pending submit budget");
        enter.Calls.Select(static call => call.MinComplete)
            .Should().Equal(new uint[] { 1u, 1u }, "retrying SubmitAndWait after EINTR should preserve the requested minComplete");
        enter.Calls.Select(static call => (call.Flags & IoUringConstants.IORING_ENTER_GETEVENTS) != 0)
            .Should().Equal(new[] { true, true }, "retrying SubmitAndWait after EINTR should preserve GETEVENTS");
        submitted.Should().Be(2);
    }

    [Fact]
    public Task SubmitAndWait_AfterEbusyReturn_SeesKernelVisiblePendingBudget()
        => RunWithTimeout(SubmitAndWait_AfterEbusyReturn_SeesKernelVisiblePendingBudgetCore);

    private unsafe void SubmitAndWait_AfterEbusyReturn_SeesKernelVisiblePendingBudgetCore()
    {
        if (!Ring.IsSupported) return;

        var enter = new ScriptedEnterInvoker(
            _ => Ring.EnterInvocationResult.Failure(IoUringConstants.EBUSY),
            call => Ring.EnterInvocationResult.Success((int)call.ToSubmit));

        using var ring = CreateRing(enter);
        StageNops(ring, 2);

        int first = ring.SubmitAndWait(1);
        int second = ring.SubmitAndWait(1);

        enter.Calls.Should().HaveCount(2);
        enter.Calls.Select(static call => call.MinComplete)
            .Should().Equal(new uint[] { 1u, 1u },
                "SubmitAndWait retries should preserve the requested minComplete");
        enter.Calls.Select(static call => (call.Flags & IoUringConstants.IORING_ENTER_GETEVENTS) != 0)
            .Should().Equal(new[] { true, true },
                "SubmitAndWait retries should preserve GETEVENTS even when the submit budget is wrong");
        first.Should().Be(0);
        enter.Calls.Select(static call => call.ToSubmit)
            .Should().Equal(new uint[] { 2u, 2u },
                "after EBUSY returns control, the next SubmitAndWait should still enter the kernel-visible pending SQEs even when Flush() returns 0");
        second.Should().Be(2);
    }

    [Fact]
    public Task EnterForTeardown_PersistentEexist_ReturnsFailureWithoutRetrying()
        => RunWithTimeout(EnterForTeardown_PersistentEexist_ReturnsFailureWithoutRetryingCore);

    private void EnterForTeardown_PersistentEexist_ReturnsFailureWithoutRetryingCore()
    {
        if (!Ring.IsSupported) return;

        var enter = new ScriptedEnterInvoker(
            _ => Ring.EnterInvocationResult.Failure(IoUringConstants.EEXIST),
            _ => Ring.EnterInvocationResult.Failure(IoUringConstants.EEXIST));

        using var ring = CreateRing(enter);

        Ring.EnterInvocationResult result = ring.EnterForTeardown();

        result.ReturnValue.Should().Be(-1);
        result.Errno.Should().Be(IoUringConstants.EEXIST);
        enter.Calls.Should().ContainSingle(
            "wrong-issuer EEXIST during final teardown is persistent, so the bounded path must stop quickly instead of spinning");
        enter.Calls.Select(static call => call.ToSubmit)
            .Should().Equal(new uint[] { 0u },
                "the bounded teardown enter must keep its zero submit budget when DEFER_TASKRUN wrong-issuer EEXIST is reported");
        enter.Calls.Select(static call => call.MinComplete)
            .Should().Equal(new uint[] { 0u },
                "the bounded teardown enter must stay nonblocking when wrong-issuer EEXIST stops materialization");
        enter.Calls.Select(static call => (call.Flags & IoUringConstants.IORING_ENTER_GETEVENTS) != 0)
            .Should().Equal(new[] { true },
                "the bounded teardown enter must preserve GETEVENTS when reporting wrong-issuer EEXIST");
    }

    [Fact]
    public Task EnterForTeardown_RepeatedEintr_StopsAtRetryBudget()
        => RunWithTimeout(EnterForTeardown_RepeatedEintr_StopsAtRetryBudgetCore);

    private void EnterForTeardown_RepeatedEintr_StopsAtRetryBudgetCore()
    {
        if (!Ring.IsSupported) return;

        var enter = new ScriptedEnterInvoker(
            Enumerable.Repeat<Func<EnterCall, Ring.EnterInvocationResult>>(
                _ => Ring.EnterInvocationResult.Failure(IoUringConstants.EINTR),
                Ring.TeardownEnterEintrRetryLimit + 2).ToArray());

        using var ring = CreateRing(enter);

        Ring.EnterInvocationResult result = ring.EnterForTeardown();

        result.ReturnValue.Should().Be(-1);
        result.Errno.Should().Be(IoUringConstants.EINTR);
        enter.Calls.Should().HaveCount(Ring.TeardownEnterEintrRetryLimit + 1,
            "the bounded teardown enter should stop after a small fixed EINTR retry cap");
        enter.Calls.Select(static call => call.ToSubmit)
            .Should().OnlyContain(static toSubmit => toSubmit == 0u,
                "bounded teardown retries must never pick up pending SQEs from the shared submit path");
        enter.Calls.Select(static call => call.MinComplete)
            .Should().OnlyContain(static minComplete => minComplete == 0u);
        enter.Calls.Select(static call => (call.Flags & IoUringConstants.IORING_ENTER_GETEVENTS) != 0)
            .Should().OnlyContain(static hasGetEvents => hasGetEvents);
    }

    [Theory]
    [InlineData(IoUringConstants.EAGAIN)]
    [InlineData(IoUringConstants.EBUSY)]
    public Task EnterForTeardown_WhenKernelCannotMaterializeImmediately_ReturnsZeroWithoutRetry(int errno)
        => RunWithTimeout(() => EnterForTeardown_WhenKernelCannotMaterializeImmediately_ReturnsZeroWithoutRetryCore(errno));

    private void EnterForTeardown_WhenKernelCannotMaterializeImmediately_ReturnsZeroWithoutRetryCore(int errno)
    {
        if (!Ring.IsSupported) return;

        var enter = new ScriptedEnterInvoker(
            _ => Ring.EnterInvocationResult.Failure(errno));

        using var ring = CreateRing(enter);

        Ring.EnterInvocationResult result = ring.EnterForTeardown();

        result.ReturnValue.Should().Be(0);
        result.Errno.Should().Be(0);
        enter.Calls.Select(static call => call.ToSubmit)
            .Should().Equal(new uint[] { 0u },
                "the teardown enter must remain bounded instead of retrying forever when no CQE is immediately materializable");
        enter.Calls.Select(static call => call.MinComplete)
            .Should().Equal(new uint[] { 0u });
        enter.Calls.Select(static call => (call.Flags & IoUringConstants.IORING_ENTER_GETEVENTS) != 0)
            .Should().Equal(new[] { true });
    }

    [Fact]
    public Task EnterForTeardown_UnexpectedErrno_ReturnsFailureWithoutThrowing()
        => RunWithTimeout(EnterForTeardown_UnexpectedErrno_ReturnsFailureWithoutThrowingCore);

    private void EnterForTeardown_UnexpectedErrno_ReturnsFailureWithoutThrowingCore()
    {
        if (!Ring.IsSupported) return;

        var enter = new ScriptedEnterInvoker(
            _ => Ring.EnterInvocationResult.Failure(IoUringConstants.EINVAL));

        using var ring = CreateRing(enter);

        Ring.EnterInvocationResult result = ring.EnterForTeardown();

        result.ReturnValue.Should().Be(-1);
        result.Errno.Should().Be(IoUringConstants.EINVAL);
        enter.Calls.Should().ContainSingle();
        enter.Calls[0].ToSubmit.Should().Be(0u);
        enter.Calls[0].MinComplete.Should().Be(0u);
        (enter.Calls[0].Flags & IoUringConstants.IORING_ENTER_GETEVENTS).Should().NotBe(0u);
    }

    [Fact]
    public Task EnterForTeardown_WithStagedAndFlushedBacklog_KeepsZeroSubmitBudgetOnRetries()
        => RunWithTimeout(EnterForTeardown_WithStagedAndFlushedBacklog_KeepsZeroSubmitBudgetOnRetriesCore);

    private unsafe void EnterForTeardown_WithStagedAndFlushedBacklog_KeepsZeroSubmitBudgetOnRetriesCore()
    {
        if (!Ring.IsSupported) return;

        var enter = new ScriptedEnterInvoker(
            _ => Ring.EnterInvocationResult.Failure(IoUringConstants.EAGAIN),
            _ => Ring.EnterInvocationResult.Failure(IoUringConstants.EINTR),
            _ => Ring.EnterInvocationResult.Success(0));

        using var ring = CreateRing(enter);
        StageNops(ring, 2);

        ring.Submit().Should().Be(0,
            "the first submit leaves flushed SQEs kernel-visible so teardown retry budgeting can prove it stays at zero");

        StageNops(ring, 1);

        Ring.EnterInvocationResult result = ring.EnterForTeardown();

        result.ReturnValue.Should().Be(0);
        result.Errno.Should().Be(0);
        enter.Calls.Skip(1).Select(static call => call.ToSubmit)
            .Should().Equal(new uint[] { 0u, 0u },
                "teardown retries must never resubmit flushed backlog or newly staged SQEs");
        enter.Calls.Skip(1).Select(static call => call.MinComplete)
            .Should().Equal(new uint[] { 0u, 0u });
        enter.Calls.Skip(1).Select(static call => (call.Flags & IoUringConstants.IORING_ENTER_GETEVENTS) != 0)
            .Should().Equal(new[] { true, true });
    }

    [Fact]
    public Task CaptureCompletionSnapshotForTeardown_WithDeferTaskRun_InvokesNonBlockingGetEventsBeforeSnapshot()
        => RunWithTimeout(CaptureCompletionSnapshotForTeardown_WithDeferTaskRun_InvokesNonBlockingGetEventsBeforeSnapshotCore);

    private void CaptureCompletionSnapshotForTeardown_WithDeferTaskRun_InvokesNonBlockingGetEventsBeforeSnapshotCore()
    {
        if (!Ring.IsSupported) return;

        var enter = new ScriptedEnterInvoker(
            _ => Ring.EnterInvocationResult.Success(0));

        using var ring = CreateRing(enter, IoUringConstants.IORING_SETUP_DEFER_TASKRUN);
        if ((ring.SetupFlags & IoUringConstants.IORING_SETUP_DEFER_TASKRUN) == 0)
            return;

        Ring.TeardownCompletionSnapshot snapshot = ring.CaptureCompletionSnapshotForTeardown();

        snapshot.AvailableCompletions.Should().Be(0);
        snapshot.MaterializationErrno.Should().Be(0);
        snapshot.HasNodropOverflowBacklog.Should().BeFalse();
        enter.Calls.Should().ContainSingle(
            "DEFER_TASKRUN rings must issue one bounded GETEVENTS enter so deferred task work can materialize into CQEs before teardown peeks");
        enter.Calls[0].ToSubmit.Should().Be(0u);
        enter.Calls[0].MinComplete.Should().Be(0u);
        (enter.Calls[0].Flags & IoUringConstants.IORING_ENTER_GETEVENTS).Should().NotBe(0u);
    }

    [Fact]
    public Task CaptureCompletionSnapshotForTeardown_WithSqOverflowFlag_PreservesHiddenOverflowSignalDuringBoundaryCapture()
        => RunWithTimeout(CaptureCompletionSnapshotForTeardown_WithSqOverflowFlag_PreservesHiddenOverflowSignalDuringBoundaryCaptureCore);

    private void CaptureCompletionSnapshotForTeardown_WithSqOverflowFlag_PreservesHiddenOverflowSignalDuringBoundaryCaptureCore()
    {
        if (!Ring.IsSupported) return;

        var enter = new ScriptedEnterInvoker(
            _ => Ring.EnterInvocationResult.Success(0));

        using var ring = CreateRing(enter);
        if (!SupportsNodropFeature(ring))
            return;

        uint originalSqFlags = ReadSqFlags(ring);
        uint originalCqOverflow = ReadCqOverflowCount(ring);

        try
        {
            WriteSqFlags(ring, originalSqFlags | IoUringConstants.IORING_SQ_CQ_OVERFLOW);
            WriteCqOverflowCount(ring, 0u);

            Ring.TeardownCompletionSnapshot snapshot = ring.CaptureCompletionSnapshotForTeardown();

            snapshot.AvailableCompletions.Should().Be(0);
            snapshot.MaterializationErrno.Should().Be(0);
            snapshot.HasNodropOverflowBacklog.Should().BeTrue(
                "the boundary snapshot should report hidden overflow-list work without folding it into the visible CQ snapshot before the drain requests another bounded round");
            enter.Calls.Should().BeEmpty(
                "the ring should leave NODROP overflow materialization to the bounded drain rounds instead of spinning before the first visible snapshot is drained");
        }
        finally
        {
            WriteSqFlags(ring, originalSqFlags);
            WriteCqOverflowCount(ring, originalCqOverflow);
        }
    }

    [Fact]
    public Task CaptureCompletionSnapshotForTeardown_WithSqOverflowFlag_OverflowRoundRechecksOnceAndClearsFlag()
        => RunWithTimeout(CaptureCompletionSnapshotForTeardown_WithSqOverflowFlag_OverflowRoundRechecksOnceAndClearsFlagCore);

    private void CaptureCompletionSnapshotForTeardown_WithSqOverflowFlag_OverflowRoundRechecksOnceAndClearsFlagCore()
    {
        if (!Ring.IsSupported) return;

        Ring? attachedRing = null;
        uint originalSqFlags = 0;
        var enter = new ScriptedEnterInvoker(
            _ =>
            {
                WriteSqFlags(
                    attachedRing ?? throw new InvalidOperationException("Attach ring before clearing SQ overflow backlog."),
                    originalSqFlags & ~IoUringConstants.IORING_SQ_CQ_OVERFLOW);
                return Ring.EnterInvocationResult.Success(0);
            });

        using var ring = attachedRing = CreateRing(enter);
        if (!SupportsNodropFeature(ring))
            return;

        originalSqFlags = ReadSqFlags(ring);
        uint originalCqOverflow = ReadCqOverflowCount(ring);

        try
        {
            WriteSqFlags(ring, originalSqFlags | IoUringConstants.IORING_SQ_CQ_OVERFLOW);
            WriteCqOverflowCount(ring, 0u);

            Ring.TeardownCompletionSnapshot snapshot = ring.CaptureCompletionSnapshotForTeardown(
                materializeNodropOverflowBacklog: true);

            snapshot.AvailableCompletions.Should().Be(0);
            snapshot.MaterializationErrno.Should().Be(0);
            snapshot.HasNodropOverflowBacklog.Should().BeFalse();
            enter.Calls.Should().ContainSingle(
                "each bounded overflow round should perform only one nonblocking enter and then re-read the live SQ overflow flag");
            enter.Calls[0].ToSubmit.Should().Be(0u);
            enter.Calls[0].MinComplete.Should().Be(0u);
            (enter.Calls[0].Flags & IoUringConstants.IORING_ENTER_GETEVENTS).Should().NotBe(0u);
        }
        finally
        {
            WriteSqFlags(ring, originalSqFlags);
            WriteCqOverflowCount(ring, originalCqOverflow);
        }
    }

    [Fact]
    public Task CaptureCompletionSnapshotForTeardown_WithPersistentSqOverflowFlag_OverflowRoundPerformsSingleStep()
        => RunWithTimeout(CaptureCompletionSnapshotForTeardown_WithPersistentSqOverflowFlag_OverflowRoundPerformsSingleStepCore);

    private void CaptureCompletionSnapshotForTeardown_WithPersistentSqOverflowFlag_OverflowRoundPerformsSingleStepCore()
    {
        if (!Ring.IsSupported) return;

        var enter = new ScriptedEnterInvoker(
            _ => Ring.EnterInvocationResult.Success(0));

        using var ring = CreateRing(enter);
        if (!SupportsNodropFeature(ring))
            return;

        uint originalSqFlags = ReadSqFlags(ring);
        uint originalCqOverflow = ReadCqOverflowCount(ring);

        try
        {
            WriteSqFlags(ring, originalSqFlags | IoUringConstants.IORING_SQ_CQ_OVERFLOW);
            WriteCqOverflowCount(ring, 0u);

            Ring.TeardownCompletionSnapshot snapshot = ring.CaptureCompletionSnapshotForTeardown(
                materializeNodropOverflowBacklog: true);

            snapshot.AvailableCompletions.Should().Be(0);
            snapshot.MaterializationErrno.Should().Be(0);
            snapshot.HasNodropOverflowBacklog.Should().BeTrue(
                "the overflow-round snapshot should preserve the live hidden-overflow signal when one bounded enter cannot clear it");
            enter.Calls.Should().ContainSingle(
                "persistent hidden overflow-list work should be reported back to the bounded drain loop after one enter, not handled by a nested retry loop inside Ring");
            enter.Calls.Should().OnlyContain(static call =>
                call.ToSubmit == 0u &&
                call.MinComplete == 0u &&
                (call.Flags & IoUringConstants.IORING_ENTER_GETEVENTS) != 0u);
        }
        finally
        {
            WriteSqFlags(ring, originalSqFlags);
            WriteCqOverflowCount(ring, originalCqOverflow);
        }
    }

    [Fact]
    public Task CaptureCompletionSnapshotForTeardown_WithHistoricalOverflowCountButNoSqOverflowFlag_SkipsMaterialization()
        => RunWithTimeout(CaptureCompletionSnapshotForTeardown_WithHistoricalOverflowCountButNoSqOverflowFlag_SkipsMaterializationCore);

    private void CaptureCompletionSnapshotForTeardown_WithHistoricalOverflowCountButNoSqOverflowFlag_SkipsMaterializationCore()
    {
        if (!Ring.IsSupported) return;

        var enter = new ScriptedEnterInvoker(
            _ => Ring.EnterInvocationResult.Success(0));

        using var ring = CreateRing(enter);
        if (!SupportsNodropFeature(ring))
            return;

        uint originalSqFlags = ReadSqFlags(ring);
        uint originalCqOverflow = ReadCqOverflowCount(ring);

        try
        {
            WriteSqFlags(ring, originalSqFlags & ~IoUringConstants.IORING_SQ_CQ_OVERFLOW);
            WriteCqOverflowCount(ring, originalCqOverflow + 7u);

            Ring.TeardownCompletionSnapshot snapshot = ring.CaptureCompletionSnapshotForTeardown(
                materializeNodropOverflowBacklog: true);

            snapshot.AvailableCompletions.Should().Be(0);
            snapshot.MaterializationErrno.Should().Be(0);
            snapshot.HasNodropOverflowBacklog.Should().BeFalse();
            enter.Calls.Should().BeEmpty(
                "historical CQ overflow counts must not trigger teardown materialization when the live SQ overflow-backlog flag is clear");
        }
        finally
        {
            WriteSqFlags(ring, originalSqFlags);
            WriteCqOverflowCount(ring, originalCqOverflow);
        }
    }

    private static async Task RunWithTimeout(Action action)
    {
        await Task.Run(action).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static Ring CreateRing(ScriptedEnterInvoker enter, uint setupFlags = 0)
    {
        var ring = new Ring(8, setupFlags, enter.Invoke);
        enter.Attach(ring);
        return ring;
    }

    private static unsafe void StageNops(Ring ring, int count)
    {
        lock (ring.SubmitLock)
        {
            for (int i = 0; i < count; i++)
            {
                ring.TryGetSqe(out IoUringSqe* sqe).Should().BeTrue();
                sqe->Opcode = IoUringConstants.IORING_OP_NOP;
                sqe->UserData = (ulong)(i + 1);
            }
        }
    }

    private sealed class ScriptedEnterInvoker
    {
        private const int MaxInvocations = 16;
        private readonly Queue<Func<EnterCall, Ring.EnterInvocationResult>> _steps;
        private Ring? _ring;

        public ScriptedEnterInvoker(params Func<EnterCall, Ring.EnterInvocationResult>[] steps)
        {
            _steps = new Queue<Func<EnterCall, Ring.EnterInvocationResult>>(steps);
        }

        public List<EnterCall> Calls { get; } = new();

        public void Attach(Ring ring) => _ring = ring;

        public Ring.EnterInvocationResult Invoke(int fd, uint toSubmit, uint minComplete, uint flags)
        {
            var call = new EnterCall(fd, toSubmit, minComplete, flags);
            Calls.Add(call);
            uint ringEntries = ReadRingEntries(_ring ?? throw new InvalidOperationException(
                "Attach the scripted enter invoker to a Ring before exercising enter paths."));
            call.ToSubmit.Should().BeLessThanOrEqualTo(ringEntries,
                $"io_uring_enter toSubmit should never exceed the SQ ring capacity ({ringEntries})");

            if (Calls.Count > MaxInvocations)
                throw new XunitException(
                    $"Exceeded scripted io_uring_enter cap of {MaxInvocations}. Call sequence: {FormatCallSequence()}");

            if (_steps.Count == 0)
                return ReportScriptedResult(call, Ring.EnterInvocationResult.Success((int)toSubmit));

            return ReportScriptedResult(call, _steps.Dequeue()(call));
        }

        private Ring.EnterInvocationResult ReportScriptedResult(EnterCall call, Ring.EnterInvocationResult scripted)
        {
            if (scripted.ReturnValue > 0)
                ConsumeWithRealEnter(call, (uint)scripted.ReturnValue);

            return scripted;
        }

        private void ConsumeWithRealEnter(EnterCall call, uint consumedCount)
        {
            Ring ring = _ring ?? throw new InvalidOperationException(
                "Attach the scripted enter invoker to a Ring before exercising positive scripted outcomes.");

            uint headBefore = ReadKernelHead(ring);
            uint helperFlags = call.Flags & IoUringConstants.IORING_ENTER_SQ_WAKEUP;
            int actual = IoUringNative.IoUringEnter(call.Fd, consumedCount, 0, helperFlags);
            if (actual < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                throw new XunitException(
                    $"Real helper io_uring_enter(fd={call.Fd}, toSubmit={consumedCount}, minComplete=0, flags=0x{helperFlags:X}) failed with errno {errno}. Call sequence: {FormatCallSequence()}");
            }

            uint headAfter = ReadKernelHead(ring);
            actual.Should().Be((int)consumedCount,
                $"real helper enter should consume the scripted positive prefix {consumedCount} for {FormatCall(call)}; calls: {FormatCallSequence()}");
            headAfter.Should().Be(headBefore + consumedCount,
                $"real helper enter should advance the shared sq.head by {consumedCount} for {FormatCall(call)}; calls: {FormatCallSequence()}");
        }

        private string FormatCallSequence()
        {
            return Calls.Count == 0
                ? "<none>"
                : string.Join(" -> ", Calls.Select(static (call, index) => $"#{index + 1}:{FormatCall(call)}"));
        }

        private static string FormatCall(EnterCall call)
        {
            return $"submit={call.ToSubmit},minComplete={call.MinComplete},flags=0x{call.Flags:X}";
        }
    }

    private readonly record struct EnterCall(int Fd, uint ToSubmit, uint MinComplete, uint Flags);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_sq")]
    private static extern ref readonly SubmissionQueue GetSubmissionQueue(Ring ring);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_head")]
    private static unsafe extern ref readonly uint* GetSubmissionQueueHeadPointer(SubmissionQueue submissionQueue);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_flags")]
    private static unsafe extern ref readonly uint* GetSubmissionQueueFlagsPointer(SubmissionQueue submissionQueue);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_cq")]
    private static extern ref readonly CompletionQueue GetCompletionQueue(Ring ring);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_overflow")]
    private static unsafe extern ref readonly uint* GetCompletionQueueOverflowPointer(CompletionQueue completionQueue);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_features")]
    private static extern ref readonly uint GetRingFeatures(Ring ring);

    private static unsafe uint ReadSqFlags(Ring ring)
    {
        SubmissionQueue submissionQueue = GetSubmissionQueue(ring);
        uint* sqFlagsPointer = GetSubmissionQueueFlagsPointer(submissionQueue);
        return Volatile.Read(ref *sqFlagsPointer);
    }

    private static unsafe void WriteSqFlags(Ring ring, uint flags)
    {
        SubmissionQueue submissionQueue = GetSubmissionQueue(ring);
        uint* sqFlagsPointer = GetSubmissionQueueFlagsPointer(submissionQueue);
        Volatile.Write(ref *sqFlagsPointer, flags);
    }

    private static unsafe uint ReadCqOverflowCount(Ring ring)
    {
        CompletionQueue completionQueue = GetCompletionQueue(ring);
        uint* cqOverflowPointer = GetCompletionQueueOverflowPointer(completionQueue);
        return Volatile.Read(ref *cqOverflowPointer);
    }

    private static unsafe void WriteCqOverflowCount(Ring ring, uint overflowCount)
    {
        CompletionQueue completionQueue = GetCompletionQueue(ring);
        uint* cqOverflowPointer = GetCompletionQueueOverflowPointer(completionQueue);
        Volatile.Write(ref *cqOverflowPointer, overflowCount);
    }

    private static unsafe uint ReadKernelHead(Ring ring)
    {
        SubmissionQueue submissionQueue = GetSubmissionQueue(ring);
        uint* headPointer = GetSubmissionQueueHeadPointer(submissionQueue);
        return Volatile.Read(ref *headPointer);
    }

    private static uint ReadRingEntries(Ring ring) => GetSubmissionQueue(ring).RingEntries;

    private static bool SupportsNodropFeature(Ring ring) =>
        (GetRingFeatures(ring) & IoUringConstants.IORING_FEAT_NODROP) != 0;
}
