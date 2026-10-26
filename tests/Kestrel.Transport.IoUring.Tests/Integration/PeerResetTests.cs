using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Xunit;
using static Kestrel.Transport.IoUring.Tests.Integration.TestHost;

namespace Kestrel.Transport.IoUring.Tests.Integration;

public class PeerResetTests
{
    [Fact]
    public async Task PeerReset_SignalsRequestAborted()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = BuildApp(null, a =>
            a.MapGet("/hang", async ctx =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    aborted.TrySetResult();
                }
            }));
        await app.StartAsync();

        using Socket socket = await ConnectAsync(EndpointOf(app));
        await socket.SendAsync(
            Encoding.ASCII.GetBytes("GET /hang HTTP/1.1\r\nHost: x\r\n\r\n"), SocketFlags.None);
        await started.Task.WaitAsync(Short);

        // SO_LINGER 0 + close sends RST rather than FIN.
        socket.LingerState = new LingerOption(true, 0);
        socket.Close();

        await aborted.Task.WaitAsync(Short);
    }
}
