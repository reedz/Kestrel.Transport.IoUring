using FluentAssertions;
using Kestrel.Transport.IoUring.Transport;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

/// <summary>
/// Provided-buffer exhaustion terminates a socket's multishot receive. The retry pass must re-arm such
/// sockets fairly and in bounded batches, and must not bypass receive backpressure or a pending cancel.
/// </summary>
public class ListenerRecvRetryBudgetTests
{
    private sealed class Slot
    {
        public bool InFlight;
        public bool CancelPending;
        public bool RearmPending;
        public bool ArmSucceeds = true;
    }

    private struct Adapter(Dictionary<long, Slot> slots, List<long> armed)
        : ListenerRecvRetryState.IRecvRetryPassAdapter<Adapter>
    {
        private readonly Dictionary<long, Slot> _slots = slots;
        private readonly List<long> _armed = armed;

        public static bool TryGetRecvState(
            ref Adapter self,
            long connectionId,
            out ListenerRecvRetryState.RecvRetryConnectionState connectionState)
        {
            if (!self._slots.TryGetValue(connectionId, out Slot? slot))
            {
                connectionState = default;
                return false;
            }

            connectionState = new ListenerRecvRetryState.RecvRetryConnectionState(
                generation: 1,
                recvEpoch: 1,
                isClosing: false,
                hasRecvInFlight: slot.InFlight,
                recvCancelPending: slot.CancelPending,
                usingBufferRing: true,
                recvRearmPending: slot.RearmPending);
            return true;
        }

        public static bool SubmitRecv(
            ref Adapter self,
            long connectionId,
            in ListenerRecvRetryState.RecvRetryConnectionState connectionState) =>
            Arm(ref self, connectionId);

        public static bool SubmitBufferRingRecv(
            ref Adapter self,
            long connectionId,
            in ListenerRecvRetryState.RecvRetryConnectionState connectionState) =>
            Arm(ref self, connectionId);

        public static bool SubmitRecvCancel(
            ref Adapter self,
            long connectionId,
            in ListenerRecvRetryState.RecvRetryConnectionState connectionState,
            in ListenerRecvRetryState.RecvCancelRetryOrigin origin) => false;

        private static bool Arm(ref Adapter self, long connectionId)
        {
            Slot slot = self._slots[connectionId];
            if (!slot.ArmSucceeds)
                return false;

            slot.InFlight = true;
            self._armed.Add(connectionId);
            return true;
        }
    }

    private static (ListenerRecvRetryState State, Adapter Adapter, List<long> Armed, Dictionary<long, Slot> Slots)
        Create(int connections)
    {
        var slots = Enumerable.Range(0, connections).ToDictionary(i => (long)i, _ => new Slot());
        var armed = new List<long>();
        var state = new ListenerRecvRetryState();
        for (int i = 0; i < connections; i++)
            state.AddRecvRetry(i);
        return (state, new Adapter(slots, armed), armed, slots);
    }

    [Fact]
    public void RunRetryPass_WithBudget_ArmsOldestFirstAndLeavesTheRestQueued()
    {
        var (state, adapter, armed, _) = Create(5);

        state.RunRetryPass(ref adapter, maxArms: 2);

        armed.Should().Equal(0L, 1L);
        state.RecvRetryCount.Should().Be(3);
        state.BudgetLimited.Should().BeTrue("retries remain queued, so the loop must not park");

        state.RunRetryPass(ref adapter, maxArms: 2);
        armed.Should().Equal(0L, 1L, 2L, 3L);
        state.BudgetLimited.Should().BeTrue();

        state.RunRetryPass(ref adapter, maxArms: 2);
        armed.Should().Equal(0L, 1L, 2L, 3L, 4L);
        state.RecvRetryCount.Should().Be(0);
        state.BudgetLimited.Should().BeFalse();
    }

    [Fact]
    public void RunRetryPass_SocketThatLosesTheBufferRaceAgain_GoesBehindOlderRetries()
    {
        var (state, adapter, armed, slots) = Create(4);

        state.RunRetryPass(ref adapter, maxArms: 2);
        armed.Should().Equal(0L, 1L);

        // Connection 0 got ENOBUFS again: its multishot ended and it is queued once more.
        slots[0].InFlight = false;
        state.AddRecvRetry(0);

        state.RunRetryPass(ref adapter, maxArms: 2);

        armed.Should().Equal(new[] { 0L, 1L, 2L, 3L },
            "connections 2 and 3 waited longer than the repeat offender and must be served first");
    }

    [Fact]
    public void RunRetryPass_WithoutBudget_ArmsEverything()
    {
        var (state, adapter, armed, _) = Create(4);

        state.RunRetryPass(ref adapter);

        armed.Should().Equal(0L, 1L, 2L, 3L);
        state.BudgetLimited.Should().BeFalse();
    }

    [Fact]
    public void RunRetryPass_DoesNotRearmWhileCancelIsPending()
    {
        var (state, adapter, armed, slots) = Create(1);
        slots[0].CancelPending = true;

        state.RunRetryPass(ref adapter);

        armed.Should().BeEmpty("HandleCancel re-arms after the cancel completes; arming now would leave two receives");
        state.RecvRetryCount.Should().Be(0, "the marker is dropped because another path owns the re-arm");
    }

    [Fact]
    public void RunRetryPass_DoesNotRearmWhileInputFlushIsPending()
    {
        var (state, adapter, armed, slots) = Create(1);
        slots[0].RearmPending = true;

        state.RunRetryPass(ref adapter);

        armed.Should().BeEmpty("re-arming before the application drains its input bypasses receive backpressure");
        state.RecvRetryCount.Should().Be(0);
    }

    [Fact]
    public void RunRetryPass_SqFull_KeepsTheRetryQueuedInPlace()
    {
        var (state, adapter, armed, slots) = Create(3);
        slots[0].ArmSucceeds = false;

        state.RunRetryPass(ref adapter);

        armed.Should().Equal(1L, 2L);
        state.RecvRetryCount.Should().Be(1);
        state.BlockedOnSq.Should().BeTrue();

        slots[0].ArmSucceeds = true;
        state.RunRetryPass(ref adapter);
        armed.Should().Equal(1L, 2L, 0L);
        state.RecvRetryCount.Should().Be(0);
    }

    [Fact]
    public void RunRetryPass_ReleasedConnection_IsNotArmed()
    {
        var (state, adapter, armed, _) = Create(2);

        state.OnConnectionReleased(0);
        state.RunRetryPass(ref adapter);

        armed.Should().Equal(1L);
    }
}
