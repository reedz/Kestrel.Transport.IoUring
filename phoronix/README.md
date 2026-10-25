# Phoronix Test Suite profile: ASP.NET Core Kestrel, io_uring vs Sockets

A **local, unpublished** PTS test profile (`local/aspnet-kestrel-iouring`) that compares ASP.NET Core's Kestrel
running on the stock epoll **Sockets** transport against this repository's **io_uring** transport, driven by `wrk`.

Nothing here is published. The profile is never uploaded to OpenBenchmarking.org, no results are uploaded, and no
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

Result: **Requests Per Second** (higher is better), parsed from `wrk`'s `Requests/sec`. Defaults: 90 s per run, 3 runs.

Guards that make a run fail instead of producing a misleading number:

* **Transport proof.** `pre.sh` counts `io_uring` file descriptors of the server process in `/proc`. `io_uring`
  requires at least one, `sockets` requires zero. The server itself refuses to start as io_uring when it cannot
  serve with a ring (`IOURING_REQUIRE`, exit 3), so a silent fallback to sockets (for example under Docker's
  default seccomp profile) aborts the configuration with no result.
* **Valid runs only.** The wrapper fails a repetition on any `wrk` socket error or non-2xx response, a dead
  server, or a transport change. `wrk` exits 0 and still prints `Requests/sec` in those cases, so this check is
  what keeps such a run from becoming a number.
* **`wrk` thread rule.** `wrk` rejects fewer connections than threads and divides connections evenly across
  threads, so `-t` is the largest divisor of the connection count that is at most `min(cores, connections)`.
  (The nginx profile's `-t $NUM_CPU_CORES` makes `-c 1` fail on multi-core machines.)
* **Cleanup by PID.** The server is stopped by its recorded PID (guarded against PID reuse), never by name.
* The run's footnote records the real transport, ring count, runtime build, kernel and open-file limit.

## Requirements

* Linux x86_64. Kernel **6.0 or newer** with io_uring enabled (`kernel.io_uring_disabled=0`) and not blocked by a
  container's seccomp profile (Docker's default profile blocks it). Validated on 6.17.
* `sudo` (PTS installs `build-essential autoconf libssl-dev` and its own helper packages with the system package
  manager) and PHP for PTS: `sudo apt install php-cli php-xml php-zip php-curl`.
* About 2.6 GB under `~/.phoronix-test-suite/installed-tests` (two SDKs and two published servers) plus about
  0.5 GB of cached downloads.
* Network access **only for `install`** (SDK/wrk downloads unless pre-cached, plus the NuGet restore that
  `dotnet publish` performs). Measurements need none.
* PTS itself: tested with PTS master `be12163` (v10.8.6). Get it from <https://github.com/phoronix-test-suite/phoronix-test-suite>;
  the commands below assume `phoronix-test-suite` is on `PATH` or exported as `PTS=/path/to/phoronix-test-suite`.

## Quick start

```bash
cd Kestrel.Transport.IoUring            # commit your changes first: the bundle is built from a commit
phoronix/make-bundle.sh                 # -> phoronix/dist (source bundle from `git archive`, profile with checksums)
phoronix/setup-local-pts.sh             # installs the profile and a no-upload PTS config into ~/.phoronix-test-suite
phoronix-test-suite install local/aspnet-kestrel-iouring
phoronix/setup-local-pts.sh --offline   # NoNetworkCommunication=TRUE, and verifies the config again
```

Smoke test (about 30 s per configuration):

```bash
phoronix/run-local.sh --duration 10 --runs 1 --connections 100 --rings 1
```

A real comparison, every combination of the lists you give (comma separated):

```bash
phoronix/run-local.sh --transport iouring,sockets --workload plaintext,json,test.html \
                      --connections 20,200,1000 --runtime net10 --rings auto --runs 3 --duration 90 --name kestrel-a-b
phoronix-test-suite result-file-to-text kestrel-a-b      # results live in ~/.phoronix-test-suite/test-results/kestrel-a-b
```

`run-local.sh` takes every option by **value** and validates it. Do not drive the profile with PTS's own
`PRESET_OPTIONS` unless you know its rules: it matches menu *positions* first (so `connections=1` selects the
second entry, `20`), treats a number equal to the number of entries as "test all", and silently falls back to the
first entry on a typo. `PRESET_OPTIONS_VALUES` (used by `run-local.sh`) passes the value straight through and the
profile rejects values it does not know.

Pre-caching the third-party downloads (optional): `setup-local-pts.sh --artifacts DIR` copies the files named in
`downloads.xml` (SDK tarballs, `wrk-4.2.0.tar.gz`, `http-test-files-1.tar.xz`) into PTS's download cache; PTS
verifies each against the SHA-256 in `downloads.xml`.

