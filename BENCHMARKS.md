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
  500 on this machine. The follow-up below, with an A/A control, puts the 20-100 connection difference within
  run-to-run noise.
* **JSON** is consistently 27-30% faster than sockets at 20/100 connections with 18-25% less CPU per request.
  The earlier small-connection plaintext figures are too noisy on this VM to quote.
* **Sustained gates through the PTS pipeline** (90 s, 1 run, one ring, no errors of any kind): plaintext at 4,000
  connections 37.9k (io_uring) vs 27.5k (sockets) RPS; static file at 1,000 connections 5.1k vs 5.8k. 4,000
  connections is the case that produced an ENOBUFS storm (27k-89k completions per second with thousands of client
  timeouts) before the receive-retry rewrite; it now completes with zero errors.
* Not measured here: more than one ring, TLS/HTTP2, any machine with more than 2 CPUs. Run
  `phoronix/run-local.sh` on a larger machine for those (see `phoronix/README.md`).

### Follow-up: static file versus sockets after the shutdown/admission work

Same VM, current branch, one ring, a fresh server process per run on CPU 0 and `wrk -t1` on CPU 1, 3 s warm-up
then 6 s measured, 5 rounds with every (variant, workload) pair in shuffled order per round. `io_uring (A/A)` is a second, identical io_uring
variant whose spread shows the run-to-run noise. CPU is server CPU per 1,000 requests.

| Workload | Conns | io_uring RPS | io_uring (A/A) RPS | Sockets RPS | CPU io_uring / sockets (ms) |
|---|---:|---:|---:|---:|---:|
| Static 118 KB | 20 | 5,479 ± 35 | 5,227 ± 466 | 5,296 ± 832 | 175 / 178 |
| Static 118 KB | 100 | 4,706 ± 679 | 4,943 ± 200 | 5,292 ± 194 | 208 / 185 |
| Static 118 KB | 500 | 4,464 ± 94 | 4,416 ± 157 | 4,316 ± 88 | 222 / 228 |
| Plaintext | 100 | 38,642 ± 390 | 37,733 ± 3,022 | 32,092 ± 3,680 | 25.0 / 28.7 |
| JSON | 100 | 42,451 ± 1,176 | 38,530 ± 4,420 | 32,588 ± 3,198 | 22.3 / 28.5 |

(± is the 95% confidence interval of the mean.) Per request, measured with `perf stat` on the server process for the
static file at 20 / 100 connections: io_uring 0.71 / 0.29 context switches, 0.45 / 0.10 `io_uring_enter` and
0.23 / 0.09 eventfd `write`s; sockets 1.69 / 1.35 context switches, 2.0 `sendmsg` and 0.82 / 0.71 `epoll_wait`.

* Plaintext (+20%) and JSON (+30%) stay clearly ahead with less CPU per request.
* The static file is at parity within the noise: io_uring is ahead at 20 and 500 connections and behind at 100,
  where the A/A pair itself differs by 5%. The per-flush cross-thread hand-off suspected earlier does not show up
  in the counts (well under one wake-up per request, and fewer context switches than sockets). No change was made;
  a candidate would need to show a win larger than this noise.

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
