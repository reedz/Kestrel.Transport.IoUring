using FluentAssertions;
using FluentAssertions.Execution;
using Kestrel.Transport.IoUring.Native;
using Kestrel.Transport.IoUring.Transport;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

public class ListenerStoppedCompletionDrainTests
{
    [Fact]
    public void Drain_ClosesExactlyTheSnapshotAcceptedSockets_AcrossFocusedScenarios()
    {
        using var _ = new AssertionScope();

        AssertCloseOracleScenario(
            "single visible accept",
            new FakeCompletionPlan(
                initialVisible: [
                    CreateAcceptCqe(socketFdOrError: 101, more: true),
                ]),
            expectedDrainedSnapshot: [
                Describe(CreateAcceptCqe(socketFdOrError: 101, more: true)),
            ],
            expectedRemaining: []);

        AssertCloseOracleScenario(
            "materialized accept snapshot",
            new FakeCompletionPlan(
                initialVisible: [
                    CreateEventFdCqe(),
                ],
                materializationRequired: true,
                materializationOutcome: FakeMaterializationOutcome.Visible,
                materializedVisible: [
                    CreateAcceptCqe(socketFdOrError: 202, more: false),
                ]),
            expectedDrainedSnapshot: [
                Describe(CreateEventFdCqe()),
                Describe(CreateAcceptCqe(socketFdOrError: 202, more: false)),
            ],
            expectedRemaining: []);

        AssertCloseOracleScenario(
            "bounded multishot burst snapshot",
            new FakeCompletionPlan(
                initialVisible: [
                    CreateAcceptCqe(socketFdOrError: 301, more: true),
                    CreateAcceptCqe(socketFdOrError: 302, more: true),
                ],
                revealedOnAdvance: [
                    [CreateAcceptCqe(socketFdOrError: 303, more: true)],
                ]),
            expectedDrainedSnapshot: [
                Describe(CreateAcceptCqe(socketFdOrError: 301, more: true)),
                Describe(CreateAcceptCqe(socketFdOrError: 302, more: true)),
            ],
            expectedRemaining: [
                Describe(CreateAcceptCqe(socketFdOrError: 303, more: true)),
            ]);

        AssertCloseOracleScenario(
            "interleaved accept and nonaccept snapshot",
            new FakeCompletionPlan(
                initialVisible: [
                    CreateEventFdCqe(),
                    CreateAcceptCqe(socketFdOrError: 401, more: true),
                    CreateConnectionCqe(IoUringConnection.OpType.Recv, 501),
                    CreateAcceptCqe(socketFdOrError: 402, more: false),
                ]),
            expectedDrainedSnapshot: [
                Describe(CreateEventFdCqe()),
                Describe(CreateAcceptCqe(socketFdOrError: 401, more: true)),
                Describe(CreateConnectionCqe(IoUringConnection.OpType.Recv, 501)),
                Describe(CreateAcceptCqe(socketFdOrError: 402, more: false)),
            ],
            expectedRemaining: []);

        AssertCloseOracleScenario(
            "nonaccept snapshot",
            new FakeCompletionPlan(
                initialVisible: [
                    CreateEventFdCqe(),
                    CreateConnectionCqe(IoUringConnection.OpType.Send, 601),
                ]),
            expectedDrainedSnapshot: [
                Describe(CreateEventFdCqe()),
                Describe(CreateConnectionCqe(IoUringConnection.OpType.Send, 601)),
            ],
            expectedRemaining: []);
    }

    [Fact]
    public void Drain_RequestsMaterializationBeforePeeking_AndDrainsNewlyVisibleSnapshot()
    {
        var result = RunDrainScenario(
            new FakeCompletionPlan(
                initialVisible: [
                    CreateEventFdCqe(),
                ],
                materializationRequired: true,
                materializationOutcome: FakeMaterializationOutcome.Visible,
                materializedVisible: [
                    CreateAcceptCqe(socketFdOrError: 101, more: true),
                ]));

        result.CaptureCallCount.Should().Be(1,
            "the teardown helper should capture one bounded CQ snapshot before it starts draining");
        result.MaterializeCallCount.Should().Be(1,
            "DEFER_TASKRUN/NODROP-style teardown sources should request a nonblocking materialization step before peeking");
        result.PeekedBeforeCapture.Should().BeFalse(
            "peeking first can miss CQEs that only become visible after the teardown GETEVENTS materialization");
        result.Drained.Should().Equal(
            [
                Describe(CreateEventFdCqe()),
                Describe(CreateAcceptCqe(socketFdOrError: 101, more: true)),
            ]);
        result.Remaining.Should().BeEmpty(
            "the bounded snapshot should be fully consumed when nothing new appears during the drain");
    }

