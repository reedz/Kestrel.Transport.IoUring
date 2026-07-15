using Microsoft.Extensions.Options;

namespace Kestrel.Transport.IoUring.Transport;

internal sealed class IoUringTransportOptionsValidator : IValidateOptions<IoUringTransportOptions>
{
    public ValidateOptionsResult Validate(string? name, IoUringTransportOptions options)
    {
        var failures = new List<string>();

        if (!IoUringTransportOptions.IsPowerOfTwo(options.RingSize) ||
            options.RingSize > IoUringTransportOptions.MaxRingEntries)
        {
            failures.Add(
                $"{nameof(options.RingSize)} must be a power of two between 1 and " +
                $"{IoUringTransportOptions.MaxRingEntries}.");
        }

        if (options.MaxConnections <= 0)
            failures.Add($"{nameof(options.MaxConnections)} must be greater than zero.");

        if (options.ThreadCount <= 0)
            failures.Add($"{nameof(options.ThreadCount)} must be greater than zero.");
        else if (options.MaxConnections > 0 && options.ThreadCount > options.MaxConnections)
            failures.Add($"{nameof(options.ThreadCount)} cannot exceed {nameof(options.MaxConnections)}.");

        if (options.ListenBacklog <= 0)
            failures.Add($"{nameof(options.ListenBacklog)} must be greater than zero.");

        if (options.AcceptQueueCapacity <= 0)
            failures.Add($"{nameof(options.AcceptQueueCapacity)} must be greater than zero.");

        if (options.LogPoolStatsInterval < 0)
            failures.Add($"{nameof(options.LogPoolStatsInterval)} cannot be negative.");

        if (options.ReceiveBufferSize <= 0)
            failures.Add($"{nameof(options.ReceiveBufferSize)} must be greater than zero.");

        if (options.MaxPendingReceiveBytesPerRing <= 0)
        {
            failures.Add(
                $"{nameof(options.MaxPendingReceiveBytesPerRing)} must be greater than zero.");
        }

        if (!IoUringTransportOptions.IsPowerOfTwo(options.BufferRingSize) ||
            options.BufferRingSize > IoUringTransportOptions.MaxRingEntries)
        {
            failures.Add(
                $"{nameof(options.BufferRingSize)} must be a power of two between 1 and " +
                $"{IoUringTransportOptions.MaxRingEntries}.");
        }

        if (options.ReceiveBufferSize > 0 &&
            (long)options.BufferRingSize * options.ReceiveBufferSize > int.MaxValue)
        {
            failures.Add(
                $"{nameof(options.BufferRingSize)} multiplied by {nameof(options.ReceiveBufferSize)} " +
                "must fit in a managed array.");
        }

        if (options.EnableDeferTaskRun && !options.EnableSingleIssuer)
        {
            failures.Add(
                $"{nameof(options.EnableDeferTaskRun)} requires {nameof(options.EnableSingleIssuer)}.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
