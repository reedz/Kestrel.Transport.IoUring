namespace Kestrel.Transport.IoUring.Transport;

internal sealed class ReceiveBufferBudget
{
    private readonly long _maxBytes;
    private long _reservedBytes;

    public ReceiveBufferBudget(long maxBytes)
    {
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));

        _maxBytes = maxBytes;
    }

    internal long ReservedBytes => Volatile.Read(ref _reservedBytes);

    public bool TryReserve(int byteCount)
    {
        if (byteCount < 0)
            throw new ArgumentOutOfRangeException(nameof(byteCount));

        while (true)
        {
            long current = Volatile.Read(ref _reservedBytes);
            long next = current + byteCount;
            if (next > _maxBytes)
                return false;

            if (Interlocked.CompareExchange(ref _reservedBytes, next, current) == current)
                return true;
        }
    }

    public void Release(int byteCount)
    {
        if (byteCount < 0)
            throw new ArgumentOutOfRangeException(nameof(byteCount));

        long remaining = Interlocked.Add(ref _reservedBytes, -byteCount);
        if (remaining < 0)
            throw new InvalidOperationException("Receive buffer budget was released more than it was reserved.");
    }
}
