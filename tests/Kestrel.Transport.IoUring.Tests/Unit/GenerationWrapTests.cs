using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Kestrel.Transport.IoUring.Transport;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

/// <summary>
/// NEW-10: only the low 16 bits of a slot's generation fit in a completion's user_data, so after 65,536 reuses an
/// occupant's wire generation repeats an earlier one's. A control request still queued by the earlier occupant (an
/// Abort from a lingering reference, a receive resubmit, a send retry) must not reach the new one: the listener's
/// control queues compare the full 64-bit generation.
/// </summary>
public class GenerationWrapTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task AbortFromAnEarlierOccupant_WithTheSameWireGeneration_DoesNotTouchTheNewOne()
    {
        if (!Ring.IsSupported) return;

        var options = new IoUringTransportOptions { ThreadCount = 1, MaxConnections = 1 };
        var listener = new IoUringConnectionListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new Ring((uint)options.EffectiveRingSize),
            options,
            NullLogger<IoUringConnectionListener>.Instance);
        listener.Bind(options.ListenBacklog);
        try
        {
            using var firstClient = new TcpClient();
            await firstClient.ConnectAsync((IPEndPoint)listener.EndPoint);
            var first = (IoUringConnection)(await listener.AcceptAsync())!;
            long firstGeneration = first.Generation;
            await first.DisposeAsync().AsTask().WaitAsync(Limit); // slot 0 is free again

            // The next occupant of slot 0 gets firstGeneration + 65,536: the same 16 bits on the wire.
            listener.SetSlotGenerationForTests(0, firstGeneration + ushort.MaxValue);
            using var secondClient = new TcpClient();
            await secondClient.ConnectAsync((IPEndPoint)listener.EndPoint);
            var second = (IoUringConnection)(await listener.AcceptAsync())!;
            second.NumericConnectionId.Should().Be(first.NumericConnectionId);
            ((ushort)second.Generation).Should().Be((ushort)firstGeneration);
            second.Generation.Should().NotBe(firstGeneration);

            first.Abort(new ConnectionAbortedException("stale abort from the earlier occupant"));
            await Task.Delay(200);

            second.ConnectionClosed.IsCancellationRequested.Should().BeFalse("the stale abort must be ignored");
            await secondClient.GetStream().WriteAsync("ping"u8.ToArray());
            var read = await second.Transport.Input.ReadAsync().AsTask().WaitAsync(Limit);
            read.Buffer.Length.Should().BeGreaterThan(0, "the new occupant still receives");
            second.Transport.Input.AdvanceTo(read.Buffer.End);
            await second.DisposeAsync().AsTask().WaitAsync(Limit);
        }
        finally
        {
            await listener.DisposeAsync().AsTask().WaitAsync(Limit);
        }
    }
}
