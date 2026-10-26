using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Xunit;
using Xunit.Abstractions;
using static Kestrel.Transport.IoUring.Tests.Integration.TestHost;

namespace Kestrel.Transport.IoUring.Tests.Integration;

[Collection(ProcessWideResourcesCollection.Name)]
public class StopUnderLoadTests(ITestOutputHelper output)
{
    [Fact]
    public async Task StopDuringConnectionChurn_DoesNotLeakFileDescriptors()
    {
        if (!Directory.Exists("/proc/self/fd")) return;

        // Count only what a leak would add: sockets plus io_uring/eventfd handles. Other runtime-internal
        // fds (threads, epoll, pipes) drift between cycles and are not what this test is about.
        static int OpenTransportFds() => Directory.GetFileSystemEntries("/proc/self/fd")
            .Count(path =>
            {
                string? target = new FileInfo(path).LinkTarget;
                return target is not null &&
                    (target.StartsWith("socket:", StringComparison.Ordinal) ||
                     target.Contains("io_uring", StringComparison.Ordinal) ||
                     target.Contains("eventfd", StringComparison.Ordinal));
            });

        // Warm up so one-time runtime fds don't count as leaks.
        await RunCycleAsync();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        int before = OpenTransportFds();

        for (int i = 0; i < 15; i++)
            await RunCycleAsync();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        await Task.Delay(200);
        int after = OpenTransportFds();
        output.WriteLine($"open socket/io_uring/eventfd fds before={before} after={after}");

        // The leak this guards against added ~1-2 sockets per cycle (24 over 15 cycles).
        after.Should().BeLessThanOrEqualTo(before + 3, "accepted sockets must be reclaimed when the listener stops");

        async Task RunCycleAsync()
        {
            await using var app = BuildApp(o => o.RingSize = 64, a => a.MapGet("/", () => "ok"));
            await app.StartAsync();
            IPEndPoint endpoint = EndpointOf(app);
            using var stopClients = new CancellationTokenSource();
            Task[] clients = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
            {
                while (!stopClients.IsCancellationRequested)
                {
                    try
                    {
                        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                        await socket.ConnectAsync(endpoint, stopClients.Token);
                    }
                    catch
                    {
                        // Refused/reset while the listener is stopping is expected.
                    }
                }
            })).ToArray();

            await Task.Delay(40);
            await app.StopAsync();
            stopClients.Cancel();
            await Task.WhenAll(clients);
        }
    }
}
