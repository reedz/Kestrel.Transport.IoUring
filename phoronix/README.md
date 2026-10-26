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
| `connections` | `1` `20` `100` `200` `500` `1000` `4000` (any integer from 1 to 999999 works with `run-local.sh`) |
| `runtime` | `net10` (10.0.402), `net11` (11.0.100-rc.2.26504.105) |
| `rings` | `1`, `auto` (one io_uring ring/event-loop thread per CPU core). Ignored by `sockets` |
| `duration` | seconds per `wrk` run: `90` (default), `30`, `10` (smoke test). Recorded in the result identity |

Result: **Requests Per Second** (higher is better), parsed from `wrk`'s `Requests/sec`. Default: 3 runs.

Guards that make a run fail instead of producing a misleading number:

* **Transport proof.** `pre.sh` counts `io_uring` file descriptors of the server process in `/proc`: io_uring
  requires at least one, sockets requires zero. The server itself refuses to start as io_uring when it cannot
  serve with a ring (`RequireIoUring`), so a silent fallback to sockets (for example under Docker's default
  seccomp profile) produces no result.
* **Endpoint ownership (sampled).** The port must be free before the server starts, the server binds `127.0.0.1`
  only, and before and after every measurement the profile asks `/__transport` and requires the answering process
  to be the one it started, with the requested runtime and transport. These are samples, not exclusivity: another
  same-user listener that joins afterwards through `SO_REUSEPORT` (multi-ring io_uring unless
  `EnableReusePort=false`, or any server that sets it)
  is not excluded. `run-local.sh --no-egress` runs in a private network namespace, where nothing else can bind
  the port, and is the way to get exclusivity.
* **Workload sanity.** One request must return HTTP 200 with the expected size (118047 bytes for `test.html`, 13
  for `plaintext`, `application/json` for `json`) before warm-up.
* **Valid runs only.** A repetition fails on any `wrk` socket error or HTTP error response (`wrk` counts status
  above 399; the server never redirects), a dead server, or a transport/endpoint change. `wrk` exits 0 and still
  prints `Requests/sec` in those cases, so this check is what keeps such a run from becoming a number.
