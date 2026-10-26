# Benchmark methodology and results

## Gate

The benchmark has two independent phases:

1. **Maximum throughput:** regular `wrk` measures RPS while the harness measures
   server-process CPU. The gate requires higher matrix-median RPS and RPS/CPU
   than Kestrel SocketTransport (Linux epoll).
2. **Matched-load latency:** `wrk2` offers the same fixed 2,400 RPS to both
   transports and records an HdrHistogram. The gate requires io_uring's
   p99 to be no worse in every cell. Each transport must sustain at least 90%
   of the offered rate.

Both phases require zero socket, timeout, and non-2xx errors. Each cell uses one
discarded warmup and five measured trials. Transport order alternates by trial.
The native load generator and server are pinned to separate CPUs.

## Witness environment

- Ubuntu 24.04, Linux 6.17
- .NET SDK/runtime 10.0.108/10.0.8
- AMD EPYC 9V74, two available CPUs; one assigned to the server and one to the load generator
- CPU governor unavailable in the virtualized witness environment
- Repository base `473fd53` plus the reviewed v2.3.0 working tree
- `RingSize=1024`, `BufferRingSize=1024`, one ring
- `wrk` 4.1.0
- `wrk2` commit `44a94c17d8e6a0bac8559b53da76848e430cb7a7`

## Final results

Maximum-throughput medians:

| Scenario | Connections | Socket RPS | io_uring RPS | Socket RPS/CPU | io_uring RPS/CPU |
|---|---:|---:|---:|---:|---:|
| persistent | 16 | 31,795 | 38,101 | 57,263 | 91,344 |
| persistent | 64 | 43,462 | 52,667 | 65,565 | 91,808 |
| persistent | 128 | 48,492 | 54,686 | 68,424 | 89,476 |
| persistent | 256 | 53,583 | 65,826 | 69,449 | 87,379 |
| pooled | 16 | 47,276 | 72,726 | 59,920 | 99,334 |
| pooled | 64 | 47,463 | 71,784 | 66,382 | 91,584 |
| pooled | 128 | 55,831 | 69,749 | 69,922 | 88,866 |
| connection churn | 64 | 11,926 | 12,428 | 15,371 | 15,946 |

Matched-load p99 medians at 2,400 offered RPS:

| Scenario | Connections | Socket p99 | io_uring p99 |
|---|---:|---:|---:|
| persistent | 16 | 9.41 ms | 6.86 ms |
| persistent | 64 | 8.88 ms | 6.17 ms |
| persistent | 128 | 7.93 ms | 5.87 ms |
| persistent | 256 | 8.61 ms | 6.61 ms |
| pooled | 16 | 8.29 ms | 6.22 ms |
| pooled | 64 | 7.40 ms | 6.07 ms |
| pooled | 128 | 7.22 ms | 6.31 ms |
| connection churn | 64 | 16.64 ms | 6.29 ms |

Matrix medians:

| Metric | SocketTransport | io_uring | Result |
|---|---:|---:|---:|
| RPS | 47,091 | 60,783 | io_uring +29% |
| RPS/server-CPU | 65,441 | 89,809 | io_uring +37% |
| Matched-load p99 | 8.51 ms | 6.30 ms | io_uring -26% |
| Errors | 0 | 0 | pass |

Raw CSV and `wrk`/`wrk2` output for this run are retained as session witness
artifacts rather than committed benchmark products.

## ASP.NET Core workloads under the Phoronix profile (2 vCPU development VM)

These numbers come from the `phoronix/` profile and an A/B harness on a **2 vCPU, shared** Azure VM (Linux 6.17,
.NET 10.0.13), one io_uring ring, server and `wrk` pinned to separate CPUs, 8 s runs, 3 rounds with rotating
variant order. They validate correctness and relative effects, **not** multi-ring scaling or absolute throughput,
and spreads between rounds were large (the plaintext rows moved by 40-100%), so only large, consistent effects are
reported. Variants: `base` is the baseline commit `ff018fb` (before the lifecycle and receive-retry fixes and
before gather send), `no-gather` is the current tree with gather send reverted, `current` is the current tree.

| Workload | Connections | base | no-gather | current | Sockets |
|---|---:|---:|---:|---:|---:|
| JSON (RPS) | 20 | 48.7k | 45.2k | 42.8k | 33.5k |
| JSON (RPS) | 100 | 47.1k | 45.7k | 44.8k | 34.5k |
| Static 118 KB (RPS) | 20 | 2.6k | 2.3k | **6.2k** | 6.4k |
| Static 118 KB (RPS) | 100 | 2.7k | 2.6k | **5.6k** | 6.4k |
| Static 118 KB (RPS) | 500 | 2.6k | 2.5k | **5.2k** | 4.3k |

Server CPU per 1,000 requests (ms, lower is better): static file 361/382 (base/no-gather) vs 146-195 (current) vs
140-221 (sockets); JSON about 20 (io_uring, all variants) vs 24-28 (sockets).

* **Gather send is kept.** Sending a response that arrives as many pipe segments with one `SENDMSG` instead of one
  `SEND` plus completion per segment roughly **doubles** the static-file throughput and halves its CPU per request
  (the `no-gather` variant matches `base`). Single small segments (plaintext, JSON) still use the scratch `SEND`.
* **Static file versus sockets:** io_uring was behind by about 0-15% at 20-100 connections and ahead by about 20% at
  500 on this machine. The cause was the pipes' memory pool; with it fixed io_uring is ahead at every connection
  count (see the follow-up below).
