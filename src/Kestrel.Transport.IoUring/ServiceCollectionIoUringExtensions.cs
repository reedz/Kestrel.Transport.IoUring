using Kestrel.Transport.IoUring.Transport;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Kestrel.Transport.IoUring;

/// <summary>Extension methods for registering the io_uring transport on <see cref="IServiceCollection"/>.</summary>
public static class ServiceCollectionIoUringExtensions
{
    /// <summary>Registers the io_uring connection listener factory in the service collection.</summary>
    public static IServiceCollection AddIoUringTransport(
        this IServiceCollection services,
        Action<IoUringTransportOptions>? configure = null)
    {
        services.AddOptions<IoUringTransportOptions>();
        services.AddOptions<SocketTransportOptions>();
        if (configure != null)
            services.Configure(configure);

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<IoUringTransportOptions>, IoUringTransportOptionsValidator>());
        services.AddSingleton<IConnectionListenerFactory, IoUringTransportFactory>();
        return services;
    }
}
