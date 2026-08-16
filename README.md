# Kestrel.Transport.IoUring

[![NuGet](https://img.shields.io/nuget/v/Kestrel.Transport.IoUring.svg)](https://www.nuget.org/packages/Kestrel.Transport.IoUring)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A Linux `io_uring` transport for ASP.NET Core Kestrel. It is registered as an
`IConnectionListenerFactory` and falls back to Kestrel's `SocketTransport` when
`io_uring`, the endpoint type, or ring setup is unavailable.

## Features

- Multishot accept with automatic single-shot fallback.
- Multishot receive backed by provided buffer rings, including cancellation under pipe backpressure.
- Small-response sends through per-connection pinned scratch buffers.
- Optional registered file descriptors (`EnableRegisteredFiles`).
- Generation-tagged connection slots that reject stale completions.
- One IO-loop thread per ring and optional multi-ring `SO_REUSEPORT` listeners.
- Socket fallback for unsupported hosts, setup failures, and non-IP endpoints.
- Targets .NET 8, .NET 9, and .NET 10.

## Requirements

- Linux 5.1+ for core `io_uring` operations.
- Linux 5.19+/6.0+ for provided-buffer and multishot features; unsupported
  features fall back automatically.
- .NET 8 or later.

Containers or hosts that block `io_uring_setup` through seccomp,
`kernel.io_uring_disabled`, or permissions use SocketTransport.

## Installation

```bash
dotnet add package Kestrel.Transport.IoUring
```

## Usage

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseIoUring(options =>
{
    options.RingSize = 1024;
    options.BufferRingSize = 1024;
    options.MaxConnections = 16_384;
});

var app = builder.Build();
app.MapGet("/", () => "Hello from io_uring!");
app.Run();
```

`UseIoUring()` is also available on `IHostBuilder`. For direct DI registration,
use `services.AddIoUringTransport(...)`.

## Important options

| Option | Default | Description |
|---|---:|---|
| `RingSize` | 1024 | SQ/CQ depth; power of two, maximum 32768. |
| `BufferRingSize` | 1024 | Provided receive buffers per ring; power of two. |
| `ReceiveBufferSize` | 2048 | Bytes per receive buffer. |
| `MaxConnections` | 16384 | Aggregate simultaneous connection limit. |
| `AcceptQueueCapacity` | 1024 | Accepted connections waiting for Kestrel. |
| `ThreadCount` | 1 | Rings/listener threads using `SO_REUSEPORT`. |
| `EnableMultishotAccept` | `true` | Multishot accept with kernel fallback. |
| `EnableBufferRing` | `true` | Provided-buffer multishot receive. |
| `EnableRegisteredFiles` | `false` | Fixed-file table; adds synchronous updates on connection churn. |
| `UnsafeInlineScheduling` | `true` | Runs Kestrel processing on the IO thread; disable for blocking middleware. |

`EnableCoopTaskRun`, `EnableSingleIssuer`, `EnableDeferTaskRun`, and
`EnableSqPoll` are advanced kernel options and default to `false`.

Diagnostics can be enabled with `LogPoolStatsInterval` or the
`IOURING_LOG_POOL_STATS_INTERVAL` environment variable. `IOURING_SPIN_COUNT`
controls optional completion polling before the IO thread parks.
`IOURING_FORCE_FALLBACK=1` forces SocketTransport for fallback testing.

## Architecture

```text
Kestrel
  |
  +-- IoUringTransportFactory
        |
        +-- Ring (SQ, CQ, mmap ownership)
        |
        +-- IoUringConnectionListener (accept, completion loop, lifecycle)
              |
              +-- IoUringConnection (pipes, recv/send state)
              +-- ProvidedBufferRing
              +-- IoUringPipeScheduler
```

Each SQE carries a 64-bit routing value:

```text
bits 63..24: connection slot (40 bits)
bits 23..8 : slot generation (16 bits)
bits 7..0  : operation type (8 bits)
```

Accept and eventfd operations use reserved sentinel values.

## Performance

The reproducible two-phase benchmark compares maximum RPS/RPS-per-server-CPU
with Linux SocketTransport (epoll), then compares p99 at the same offered rate.
On the repository's 2-CPU Linux witness host, the current matrix produced:

- **60,783 vs 47,091 median RPS** (+29%).
- **89,809 vs 65,441 median RPS/server-CPU** (+37%).
- Lower matched-load p99 in every matrix cell at an equal sustainable offered rate.
- **Zero request errors**.

See [BENCHMARKS.md](BENCHMARKS.md) for prerequisites, raw methodology, per-cell
results, and the exact command.

## License

MIT
