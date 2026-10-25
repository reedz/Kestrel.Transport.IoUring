using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Kestrel.Transport.IoUring.Transport;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

/// <summary>
/// A peer that sends a lot and immediately half-closes must have every byte delivered before the input
/// completes, even when the application is slow to read: the multishot receive can post the EOF completion
/// while earlier data is still queued behind pipe backpressure.
/// </summary>
public class RecvEofOrderingTests
{
    // The input pipe pauses at 1 MiB; sizes just above it put the paused tail and the FIN in the same completion
    // batch (the window in which the multishot cancel has not landed yet), larger ones let the cancel win.
    private static readonly int[] Totals =
    [
        1024 * 1024 + 1024,
        1024 * 1024 + 64 * 1024,
        1024 * 1024 + 256 * 1024,
        2 * 1024 * 1024,
        3 * 1024 * 1024,
    ];
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task DataQueuedBehindBackpressure_IsDeliveredBeforeEof()
    {
        if (!Ring.IsSupported) return;

        var listener = ListenerLoopFailureTests.CreateListener();
        try
        {
            // Without the provided-buffer ring the receive is single-shot and this race cannot occur.
            listener.UsesBufferRing.Should().BeTrue("the EOF-overtakes-data race only exists with multishot receive");

            var failures = new List<string>();
            for (int round = 0; round < Totals.Length * 6; round++)
            {
                int total = Totals[round % Totals.Length];
                using var client = new TcpClient();
                await client.ConnectAsync((IPEndPoint)listener.EndPoint);
                var connection = await listener.AcceptAsync();
                connection.Should().NotBeNull();

                var upload = Task.Run(() =>
                {
                    var chunk = new byte[total];
                    client.Client.Send(chunk);
                    client.Client.Shutdown(SocketShutdown.Send);
                });

                // Not reading lets the input pipe pause its writer, so later chunks queue behind it.
                await Task.Delay(400);

                long received = 0;
                using var cts = new CancellationTokenSource(Limit);
                while (true)
                {
                    var result = await connection!.Transport.Input.ReadAsync(cts.Token);
                    received += result.Buffer.Length;
                    connection.Transport.Input.AdvanceTo(result.Buffer.End);
                    if (result.IsCompleted)
                        break;
                }

                await upload;
                if (received != total)
                    failures.Add($"round {round}: received {received} of {total} bytes");

                await connection!.DisposeAsync();
            }

            failures.Should().BeEmpty("EOF must not overtake data that was queued behind backpressure");
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }
}
