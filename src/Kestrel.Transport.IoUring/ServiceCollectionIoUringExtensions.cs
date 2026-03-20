using Kestrel.Transport.IoUring.Transport;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;

namespace Kestrel.Transport.IoUring;

/// <summary>Extension methods for registering the io_uring transport on <see cref="IServiceCollection"/>.</summary>
public static class ServiceCollectionIoUringExtensions
{
    /// <summary>Registers the io_uring connection listener factory in the service collection.</summary>
    public static IServiceCollection AddIoUringTransport(
        this IServiceCollection services,
        Action<IoUringTransportOptions>? configure = null)
    {
        if (configure != null)
            services.Configure(configure);
        services.AddSingleton<IConnectionListenerFactory, IoUringTransportFactory>();
        return services;
    }
}
