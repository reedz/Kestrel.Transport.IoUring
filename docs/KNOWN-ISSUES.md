# Known issues

Findings from the cross-verified reviews of the `feature/pts-readiness` work that are **not fixed**, so nobody has to
rediscover them. Fixed items are in the commit history; this list is only what remains. "Reproduced" means a test or
probe showed it; otherwise it was established by independent source reviews.

## Shutdown

* **Quarantine is a deliberate leak.** If disposal cannot show within 5 seconds that the kernel has finished every
  request of a ring (a request that never ends, a lost completion, or an IO loop that does not stop), the listener,
  its ring, mappings, provided buffers and the connections' pinned memory are kept for the rest of the process
  rather than released while the kernel might still write into them. The leak is bounded per affected ring but
  accumulates if it keeps happening; it is logged as an error and counted (`IoUringQuarantine`). Not observed outside
  tests that force it.
* **Deferred recv EOF is not observable while the input pipe is paused (parity, not a regression).** A peer FIN that
  arrives behind unread, backpressured data is delivered after that data. While the application is not reading,
  neither `ConnectionClosed` nor the end of input is signalled; stock `SocketTransport` behaves the same (its receive
  loop awaits the paused flush before it can read the FIN).

## Multi-ring (`ThreadCount > 1`)

* **Port check window.** An explicit port is checked with an exclusive bind before the rings bind it with
  `SO_REUSEPORT`; a process that binds the port with `SO_REUSEPORT` in the instant between the two can still share
  it. Ephemeral ports (0) are not checked (nobody else can hold the port the kernel picks).
* **Per-ring tables hold the whole limit.** Connection admission is shared, so each ring's slot table (about 20 bytes
  per slot) and, with `EnableRegisteredFiles`, its fixed-file table are sized for the full `MaxConnections`. A
  fixed-file table larger than `RLIMIT_NOFILE` cannot be registered and the ring silently runs without registered
  files.

## Receive path

* **Pending-data allowance.** The per-connection cap on receive data queued behind a paused pipe is
  `max(8 MiB, CQ entries x receive buffer size)`; with the defaults and 16,384 connections that is an allowance of
  about 128 GiB in total. It is a runaway guard for completions already in flight, not memory-DoS protection, which
  would need an aggregate budget.
* **Kernels older than 6.0** use single-shot receive by design (5.10-5.19), and kernels older than 5.10 or rings that
  do not implement the transport's opcodes (checked with `IORING_REGISTER_PROBE`) use SocketTransport. CI runs the
  whole suite on the single-shot path (`TEST_IOURING_SINGLESHOT=1`) but on a current kernel; no 5.x kernel has been
  tested. The version floor is a heuristic (it exists because the non-blocking eventfd `READ` used for wake-ups does
  not wait correctly on 5.6-5.7); the probe is the real check.
* **Retry passes run after each completion batch** (NEW-10b): sustained completion traffic delays receive, cancel and
  send retries by up to one bounded batch.

## Formal models

* `specs/tla/MultiListener.tla` still abstracts worker unbind as completing only once the worker's live connections
  are gone (header item 7). Its ownership claims do not depend on that timing, and the unbind-alone contract is
  checked in `Listener.tla` (`UnbindAloneKeepsLoopRunning`) and by the shutdown tests.
* The receive-retry arm budget and FIFO order are tested (`ListenerRecvRetryBudgetTests`) but only abstracted in the
  models.
* `Teardown.tla` models the drain with one request per kind and a bounded completion queue; `Listener.tla` models
  the bounded snapshot drain that now runs only for a quarantined ring.

## Benchmarking

* All measurements were taken on a 2 vCPU shared VM with high run-to-run variance; see `BENCHMARKS.md` for what was
  and was not concluded. Multi-ring scaling, TLS and HTTP/2 were not measured.
