using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Kestrel.Transport.IoUring.Native;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kestrel.Transport.IoUring.Transport;

/// <summary>Kestrel connection listener factory backed by io_uring, with automatic socket fallback.</summary>
public sealed class IoUringTransportFactory : IConnectionListenerFactory
{
    private readonly IoUringTransportOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<IoUringTransportFactory> _logger;
    // Lazily-created socket fallback — only used when io_uring is unavailable.
    private readonly Lazy<SocketTransportFactory> _socketFallback;

    /// <summary>Initializes a new instance of the <see cref="IoUringTransportFactory"/> class.</summary>
    public IoUringTransportFactory(
        IOptions<IoUringTransportOptions> options,
        IOptions<SocketTransportOptions> socketOptions,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<IoUringTransportFactory>();
        _socketFallback = new Lazy<SocketTransportFactory>(
            () => new SocketTransportFactory(socketOptions, loggerFactory));
    }

    /// <summary>
    /// <see langword="true"/> when the io_uring transport is active;
    /// <see langword="false"/> when the socket transport fallback is in use.
    /// </summary>
    public static bool IsUsingIoUring => Ring.IsSupported;

    /// <inheritdoc />
    public async ValueTask<IConnectionListener> BindAsync(
        EndPoint endpoint,
        CancellationToken cancellationToken = default)
    {
        if (!Ring.IsSupported)
        {
            _logger.LogWarning(
                "io_uring is not supported on this system (Linux 5.1+ required). " +
                "Falling back to the default socket transport.");
            return await _socketFallback.Value.BindAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }

        if (_options.ThreadCount > 1)
        {
            _logger.LogInformation(
                "Starting io_uring transport with {ThreadCount} rings (SO_REUSEPORT).",
                _options.ThreadCount);
            return new IoUringMultiListener(endpoint, _options, _loggerFactory);
        }

        Ring? ring = null;
        IoUringConnectionListener? listener = null;
        try
        {
            ring = new Ring((uint)_options.EffectiveRingSize, GetSetupFlags());
            var logger = _loggerFactory.CreateLogger<IoUringConnectionListener>();
            listener = new IoUringConnectionListener(endpoint, ring, _options, logger);
            listener.Bind(_options.ListenBacklog);
            return listener;
        }
        catch
        {
            if (listener != null)
                await listener.DisposeAsync().ConfigureAwait(false);
            else
                ring?.Dispose();
            throw;
        }
    }

    internal uint GetSetupFlags()
    {
        uint flags = 0;
        if (_options.EnableSqPoll)
            flags |= IoUringConstants.IORING_SETUP_SQPOLL;
        if (_options.EnableCoopTaskRun)
            flags |= IoUringConstants.IORING_SETUP_COOP_TASKRUN;
        if (_options.EnableSingleIssuer)
            flags |= IoUringConstants.IORING_SETUP_SINGLE_ISSUER;
        // DEFER_TASKRUN requires SINGLE_ISSUER; the kernel will EINVAL otherwise (and Ring will retry).
        if (_options.EnableDeferTaskRun && _options.EnableSingleIssuer)
            flags |= IoUringConstants.IORING_SETUP_DEFER_TASKRUN;
        return flags;
    }
}