* **JSON** is consistently 27-30% faster than sockets at 20/100 connections with 18-25% less CPU per request.
  The earlier small-connection plaintext figures are too noisy on this VM to quote.
* **Sustained gates through the PTS pipeline** (90 s, 1 run, one ring, no errors of any kind): plaintext at 4,000
  connections 37.9k (io_uring) vs 27.5k (sockets) RPS; static file at 1,000 connections 5.1k vs 5.8k. 4,000
  connections is the case that produced an ENOBUFS storm (27k-89k completions per second with thousands of client
  timeouts) before the receive-retry rewrite; it now completes with zero errors. Rerun on the final tree (with the
  pipe memory pool below, `--no-egress`): plaintext at 4,000 connections 41.9k vs 39.9k, static file at 1,000
  connections 7.4k vs 6.8k, again without errors. Single runs on a shared VM: treat these as gates, not
  comparisons.
* Not measured here: more than one ring, TLS/HTTP2, any machine with more than 2 CPUs. Run
  `phoronix/run-local.sh` on a larger machine for those (see `phoronix/README.md`).

### Follow-up: the static-file gap and its fix (pipe memory pool)

Same VM, one ring, a fresh server process per run on CPU 0 and `wrk -t1` on CPU 1, 3 s warm-up then 6 s measured,
5 rounds with every (variant, workload) pair in shuffled order per round. `pool` is the current tree, `before` the
same tree without the change below, `sockets` Kestrel's socket transport. ± is a 95% Student-t interval (n = 5);
CPU is server CPU per 1,000 requests. No run had an error.

| Workload | Conns | pool RPS | before RPS | Sockets RPS | CPU pool / before / sockets (ms) |
|---|---:|---:|---:|---:|---:|
| Static 118 KB | 20 | 6,075 ± 219 | 5,567 ± 161 | 5,777 ± 263 | 157 / 172 / 165 |
| Static 118 KB | 100 | 6,333 ± 355 | 5,070 ± 204 | 5,625 ± 410 | 155 / 194 / 175 |
| Static 118 KB | 500 | 5,020 ± 164 | 4,471 ± 151 | 4,345 ± 186 | 197 / 221 / 227 |
| Plaintext | 100 | 38,617 ± 2,477 | 39,555 ± 1,259 | 33,647 ± 1,180 | 24.8 / 24.3 / 28.4 |
| JSON | 100 | 45,098 ± 1,436 | 42,551 ± 1,830 | 34,089 ± 3,004 | 21.1 / 22.3 / 28.1 |

* **The gap was real.** `before` trailed sockets in every round at 20 and 100 connections (by 2-17%). An earlier
  write-up called it noise: it used normal rather than Student-t intervals and did not compare the rounds.
* **Cause:** the connection pipes used the pipe default, which rents from `ArrayPool<byte>.Shared`. Output blocks
  are rented on ThreadPool threads and returned on the IO thread, and a 118 KB response needs about 29 of them;
  the measurements fit the shared pool's bounded per-thread and per-core caches overflowing, so new arrays kept
  being allocated (and each send had to pin its arrays with a `GCHandle`). Per request at 100 connections (two
  measurements): 6.1-6.8 page faults versus 1.4-1.6 for sockets, and 3.4-4.1x the `madvise`/`mprotect` calls; a
  page-fault profile put 91% of the faults under the static-file copy loop.
* **Fix:** each listener now gives its connections a pool of recycled, pinned 4 KiB blocks, as the socket transport
  does: Kestrel's pool on .NET 10 (`IMemoryPoolFactory<byte>`), a copy of its design on .NET 8/9. Page faults drop
  to about 1.0 per request. `pool` beats `before` in every round on the static file (+6% to +33%) and beats sockets
  in 14 of 15 static rounds.
* **Plaintext and JSON are unchanged:** a separate paired run, 10 rounds of `pool` against `before` only, gives
  +1.1% ± 2.3% (plaintext) and -0.1% ± 2.0% (JSON).
* **Memory:** after a 10 s static-file run at 500 connections, resident memory 5 s later is 228 MB (`pool`), 172 MB
  (`before`) and 232 MB (sockets); the peaks are 282, 207 and 239 MB. Like the socket transport's, the pool keeps
  blocks for reuse; on .NET 10 Kestrel's pool releases idle blocks over time, on .NET 8/9 it keeps its peak (as
  Kestrel's own pool does on those versions).
* Measured on .NET 10 only. Scripts and raw data are not part of the repository.

## Prerequisites

Install `wrk`:

```bash
sudo apt-get install wrk
```

Build the pinned `wrk2` revision:

```bash
git clone https://github.com/giltene/wrk2.git
cd wrk2
git checkout 44a94c17d8e6a0bac8559b53da76848e430cb7a7
make -j
sudo install -m 0755 wrk /usr/local/bin/wrk2
```

## Run

```bash
dotnet run -c Release \
  --project benchmarks/Kestrel.Transport.IoUring.Benchmarks \
  -- quick \
  --trials 5 \
  --duration 5 \
  --latency-duration 10 \
  --latency-rate 2400 \
  --output benchmark-results.csv
```

The command exits with code 2 when the matrix gate fails. It writes maximum
throughput samples to `benchmark-results.csv`, matched-load samples to
`benchmark-results.latency.csv`, and full native output to
`benchmark-results.wrk.txt`.
