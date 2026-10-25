# Known issues

Findings from the cross-verified reviews of the `feature/pts-readiness` work that are **not fixed**, so nobody has to
rediscover them. Fixed items are in the commit history; this list is only what remains. "Reproduced" means a test or
probe showed it; otherwise it was established by two independent source reviews.

## Shutdown and teardown

* **Ring close is not a kernel quiescence barrier (NEW-01).** `DisposeAsync` closes the ring and then releases send
  pins, buffers and the fixed-file table, but the kernel may still be completing an in-flight operation that touches
  them. The TLA model has a `kernelTeardownDone` step that production never observes. Impact: a rare use-after-unpin
  window at disposal under load; not reproduced.
* **Late accepted sockets can escape the final completion snapshot (NEW-02 residual).** Unbind now cancels the accept
  request on the IO loop and a dead loop shuts the listening socket down, which narrows the window, but a connection
  already dequeued by the kernel can still publish its descriptor after the bounded drain at disposal. Closing the ring
  does not close that descriptor. Impact: a leaked descriptor in a rare shutdown race.
* **Deferred recv EOF is not observable while the input pipe is paused (parity, not a regression).** A peer FIN that
  arrives behind unread, backpressured data is delivered after that data. While the application is not reading, neither
  `ConnectionClosed` nor the end of input is signalled; stock `SocketTransport` behaves the same (its receive loop
  awaits the paused flush before it can read the FIN).

## Multi-ring (`ThreadCount > 1`)

* **Accidental port sharing.** Multi-ring listeners use `SO_REUSEPORT`; a second process doing the same binds the same
  port without error and splits the traffic. The default single-ring listener fails with "address already in use".
* **Merged accept channel (NEW-09).** It never completes when every worker finishes on its own; the application only
  sees the end of `AcceptAsync` after an explicit unbind.
* **Per-worker connection quota (H-06).** `MaxConnections` is divided across workers, so one worker can refuse
  connections while another has capacity.

## Receive path

* **Pending-data allowance.** The per-connection cap on receive data queued behind a paused pipe is
  `max(8 MiB, CQ entries x receive buffer size)`; with the defaults and 16,384 connections that is an allowance of
  about 128 GiB in total. It is a runaway guard for completions already in flight, not memory-DoS protection, which
  would need an aggregate budget.
* **Kernels older than 6.0** use single-shot receive by design (5.10-5.19), and kernels older than 5.10 or rings that do
  not implement the transport's opcodes (checked with `IORING_REGISTER_PROBE`) use SocketTransport. The single-shot
  path is not covered by CI; only Linux 6.17 has been tested. The version floor is a heuristic (it exists because the
  non-blocking eventfd `READ` used for wake-ups does not wait correctly on 5.6-5.7); the probe is the real check.
* **16-bit generation wrap (NEW-10).** A delayed abort from a connection whose slot has since been reused 65,536 times
  could in theory hit the new occupant; control queues should carry the connection object, not slot and generation.

## Smaller items

* `SINGLE_ISSUER` (opt-in) binds the constructing thread (NEW-05): submissions from the IO thread then fail with
  `EEXIST` and `Ring.Enter` retries forever. Do not enable it until the ring is created on the IO thread.
* A failed fixed-file unregister leaves the kernel's file reference until the ring is destroyed and blocks a retry
  (NEW-14; only with `EnableRegisteredFiles`).
* The `recvRetryQ` diagnostic gauge is a shared, stale high-water mark, not live occupancy (NEW-13).
* The accept channel is bounded (`AcceptQueueCapacity`); a burst larger than the queue plus kernel backlog aborts the
  excess accepted connections (H-03). Not measured at benchmark scale.
* `SocketTransportOptions` (for example `NoDelay`, backlog) are not honoured by the io_uring listener (H-08).
* The SQPOLL submission path relies on `Monitor.Exit` for its store-to-flag ordering instead of an explicit fence
  (H-09, hardening only).
* Retry/cancel/send-retry passes run after each completion batch; sustained completion traffic delays them by up to
  one bounded batch (NEW-10b).

## Formal models

* `specs/tla/MultiListener.tla` still abstracts the pre-repair worker unbind (it completes only once the worker's
  live connections are gone). Its ownership claims do not depend on that timing, and the unbind-alone contract is
  checked in `Listener.tla` (`UnbindAloneKeepsLoopRunning`) and by the shutdown tests, but the multi-listener module
  should be re-modelled with unbind separated from disposal (header item 7 of the file).
* The retry arm budget and FIFO order are tested (`ListenerRecvRetryBudgetTests`) but only abstracted in the model.

## Benchmarking

* All measurements were taken on a 2 vCPU shared VM with high run-to-run variance; see `BENCHMARKS.md` for what was
  and was not concluded. Multi-ring scaling, TLS and HTTP/2 were not measured.
