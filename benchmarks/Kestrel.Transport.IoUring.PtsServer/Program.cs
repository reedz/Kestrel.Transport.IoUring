using System.Diagnostics;
using System.Net;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Kestrel.Transport.IoUring;
using Kestrel.Transport.IoUring.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// HTTP server used by the Phoronix Test Suite profile. It serves three workloads on one port and lets the
// profile choose the transport, so the transport is the only variable between runs:
//   /test.html   static file (parity with the nginx/apache PTS profiles)
//   /plaintext   13-byte text/plain body
//   /json        {"message":"Hello, World!"} serialized per request
//   /__transport JSON describing what actually serves traffic (used by the profile to refuse a silent fallback)
//
// Configuration (environment):
//   PTS_TRANSPORT  iouring | sockets                 (required)
//   PTS_PORT       listen port                       (default 8099)
//   PTS_BIND       listen address                    (default 127.0.0.1)
//   PTS_THREADS    io_uring rings, or "auto"         (default 1; ignored for sockets)
//   PTS_WWWROOT    static files directory            (default ./wwwroot)
//   PTS_BUFFERS    io_uring BufferRingSize           (default: library auto-sizing)
//   PTS_RING       io_uring RingSize                 (default: library default)

static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

string transport = (Env("PTS_TRANSPORT") ?? "").ToLowerInvariant();
if (transport is not ("iouring" or "sockets"))
{
    Console.Error.WriteLine("PTS_TRANSPORT must be 'iouring' or 'sockets'.");
    return 2;
}

int port = int.Parse(Env("PTS_PORT") ?? "8099");
var bind = IPAddress.Parse(Env("PTS_BIND") ?? "127.0.0.1");
string webRoot = Env("PTS_WWWROOT") ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
int rings = string.Equals(Env("PTS_THREADS"), "auto", StringComparison.OrdinalIgnoreCase)
    ? Environment.ProcessorCount
    : int.Parse(Env("PTS_THREADS") ?? "1");

var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
{
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Directory.Exists(webRoot) ? webRoot : null,
});
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxConcurrentConnections = null;
    options.Listen(bind, port);
});

if (transport == "iouring")
{
    builder.WebHost.UseIoUring(options =>
    {
        // A benchmark labelled io_uring must never be served by the socket transport.
        options.RequireIoUring = true;
        options.ThreadCount = rings;
        if (Env("PTS_BUFFERS") is { } buffers)
            options.BufferRingSize = int.Parse(buffers);
        if (Env("PTS_RING") is { } ring)
            options.RingSize = int.Parse(ring);
        options.MaxConnections = 65536;
    });
}

var app = builder.Build();

byte[] plaintext = "Hello, World!"u8.ToArray();
if (Directory.Exists(webRoot))
    app.UseStaticFiles();

app.Use(async (context, next) =>
{
    string path = context.Request.Path.Value ?? "";
    if (path == "/plaintext")
    {
        context.Response.ContentType = "text/plain";
        context.Response.ContentLength = plaintext.Length;
        await context.Response.Body.WriteAsync(plaintext);
    }
    else if (path == "/json")
    {
        context.Response.ContentType = "application/json";
        await using var writer = new Utf8JsonWriter(context.Response.BodyWriter);
        writer.WriteStartObject();
        writer.WriteString("message", "Hello, World!");
        writer.WriteEndObject();
    }
    else if (path == "/__transport")
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(DescribeTransport(app, transport, rings));
    }
    else
    {
        await next(context);
    }
});

await app.StartAsync();

// Refuse to report ready unless the requested transport is really the one serving traffic.
string description = DescribeTransport(app, transport, rings);
using (JsonDocument doc = JsonDocument.Parse(description))
{
    string actual = doc.RootElement.GetProperty("actualTransport").GetString()!;
    int ioUringFds = doc.RootElement.GetProperty("ioUringFds").GetInt32();
    if ((transport == "iouring" && actual != "io_uring") || (transport == "sockets" && ioUringFds != 0))
    {
        Console.Error.WriteLine($"Requested transport '{transport}' but actual transport is '{actual}' with {ioUringFds} io_uring fds.");
        await app.StopAsync();
        return 3;
    }
}

Console.WriteLine($"PTS_SERVER_READY {description}");
Console.Out.Flush();
await app.WaitForShutdownAsync();
return 0;

static int CountIoUringFds()
{
    if (!Directory.Exists("/proc/self/fd"))
        return -1;

    int count = 0;
    foreach (string fd in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
    {
        try
        {
            if (new FileInfo(fd).LinkTarget?.Contains("io_uring", StringComparison.Ordinal) == true)
                count++;
        }
        catch (IOException)
        {
            // The fd used for the enumeration itself can disappear.
        }
    }

    return count;
}

static string DescribeTransport(WebApplication app, string requested, int rings)
{
    IoUringBindingInfo? binding = app.Services
        .GetServices<IConnectionListenerFactory>()
        .OfType<IoUringTransportFactory>()
        .FirstOrDefault()?.Bindings.FirstOrDefault();

    string actual = binding is null
        ? "sockets"
        : binding.Mode == IoUringBindingMode.IoUring ? "io_uring" : "sockets";

    var buffer = new System.Buffers.ArrayBufferWriter<byte>();
    using (var writer = new Utf8JsonWriter(buffer))
    {
        writer.WriteStartObject();
        writer.WriteString("requestedTransport", requested);
        writer.WriteString("actualTransport", actual);
        writer.WriteNumber("ioUringFds", CountIoUringFds());
        writer.WriteNumber("rings", binding?.RingCount ?? 0);
        writer.WriteNumber("setupFlags", binding?.SetupFlags ?? 0);
        writer.WriteBoolean("providedBufferRing", binding?.UsesProvidedBufferRing ?? false);
        writer.WriteString("fallbackReason", binding?.FallbackReason);
        writer.WriteNumber("requestedRings", requested == "iouring" ? rings : 0);
        writer.WriteString("runtime", RuntimeInformation.FrameworkDescription);
        writer.WriteString("os", RuntimeInformation.OSDescription);
        writer.WriteBoolean("serverGc", GCSettings.IsServerGC);
        writer.WriteNumber("processorCount", Environment.ProcessorCount);
        writer.WriteNumber("pid", Environment.ProcessId);
        writer.WriteEndObject();
    }

    return Encoding.UTF8.GetString(buffer.WrittenSpan);
}
