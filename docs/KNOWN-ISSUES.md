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

* **`EnableReusePort` admits same-user processes.** With the opt-in per-ring `SO_REUSEPORT` sockets, Linux lets any
  socket of a process with the same effective user ID join the group at any time, not only at startup (see
  `socket(7)`): such a process then receives a share of the new connections (for example the plaintext HTTP
  requests). nginx's `reuseport` and other `SO_REUSEPORT` servers have the same property. The exclusive bind that
  checks an explicit port first only turns an instance that is already running into Kestrel's "address already in
  use" error; it cannot keep a later joiner out. The default (one shared, exclusively bound socket) has no such
  window. Ephemeral ports (0) are not checked.
* **Shared-socket accept is single-shot.** Accept requests wait on the shared socket exclusively and the kernel gives
  each new connection to the first one queued; a multishot accept stays first and took every connection (256 of 256
  over two rings, measured), so the default multi-ring listener uses one single-shot accept per ring, which requeues
  behind its siblings (117-139 of 256 per ring in the same test; `SO_REUSEPORT` hashing: 112-144). That is one accept
  submission per connection instead of one per ring; a connection-churn test on this VM showed no loss
  (6-14k accepts per second either way). The spread depends on which ring has an accept queued when a client
  arrives, so a ring that is busy longer takes fewer new connections; it is not a hash.
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
* **Pipe memory is kept for reuse.** Connection pipes use recycled pinned 4 KiB blocks, one pool per ring. On
  .NET 10 that is Kestrel's pool, which releases idle blocks over time; on .NET 8/9 the transport's own pool keeps
  its peak for the listener's lifetime, as Kestrel's socket transport does on those versions. A quarantined
  listener keeps its pool.

## Formal models

* `specs/tla/MultiListener.tla` still abstracts worker unbind as completing only once the worker's live connections
  are gone (header item 7). Its ownership claims do not depend on that timing, and the unbind-alone contract is
  checked in `Listener.tla` (`UnbindAloneKeepsLoopRunning`) and by the shutdown tests.
* The receive-retry arm budget and FIFO order are tested (`ListenerRecvRetryBudgetTests`) but only abstracted in the
  models.
* `Teardown.tla` models the drain with one request per kind and a bounded completion queue; `Listener.tla` models
  the bounded snapshot drain that now runs only for a quarantined ring.
* Listening sockets are outside the models (`MultiListener.tla` header item 2). The shared-socket rule (a ring never
  shuts down or closes the socket it was lent; the multi-ring listener stops it after every ring has unbound, and on
  disposal) is covered by `MultiRingPortSharingTests` only.

## Benchmarking

* All measurements were taken on a 2 vCPU shared VM with high run-to-run variance; see `BENCHMARKS.md` for what was
  and was not concluded. Multi-ring scaling, TLS and HTTP/2 were not measured.