    [Theory]
    [InlineData((int)FakeMaterializationOutcome.NoNewCqe)]
    [InlineData((int)FakeMaterializationOutcome.Eagain)]
    [InlineData((int)FakeMaterializationOutcome.Ebusy)]
    public void Drain_WhenMaterializationDoesNotProduceVisibleCqes_TerminatesWithoutPeeking(int materializationOutcomeValue)
    {
        var result = RunDrainScenario(
            new FakeCompletionPlan(
                initialVisible: [],
                materializationRequired: true,
                materializationOutcome: (FakeMaterializationOutcome)materializationOutcomeValue));

        result.CaptureCallCount.Should().Be(1,
            "the teardown helper should still take its bounded snapshot when no CQE becomes visible");
        result.MaterializeCallCount.Should().Be(1,
            "the teardown helper should request materialization exactly once before deciding the snapshot is empty");
        result.PeekCallCount.Should().Be(0,
            "an empty post-materialization snapshot should terminate the teardown drain without spinning on TryPeekCompletion");
        result.AdvanceCallCount.Should().Be(0,
            "no CQE should be advanced when the post-materialization snapshot is empty");
        result.Remaining.Should().BeEmpty();
    }

    [Fact]
    public void Drain_UsesPostMaterializationSnapshotBound_ForMultishotAcceptBurst()
    {
        var result = RunDrainScenario(
            new FakeCompletionPlan(
                initialVisible: [],
                materializationRequired: true,
                materializationOutcome: FakeMaterializationOutcome.Visible,
                materializedVisible: [
                    CreateAcceptCqe(socketFdOrError: 101, more: true),
                    CreateAcceptCqe(socketFdOrError: 102, more: true),
                ],
                revealedOnAdvance: [
                    [CreateAcceptCqe(socketFdOrError: 103, more: true)],
                ]));

        result.MaterializeCallCount.Should().Be(1);
        result.AdvanceCallCount.Should().Be(2,
            "the teardown drain must stop after the snapshot visible at the materialization boundary even when a live multishot accept keeps producing CQEs");
        result.Drained.Should().Equal(
            [
                Describe(CreateAcceptCqe(socketFdOrError: 101, more: true)),
                Describe(CreateAcceptCqe(socketFdOrError: 102, more: true)),
            ]);
        result.Remaining.Should().Equal(
            [
                Describe(CreateAcceptCqe(socketFdOrError: 103, more: true)),
            ],
            because: "CQEs that arrive after the snapshot boundary should remain outside the bounded final snapshot instead of making final disposal unbounded");
    }

    [Fact]
    public void Drain_UsesPostMaterializationSnapshotBound_ForInterleavedMultishotAndNonAcceptCompletions()
    {
        var result = RunDrainScenario(
            new FakeCompletionPlan(
                initialVisible: [],
                materializationRequired: true,
                materializationOutcome: FakeMaterializationOutcome.Visible,
                materializedVisible: [
                    CreateEventFdCqe(),
                    CreateAcceptCqe(socketFdOrError: 201, more: true),
                    CreateConnectionCqe(IoUringConnection.OpType.Recv, 301),
                    CreateAcceptCqe(socketFdOrError: 202, more: true),
                ],
                revealedOnAdvance: [
                    [CreateAcceptCqe(socketFdOrError: 203, more: true)],
                    [CreateConnectionCqe(IoUringConnection.OpType.Send, 401)],
                ]));

        result.AdvanceCallCount.Should().Be(4,
            "the teardown drain should honor the post-materialization snapshot size even when accepts and nonaccept CQEs are interleaved");
        result.Drained.Should().Equal(
            [
                Describe(CreateEventFdCqe()),
                Describe(CreateAcceptCqe(socketFdOrError: 201, more: true)),
                Describe(CreateConnectionCqe(IoUringConnection.OpType.Recv, 301)),
                Describe(CreateAcceptCqe(socketFdOrError: 202, more: true)),
            ]);
        result.Remaining.Should().Equal(
            [
                Describe(CreateAcceptCqe(socketFdOrError: 203, more: true)),
                Describe(CreateConnectionCqe(IoUringConnection.OpType.Send, 401)),
            ]);
    }

