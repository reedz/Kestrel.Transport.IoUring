using FluentAssertions;
using Kestrel.Transport.IoUring;
using Kestrel.Transport.IoUring.Native;
using Kestrel.Transport.IoUring.Transport;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

public class ListenerStoppedCompletionDrainOverflowTests
{
    private const int MaxCaptureCalls = 1 + Ring.TeardownOverflowDrainRoundLimit;
    private const int MaxOverflowEnterCalls = Ring.TeardownOverflowDrainRoundLimit;
    private const int MaxOverflowEnterCallsWithInitialBoundaryStep = 1 + Ring.TeardownOverflowDrainRoundLimit;

    [Fact]
    public void Drain_WhenCqStartsFull_AndAcceptedOverflowNeedsPostDrainEnter_ClosesAllAcceptedFdsWithinHardBound()
    {
        var result = RunScenario(
            new ScriptedOverflowDrainState(
                visibleCapacity: 2,
                initialVisible:
                [
                    CreateEventFdCqe(),
                    CreateAcceptCqe(socketFdOrError: 101, more: false),
                ],
                overflowBacklog:
                [
                    CreateAcceptCqe(socketFdOrError: 202, more: false),
                ],
                requiresInitialEnter: true));

        result.MaterializationErrno.Should().Be(0);
        result.Failure.Should().BeNull();
        result.CaptureCallCount.Should().Be(2,
            "the bounded GREEN path should take one boundary snapshot and one overflow round after the visible CQ space is reclaimed");
        result.EnterCallCount.Should().Be(2,
            "the focused overflow witness should use exactly one boundary enter and one post-drain overflow enter");
        result.PeekCallCount.Should().Be(3);
        result.AdvanceCallCount.Should().Be(3);
        result.StepCount.Should().Be(12);
        result.RemainingVisible.Should().BeEmpty();
        result.MaterializableWithOneMoreEnter.Should().BeEmpty(
            because: "a completed teardown should not leave any hidden overflow-list CQE waiting for one more bounded materialization step");
        result.RemainingOverflow.Should().BeEmpty(
            because: "the hidden overflow-list backlog should be exhausted before ring close");
        result.ClosedFds.Should().Equal(
            [101, 202],
            because: "all accepted fds that are visible in a drained snapshot or that become visible in a later bounded overflow round should be reclaimed before ring close");
        AssertStepAccounting(result);
    }

    [Fact]
    public void Drain_WhenOverflowFitsVisibleSpace_ClosesVisibleAndMaterializedAccepts_WithoutTouchingNonAccept()
    {
        var result = RunScenario(
            new ScriptedOverflowDrainState(
                visibleCapacity: 3,
                initialVisible:
                [
                    CreateAcceptCqe(socketFdOrError: 101, more: false),
                ],
                overflowBacklog:
                [
                    CreateEventFdCqe(),
                    CreateAcceptCqe(socketFdOrError: 202, more: false),
                ],
                requiresInitialEnter: true));

        result.MaterializationErrno.Should().Be(0);
        result.Failure.Should().BeNull();
        result.CaptureCallCount.Should().Be(1,
            "the boundary GETEVENTS step can materialize hidden overflow-list CQEs immediately when visible CQ space is already available");
        result.EnterCallCount.Should().Be(1);
        result.PeekCallCount.Should().Be(3);
        result.AdvanceCallCount.Should().Be(3);
        result.StepCount.Should().Be(10);
        result.Drained.Should().Equal(
            [
                Describe(CreateAcceptCqe(socketFdOrError: 101, more: false)),
                Describe(CreateEventFdCqe()),
                Describe(CreateAcceptCqe(socketFdOrError: 202, more: false)),
            ]);
        result.RemainingVisible.Should().BeEmpty();
        result.RemainingOverflow.Should().BeEmpty();
        result.MaterializableWithOneMoreEnter.Should().BeEmpty();
        result.ClosedFds.Should().Equal(
            [101, 202],
            because: "accepted fds that are already visible or that materialize into a bounded drained snapshot should be closed, while the eventfd CQE remains non-owning");
        AssertStepAccounting(result);
    }

