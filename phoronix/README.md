# Phoronix Test Suite profile: ASP.NET Core Kestrel, io_uring vs Sockets

A **local, unpublished** PTS test profile (`local/aspnet-kestrel-iouring`) that compares ASP.NET Core's Kestrel
running on the stock epoll **Sockets** transport against this repository's **io_uring** transport, driven by `wrk`.

Nothing here is published. The profile is never uploaded to OpenBenchmarking.org, results are never uploaded, and no
NuGet package is pushed. See [Not uploading](#not-uploading-what-is-and-is-not-guaranteed) for the guarantees and
their limits, and [Publishing](#publishing-not-done) for what publishing would involve.

## What is measured

| Option (`<Value>` you pass) | Values |
|---|---|
| `transport` | `iouring`, `sockets` |
| `workload` | `test.html` (118 KB static file, the nginx/apache profiles' file), `plaintext` (13 bytes), `json` (serialized per request) |
| `connections` | `1` `20` `100` `200` `500` `1000` `4000` (any positive integer works with `run-local.sh`) |
| `runtime` | `net10` (10.0.402), `net11` (11.0.100-rc.2.26504.105) |
| `rings` | `1`, `auto` (one io_uring ring/event-loop thread per CPU core). Ignored by `sockets` |
| `duration` | seconds per `wrk` run: `90` (default), `30`, `10` (smoke test). Recorded in the result identity |

Result: **Requests Per Second** (higher is better), parsed from `wrk`'s `Requests/sec`. Default: 3 runs.

Guards that make a run fail instead of producing a misleading number:

* **Transport proof.** `pre.sh` counts `io_uring` file descriptors of the server process in `/proc`: io_uring
  requires at least one, sockets requires zero. The server itself refuses to start as io_uring when it cannot
  serve with a ring (`RequireIoUring`), so a silent fallback to sockets (for example under Docker's default
  seccomp profile) produces no result.
* **Endpoint ownership.** The port must be free before the server starts, the server binds `127.0.0.1` only, and
  before and after every measurement the profile asks `/__transport` and requires the answering process to be the
  one it started, with the requested runtime and transport. (A same-user io_uring server could otherwise share the
  port through `SO_REUSEPORT` and contribute responses.)
* **Workload sanity.** One request must return HTTP 200 with the expected size (118047 bytes for `test.html`, 13
  for `plaintext`, `application/json` for `json`) before warm-up.
* **Valid runs only.** A repetition fails on any `wrk` socket error or HTTP error response (`wrk` counts status
  above 399; the server never redirects), a dead server, or a transport/endpoint change. `wrk` exits 0 and still
  prints `Requests/sec` in those cases, so this check is what keeps such a run from becoming a number.
* **`wrk` thread rule.** `wrk` rejects fewer connections than threads and divides connections evenly across
  threads, so `-t` is the largest divisor of the connection count that is at most `min(cores, connections)`.
  (The nginx profile's `-t $NUM_CPU_CORES` makes `-c 1` fail on multi-core machines.)
* **Invalid runs are never silent.** PTS averages whatever repetitions survive and does not say how many were
  dropped, so every invalid repetition and every configuration whose setup failed is appended to
  `invalid-runs.log`; `run-local.sh` prints it and **exits non-zero** if it is not empty.
* **Cleanup by PID.** The server is stopped by its recorded PID and process start time (so a recycled PID is never
  signalled), never by name. `run-local.sh` also stops it when interrupted.
* The result footnote records the actual transport, ring count, runtime build, kernel, open-file limit, `wrk`
  threads, warm-up, the bundled source commit and any `DOTNET_*`/`IOURING_*` overrides.

## Requirements

* Linux x86_64. Kernel **6.0 or newer** with io_uring enabled (`kernel.io_uring_disabled=0`) and not blocked by a
  container's seccomp profile (Docker's default profile blocks it). Validated on 6.17.
* `sudo` for the build prerequisites PTS installs (`build-essential autoconf libssl-dev`), `curl`, and PHP for PTS:
  `sudo apt install php-cli php-xml php-zip php-curl curl`.
* About 2.6 GB under `~/.phoronix-test-suite/installed-tests` (two SDKs and two published servers) plus about
  0.5 GB of cached downloads.
* PTS itself: tested with PTS master `be12163` (v10.8.6), <https://github.com/phoronix-test-suite/phoronix-test-suite>.
  The commands below assume `phoronix-test-suite` is on `PATH` or exported as `PTS=/path/to/phoronix-test-suite`.
* Network access only for `fetch-artifacts.sh` (plain `curl` to the vendors' download hosts). PTS, the install and
  every measurement run with **no network at all**: the build is offline (see below).

## Quick start

```bash
cd Kestrel.Transport.IoUring              # commit your changes first: the bundle is built from a commit
phoronix/fetch-artifacts.sh               # once: SDKs, wrk, test files -> PTS download cache (SHA-256 verified)
phoronix/make-bundle.sh                   # -> phoronix/dist (source bundle from `git archive`, generated profile)
phoronix/setup-local-pts.sh               # installs the profile and the no-upload, no-network PTS configuration
phoronix-test-suite force-install local/aspnet-kestrel-iouring
phoronix/run-local.sh --duration 10 --runs 1 --connections 100 --rings 1      # smoke test, ~1.5 minutes
```

A real comparison, every combination of the lists you give (comma separated):

```bash
phoronix/run-local.sh --transport iouring,sockets --workload plaintext,json,test.html \
                      --connections 20,200,1000 --runtime net10 --rings auto --runs 3 --duration 90 --name kestrel-a-b
phoronix-test-suite result-file-to-text kestrel-a-b       # stored in ~/.phoronix-test-suite/test-results/kestrel-a-b
```

After every code change: commit, `make-bundle.sh`, `setup-local-pts.sh`, `force-install`. Use `force-install`:
PTS keeps a download that already exists in an install directory without re-hashing it, which once silently
installed stale code. The bundle name therefore carries the commit, and `install.sh` verifies its SHA-256 and
records the commit it installed (`run-local.sh` warns when the installed commit differs from the last bundle).

`run-local.sh` takes every option by **value** and validates it. Do not drive the profile with PTS's own
`PRESET_OPTIONS`: it matches menu *positions* first (so `connections=1` selects the second entry, `20`; `rings=1`
selects `auto`), treats a number equal to the number of entries as "test all", and silently falls back to the first
entry on a typo. `PRESET_OPTIONS_VALUES` (used by `run-local.sh`) passes values through and the profile rejects
values it does not know. Results show raw values (`iouring`, `net10`) for that reason.

### Environment knobs

| Variable | Meaning |
|---|---|
| `PTS_WARMUP` | warm-up seconds before the first run of a configuration (default 8; shown in the footnote) |
| `PTS_KESTREL_PORT` | server port (default 18099; must be free) |
| `PTS_SKIP_NET11=1` | at install time, skip the .NET 11 SDK and publish |
| `IOURING_FORCE_FALLBACK=1` | test hook: forces the "io_uring unavailable" path; the io_uring configurations must then fail while sockets ones still run |

## Not uploading: what is and is not guaranteed

Done by the profile and its scripts:

* `phoronix/config/user-config.xml` turns off result upload, anonymous usage reporting, system-log upload, browser
  opening and batch prompts, loads no modules, and sets `NoNetworkCommunication=TRUE`. Because the install itself
  is offline, PTS never needs that flag off, so its OpenBenchmarking connectivity, GSID/account and index traffic
  never happens.
* The build is offline: `SourceLink` is not part of the PTS build, the `net10.0` reference packs that SDK 11 needs
  are linked from SDK 10 (which ships the same version), and `dotnet publish` restores from an empty local source
  with `NuGetAudit` off, so any attempt to reach nuget.org fails loudly instead of succeeding.
* **The configuration is checked as PTS will read it**, not by grepping: `runtime/check-pts-config.php` parses
  `user-config.xml` as XML, chooses the file the way PTS does (`PTS_USER_PATH_OVERRIDE` is appended literally
  without a slash, otherwise the POSIX account's home, not `$HOME`), and requires every privacy key to appear
  exactly once with the exact value. This matters because PTS silently falls back to defaults that **enable**
  uploads when it cannot parse the file; a double hyphen inside an XML comment (`<!-- ... --offline ... -->`) is
  malformed XML and triggered exactly that during development. Plain, well-formed comments are fine.
* `run-local.sh` and every `pre.sh` refuse to proceed unless that check passes. `run-local.sh` additionally sets
  `PTS_IGNORE_MODULES=1` (modules such as Pushover or e-mail export use direct cURL/SMTP regardless of
  `NoNetworkCommunication`), refuses to run a profile that is not installed (so `batch-benchmark` cannot start an
  install on its own), and only ever calls `batch-benchmark`. Nothing in this directory calls `upload-result` or
  `upload-test-profile`.

Not guaranteed (these are PTS settings, not an OS sandbox):

* PTS's own dependency installation (`apt`) and the profile's shell scripts are ordinary processes; the profile's
  scripts make no requests except to `127.0.0.1`.
* For a guarantee that does not depend on any setting, run the measured phase with only loopback available:

  ```bash
  phoronix/run-local.sh --no-egress --duration 10 --runs 1 --connections 100     # uses sudo + unshare -n
  ```

  This was tested: the host reached the internet, the namespace did not, and the full matrix (including the
  install, run the same way with `unshare -n`) completed.

## Methodology notes and caveats

* The development machine had 2 vCPUs, shared by `wrk`, the server and the harness. It validates correctness
  and the PTS plumbing, **not** multi-ring scaling or absolute numbers. Run the real comparison on a machine with at
  least 8 cores; give `wrk` and the server separate CPU sets if you can (`taskset`).
* The full default matrix is 2 transports x 3 workloads x 7 connection counts x 2 runtimes x 2 ring settings =
  168 configurations of 3 x 90 s plus warm-up, roughly 14 hours of load. Pick subsets with `run-local.sh`.
* Compare like with like: the same runtime, and `rings` set explicitly for io_uring. Sockets ignores `rings`.
* .NET 11 is a release candidate (RC2). Stock .NET 11 has no io_uring in its socket layer; see
  [`docs/DOTNET11.md`](../docs/DOTNET11.md). The library's `net10.0` asset is what a .NET 11 app consumes.
* `wrk` response contents are not validated per request; the profile checks one response per configuration.

## Troubleshooting

* *"... cannot serve ..." / "server did not become ready"* in the invalid-runs report: io_uring is disabled or
  blocked (container seccomp, `kernel.io_uring_disabled`, kernel too old). The server log is
  `~/.phoronix-test-suite/installed-tests/local/aspnet-kestrel-iouring/server-last.log`.
* *"open-file limit ... too low"*: 500 or more connections need a hard `nofile` limit of at least
  `2 x connections + 64`.
* *"port ... already has a listener"*: stop whatever holds it or set `PTS_KESTREL_PORT`.
* *"PTS configuration is not local-only"*: run `phoronix/setup-local-pts.sh`; `setup-local-pts.sh --verify` only
  checks.
* A PTS `pre-test-exit-status` failure would leave PTS's run lock held (PTS 10.8 never releases it on that path)
  and silently skip every remaining configuration, so the profile latches setup failures and reports them through
  the invalid-runs log instead.
* `phoronix-test-suite validate-test-profile` rewrites the profile files, so run it on a scratch copy. Its parser
  also misaligns packages that come after the URL-less source-bundle package, which is why that package is last in
  `downloads.xml.in`.

## Publishing (not done)

Publishing is a separate, deliberate decision and none of it has been performed. It would involve at least:
explicit approval; an OpenBenchmarking.org account and `upload-test-profile` (which validates, rewrites and
uploads a ZIP of the profile; `Private` metadata is not an access control); replacing the cache-only source package
with a public, immutable, versioned source release with a hash (a profile that fetches a private bundle cannot be
installed by anyone else) and reviewing redistribution rights of everything it fetches; pinning and re-hashing
every third-party download; separating the private no-upload configuration from the public profile; clean-machine
install tests and the full/negative matrix on documented hardware; and a separate decision on result sharing
(uploading results can attach saved system logs even with `AlwaysUploadSystemLogs=FALSE`). Until then keep
`AllowResultUploadsToOpenBenchmarking` off and do not run `upload-result`.
