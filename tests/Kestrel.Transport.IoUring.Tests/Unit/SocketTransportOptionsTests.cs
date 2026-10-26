using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Kestrel.Transport.IoUring.Transport;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

/// <summary>
/// H-08: the <see cref="SocketTransportOptions"/> settings that mean the same thing for this transport (NoDelay,
/// Backlog, MaxReadBufferSize, MaxWriteBufferSize) are honoured; an explicitly set io_uring ListenBacklog wins.
/// Checked against real socket and pipe state, not timing.
/// </summary>
public class SocketTransportOptionsTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    [Fact]
    public void ExplicitListenBacklog_WinsOverSocketBacklog_EvenWhenItEqualsTheDefault()
    {
        var socketOptions = new SocketTransportOptions { Backlog = 1024 };

        new IoUringTransportOptions { ListenBacklog = 512 }.ResolveWith(socketOptions)
            .ListenBacklog.Should().Be(512, "an explicit value is kept even if it equals the default");
        new IoUringTransportOptions().ResolveWith(socketOptions)
            .ListenBacklog.Should().Be(1024, "an unset backlog takes SocketTransportOptions.Backlog");
    }

    [Fact]
    public void Resolve_TakesNoDelayAndBufferLimits_AndWorkersInheritThem()
    {
        var resolved = new IoUringTransportOptions { ThreadCount = 2, MaxConnections = 8 }.ResolveWith(
            new SocketTransportOptions { NoDelay = false, MaxReadBufferSize = 4096, MaxWriteBufferSize = null });

        resolved.NoDelay.Should().BeFalse();
        resolved.MaxReadBufferSize.Should().Be(4096);
        resolved.MaxWriteBufferSize.Should().BeNull();

        var worker = IoUringMultiListener.CreateWorkerOptions(resolved, connectionShare: 4);
        worker.NoDelay.Should().BeFalse();
        worker.MaxReadBufferSize.Should().Be(4096);
        worker.MaxWriteBufferSize.Should().BeNull();
        worker.ListenBacklog.Should().Be(resolved.ListenBacklog);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Defaults_MatchSocketTransport(int threadCount)
    {
        if (!Ring.IsSupported) return;

        var (connection, listener, client) = await AcceptOneAsync(new SocketTransportOptions(), threadCount);
        try
        {
            ServerSideNoDelay(connection).Should().BeTrue();
            connection.InputPauseThreshold.Should().Be(1024 * 1024);
            connection.OutputPauseThreshold.Should().Be(64 * 1024);
        }
        finally
        {
            await CleanupAsync(connection, listener, client);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task NoDelayFalseAndBufferLimits_AreApplied(int threadCount)
    {
        if (!Ring.IsSupported) return;

        var (connection, listener, client) = await AcceptOneAsync(
            new SocketTransportOptions { NoDelay = false, MaxReadBufferSize = 4096, MaxWriteBufferSize = null },
            threadCount);
        try
        {
            ServerSideNoDelay(connection).Should().BeFalse("SocketTransportOptions.NoDelay = false");
            connection.InputPauseThreshold.Should().Be(4096);
            connection.OutputPauseThreshold.Should().Be(0, "a null MaxWriteBufferSize means no limit");
        }
        finally
        {
            await CleanupAsync(connection, listener, client);
        }
    }

    [Theory]
    [InlineData(-4L, null, 512)]
    [InlineData(null, -1L, 512)]
    [InlineData(null, null, 0)]
    public async Task InvalidSocketTransportOptions_AreRejectedAtBind(long? maxRead, long? maxWrite, int backlog)
    {
        if (!Ring.IsSupported) return;

        var factory = CreateFactory(
            new SocketTransportOptions { MaxReadBufferSize = maxRead, MaxWriteBufferSize = maxWrite, Backlog = backlog },
            threadCount: 1);

        Func<Task> bind = async () => await factory.BindAsync(new IPEndPoint(IPAddress.Loopback, 0));
        await bind.Should().ThrowAsync<ArgumentOutOfRangeException>(
            "an invalid limit must fail at startup, not make every accepted connection fail");
    }

    [Fact]
    public async Task SocketBacklog_ReachesListen()
    {
        if (!Ring.IsSupported || !CanRunSs()) return;

        var factory = CreateFactory(new SocketTransportOptions { Backlog = 77 }, threadCount: 1);
        var listener = await factory.BindAsync(new IPEndPoint(IPAddress.Loopback, 0));
        try
        {
            int port = ((IPEndPoint)listener.EndPoint).Port;
            // For a listening TCP socket, ss reports the backlog as Send-Q.
            string line = RunSs($"-Hltn sport = :{port}").Trim();
            line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2].Should().Be("77");
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    private static IoUringTransportFactory CreateFactory(SocketTransportOptions socketOptions, int threadCount) =>
        new(
            Options.Create(TestTransport.Apply(new IoUringTransportOptions
            {
                RequireIoUring = true,
                ThreadCount = threadCount,
                MaxConnections = 16,
            })),
            Options.Create(socketOptions),
            NullLoggerFactory.Instance);

    private static async Task<(IoUringConnection Connection, IConnectionListener Listener, TcpClient Client)>
        AcceptOneAsync(SocketTransportOptions socketOptions, int threadCount)
    {
        var listener = await CreateFactory(socketOptions, threadCount).BindAsync(new IPEndPoint(IPAddress.Loopback, 0));
        var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.EndPoint);
        var connection = await listener.AcceptAsync().AsTask().WaitAsync(Limit);
        return ((IoUringConnection)connection!, listener, client);
    }

    private static bool ServerSideNoDelay(IoUringConnection connection)
    {
        using var handle = new SafeSocketHandle(connection.SocketFd, ownsHandle: false);
        using var socket = new Socket(handle);
        return socket.NoDelay;
    }

    private static async Task CleanupAsync(IoUringConnection connection, IConnectionListener listener, TcpClient client)
    {
        client.Dispose();
        await connection.DisposeAsync().AsTask().WaitAsync(Limit);
        await listener.DisposeAsync().AsTask().WaitAsync(Limit);
    }

    private static bool CanRunSs()
    {
        try
        {
            RunSs("-V");
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string RunSs(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("ss", arguments)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}
