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
public sealed class IoUringTransportFactory :
    IConnectionListenerFactory,
    IConnectionListenerFactorySelector
{
    private readonly IoUringTransportOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<IoUringTransportFactory> _logger;
    private readonly Func<bool> _isSupported;
    private readonly Func<uint, uint, Ring> _createRing;
    // Lazily-created socket fallback — only used when io_uring is unavailable.
    private readonly Lazy<SocketTransportFactory> _socketFallback;
    private readonly object _bindingsLock = new();
    private IoUringBindingInfo[] _bindings = [];

    /// <summary>Initializes a new instance of the <see cref="IoUringTransportFactory"/> class.</summary>
    public IoUringTransportFactory(
        IOptions<IoUringTransportOptions> options,
        IOptions<SocketTransportOptions> socketOptions,
        ILoggerFactory loggerFactory)
        : this(options, socketOptions, loggerFactory, isSupported: null, createRing: null)
    {
    }

    // Test seam: lets unit tests simulate an unsupported host or a failing ring setup.
    internal IoUringTransportFactory(
        IOptions<IoUringTransportOptions> options,
        IOptions<SocketTransportOptions> socketOptions,
        ILoggerFactory loggerFactory,
        Func<bool>? isSupported,
        Func<uint, uint, Ring>? createRing)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<IoUringTransportFactory>();
        _isSupported = isSupported ?? (() => Ring.IsSupported);
        _createRing = createRing ?? ((entries, flags) => new Ring(entries, flags));
        _socketFallback = new Lazy<SocketTransportFactory>(
            () => new SocketTransportFactory(socketOptions, loggerFactory));
    }

    /// <summary>
    /// <see langword="true"/> when this host supports io_uring (a kernel capability probe).
    /// This does <b>not</b> mean any listener is using io_uring: an endpoint type io_uring does not serve,
    /// a failed ring setup, or an explicit socket-transport configuration still uses SocketTransport.
    /// Use <see cref="Bindings"/> to see what actually served each bind.
    /// </summary>
    public static bool IsUsingIoUring => Ring.IsSupported;

    /// <summary>
    /// What actually served each successful <see cref="BindAsync"/> call on this factory, in bind order.
    /// A refused bind (see <see cref="IoUringTransportOptions.RequireIoUring"/>) is not listed.
    /// </summary>
    public IReadOnlyList<IoUringBindingInfo> Bindings => Volatile.Read(ref _bindings);

    /// <inheritdoc />
    public async ValueTask<IConnectionListener> BindAsync(
        EndPoint endpoint,
        CancellationToken cancellationToken = default)
    {
        _options.Validate();

        if (!CanBind(endpoint))
        {
            return await BindSocketTransportAsync(
                endpoint,
                $"endpoint type {endpoint.GetType().Name} is not served by io_uring",
                cancellationToken).ConfigureAwait(false);
        }

        if (!_isSupported())
        {
            return await FallbackOrThrowAsync(
                endpoint,
                "io_uring is not supported on this host (kernel too old, disabled via " +
                "kernel.io_uring_disabled/seccomp, or IOURING_FORCE_FALLBACK=1)",
                inner: null,
                cancellationToken).ConfigureAwait(false);
        }

        if (_options.ThreadCount > 1)
        {
            _logger.LogInformation(
                "Starting io_uring transport with {ThreadCount} rings (SO_REUSEPORT).",
                _options.ThreadCount);
            try
            {
                var multiListener = new IoUringMultiListener(endpoint, _options, _loggerFactory);
                RecordIoUringBinding(multiListener);
                return multiListener;
            }
            // AddressInUseException derives from InvalidOperationException but is not a ring setup problem:
            // falling back to SocketTransport would only fail again with a misleading reason.
            catch (InvalidOperationException ex) when (ex is not AddressInUseException)
            {
                return await FallbackOrThrowAsync(
                    endpoint,
                    $"io_uring ring setup failed: {ex.Message}",
                    ex,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        Ring ring;
        try
        {
            ring = _createRing((uint)_options.EffectiveRingSize, GetSetupFlags());
        }
        catch (InvalidOperationException ex)
        {
            return await FallbackOrThrowAsync(
                endpoint,
                $"io_uring ring setup failed: {ex.Message}",
                ex,
                cancellationToken).ConfigureAwait(false);
        }
        var logger = _loggerFactory.CreateLogger<IoUringConnectionListener>();
        IoUringConnectionListener? listener = null;
        try
        {
            listener = new IoUringConnectionListener(endpoint, ring, _options, logger);
            listener.Bind(_options.ListenBacklog);
        }
        catch
        {
            if (listener == null)
                ring.Dispose();
            else
                await listener.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        RecordIoUringBinding(listener);
        return listener;
    }

    private bool IsIoUringRequired =>
        _options.RequireIoUring ||
        Environment.GetEnvironmentVariable("IOURING_REQUIRE") == "1";

    private async ValueTask<IConnectionListener> FallbackOrThrowAsync(
        EndPoint endpoint,
        string reason,
        Exception? inner,
        CancellationToken cancellationToken)
    {
        if (IsIoUringRequired)
        {
            throw new NotSupportedException(
                $"The io_uring transport is required (IoUringTransportOptions.RequireIoUring or IOURING_REQUIRE=1) " +
                $"but cannot serve {endpoint}: {reason}.",
                inner);
        }

        if (inner is null)
        {
            _logger.LogWarning(
                "{Reason}. Falling back to the default socket transport.",
                reason);
        }
        else
        {
            _logger.LogWarning(
                inner,
                "Failed to initialize io_uring; falling back to the default socket transport.");
        }

        return await BindSocketTransportAsync(endpoint, reason, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IConnectionListener> BindSocketTransportAsync(
        EndPoint endpoint,
        string reason,
        CancellationToken cancellationToken)
    {
        IConnectionListener listener =
            await _socketFallback.Value.BindAsync(endpoint, cancellationToken).ConfigureAwait(false);
        AddBinding(new IoUringBindingInfo(
            listener.EndPoint,
            IoUringBindingMode.SocketFallback,
            RingCount: 0,
            SetupFlags: 0,
            UsesProvidedBufferRing: false,
            FallbackReason: reason));
        return listener;
    }

    private void RecordIoUringBinding(IConnectionListener listener)
    {
        var info = (IIoUringListenerInfo)listener;
        AddBinding(new IoUringBindingInfo(
            listener.EndPoint,
            IoUringBindingMode.IoUring,
            info.RingCount,
            info.SetupFlags,
            info.UsesProvidedBufferRing,
            FallbackReason: null));
    }

    private void AddBinding(IoUringBindingInfo binding)
    {
        lock (_bindingsLock)
            _bindings = [.. _bindings, binding];
    }

    /// <inheritdoc />
    public bool CanBind(EndPoint endpoint) => endpoint is IPEndPoint;

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
