using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AspNetCoreUring.Transport;

public sealed class IoUringTransportFactory : IConnectionListenerFactory
{
    private readonly IoUringTransportOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<IoUringTransportFactory> _logger;
    // Lazily-created socket fallback — only used when io_uring is unavailable.
    private readonly Lazy<SocketTransportFactory> _socketFallback;

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

    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        if (!Ring.IsSupported)
        {
            _logger.LogWarning(
                "io_uring is not supported on this system (Linux 5.1+ required). " +
                "Falling back to the default socket transport.");
            return _socketFallback.Value.BindAsync(endpoint, cancellationToken);
        }

        var ring = new Ring((uint)_options.RingSize);
        var logger = _loggerFactory.CreateLogger<IoUringConnectionListener>();
        var listener = new IoUringConnectionListener(endpoint, ring, _options, logger);
        listener.Bind(_options.ListenBacklog);

        return ValueTask.FromResult<IConnectionListener>(listener);
    }
}
