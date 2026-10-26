using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using Kestrel.Transport.IoUring.Native;
using Kestrel.Transport.IoUring.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

/// <summary>
/// One completion pass handles at most a bounded number of completions and then returns to the IO loop, which submits
/// everything staged and runs the retry passes. Completions that keep arriving (other connections' multishot receives)
/// must not hold back a request staged outside the fairness point's conditions, such as a send staged while handling
/// an eventfd completion, a receive re-arm or a cancel: an unbounded pass left them unsubmitted until the queue
/// emptied.
/// </summary>
public class CompletionPassBoundTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    // Decodes to a connection id far beyond any slot, so the listener ignores these completions.
    private const ulong ForeignUserData = 0x0000_7FFF_FF00_0000UL;

    [Fact]
    public async Task ACompletionPass_StopsAtItsLimit_AndTheLoopContinuesWithTheRest()
    {
        if (!Ring.IsSupported) return;

        const int injected = 32;
        const int passLimit = 4;
        var options = TestTransport.Apply(new IoUringTransportOptions { ThreadCount = 1 });
        var ring = new Ring((uint)options.EffectiveRingSize);
        var passes = new ConcurrentQueue<int>();
        int inject = 0;
        var listener = new IoUringConnectionListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            ring,
            options,
            NullLogger<IoUringConnectionListener>.Instance)
        {
            CompletionPassLimitForTests = passLimit,
            CompletionPassEndedForTests = passes.Enqueue,
            IoLoopIterationHook = () =>
            {
                if (Interlocked.Exchange(ref inject, 0) == 1)
                    InjectNops(ring, injected);
            },
        };
        listener.Bind(options.ListenBacklog);
        try
        {
            passes.Clear();
            Volatile.Write(ref inject, 1);
            listener.WakeIoLoopForTests();

            var deadline = DateTime.UtcNow + Limit;
            while (passes.Sum() < injected && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            passes.Sum().Should().BeGreaterThanOrEqualTo(injected, "every injected completion was consumed");
            passes.Should().OnlyContain(count => count <= passLimit, "no pass may exceed its limit");
            passes.Count(count => count == passLimit).Should().BeGreaterThanOrEqualTo(
                injected / passLimit - 1,
                "the queue held more completions than one pass may take, so the loop came back for the rest");
        }
        finally
        {
            await listener.DisposeAsync().AsTask().WaitAsync(Limit);
        }

        listener.Teardown.Should().Be(IoUringConnectionListener.TeardownOutcome.Quiesced);
    }

    // Runs on the IO loop, the only thread that may touch the SQ; the loop submits them right after the hook.
    private static unsafe void InjectNops(Ring ring, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (!ring.TryGetSqe(out IoUringSqe* sqe))
                throw new InvalidOperationException("SQ full");
            sqe->Opcode = IoUringConstants.IORING_OP_NOP;
            sqe->UserData = ForeignUserData;
        }
    }
}
