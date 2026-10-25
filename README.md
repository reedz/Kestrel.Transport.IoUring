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

- Linux 6.0+ for the default receive path (multishot receive with provided buffer rings).
- Linux 5.10-5.19 works through automatic single-shot receive, which is slower and not covered by CI.
  Older kernels, and environments whose io_uring does not implement the opcodes the transport needs (checked with
  `IORING_REGISTER_PROBE`, for example gVisor's experimental support), use SocketTransport. Only Linux 6.17 has
  been tested.
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
| `BufferRingSize` | 0 (auto) | Provided receive buffers per ring; power of two, or 0 to size from the connections the ring is expected to serve (`MaxConnections`, or its even share per ring with `ThreadCount` > 1; 1024-4096). Keep it at least the number of concurrent connections per ring: with fewer buffers, receives hit `ENOBUFS` and are re-armed as buffers are recycled, which costs throughput and can time out under thousands of simultaneously active connections. |
| `ReceiveBufferSize` | 2048 | Bytes per receive buffer. |
| `MaxConnections` | 16384 | Simultaneous connection limit for the listener, shared by all of its rings (one admission count, so the kernel's `SO_REUSEPORT` hashing cannot make one ring refuse connections while another has room). |
| `AcceptQueueCapacity` | 1024 | Accepted connections waiting for Kestrel. When it is full the transport pauses accepting (clients wait in the listen backlog) instead of aborting connections. |
| `ThreadCount` | 1 | Rings/listener threads. More than 1 binds one `SO_REUSEPORT` socket per ring; an explicit port is first checked with an exclusive bind, so a second instance fails with Kestrel's "address already in use" like the default single-ring listener (a process binding in the instant between that check and the rings can still share the port). |
| `EnableMultishotAccept` | `true` | Multishot accept with kernel fallback. |
| `EnableBufferRing` | `true` | Provided-buffer multishot receive. |
| `EnableRegisteredFiles` | `false` | Fixed-file table; adds synchronous updates on connection churn. |
| `UnsafeInlineScheduling` | `true` | Runs Kestrel processing on the IO thread; disable for blocking middleware. |
| `RequireIoUring` | `false` | Throw instead of silently falling back to SocketTransport when io_uring is unavailable or ring setup fails (also `IOURING_REQUIRE=1`). |
| `ListenBacklog` | 512 | `listen(2)` backlog. When not set, `SocketTransportOptions.Backlog` applies. |

`EnableCoopTaskRun`, `EnableSingleIssuer`, `EnableDeferTaskRun`, and
`EnableSqPoll` are advanced kernel options and default to `false`. With `EnableSingleIssuer` the ring is created
disabled and enabled by its IO loop thread, which becomes the ring's only issuer; whether the kernel accepted the flag
is visible in `Bindings[..].SetupFlags`.

### SocketTransportOptions

The `SocketTransportOptions` settings that mean the same thing here are honoured: `NoDelay` (default `true`),
`Backlog` (unless `ListenBacklog` is set explicitly), and `MaxReadBufferSize` / `MaxWriteBufferSize` (the input and
output pipe limits; `null` means unlimited). Configure them with
`services.Configure<SocketTransportOptions>(...)`. Socket-specific settings such as `IOQueueCount`,
`WaitForDataBeforeAllocatingBuffer` and `UnsafePreferInlineScheduling` do not apply; see `UnsafeInlineScheduling`.

### Shutdown

`UnbindAsync` only stops accepting. `DisposeAsync` aborts what the application has not closed, then the ring's
owner thread cancels every outstanding request and consumes completions until each request has ended, and only
then is memory released (closing the ring alone does not stop the kernel from using it). If that cannot be shown
within 5 seconds, the listener and its memory are kept for the rest of the process instead (logged as an error)
rather than risk the kernel writing into released memory.

### Verifying which transport is active

`IoUringTransportFactory.IsUsingIoUring` only reports whether the host *supports* io_uring. To see what actually
served each listener (for example before trusting a benchmark), resolve the factory and read `Bindings`:

```csharp
var factory = app.Services.GetServices<IConnectionListenerFactory>()
    .OfType<IoUringTransportFactory>().Single();
foreach (var b in factory.Bindings)
    Console.WriteLine($"{b.EndPoint}: {b.Mode}, rings={b.RingCount}, fallback={b.FallbackReason}");
```

Set `RequireIoUring = true` in benchmarks and tests so a host that blocks io_uring fails loudly. Endpoint types that
io_uring never serves (for example Unix domain sockets) still use SocketTransport and are reported as
`SocketFallback`.

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
bits 23..8 : low 16 bits of the slot generation
bits 7..0  : operation type (8 bits)
```

Accept and eventfd operations use reserved sentinel values. The listener's own control queues compare the full
64-bit generation; 16 bits suffice in completions because a slot is reused only after every request of its previous
occupant has completed.

## Performance

The reproducible two-phase benchmark compares maximum RPS/RPS-per-server-CPU
with Linux SocketTransport (epoll), then compares p99 at the same offered rate.
On the repository's 2-CPU Linux witness host, the current matrix produced:

- **60,783 vs 47,091 median RPS** (+29%).
- **89,809 vs 65,441 median RPS/server-CPU** (+37%).
- Lower matched-load p99 in every matrix cell at an equal sustainable offered rate.
- **Zero request errors**.

See [BENCHMARKS.md](BENCHMARKS.md) for prerequisites, raw methodology, per-cell
results, and the exact command. A local Phoronix Test Suite profile that compares
the transports on ASP.NET Core workloads (static file, plaintext, JSON; .NET 10 and
.NET 11 RC2) lives in [`phoronix/`](phoronix/README.md); it is not published anywhere.

## More documentation

- [docs/DOTNET11.md](docs/DOTNET11.md): evidence that stock .NET 11 has no io_uring socket transport.
- [docs/KNOWN-ISSUES.md](docs/KNOWN-ISSUES.md): review findings that are not fixed.

## License

MIT