### Environment knobs

| Variable | Meaning |
|---|---|
| `PTS_DURATION` / `--duration` | seconds per `wrk` run (default 90) |
| `PTS_WARMUP` | warm-up seconds before the first run of a configuration (default 8) |
| `PTS_KESTREL_PORT` | server port (default 18099) |
| `PTS_ALLOW_ERRORS=1` | keep runs that have `wrk` socket errors or non-2xx responses (off by default) |
| `PTS_KESTREL_ALLOW_UPLOAD_CONFIG=1` | skip the refusal to run under an upload-enabled PTS config (do not) |
| `PTS_SKIP_NET11=1` | at install time, skip the .NET 11 SDK and publish |
| `IOURING_FORCE_FALLBACK=1` | test hook: forces the "io_uring unavailable" path; the io_uring configuration must then abort with no result |

## Not uploading: what is and is not guaranteed

Done by the profile and its scripts:

* `phoronix/config/user-config.xml` turns off result upload, anonymous usage reporting, system-log upload, browser
  opening and batch prompts, and sets exact repetition counts. `setup-local-pts.sh` installs it and `--verify`
  re-checks the file PTS actually reads.
* **The config file must stay free of XML comments.** PTS silently regenerates default settings, with uploads
  enabled, when it cannot parse the file. A comment did exactly that during validation.
* `pre.sh` refuses to start any configuration while the effective PTS config allows uploads, usage reporting or
  log uploads, and `run-local.sh` refuses to start PTS at all in that case.
* `run-local.sh` only ever calls `batch-benchmark`; nothing in this directory calls `upload-result` or
  `upload-test-profile`. The profile's own scripts make no network requests while measuring (the server listens on
  `127.0.0.1`).

Not guaranteed (these are PTS settings, not an OS sandbox):

* Install needs the network. After install, `setup-local-pts.sh --offline` sets `NoNetworkCommunication=TRUE`.
* For a literal guarantee, run the measured phase with only loopback available. This works and was tested:

  ```bash
  PTS=/path/to/phoronix-test-suite
  sudo unshare -n bash -c "ip link set lo up && exec sudo -u $USER -H env PATH=$PATH PTS=$PTS \
      $PWD/phoronix/run-local.sh --runs 1 --duration 10 --name noegress"
  ```

## Methodology notes and caveats

* The development machine had 2 vCPUs, shared by `wrk`, the server and the harness. It validates correctness
  and the PTS plumbing, **not** multi-ring scaling or absolute numbers. Run the real comparison on a machine with at
  least 8 cores; give `wrk` and the server separate CPU sets if you can (`taskset`).
* The full default matrix is 2 transports x 3 workloads x 7 connection counts x 2 runtimes x 2 ring settings =
  168 configurations of 3 x 90 s plus warm-up, roughly 14 hours of load. Pick subsets with `run-local.sh`.
* Compare like with like: the same runtime, and `rings` set explicitly for io_uring. Sockets ignores `rings`.
* .NET 11 is a release candidate (RC2). Stock .NET 11 has no io_uring in its socket layer; see
  [`docs/DOTNET11.md`](../docs/DOTNET11.md). The library's `net10.0` asset is what a .NET 11 app consumes.
* `/proc` evidence of io_uring is per process. Do not run two servers on the same port.

## Troubleshooting

* *"io_uring transport is required ... cannot serve"* in the pre-test output: io_uring is disabled or blocked
  (container seccomp, `kernel.io_uring_disabled`, kernel too old). The log is in
  `~/.phoronix-test-suite/installed-tests/local/aspnet-kestrel-iouring/server-last.log`.
* *"open-file limit ... too low"*: 500 or more connections need a hard `nofile` limit of at least
  `2 x connections + 64`.
* *Port in use*: set `PTS_KESTREL_PORT`.
* *Results are not what you asked for*: you used `PRESET_OPTIONS`; use `run-local.sh`.
* `phoronix-test-suite validate-test-profile` rewrites the profile files, so run it on a scratch copy. Its parser
  also misaligns packages that come after the URL-less source-bundle package, which is why that package is last in
  `downloads.xml.in`.

## Publishing (not done)

Publishing is a separate, deliberate decision and none of it has been performed. It would involve at least:
an OpenBenchmarking.org account and `upload-test-profile`; replacing the cache-only source package with a public,
versioned source release (a profile that fetches a private bundle cannot be installed by anyone else); pinning and
re-hashing every third-party download; dropping the local-only config assumptions; running the full matrix on
documented hardware; and deciding whether results may be uploaded. Until then keep `AllowResultUploadsToOpenBenchmarking`
off and do not run `upload-result`.