    [Fact]
    public void Drain_WhenOverflowMaterializationNeverMakesProgress_StopsWithinHardBound()
    {
        var result = RunScenario(
            new ScriptedOverflowDrainState(
                visibleCapacity: 1,
                initialVisible:
                [
                    CreateAcceptCqe(socketFdOrError: 101, more: false),
                ],
                overflowBacklog:
                [
                    CreateAcceptCqe(socketFdOrError: 202, more: false),
                ],
                requiresInitialEnter: true,
                enterOperations:
                [
                    EnterOperation.NoOp(),
                    EnterOperation.NoOp(),
                ],
                defaultEnterOperation: EnterOperation.NoOp()));

        result.MaterializationErrno.Should().Be(0);
        result.Failure.Should().BeNull();
        result.CaptureCallCount.Should().Be(2);
        result.EnterCallCount.Should().Be(2);
        result.PeekCallCount.Should().Be(1);
        result.AdvanceCallCount.Should().Be(1);
        result.StepCount.Should().Be(7,
            "the bounded no-progress path should stop after one failed overflow round instead of spinning forever");
        result.CaptureCallCount.Should().BeLessThanOrEqualTo(MaxCaptureCalls);
        result.EnterCallCount.Should().BeLessThanOrEqualTo(MaxOverflowEnterCallsWithInitialBoundaryStep,
            "persistent overflow/no-progress enters must terminate instead of spinning forever");
        result.RemainingVisible.Should().BeEmpty();
        result.RemainingOverflow.Should().Equal(
            [
                Describe(CreateAcceptCqe(socketFdOrError: 202, more: false)),
            ]);
        result.MaterializableWithOneMoreEnter.Should().BeEmpty(
            "this bounded no-progress case models a kernel that never turns the hidden backlog into a visible CQE");
        result.ClosedFds.Should().Equal(
            [101],
            because: "only the accepted fd that was actually visible inside a drained snapshot can be reclaimed when hidden overflow progress never occurs");
        AssertStepAccounting(result);
    }

