# Kestrel.Transport.IoUring

[![NuGet](https://img.shields.io/nuget/v/Kestrel.Transport.IoUring.svg)](https://www.nuget.org/packages/Kestrel.Transport.IoUring)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A high-performance **io_uring** transport for [Kestrel](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel) / ASP.NET Core that replaces the default socket transport with Linux's `io_uring` interface. **Up to 30% faster** at 128+ concurrent connections.

## Features

- **Drop-in replacement** — one `UseIoUring()` call replaces the socket transport
- **Multishot recv** with **provided buffer rings** — eliminates per-recv memory pinning and SQE resubmission
- **Zero-copy send** (`SEND_ZC`) for payloads >4KB — avoids kernel buffer copy
- **Registered files** (`IOSQE_FIXED_FILE`) — kernel skips fd lookup per SQE
- **Single-issuer IO loop** — dedicated thread for io_uring_enter, drain tasks submit via eventfd wake
- **Graceful fallback** — auto-detects `ENOSYS`/`EPERM` and falls back to socket transport
- **Zero-allocation** hot path with pooled `IValueTaskSource` completions
- **Multi-ring** support via `SO_REUSEPORT` (opt-in `ThreadCount` option)
- Targets **net8.0**, **net9.0**, **net10.0**

## Requirements

- Linux with kernel **6.0+** (for multishot recv + buffer rings)
- Kernel **5.1+** works with reduced features (single-shot recv, no buffer rings)
- .NET 8 or later

> **Fallback**: On unsupported systems, a warning is logged and the standard `SocketTransportFactory` is used — your app keeps working.

## Installation

```bash
dotnet add package Kestrel.Transport.IoUring
```

## Quick Start

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseIoUring(options =>
{
    options.RingSize = 256;           // SQ/CQ ring depth (power of two)
    options.MaxConnections = 1024;    // connection limit
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

## Performance

On a 2-core VM (AMD EPYC 9V74, Linux 6.17, .NET 10):

| Scenario | Socket | io_uring | Improvement |
|----------|--------|----------|-------------|
| 128 persistent connections | 17,897 req/s | **23,488 req/s** | **+31%** |
| 256 persistent connections | 18,800 req/s | **23,785 req/s** | **+27%** |
| 20K requests @ 128 concurrency | 30,868 req/s | **35,837 req/s** | **+16%** |

io_uring's advantage grows with connection count. On multi-core servers, the improvement is larger.

See [BENCHMARKS.md](BENCHMARKS.md) for detailed results and methodology.

## Project Structure

```
├── src/Kestrel.Transport.IoUring/   ← NuGet library (net8.0/net9.0/net10.0)
├── samples/SampleApp/                ← Minimal ASP.NET Core sample
└── benchmarks/                        ← Stress benchmarks
```

## io_uring Features Used

| Feature | Kernel | Purpose |
|---------|--------|---------|
| `IORING_OP_ACCEPT` | 5.1+ | Accept new TCP connections |
| `IORING_OP_RECV` (multishot) | 6.0+ | Receive data with buffer ring selection |
| `IORING_OP_SEND` | 5.1+ | Send data (small payloads) |
| `IORING_OP_SEND_ZC` | 6.0+ | Zero-copy send (payloads >4KB) |
| `IORING_OP_READ` | 5.1+ | eventfd wake for IO loop |
| `IORING_REGISTER_FILES` | 5.1+ | Registered file descriptors |
| `IORING_REGISTER_PBUF_RING` | 5.19+ | Provided buffer ring for recv |
| `IORING_FEAT_SINGLE_MMAP` | 5.4+ | Shared SQ/CQ mmap region |

## User Data Encoding

Each SQE carries a `user_data` field used to route the CQE back to the correct connection and operation:

```
bits [63..8] = connection_id (int, shifted left 8)
bits [7..0]  = op_type  (0=Accept, 1=Recv, 2=Send, 3=Close)
```

## License

MIT
