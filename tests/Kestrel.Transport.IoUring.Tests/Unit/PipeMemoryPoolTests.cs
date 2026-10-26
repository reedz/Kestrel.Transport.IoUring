using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using FluentAssertions;
using Kestrel.Transport.IoUring.Tests.Integration;
using Kestrel.Transport.IoUring.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

/// <summary>
/// Connection pipes take their memory from the listener's pool of recycled pinned 4 KiB blocks (Kestrel's pool on
/// .NET 10, as the socket transport does), not from the pipe default, which kept allocating new arrays for large
/// responses (about 4x the page faults per request of the socket transport on a 118 KB static file).
/// </summary>
[Collection(ProcessWideResourcesCollection.Name)]
public class PipeMemoryPoolTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    [Fact]
    public void Pool_RecyclesBlocks_AndKeepsWorkingAfterDispose()
    {
        var pool = new PinnedBlockMemoryPool();
        pool.MaxBufferSize.Should().Be(4096);

        var first = pool.Rent();
        first.Memory.Length.Should().Be(4096);
        MemoryMarshal.TryGetArray<byte>(first.Memory, out var firstArray).Should().BeTrue();
        first.Dispose();

        var second = pool.Rent(100);
        MemoryMarshal.TryGetArray<byte>(second.Memory, out var secondArray).Should().BeTrue();
        secondArray.Array.Should().BeSameAs(firstArray.Array, "a returned block is handed out again");

        pool.Invoking(p => p.Rent(4097)).Should().Throw<ArgumentOutOfRangeException>();

        pool.Dispose();
        pool.IsDisposed.Should().BeTrue();
        second.Dispose();
        var late = pool.Rent();
        late.Memory.Length.Should().Be(4096, "a writer that outlives its listener still gets memory");
        MemoryMarshal.TryGetArray<byte>(late.Memory, out var lateArray).Should().BeTrue();
        lateArray.Array.Should().NotBeSameAs(secondArray.Array, "a disposed pool keeps nothing");
    }

    [Fact]
    public async Task Connections_UseTheListenersPool_WhichCleanDisposalDisposes()
    {
        if (!Ring.IsSupported) return;

        var pool = new CountingPool();
        var options = TestTransport.Apply(new IoUringTransportOptions
        {
            ThreadCount = 1,
            MemoryPoolFactory = () => pool,
        });
        var listener = new IoUringConnectionListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new Ring((uint)options.EffectiveRingSize),
            options,
            NullLogger<IoUringConnectionListener>.Instance);
        listener.Bind(options.ListenBacklog);
        listener.MemoryPoolForTests.Should().BeSameAs(pool);

        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync((IPEndPoint)listener.EndPoint);
            var connection = await listener.AcceptAsync().AsTask().WaitAsync(Limit);
            connection.Should().NotBeNull();

            await client.GetStream().WriteAsync("ping"u8.ToArray());
            long received = 0;
            while (received < 4)
            {
                var result = await connection!.Transport.Input.ReadAsync().AsTask().WaitAsync(Limit);
                received = result.Buffer.Length;
                connection.Transport.Input.AdvanceTo(result.Buffer.Start, result.Buffer.End);
            }
            int afterReceive = Volatile.Read(ref pool.Rents);
            afterReceive.Should().BeGreaterThan(0, "received bytes are copied into input pipe blocks from the pool");

            await connection!.Transport.Output.WriteAsync("pong"u8.ToArray()).AsTask().WaitAsync(Limit);
            var reply = new byte[4];
            await client.GetStream().ReadExactlyAsync(reply).AsTask().WaitAsync(Limit);
            reply.Should().Equal("pong"u8.ToArray());
            Volatile.Read(ref pool.Rents).Should().BeGreaterThan(afterReceive, "the response was written into a pool block");

            await connection.DisposeAsync().AsTask().WaitAsync(Limit);
        }
        finally
        {
            await listener.DisposeAsync().AsTask().WaitAsync(Limit);
        }

        pool.Disposed.Should().BeTrue();
    }

    [Fact]
    public void Listener_WhosePoolFactoryThrows_LeaksNothing()
    {
        if (!Ring.IsSupported) return;

        var options = TestTransport.Apply(new IoUringTransportOptions
        {
            ThreadCount = 1,
            MemoryPoolFactory = () => throw new InvalidOperationException("pool"),
        });
        int eventFdsBefore = CountEventFds();
        for (int i = 0; i < 3; i++)
        {
            using var ring = new Ring((uint)options.EffectiveRingSize);
            Action create = () => _ = new IoUringConnectionListener(
                new IPEndPoint(IPAddress.Loopback, 0),
                ring,
                options,
                NullLogger<IoUringConnectionListener>.Instance);
            create.Should().Throw<InvalidOperationException>().WithMessage("pool");
        }

        CountEventFds().Should().Be(eventFdsBefore, "a failed constructor must release its eventfd");
    }

    private static int CountEventFds()
    {
        int count = 0;
        foreach (var fd in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
        {
            try
            {
                if (new FileInfo(fd).LinkTarget == "anon_inode:[eventfd]")
                    count++;
            }
            catch (IOException)
            {
            }
        }
        return count;
    }

#if NET10_0_OR_GREATER
    [Fact]
    public async Task KestrelHost_GivesTheTransportKestrelsMemoryPool()
    {
        if (!Ring.IsSupported) return;

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseIoUring(o =>
        {
            o.RequireIoUring = true;
            o.ThreadCount = 1;
            TestTransport.ApplyLane(o);
        });
        await using var app = builder.Build();
        var factory = app.Services.GetServices<IConnectionListenerFactory>().OfType<IoUringTransportFactory>().Single();
        using var kestrelPool = app.Services.GetRequiredService<IMemoryPoolFactory<byte>>().Create();

        var listener = (IoUringConnectionListener)await factory.BindAsync(new IPEndPoint(IPAddress.Loopback, 0));
        try
        {
            listener.MemoryPoolForTests.Should().NotBeOfType<PinnedBlockMemoryPool>();
            listener.MemoryPoolForTests.GetType().Should().Be(kestrelPool.GetType());
        }
        finally
        {
            await listener.DisposeAsync().AsTask().WaitAsync(Limit);
        }
    }
#endif

    private sealed class CountingPool : MemoryPool<byte>
    {
        private readonly PinnedBlockMemoryPool _inner = new();
        public int Rents;
        public bool Disposed;

        public override int MaxBufferSize => _inner.MaxBufferSize;

        public override IMemoryOwner<byte> Rent(int size = -1)
        {
            Interlocked.Increment(ref Rents);
            return _inner.Rent(size);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            _inner.Dispose();
        }
    }
}