    [Fact]
    public void Drain_WhenOverflowMaterializationEnterFails_ReportsErrno_AndStillDrainsVisibleSnapshot()
    {
        var result = RunScenario(
            new ScriptedOverflowDrainState(
                visibleCapacity: 2,
                initialVisible:
                [
                    CreateEventFdCqe(),
                    CreateAcceptCqe(socketFdOrError: 101, more: false),
                ],
                overflowBacklog:
                [
                    CreateAcceptCqe(socketFdOrError: 202, more: false),
                ],
                requiresInitialEnter: true,
                enterOperations:
                [
                    EnterOperation.Error(IoUringConstants.EEXIST),
                ],
                defaultEnterOperation: EnterOperation.Error(IoUringConstants.EEXIST)));

        result.MaterializationErrno.Should().Be(IoUringConstants.EEXIST,
            "wrong-issuer or other teardown enter failures should surface through the shared drain seam instead of hanging or throwing");
        result.Failure.Should().BeNull();
        result.CaptureCallCount.Should().Be(1);
        result.EnterCallCount.Should().Be(1);
        result.PeekCallCount.Should().Be(2);
        result.AdvanceCallCount.Should().Be(2);
        result.StepCount.Should().Be(7);
        result.Drained.Should().Equal(
            [
                Describe(CreateEventFdCqe()),
                Describe(CreateAcceptCqe(socketFdOrError: 101, more: false)),
            ]);
        result.RemainingVisible.Should().BeEmpty();
        result.RemainingOverflow.Should().Equal(
            [
                Describe(CreateAcceptCqe(socketFdOrError: 202, more: false)),
            ]);
        result.MaterializableWithOneMoreEnter.Should().BeEmpty(
            "this bounded error case models a kernel that keeps rejecting the post-drain materialization enter");
        result.ClosedFds.Should().Equal(
            [101],
            because: "the visible accepted fd is still owned by the listener-stop snapshot even when the overflow enter fails");
        AssertStepAccounting(result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Drain_WhenHiddenOverflowBacklogFitsRoundCap_ReclaimsBacklogLengthsOneThroughCap(int overflowBacklogLength)
    {
        var result = RunScenario(
            new ScriptedOverflowDrainState(
                visibleCapacity: 1,
                initialVisible:
                [
                    CreateEventFdCqe(),
                ],
                overflowBacklog: CreateAcceptOverflowBacklog(overflowBacklogLength, firstSocketFd: 200),
                requiresInitialEnter: false,
                defaultEnterOperation: EnterOperation.MaterializeIfSpace(maxMaterialized: 1)));

        result.MaterializationErrno.Should().Be(0);
        result.Failure.Should().BeNull();
        result.CaptureCallCount.Should().Be(1 + overflowBacklogLength);
        result.EnterCallCount.Should().Be(overflowBacklogLength);
        result.PeekCallCount.Should().Be(1 + overflowBacklogLength);
        result.AdvanceCallCount.Should().Be(1 + overflowBacklogLength);
        result.StepCount.Should().Be(3 + (5 * overflowBacklogLength),
            "the focused backlog witness should consume exactly one bounded overflow round per reclaimed hidden accept when visible CQ capacity is 1");
        result.CaptureCallCount.Should().BeLessThanOrEqualTo(MaxCaptureCalls);
        result.EnterCallCount.Should().BeLessThanOrEqualTo(MaxOverflowEnterCalls);
        result.Drained.Should().Equal(
            new[] { Describe(CreateEventFdCqe()) }
                .Concat(Describe(CreateAcceptOverflowBacklog(overflowBacklogLength, firstSocketFd: 200)))
                .ToArray());
        result.RemainingVisible.Should().BeEmpty();
        result.RemainingOverflow.Should().BeEmpty();
        result.MaterializableWithOneMoreEnter.Should().BeEmpty(
            because: "backlog lengths up to the bounded round cap should be fully reclaimed");
        result.ClosedFds.Should().Equal(
            Enumerable.Range(200, overflowBacklogLength).ToArray(),
            because: "every accepted fd that becomes visible within the bounded overflow rounds should be closed");
        AssertStepAccounting(result);
    }

    [Fact]
    public void Drain_WhenHiddenOverflowBacklogExceedsRoundCap_LeavesDocumentedResidual()
    {
        int roundCap = Ring.TeardownOverflowDrainRoundLimit;
        var result = RunScenario(
            new ScriptedOverflowDrainState(
                visibleCapacity: 1,
                initialVisible:
                [
                    CreateEventFdCqe(),
                ],
                overflowBacklog: CreateAcceptOverflowBacklog(roundCap + 1, firstSocketFd: 300),
                requiresInitialEnter: false,
                defaultEnterOperation: EnterOperation.MaterializeIfSpace(maxMaterialized: 1)));

        result.MaterializationErrno.Should().Be(0);
        result.Failure.Should().BeNull();
        result.CaptureCallCount.Should().Be(1 + roundCap,
            "the bounded GREEN path should stop after the named overflow round cap instead of recursing until the hidden backlog disappears");
        result.EnterCallCount.Should().Be(roundCap);
        result.PeekCallCount.Should().Be(1 + roundCap);
        result.AdvanceCallCount.Should().Be(1 + roundCap);
        result.StepCount.Should().Be(3 + (5 * roundCap));
        result.CaptureCallCount.Should().BeLessThanOrEqualTo(MaxCaptureCalls);
        result.EnterCallCount.Should().BeLessThanOrEqualTo(MaxOverflowEnterCalls);
        result.Drained.Should().Equal(
            new[] { Describe(CreateEventFdCqe()) }
                .Concat(Describe(CreateAcceptOverflowBacklog(roundCap, firstSocketFd: 300)))
                .ToArray());
        result.RemainingVisible.Should().BeEmpty();
        result.RemainingOverflow.Should().Equal(
            [
                Describe(CreateAcceptCqe(socketFdOrError: 300 + roundCap, more: false)),
            ],
            because: "cap exhaustion should leave the documented residual hidden overflow-list CQE behind");
        result.MaterializableWithOneMoreEnter.Should().Equal(
            [
                Describe(CreateAcceptCqe(socketFdOrError: 300 + roundCap, more: false)),
            ],
            because: "the bounded teardown should leave at most the documented one-more-round residual instead of spinning unboundedly");
        result.ClosedFds.Should().Equal(
            Enumerable.Range(300, roundCap).ToArray());
        AssertStepAccounting(result);
    }

    private static OverflowDrainResult RunScenario(ScriptedOverflowDrainState state)
    {
        var completionSource = new ScriptedOverflowCompletionSource(state);
        var closeRecorder = new ScriptedAcceptedSocketCloseRecorder(state);

        ListenerStoppedCompletionDrain.DrainResult drainResult =
            ListenerStoppedCompletionDrain.Drain(ref completionSource, ref closeRecorder);

        return new OverflowDrainResult(
            state.CaptureCallCount,
            state.EnterCallCount,
            state.PeekCallCount,
            state.AdvanceCallCount,
            state.StepCount,
            drainResult.MaterializationErrno,
            drainResult.Failure,
            Describe(state.DrainedCompletions),
            Describe(state.VisibleCompletions),
            Describe(state.OverflowBacklog),
            state.PreviewMaterializableWithOneMoreEnter(),
            state.ClosedFds.OrderBy(static fd => fd).ToArray());
    }

    private static CompletionDescriptor[] Describe(IReadOnlyList<IoUringCqe> completions)
    {
        var descriptors = new CompletionDescriptor[completions.Count];
        for (int i = 0; i < completions.Count; i++)
            descriptors[i] = Describe(completions[i]);
        return descriptors;
    }

    private static IoUringCqe[] CreateAcceptOverflowBacklog(int count, int firstSocketFd)
    {
        var backlog = new IoUringCqe[count];
        for (int i = 0; i < count; i++)
            backlog[i] = CreateAcceptCqe(socketFdOrError: firstSocketFd + i, more: false);

        return backlog;
    }

    private static CompletionDescriptor Describe(in IoUringCqe cqe) =>
        new(cqe.UserData, cqe.Res, cqe.Flags);

    private static IoUringCqe CreateAcceptCqe(int socketFdOrError, bool more) =>
        new()
        {
            UserData = IoUringConstants.ACCEPT_USER_DATA,
            Res = socketFdOrError,
            Flags = more ? IoUringConstants.IORING_CQE_F_MORE : 0u,
        };

    private static IoUringCqe CreateEventFdCqe() =>
        new()
        {
            UserData = IoUringConstants.EVENTFD_USER_DATA,
            Res = sizeof(ulong),
        };

    private readonly record struct CompletionDescriptor(ulong UserData, int Res, uint Flags);

    private readonly record struct OverflowDrainResult(
        int CaptureCallCount,
        int EnterCallCount,
        int PeekCallCount,
        int AdvanceCallCount,
        int StepCount,
        int MaterializationErrno,
        Exception? Failure,
        CompletionDescriptor[] Drained,
        CompletionDescriptor[] RemainingVisible,
        CompletionDescriptor[] RemainingOverflow,
        CompletionDescriptor[] MaterializableWithOneMoreEnter,
        int[] ClosedFds);

    private enum EnterOperationKind
    {
        MaterializeIfSpace,
        NoOp,
        Error,
    }

    private readonly record struct EnterOperation(
        EnterOperationKind Kind,
        int Errno = 0,
        int MaxMaterialized = int.MaxValue)
    {
        internal static EnterOperation MaterializeIfSpace(int maxMaterialized = int.MaxValue) =>
            new(EnterOperationKind.MaterializeIfSpace, MaxMaterialized: maxMaterialized);

        internal static EnterOperation NoOp() => new(EnterOperationKind.NoOp);

        internal static EnterOperation Error(int errno) => new(EnterOperationKind.Error, Errno: errno);
    }

    private sealed class ScriptedOverflowDrainState(
        int visibleCapacity,
        IoUringCqe[] initialVisible,
        IoUringCqe[] overflowBacklog,
        bool requiresInitialEnter,
        EnterOperation[]? enterOperations = null,
        EnterOperation? defaultEnterOperation = null)
    {
        private readonly int _visibleCapacity = visibleCapacity;
        private readonly bool _requiresInitialEnter = requiresInitialEnter;
        private readonly Queue<EnterOperation> _enterOperations = new(enterOperations ?? []);
        private readonly EnterOperation _defaultEnterOperation =
            defaultEnterOperation ?? EnterOperation.MaterializeIfSpace();

        internal List<IoUringCqe> VisibleCompletions { get; } = new(initialVisible);
        internal List<IoUringCqe> OverflowBacklog { get; } = new(overflowBacklog);
        internal List<IoUringCqe> DrainedCompletions { get; } = [];
        internal List<int> ClosedFds { get; } = [];

        internal int CaptureCallCount { get; private set; }
        internal int EnterCallCount { get; private set; }
        internal int PeekCallCount { get; private set; }
        internal int AdvanceCallCount { get; private set; }
        internal int StepCount { get; private set; }

        internal (uint AvailableCompletions, int MaterializationErrno, bool HasNodropOverflowBacklog)
            CaptureAvailableCompletions(bool materializeNodropOverflowBacklog)
        {
            NoteStep();
            CaptureCallCount++;

            int materializationErrno = 0;
            if (_requiresInitialEnter && CaptureCallCount == 1)
                materializationErrno = ExecuteEnter();

            if (materializationErrno == 0 &&
                materializeNodropOverflowBacklog &&
                OverflowBacklog.Count != 0)
            {
                materializationErrno = ExecuteEnter();
            }

            return ((uint)VisibleCompletions.Count, materializationErrno, OverflowBacklog.Count != 0);
        }

        internal bool TryPeekCompletion(out IoUringCqe cqe)
        {
            NoteStep();
            PeekCallCount++;

            if (VisibleCompletions.Count == 0)
            {
                cqe = default;
                return false;
            }

            cqe = VisibleCompletions[0];
            return true;
        }

        internal void AdvanceCompletion()
        {
            NoteStep();
            AdvanceCallCount++;
            DrainedCompletions.Add(VisibleCompletions[0]);
            VisibleCompletions.RemoveAt(0);
        }

        internal void CloseAcceptedSocket(int socketFd)
        {
            NoteStep();
            ClosedFds.Add(socketFd);
        }

        internal CompletionDescriptor[] PreviewMaterializableWithOneMoreEnter()
        {
            EnterOperation operation = _enterOperations.Count != 0
                ? _enterOperations.Peek()
                : _defaultEnterOperation;
            if (operation.Kind != EnterOperationKind.MaterializeIfSpace)
                return [];

            int visibleSpace = _visibleCapacity - VisibleCompletions.Count;
            if (visibleSpace <= 0 || OverflowBacklog.Count == 0)
                return [];

            int materializedCount = Math.Min(
                visibleSpace,
                Math.Min(operation.MaxMaterialized, OverflowBacklog.Count));
            if (materializedCount <= 0)
                return [];

            return Describe(OverflowBacklog.Take(materializedCount).ToArray());
        }

        private int ExecuteEnter()
        {
            NoteStep();
            EnterCallCount++;

            EnterOperation operation = _enterOperations.Count != 0
                ? _enterOperations.Dequeue()
                : _defaultEnterOperation;

            if (operation.Kind == EnterOperationKind.Error)
                return operation.Errno;

            if (operation.Kind == EnterOperationKind.MaterializeIfSpace)
            {
                int visibleSpace = _visibleCapacity - VisibleCompletions.Count;
                int materializedCount = Math.Min(
                    visibleSpace,
                    Math.Min(operation.MaxMaterialized, OverflowBacklog.Count));
                for (int i = 0; i < materializedCount; i++)
                {
                    VisibleCompletions.Add(OverflowBacklog[0]);
                    OverflowBacklog.RemoveAt(0);
                }
            }

            return 0;
        }

        private void NoteStep() => StepCount++;
    }

    private readonly struct ScriptedOverflowCompletionSource(
        ScriptedOverflowDrainState state)
        : ListenerStoppedCompletionDrain.ICompletionSource<ScriptedOverflowCompletionSource>
    {
        private readonly ScriptedOverflowDrainState _state = state;

        public static (uint AvailableCompletions, int MaterializationErrno, bool HasNodropOverflowBacklog)
            CaptureAvailableCompletions(
            ref ScriptedOverflowCompletionSource self,
            bool materializeNodropOverflowBacklog) =>
            self._state.CaptureAvailableCompletions(materializeNodropOverflowBacklog);

        public static bool TryPeekCompletion(
            ref ScriptedOverflowCompletionSource self,
            out IoUringCqe cqe) =>
            self._state.TryPeekCompletion(out cqe);

        public static void AdvanceCompletion(ref ScriptedOverflowCompletionSource self) =>
            self._state.AdvanceCompletion();
    }

    private readonly struct ScriptedAcceptedSocketCloseRecorder(
        ScriptedOverflowDrainState state)
        : ListenerStoppedCompletionDrain.IAcceptedSocketCloseRecorder<ScriptedAcceptedSocketCloseRecorder>
    {
        private readonly ScriptedOverflowDrainState _state = state;

        public static void CloseAcceptedSocket(
            ref ScriptedAcceptedSocketCloseRecorder self,
            int socketFd) =>
            self._state.CloseAcceptedSocket(socketFd);
    }

    private static void AssertStepAccounting(OverflowDrainResult result)
    {
        result.StepCount.Should().Be(
            result.CaptureCallCount +
            result.EnterCallCount +
            result.PeekCallCount +
            result.AdvanceCallCount +
            result.ClosedFds.Length,
            "the scripted step counter should equal the exact bounded capture/enter/peek/advance/close operations the drain performed");
    }
}
