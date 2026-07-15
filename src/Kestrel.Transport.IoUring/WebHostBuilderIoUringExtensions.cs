using Kestrel.Transport.IoUring.Transport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kestrel.Transport.IoUring;

/// <summary>Extension methods for configuring the io_uring transport on <see cref="IWebHostBuilder"/>.</summary>
public static class WebHostBuilderIoUringExtensions
{
    /// <summary>Configures Kestrel to use the io_uring transport.</summary>
    public static IWebHostBuilder UseIoUring(
        this IWebHostBuilder builder,
        Action<IoUringTransportOptions>? configure = null)
    {
        return builder.ConfigureServices(services =>
        {
            services.AddIoUringTransport(configure);
        });
    }
}

/// <summary>Extension methods for configuring the io_uring transport on <see cref="IHostBuilder"/>.</summary>
public static class HostBuilderIoUringExtensions
{
    /// <summary>Configures Kestrel to use the io_uring transport.</summary>
    public static IHostBuilder UseIoUring(
        this IHostBuilder builder,
        Action<IoUringTransportOptions>? configure = null)
    {
        return builder.ConfigureServices((_, services) =>
        {
            services.AddIoUringTransport(configure);
        });
    }
}