    [Fact]
    public void Drain_IgnoresNonAcceptCqes()
    {
        var result = RunDrainScenario(
            new FakeCompletionPlan(
                initialVisible: [
                    CreateEventFdCqe(),
                    CreateConnectionCqe(IoUringConnection.OpType.Send, 101),
                ]));

        result.Drained.Should().Equal(
            [
                Describe(CreateEventFdCqe()),
                Describe(CreateConnectionCqe(IoUringConnection.OpType.Send, 101)),
            ],
            because: "final disposal should still exhaust nonblocking CQ snapshots even when every CQE belongs to another operation");
        result.ClosedFds.Should().BeEmpty(
            "final disposal must not close arbitrary fds from non-accept completions");
    }

    [Fact]
    public void Drain_IgnoresNegativeAcceptResult()
    {
        var result = RunDrainScenario(
            new FakeCompletionPlan(
                initialVisible: [
                    CreateAcceptCqe(socketFdOrError: -IoUringConstants.EAGAIN, more: false),
                ]));

        result.Drained.Should().Equal(
            [
                Describe(CreateAcceptCqe(socketFdOrError: -IoUringConstants.EAGAIN, more: false)),
            ],
            because: "the final disposal seam should still consume failed accept CQEs when the loop has already stopped");
        result.ClosedFds.Should().BeEmpty(
            "failed accept CQEs do not transfer accepted-socket ownership");
    }

    [Fact]
    public void Drain_DoesNotTreatLiveConnectionCompletionAsAcceptedSocketOwnership()
    {
        const int liveConnectionFd = 101;
        var result = RunDrainScenario(
            new FakeCompletionPlan(
                initialVisible: [
                    CreateConnectionCqe(IoUringConnection.OpType.Recv, liveConnectionFd),
                ]));

        result.Drained.Should().Equal(
            [
                Describe(CreateConnectionCqe(IoUringConnection.OpType.Recv, liveConnectionFd)),
            ],
            because: "the deterministic teardown seam should consume live-connection CQEs that were left pending when the ring stops");
        result.ClosedFds.Should().BeEmpty(
            "connection CQEs represent already-dispatched live ownership and must not be treated as accepted-fd cleanup");
    }

    [Fact]
    public void Drain_ReportsMaterializationErrno_AndStillConsumesVisibleSnapshot()
    {
        var result = RunDrainScenario(
            new FakeCompletionPlan(
                initialVisible: [
                    CreateEventFdCqe(),
                ],
                materializationRequired: true,
                materializationErrno: IoUringConstants.EEXIST));

        result.MaterializationErrno.Should().Be(IoUringConstants.EEXIST,
            "bounded teardown capture should report wrong-issuer or other materialization errno values instead of throwing");
        result.Failure.Should().BeNull();
        result.Drained.Should().Equal(
            [
                Describe(CreateEventFdCqe()),
            ],
            because: "already visible CQEs should still be drained even when the final nonblocking GETEVENTS step reports an errno");
    }

    [Fact]
    public void Drain_ReturnsFailureInsteadOfThrowing_WhenCompletionSourceThrows()
    {
        var completionSource = new ThrowingCompletionSource();
        var closeRecorder = new FakeAcceptedSocketCloseRecorder([]);

        ListenerStoppedCompletionDrain.DrainResult result =
            ListenerStoppedCompletionDrain.Drain(ref completionSource, ref closeRecorder);

        result.MaterializationErrno.Should().Be(0);
        result.Failure.Should().BeOfType<InvalidOperationException>();
        result.Failure!.Message.Should().Contain("simulated");
    }

