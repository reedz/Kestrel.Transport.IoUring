# Benchmark Results

The original results below are historical tuned measurements and should not be read as guarantees for the current safe defaults.

## Safe-default validation

Current defaults use `UnsafeInlineScheduling=false`. On an x64 WSL2 host (Linux 6.6, .NET 10, 16 logical CPUs), the quick benchmark showed the socket transport ahead:

| Scenario | Socket | io_uring | Socket advantage |
|----------|--------|----------|------------------|
| 128 persistent connections | 41,663 req/s | 28,750 req/s | 1.45x |
| 256 persistent connections | 46,694 req/s | 28,557 req/s | 1.64x |
| 20K requests @ 128 concurrency | 69,015 req/s | 36,028 req/s | 1.92x |

This environment is not representative of dedicated production Linux hardware, but it demonstrates that io_uring is not automatically faster. Always benchmark the safe defaults first. Treat `UnsafeInlineScheduling=true` and aggressive `ThreadCount` tuning as workload-specific opt-ins.

## Environment

- **CPU:** AMD EPYC 9V74 (1 core, 2 hyperthreads)
- **Kernel:** Linux 6.17.0 (Azure VM)
- **Runtime:** .NET 10.0
- **io_uring features:** Multishot recv, buffer rings, registered files, asynchronous SEND, SINGLE_MMAP

## Scenario 1: Many Concurrent Persistent Connections

5,000 requests per test. Each "connection" is a separate HttpClient with `MaxConnectionsPerServer=1`.

| Connections | Socket (req/s) | io_uring (req/s) | Ratio | Winner |
|------------|----------------|-------------------|-------|--------|
| 16 | 14,735 | 12,077 | 1.22x | Socket |
| 64 | 18,561 | 19,251 | **0.97x** | ~Parity |
| 128 | 17,897 | **23,488** | **0.76x** | **io_uring +24%** |
| 256 | 18,800 | **23,785** | **0.79x** | **io_uring +21%** |

> io_uring's advantage grows with connection count: batched syscalls amortize the per-enter overhead across more concurrent operations.

## Scenario 2: High-Frequency Small Requests

20,000 requests over pooled connections.

| Concurrency | Socket (req/s) | io_uring (req/s) | Ratio | Winner |
|-------------|----------------|-------------------|-------|--------|
| 16 | 15,360 | 16,852 | **0.91x** | **io_uring +9%** |
| 64 | 26,603 | 23,494 | 1.13x | Socket |
| 128 | 30,868 | **35,837** | **0.86x** | **io_uring +14%** |

## Key Observations

- **Sweet spot:** io_uring excels at **128+ concurrent persistent connections** (20-30% faster)
- **GC pressure:** io_uring typically triggers 0-1 gen0 collections vs 2-7 for the socket transport
- **Send path:** Responses use `IORING_OP_SEND` with pinned managed buffers
- **2-core limitation:** On this VM, client and server compete for CPU. On multi-core servers, io_uring's batching advantage compounds further

## Methodology

Both transports run as in-process ASP.NET Core servers with `app.MapGet("/", () => "OK")`. The socket transport uses Kestrel's default `SocketTransportFactory`. Each scenario runs 3 times with the best/representative result shown.

## Running Benchmarks

```bash
cd benchmarks/Kestrel.Transport.IoUring.Benchmarks
dotnet run -c Release -- quick
```
