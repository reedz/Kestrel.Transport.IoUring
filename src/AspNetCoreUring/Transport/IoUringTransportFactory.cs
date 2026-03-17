using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AspNetCoreUring.Transport;

public sealed class IoUringTransportFactory : IConnectionListenerFactory
{
    private readonly IoUringTransportOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<IoUringTransportFactory> _logger;

    public IoUringTransportFactory(
        IOptions<IoUringTransportOptions> options,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<IoUringTransportFactory>();
    }

    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        if (!Ring.IsSupported)
        {
            _logger.LogWarning("io_uring is not supported on this system. Please ensure Linux 5.1+ or remove UseIoUring().");
            throw new NotSupportedException("io_uring is not supported on this system.");
        }

        var ring = new Ring((uint)_options.RingSize);
        var logger = _loggerFactory.CreateLogger<IoUringConnectionListener>();
        var listener = new IoUringConnectionListener(endpoint, ring, logger);
        listener.Bind();

        return ValueTask.FromResult<IConnectionListener>(listener);
    }
}