    [Fact]
    public void Drain_WhenAcceptedSocketCloseRecorderThrows_StillAdvancesAndClosesLaterAcceptedSocket()
    {
        var completionSource = new FakeCompletionSource(
            new FakeCompletionPlan(
                initialVisible: [
                    CreateAcceptCqe(socketFdOrError: 101, more: true),
                    CreateAcceptCqe(socketFdOrError: 102, more: false),
                ]));
        var closeRecorder = new ThrowOnFirstAcceptedSocketCloseRecorder(
            new ThrowOnFirstAcceptedSocketCloseRecorderState());

        ListenerStoppedCompletionDrain.DrainResult result =
            ListenerStoppedCompletionDrain.Drain(ref completionSource, ref closeRecorder);

        result.MaterializationErrno.Should().Be(0);
        result.Failure.Should().BeOfType<InvalidOperationException>();
        result.Failure!.Message.Should().Contain("simulated close failure for fd 101");
        completionSource.AdvanceCallCount.Should().Be(2,
            "advance must stay in finally so one failing close attempt cannot strand later accepted sockets inside the captured snapshot");
        Describe(completionSource.DrainedCompletions).Should().Equal(
            [
                Describe(CreateAcceptCqe(socketFdOrError: 101, more: true)),
                Describe(CreateAcceptCqe(socketFdOrError: 102, more: false)),
            ]);
        closeRecorder.ClosedFds.Should().Equal(
            [102],
            because: "later accepted sockets in the captured snapshot should still be reclaimed even when an earlier close attempt throws");
    }

    [Fact]
    public void Drain_WhenMultipleCqeAndMaterializationFailuresOccur_ReturnsFlatAggregate_AndStillDrainsSnapshot()
    {
        var completionSource = new AggregateMaterializationFailureCompletionSource(
            new AggregateMaterializationFailureCompletionSourceState(
                initialVisible: [
                    CreateAcceptCqe(socketFdOrError: 101, more: true),
                    CreateAcceptCqe(socketFdOrError: 102, more: true),
                    CreateAcceptCqe(socketFdOrError: 103, more: true),
                    CreateAcceptCqe(socketFdOrError: 104, more: true),
                    CreateAcceptCqe(socketFdOrError: 105, more: false),
                ]));
        var closeRecorder = new ThrowOnSelectedAcceptedSocketCloseRecorder(
            new ThrowOnSelectedAcceptedSocketCloseRecorderState(
                throwingSocketFds: [101, 102, 103, 104]));

        ListenerStoppedCompletionDrain.DrainResult result =
            ListenerStoppedCompletionDrain.Drain(ref completionSource, ref closeRecorder);

        result.MaterializationErrno.Should().Be(0);

        AggregateException failure = result.Failure.Should().BeOfType<AggregateException>().Which;
        string[] expectedMessages =
        [
            "simulated close failure for fd 101",
            "simulated close failure for fd 102",
            "simulated close failure for fd 103",
            "simulated close failure for fd 104",
            "simulated overflow materialization failure A",
            "simulated overflow materialization failure B",
        ];
        string[] actualMessages = failure.InnerExceptions.Select(static ex => ex.Message).ToArray();

        actualMessages.Should().OnlyHaveUniqueItems();
        actualMessages.Should().BeEquivalentTo(expectedMessages,
            because: "every leaf CQE/materialization failure should appear exactly once in the final aggregate");
        failure.Flatten().InnerExceptions.Should().HaveCount(failure.InnerExceptions.Count,
            because: "the final teardown failure should already be flat instead of an O(N) nested pairwise AggregateException chain");
        completionSource.CaptureCallCount.Should().Be(2,
            "the bounded drain should take its initial snapshot and then surface the follow-up overflow materialization failure");
        completionSource.MaterializeCallCount.Should().Be(1);
        completionSource.AdvanceCallCount.Should().Be(5,
            "advance must stay in finally so every CQE in the captured snapshot drains even when earlier close attempts throw");
        Describe(completionSource.DrainedCompletions).Should().Equal(
            [
                Describe(CreateAcceptCqe(socketFdOrError: 101, more: true)),
                Describe(CreateAcceptCqe(socketFdOrError: 102, more: true)),
                Describe(CreateAcceptCqe(socketFdOrError: 103, more: true)),
                Describe(CreateAcceptCqe(socketFdOrError: 104, more: true)),
                Describe(CreateAcceptCqe(socketFdOrError: 105, more: false)),
            ]);
        Describe(completionSource.RemainingCompletions).Should().BeEmpty();
        closeRecorder.ClosedFds.Should().Equal(
            [105],
            because: "later accepted sockets in the same drained snapshot should still be reclaimed after earlier per-CQE failures");
    }

