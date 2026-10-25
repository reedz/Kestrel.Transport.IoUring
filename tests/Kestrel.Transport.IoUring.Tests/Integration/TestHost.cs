using System.Net;
using System.Net.Sockets;
using System.Text;
using Kestrel.Transport.IoUring.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Kestrel.Transport.IoUring.Tests.Integration;

/// <summary>
/// Tests that count process-wide resources (file descriptors) cannot share the process with other test
/// classes that open sockets concurrently, so they run in a collection that executes alone.
/// </summary>
[Xunit.CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessWideResourcesCollection
{
    public const string Name = "ProcessWideResources";
}

/// <summary>
/// Shared host/client helpers for the lifecycle and load regressions. Every host refuses a silent
/// socket fallback on io_uring hosts, so a green result means io_uring served it. Running the suite with
/// <c>IOURING_FORCE_FALLBACK=1</c> executes the same scenarios on SocketTransport as a control group.
/// </summary>
internal static class TestHost
{
    public static readonly TimeSpan Short = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Disposes the host without letting a teardown exception hide the test's real failure.
    /// Disposal behaviour itself is covered by dedicated tests.
    /// </summary>
    public sealed class QuietApp(WebApplication app) : IAsyncDisposable
    {
        public IReadOnlyCollection<string> Urls => app.Urls.ToArray();

        public Task StartAsync() => app.StartAsync();

        public Task StopAsync() => app.StopAsync();

        public async ValueTask DisposeAsync()
        {
            try
            {
                await app.DisposeAsync();
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public static QuietApp BuildApp(
        Action<IoUringTransportOptions>? configure,
        Action<WebApplication> map)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseIoUring(options =>
        {
            options.RequireIoUring = Ring.IsSupported;
            configure?.Invoke(options);
        });
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(IPAddress.Loopback, 0);
            o.AddServerHeader = false;
        });
        var app = builder.Build();
        map(app);
        return new QuietApp(app);
    }

    public static IPEndPoint EndpointOf(QuietApp app)
    {
        var uri = new Uri(app.Urls.Single());
        return new IPEndPoint(IPAddress.Parse(uri.Host), uri.Port);
    }

    public static async Task<Socket> ConnectAsync(IPEndPoint endpoint)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };
        await socket.ConnectAsync(endpoint);
        return socket;
    }

    public static async Task<string> ReadToEndAsync(Socket socket, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var sb = new StringBuilder();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            int n = await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token);
            if (n == 0)
                return sb.ToString();
            sb.Append(Encoding.ASCII.GetString(buffer, 0, n));
        }
    }
}