* **`wrk` thread rule.** `wrk` rejects fewer connections than threads and divides connections evenly across
  threads, so `-t` is the largest divisor of the connection count that is at most `min(cores, connections)`.
  (The nginx profile's `-t $NUM_CPU_CORES` makes `-c 1` fail on multi-core machines.)
* **Invalid runs are never silent.** PTS averages whatever repetitions survive, records an empty stub for a failed
  configuration, and does not say how many repetitions were dropped. Every invalid repetition and every
  configuration whose setup failed is therefore appended to `invalid-runs.log`, and the hooks fail closed: the
  setup hook latches "not completed" before doing anything, and each repetition starts with a failed status that
  only its last line clears, so a hook killed halfway cannot leave a passing number. After PTS finishes,
  `run-local.sh` prints the log and **exits non-zero** if it is not empty, if PTS failed, or if any requested
  configuration does not hold exactly `--runs` valid numbers in the stored result.
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
* Network access is needed only by `fetch-artifacts.sh` (plain `curl` to the vendors' download hosts) and by the
  package manager when PTS installs the build prerequisites (`build-essential`, `libssl-dev`; install them first to
  keep the rest offline). The profile's own install and run steps need no network: the build is offline (see
  below) and PTS runs with `NoNetworkCommunication=TRUE`. That is about this profile's scripts and PTS's
  own gated helpers, not a proof of silence; see [Not uploading](#not-uploading-what-is-and-is-not-guaranteed).

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
installed stale code. The bundle name therefore carries the commit, and `install.sh` verifies the SHA-256 of the
source bundle **and of every third-party archive** (digests generated from `downloads.xml.in`) and records the
commit it installed (`run-local.sh` warns when the installed commit differs from the last bundle). `run-local.sh`
itself calls `batch-run`, never `batch-benchmark`: the latter always invokes PTS's installer first, which can
reinstall (and run `apt`) in the middle of a measurement. It also refuses a `--name` that already exists: PTS
silently skips configurations already stored under a name, before any hook runs, so a failed rerun would otherwise
show the old numbers.

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
| `IOURING_FORCE_FALLBACK=1` | test hook: forces the "io_uring unavailable" path; the io_uring configurations must then fail while sockets ones still run |

`run-local.sh` refuses to run while PTS control variables that move files, load code, add shell prefixes or change
which repetitions count are set (for example `PTS_TEST_INSTALL_ROOT_PATH`, `PTS_DOWNLOAD_CACHE`, `TEST_EXEC_PREPEND`,
`SKIP_TESTS*`, `TOTAL_LOOP_*`); the list is in `runtime/check-pts-config.php`.

## Not uploading: what is and is not guaranteed

Done by the profile and its scripts:

* `phoronix/config/user-config.xml` turns off result upload, anonymous usage reporting, system-log upload, browser
  opening and batch prompts, loads no modules, and sets `NoNetworkCommunication=TRUE`. Because the install itself
  is offline, PTS never needs that flag off, so its OpenBenchmarking connectivity, GSID/account and index traffic
  never happens.
* The build is offline: `SourceLink` is not part of the PTS build, the `net10.0` reference packs that SDK 11 needs
  are linked from SDK 10 (which ships the same version), and `dotnet publish` restores from an empty local source
  with `NuGetAudit` off, so any attempt to reach nuget.org fails loudly instead of succeeding.
* **The configuration is checked as PTS will read it**, not by grepping: `runtime/check-pts-config.php` chooses the
  file the way PTS does (the POSIX account's home, not `$HOME`) and reads each value with the same traversal as
  PTS's own `getXMLValue` (first matching descendant, exactly one leaf), so a shadowing `<Options>` or a duplicated
  leaf that makes PTS ignore the real values is caught. It requires the four upload/usage keys `FALSE`,
  `NoNetworkCommunication=TRUE`, `NoInternetCommunication=FALSE` (PTS tests the latter first, with an `else if`, so
  `TRUE` there would leave network helpers enabled), `RunAllTestCombinations=FALSE` (otherwise PTS ignores the
  launcher's option values and runs every menu combination), empty `AutoLoadModules` and the standard directories
  (compared exactly, without trimming). `PTS_USER_PATH_OVERRIDE` is refused outright: PTS expands `~/` in the
  directory settings from the account home, never from the override, so it would split the configuration from the
  installation and results, and a relative value is resolved only after PTS changes directory. This matters
  because PTS silently falls back to defaults that **enable** uploads whenever it cannot read a value; a double
  hyphen inside an XML comment (`<!-- ... --offline ... -->`) is malformed XML and triggered exactly that during
  development. Plain, well-formed comments are fine.
* `run-local.sh` and every `pre.sh` refuse to proceed unless that check passes (the setup hook runs under the
  environment PTS gives it, so a variable set only for the hook is checked too). `run-local.sh` additionally sets
  `PTS_IGNORE_MODULES=1` (modules such as Pushover or e-mail export use direct cURL/SMTP regardless of
  `NoNetworkCommunication`), refuses to run a profile that is not installed, and only ever calls `batch-run`.
  Nothing in this directory calls `upload-result` or `upload-test-profile`.
* The profile's hook scripts (`pre.sh`, `run-test.sh`, `post.sh`) make no requests except to `127.0.0.1`, and those
  `curl` calls ignore `~/.curlrc` and proxy variables. `fetch-artifacts.sh` is the one script that deliberately
  contacts remote hosts.

Not guaranteed (these are PTS settings and ordinary processes, not an OS sandbox):

* **PTS has network paths that `NoNetworkCommunication` does not gate.** Examples verified in its source: on
  Google Compute Engine, hardware detection requests the metadata server directly, and that happens on every PTS
  start including `batch-run`; PTS modules (also when selected through the environment by a bare
  `phoronix-test-suite` command rather than by `run-local.sh`) can use raw cURL or mail; its dependency installer
  runs `apt`/`apt-file`, which have their own network access; a remote `PTS_DOWNLOAD_CACHE` is fetched without that
  check (refused here). Only a Google Cloud host is affected by the first, and `run-local.sh` never installs
  dependencies or enables modules. The .NET SDK's own network behaviour is switched off (telemetry, workload update
  notices, offline restore) but that is not a proof of silence either. For a zero-IP-egress assurance use the
  namespace mode below.
* PTS's raw option strings are interpreted by a shell before this profile can validate them. `run-local.sh`
  validates its own inputs and refuses to run while variables such as `TEST_EXEC_PREPEND` or `PTS_DOWNLOAD_CACHE`
  are set; it does not refuse `PRESET_OPTIONS`, it unsets it (the launcher's `PRESET_OPTIONS_VALUES` covers every
  option), so a hand-written `PRESET_OPTIONS` given to a bare `phoronix-test-suite` command remains trusted input.
* **A hook killed with SIGKILL runs no `EXIT` trap** (PTS's own process-killing helper uses SIGKILL), so the invalid
  repetition may not reach the log. It is still never counted: the repetition status starts as failed and the setup
  latch starts as incomplete, and the stored-result comparison at the end catches anything that is missing.
* **A guarantee about IP traffic that does not depend on any setting** is to run with only loopback available:

  ```bash
  phoronix/run-local.sh --no-egress --duration 10 --runs 1 --connections 100     # uses sudo + unshare -n
  ```

  `--no-egress` creates a fresh network namespace every time (it does not trust an inherited marker), verifies it has
  only the loopback interface, drops back to your user inside it and forwards only an allowlist of `PTS_WARMUP`,
  `PTS_KESTREL_PORT`, `IOURING_*` and `DOTNET_*` variables (echoed, because `sudo` would
  otherwise silently drop them). This was tested: the host reached the internet, the namespace did not, and both the
  install (run with `unshare -n`) and measured matrices completed. A network namespace isolates IP networking only;
  it does not confine the filesystem or host IPC such as Unix sockets.
* The scripts write under `~/.phoronix-test-suite` and, for `make-bundle.sh`, under `phoronix/dist` and a temporary
  directory; PTS itself also writes temporary lock files outside its directory.

## Methodology notes and caveats

* The development machine had 2 vCPUs, shared by `wrk`, the server and the harness. It validates correctness
  and the PTS plumbing, **not** multi-ring scaling or absolute numbers. Run the real comparison on a machine with at
  least 8 cores; give `wrk` and the server separate CPU sets if you can (`taskset`).
* The full matrix is 2 transports x 3 workloads x 7 connection counts x 2 runtimes x 2 ring settings = 168
  configurations of 3 x 90 s plus warm-up (the 90 s duration; it is also an option), roughly 14 hours of load.
  `run-local.sh` without list options runs only 2 (both transports, Plaintext, 100 connections); pick subsets with it.
  `sockets` ignores `rings`, so those configurations are duplicates under both ring settings.
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