    private static void AssertCloseOracleScenario(
        string scenarioName,
        FakeCompletionPlan plan,
        CompletionDescriptor[] expectedDrainedSnapshot,
        CompletionDescriptor[] expectedRemaining)
    {
        var result = RunDrainScenario(plan);
        using var _ = new AssertionScope(scenarioName);

        result.MaterializationErrno.Should().Be(0,
            because: "these focused drain scenarios model successful nonblocking capture rather than teardown enter failure reporting");
        result.Failure.Should().BeNull(
            because: "these focused drain scenarios exercise bounded draining, not unexpected completion-source exceptions");
        result.Drained.Should().Equal(expectedDrainedSnapshot,
            because: "the teardown drain should consume exactly the snapshot visible at the final materialization boundary");
        result.Remaining.Should().Equal(expectedRemaining,
            because: "the teardown drain must leave post-snapshot CQEs outside the bounded final snapshot");
        result.ClosedFds.Should().Equal(
            ExpectedClosedSocketFds(expectedDrainedSnapshot),
            because: "successful accepted sockets in the drained snapshot are the only fds the bounded GREEN close path should reclaim");
    }

    private static DrainScenarioResult RunDrainScenario(FakeCompletionPlan plan)
    {
        var completionSource = new FakeCompletionSource(plan);
        var closeRecorder = new FakeAcceptedSocketCloseRecorder([]);

        ListenerStoppedCompletionDrain.DrainResult drainResult =
            ListenerStoppedCompletionDrain.Drain(ref completionSource, ref closeRecorder);

        return new DrainScenarioResult(
            completionSource.CaptureCallCount,
            completionSource.MaterializeCallCount,
            completionSource.PeekCallCount,
            completionSource.AdvanceCallCount,
            completionSource.PeekedBeforeCapture,
            drainResult.MaterializationErrno,
            drainResult.Failure,
            Describe(completionSource.DrainedCompletions),
            Describe(completionSource.RemainingCompletions),
            closeRecorder.ClosedFds.OrderBy(static fd => fd).ToArray());
    }

    private static int[] ExpectedClosedSocketFds(IEnumerable<CompletionDescriptor> drainedSnapshot) =>
        drainedSnapshot
            .Where(static descriptor =>
                descriptor.UserData == IoUringConstants.ACCEPT_USER_DATA &&
                descriptor.Res >= 0)
            .Select(static descriptor => descriptor.Res)
            .OrderBy(static fd => fd)
            .ToArray();

