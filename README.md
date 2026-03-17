# AspNetCoreUring

A pluggable **io_uring** transport for [Kestrel](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel) / ASP.NET Core that replaces the default socket transport with Linux's high-performance `io_uring` interface.

## Features

- **Zero-allocation** I/O path: unsafe pointer arithmetic directly over `mmap`-backed ring buffers
- **Drop-in** replacement for the default Kestrel socket transport — one `UseIoUring()` call
- **Graceful fallback**: detects `ENOSYS` at startup and warns if `io_uring` is unavailable (kernel < 5.1)
- Uses `System.IO.Pipelines` for the connection transport (back-pressure, buffer pooling)
- Targets **net8.0+**, leveraging C# 12 features (primary constructors, collection expressions, `LibraryImport`)
- Designed to be **published as a NuGet package** (`PackageId: AspNetCoreUring`)

## Requirements

- Linux with kernel **5.1+** (io_uring support)
- .NET 8 or later
- `libc` available at runtime (standard on all Linux distros)

## Quick Start

```csharp
var builder = WebApplication.CreateBuilder(args);

// Replace the default socket transport with io_uring
builder.WebHost.UseIoUring(options =>
{
    options.RingSize = 256;              // SQ/CQ ring depth (power of two)
    options.ThreadCount = Environment.ProcessorCount; // IO thread count
});

var app = builder.Build();
app.MapGet("/", () => "Hello from io_uring!");
app.Run();
```

### IHostBuilder variant

```csharp
Host.CreateDefaultBuilder(args)
    .UseIoUring()
    .ConfigureWebHostDefaults(web => { ... })
    .Build()
    .Run();
```

## Architecture

```
┌──────────────────────────────────────────────────────────────────┐
│  ASP.NET Core / Kestrel                                          │
│   IConnectionListenerFactory.BindAsync()                         │
│         │                                                        │
│   IoUringTransportFactory  ─── creates ──► Ring (io_uring fd)   │
│         │                                        │               │
│   IoUringConnectionListener ◄─── IORING_OP_ACCEPT completions ──┤
│         │                                        │               │
│   IoUringConnection (ConnectionContext)           │               │
│    ├── Input  Pipe ◄──── IORING_OP_RECV ─────────┤               │
│    └── Output Pipe ─────► IORING_OP_SEND ────────┘               │
└──────────────────────────────────────────────────────────────────┘
```

### Native layer

| File | Description |
|------|-------------|
| `Native/IoUringNative.cs` | `io_uring_setup` / `io_uring_enter` / `io_uring_register` via `syscall()` |
| `Native/IoUringSqe.cs` | `io_uring_sqe` struct (64 bytes, `[StructLayout(Sequential, Size=64)]`) |
| `Native/IoUringCqe.cs` | `io_uring_cqe` struct (16 bytes) |
| `Native/IoUringParams.cs` | `io_uring_params` with SQ/CQ offset sub-structs |
| `Native/IoUringConstants.cs` | Opcode constants, mmap offsets, flag bits |
| `Native/Libc.cs` | `mmap` / `munmap` / `close` P/Invoke |

### Ring layer

| File | Description |
|------|-------------|
| `SubmissionQueue.cs` | Unsafe pointer arithmetic over mmap'd SQ ring; `Volatile.Read/Write` for SMP ordering |
| `CompletionQueue.cs` | Unsafe pointer arithmetic over mmap'd CQ ring |
| `Ring.cs` | Wraps SQ+CQ; `TryGetSqe`, `Submit`, `SubmitAndWait`, `TryPeekCompletion`; static `IsSupported` |

### Transport layer

| File | Description |
|------|-------------|
| `Transport/IoUringTransportFactory.cs` | Implements `IConnectionListenerFactory`; creates a `Ring` per bound endpoint |
| `Transport/IoUringConnectionListener.cs` | Binds socket, submits `ACCEPT` SQEs, single IO-loop task, bounded `Channel<ConnectionContext>` |
| `Transport/IoUringConnection.cs` | `ConnectionContext`; `Pipe`-based `IDuplexPipe`; submits `RECV`/`SEND` SQEs |
| `Transport/IoUringTransportOptions.cs` | `RingSize`, `ThreadCount`, `MaxConnections` |
| `WebHostBuilderIoUringExtensions.cs` | `UseIoUring()` for `IWebHostBuilder` and `IHostBuilder` |

## Benchmarks

BenchmarkDotNet benchmarks comparing the standard socket transport against the io_uring transport:

```
cd benchmarks/AspNetCoreUring.Benchmarks
dotnet run -c Release
```

The benchmark starts two in-process ASP.NET Core servers (one with each transport) and measures HTTP GET throughput over loopback with `HttpClient`.

## Project Structure

```
AspNetCoreUring.slnx
├── src/AspNetCoreUring/          ← NuGet library (net8.0)
├── samples/SampleApp/            ← Minimal ASP.NET Core sample
└── benchmarks/AspNetCoreUring.Benchmarks/  ← BenchmarkDotNet
```

## io_uring Opcodes Used

| Opcode | Value | Purpose |
|--------|-------|---------|
| `IORING_OP_ACCEPT` | 13 | Accept new TCP connections |
| `IORING_OP_RECV` | 27 | Receive data from a connection |
| `IORING_OP_SEND` | 26 | Send data to a connection |
| `IORING_OP_CLOSE` | 19 | Close a connection's socket fd |

## User Data Encoding

Each SQE carries a `user_data` field used to route the CQE back to the correct connection and operation:

```
bits [63..8] = connection_id (int, shifted left 8)
bits [7..0]  = op_type  (0=Accept, 1=Recv, 2=Send, 3=Close)
```

## License

MIT
