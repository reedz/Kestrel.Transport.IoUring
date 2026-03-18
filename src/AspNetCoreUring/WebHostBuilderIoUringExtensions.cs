using AspNetCoreUring.Transport;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AspNetCoreUring;

public static class WebHostBuilderIoUringExtensions
{
    public static IWebHostBuilder UseIoUring(
        this IWebHostBuilder builder,
        Action<IoUringTransportOptions>? configure = null)
    {
        return builder.ConfigureServices(services =>
        {
            // Ensure SocketTransportOptions is registered so the fallback factory can be resolved.
            services.AddOptions<SocketTransportOptions>();
            if (configure != null)
                services.Configure(configure);
            services.AddSingleton<IConnectionListenerFactory, IoUringTransportFactory>();
        });
    }
}

public static class HostBuilderIoUringExtensions
{
    public static IHostBuilder UseIoUring(
        this IHostBuilder builder,
        Action<IoUringTransportOptions>? configure = null)
    {
        return builder.ConfigureServices((_, services) =>
        {
            // Ensure SocketTransportOptions is registered so the fallback factory can be resolved.
            services.AddOptions<SocketTransportOptions>();
            if (configure != null)
                services.Configure(configure);
            services.AddSingleton<IConnectionListenerFactory, IoUringTransportFactory>();
        });
    }
}