    private static CompletionDescriptor[] Describe(IReadOnlyList<IoUringCqe> completions)
    {
        var descriptors = new CompletionDescriptor[completions.Count];
        for (int i = 0; i < completions.Count; i++)
            descriptors[i] = Describe(completions[i]);
        return descriptors;
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

    private static IoUringCqe CreateConnectionCqe(IoUringConnection.OpType opType, int result) =>
        new()
        {
            UserData = IoUringConnection.EncodeUserData(0, 1, opType),
            Res = result,
        };

    private readonly record struct CompletionDescriptor(ulong UserData, int Res, uint Flags);

    private readonly record struct DrainScenarioResult(
        int CaptureCallCount,
        int MaterializeCallCount,
        int PeekCallCount,
        int AdvanceCallCount,
        bool PeekedBeforeCapture,
        int MaterializationErrno,
        Exception? Failure,
        CompletionDescriptor[] Drained,
        CompletionDescriptor[] Remaining,
        int[] ClosedFds);

    private enum FakeMaterializationOutcome
    {
        NoNewCqe,
        Visible,
        Eagain,
        Ebusy,
    }

    private sealed class FakeCompletionPlan(
        IoUringCqe[] initialVisible,
        bool materializationRequired = false,
        FakeMaterializationOutcome materializationOutcome = FakeMaterializationOutcome.NoNewCqe,
        int materializationErrno = 0,
        IoUringCqe[]? materializedVisible = null,
        IoUringCqe[][]? revealedOnAdvance = null)
    {
        internal List<IoUringCqe> VisibleCompletions { get; } = new(initialVisible);
        internal bool MaterializationRequired { get; } = materializationRequired;
        internal FakeMaterializationOutcome MaterializationOutcome { get; } = materializationOutcome;
        internal int MaterializationErrno { get; } = materializationErrno;
        internal IoUringCqe[] MaterializedVisible { get; } = materializedVisible ?? [];
        internal Queue<IoUringCqe[]> RevealedOnAdvance { get; } = new(revealedOnAdvance ?? []);
        internal List<IoUringCqe> DrainedCompletions { get; } = [];
        internal int CaptureCallCount { get; set; }
        internal int MaterializeCallCount { get; set; }
        internal int PeekCallCount { get; set; }
        internal int AdvanceCallCount { get; set; }
        internal bool PeekedBeforeCapture { get; set; }
        internal bool MaterializationApplied { get; set; }
    }

    private struct FakeCompletionSource(FakeCompletionPlan plan)
        : ListenerStoppedCompletionDrain.ICompletionSource<FakeCompletionSource>
    {
        private readonly FakeCompletionPlan _plan = plan;

        internal int CaptureCallCount => _plan.CaptureCallCount;
        internal int MaterializeCallCount => _plan.MaterializeCallCount;
        internal int PeekCallCount => _plan.PeekCallCount;
        internal int AdvanceCallCount => _plan.AdvanceCallCount;
        internal bool PeekedBeforeCapture => _plan.PeekedBeforeCapture;
        internal IReadOnlyList<IoUringCqe> DrainedCompletions => _plan.DrainedCompletions;
        internal IReadOnlyList<IoUringCqe> RemainingCompletions => _plan.VisibleCompletions;

        public static (uint AvailableCompletions, int MaterializationErrno, bool HasNodropOverflowBacklog)
            CaptureAvailableCompletions(
                ref FakeCompletionSource self,
                bool materializeNodropOverflowBacklog)
        {
            self._plan.CaptureCallCount++;

            bool shouldMaterialize =
                self._plan.MaterializationRequired ||
                materializeNodropOverflowBacklog;

            if (!shouldMaterialize)
                return ((uint)self._plan.VisibleCompletions.Count, self._plan.MaterializationErrno, false);

            self._plan.MaterializeCallCount++;
            if (!self._plan.MaterializationApplied)
            {
                if (self._plan.MaterializationOutcome == FakeMaterializationOutcome.Visible)
                    self._plan.VisibleCompletions.AddRange(self._plan.MaterializedVisible);

                self._plan.MaterializationApplied = true;
            }

            return ((uint)self._plan.VisibleCompletions.Count, self._plan.MaterializationErrno, false);
        }

        public static bool TryPeekCompletion(ref FakeCompletionSource self, out IoUringCqe cqe)
        {
            self._plan.PeekCallCount++;
            if (self._plan.CaptureCallCount == 0)
                self._plan.PeekedBeforeCapture = true;

            if (self._plan.VisibleCompletions.Count != 0)
            {
                cqe = self._plan.VisibleCompletions[0];
                return true;
            }

            cqe = default;
            return false;
        }

        public static void AdvanceCompletion(ref FakeCompletionSource self)
        {
            self._plan.AdvanceCallCount++;
            self._plan.DrainedCompletions.Add(self._plan.VisibleCompletions[0]);
            self._plan.VisibleCompletions.RemoveAt(0);

            if (self._plan.RevealedOnAdvance.Count != 0)
                self._plan.VisibleCompletions.AddRange(self._plan.RevealedOnAdvance.Dequeue());
        }
    }

    private struct FakeAcceptedSocketCloseRecorder(List<int> closedFds)
        : ListenerStoppedCompletionDrain.IAcceptedSocketCloseRecorder<FakeAcceptedSocketCloseRecorder>
    {
        private readonly List<int> _closedFds = closedFds;

        internal IReadOnlyList<int> ClosedFds => _closedFds;

        public static void CloseAcceptedSocket(
            ref FakeAcceptedSocketCloseRecorder self,
            int socketFd)
        {
            self._closedFds.Add(socketFd);
        }
    }

    private sealed class ThrowOnFirstAcceptedSocketCloseRecorderState
    {
        internal List<int> ClosedFds { get; } = [];

        internal bool ThrowOnNextClose { get; set; } = true;
    }

    private struct ThrowOnFirstAcceptedSocketCloseRecorder(
        ThrowOnFirstAcceptedSocketCloseRecorderState state)
        : ListenerStoppedCompletionDrain.IAcceptedSocketCloseRecorder<ThrowOnFirstAcceptedSocketCloseRecorder>
    {
        private readonly ThrowOnFirstAcceptedSocketCloseRecorderState _state = state;

        internal IReadOnlyList<int> ClosedFds => _state.ClosedFds;

        public static void CloseAcceptedSocket(
            ref ThrowOnFirstAcceptedSocketCloseRecorder self,
            int socketFd)
        {
            if (self._state.ThrowOnNextClose)
            {
                self._state.ThrowOnNextClose = false;
                throw new InvalidOperationException($"simulated close failure for fd {socketFd}");
            }

            self._state.ClosedFds.Add(socketFd);
        }
    }

    private sealed class ThrowOnSelectedAcceptedSocketCloseRecorderState(int[] throwingSocketFds)
    {
        internal List<int> ClosedFds { get; } = [];
        internal HashSet<int> ThrowingSocketFds { get; } = new(throwingSocketFds);
    }

    private struct ThrowOnSelectedAcceptedSocketCloseRecorder(
        ThrowOnSelectedAcceptedSocketCloseRecorderState state)
        : ListenerStoppedCompletionDrain.IAcceptedSocketCloseRecorder<ThrowOnSelectedAcceptedSocketCloseRecorder>
    {
        private readonly ThrowOnSelectedAcceptedSocketCloseRecorderState _state = state;

        internal IReadOnlyList<int> ClosedFds => _state.ClosedFds;

        public static void CloseAcceptedSocket(
            ref ThrowOnSelectedAcceptedSocketCloseRecorder self,
            int socketFd)
        {
            if (self._state.ThrowingSocketFds.Contains(socketFd))
                throw new InvalidOperationException($"simulated close failure for fd {socketFd}");

            self._state.ClosedFds.Add(socketFd);
        }
    }

    private sealed class AggregateMaterializationFailureCompletionSourceState(IoUringCqe[] initialVisible)
    {
        internal List<IoUringCqe> VisibleCompletions { get; } = new(initialVisible);
        internal List<IoUringCqe> DrainedCompletions { get; } = [];
        internal int CaptureCallCount { get; set; }
        internal int MaterializeCallCount { get; set; }
        internal int PeekCallCount { get; set; }
        internal int AdvanceCallCount { get; set; }
    }

    private struct AggregateMaterializationFailureCompletionSource(
        AggregateMaterializationFailureCompletionSourceState state)
        : ListenerStoppedCompletionDrain.ICompletionSource<AggregateMaterializationFailureCompletionSource>
    {
        private readonly AggregateMaterializationFailureCompletionSourceState _state = state;

        internal int CaptureCallCount => _state.CaptureCallCount;
        internal int MaterializeCallCount => _state.MaterializeCallCount;
        internal int PeekCallCount => _state.PeekCallCount;
        internal int AdvanceCallCount => _state.AdvanceCallCount;
        internal IReadOnlyList<IoUringCqe> DrainedCompletions => _state.DrainedCompletions;
        internal IReadOnlyList<IoUringCqe> RemainingCompletions => _state.VisibleCompletions;

        public static (uint AvailableCompletions, int MaterializationErrno, bool HasNodropOverflowBacklog)
            CaptureAvailableCompletions(
                ref AggregateMaterializationFailureCompletionSource self,
                bool materializeNodropOverflowBacklog)
        {
            self._state.CaptureCallCount++;

            if (materializeNodropOverflowBacklog)
            {
                self._state.MaterializeCallCount++;
                throw new AggregateException(
                    new InvalidOperationException("simulated overflow materialization failure A"),
                    new InvalidOperationException("simulated overflow materialization failure B"));
            }

            return ((uint)self._state.VisibleCompletions.Count, 0, true);
        }

        public static bool TryPeekCompletion(
            ref AggregateMaterializationFailureCompletionSource self,
            out IoUringCqe cqe)
        {
            self._state.PeekCallCount++;

            if (self._state.VisibleCompletions.Count != 0)
            {
                cqe = self._state.VisibleCompletions[0];
                return true;
            }

            cqe = default;
            return false;
        }

        public static void AdvanceCompletion(ref AggregateMaterializationFailureCompletionSource self)
        {
            self._state.AdvanceCallCount++;
            self._state.DrainedCompletions.Add(self._state.VisibleCompletions[0]);
            self._state.VisibleCompletions.RemoveAt(0);
        }
    }

    private struct ThrowingCompletionSource
        : ListenerStoppedCompletionDrain.ICompletionSource<ThrowingCompletionSource>
    {
        public static (uint AvailableCompletions, int MaterializationErrno, bool HasNodropOverflowBacklog)
            CaptureAvailableCompletions(
                ref ThrowingCompletionSource self,
                bool materializeNodropOverflowBacklog) =>
            (1u, 0, false);

        public static bool TryPeekCompletion(ref ThrowingCompletionSource self, out IoUringCqe cqe)
        {
            cqe = default;
            throw new InvalidOperationException("simulated completion-source failure");
        }

        public static void AdvanceCompletion(ref ThrowingCompletionSource self)
        {
        }
    }
}
