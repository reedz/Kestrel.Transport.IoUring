using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Kestrel.Transport.IoUring.Transport;

/// <summary>
/// Pipe memory pool of 4 KiB blocks on the pinned object heap, recycled through a lock-free queue (the same design as
/// Kestrel's PinnedBlockMemoryPool, which the socket transport uses). The default pipe pool rents from
/// ArrayPool&lt;byte&gt;.Shared, whose per-thread and per-core caches overflow when blocks are rented on ThreadPool
/// threads and returned on the IO thread, so large responses kept allocating new arrays; those arrays also had to be
/// pinned with a GCHandle for every send.
/// </summary>
internal sealed class PinnedBlockMemoryPool : MemoryPool<byte>
{
    internal const int BlockSize = 4096;

    private readonly ConcurrentQueue<Block> _blocks = new();
    private volatile bool _disposed;

    public override int MaxBufferSize => BlockSize;

    internal bool IsDisposed => _disposed;

    public override IMemoryOwner<byte> Rent(int size = -1)
    {
        if (size > BlockSize)
            throw new ArgumentOutOfRangeException(nameof(size), size, $"Blocks are {BlockSize} bytes.");

        // After disposal (the listener is gone) a straggling writer still gets memory; it is just not pooled.
        return _blocks.TryDequeue(out Block? block) ? block : new Block(this);
    }

    private void Return(Block block)
    {
        if (!_disposed)
            _blocks.Enqueue(block);
    }

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        while (_blocks.TryDequeue(out _))
        {
        }
    }

    private sealed class Block : IMemoryOwner<byte>
    {
        private readonly PinnedBlockMemoryPool _pool;

        public Block(PinnedBlockMemoryPool pool)
        {
            _pool = pool;
            Memory = MemoryMarshal.CreateFromPinnedArray(
                GC.AllocateUninitializedArray<byte>(BlockSize, pinned: true), 0, BlockSize);
        }

        public Memory<byte> Memory { get; }

        public void Dispose() => _pool.Return(this);
    }
}
