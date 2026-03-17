using AspNetCoreUring.Transport;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;

namespace AspNetCoreUring;

public static class ServiceCollectionIoUringExtensions
{
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
