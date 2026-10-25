# Does .NET 11 ship io_uring? (No.)

**Short answer:** .NET 11 (through RC2, 2026-10-13; GA planned 2026-11-10) does **not** include or enable io_uring
anywhere — not in `System.Net.Sockets`, not in Kestrel, not behind an opt-in switch. Kestrel's default
`SocketTransport` on .NET 11 is epoll-based. Comparing `Kestrel.Transport.IoUring` against stock .NET 11 therefore
compares io_uring against epoll; there is no hidden io_uring path to turn off.

Verified on 2026-10-25 against primary sources (GitHub APIs, source scans, release notes) and with a local runtime test.

## 1. Upstream status (primary sources)

| Item | Status | Source |
|---|---|---|
| Ben Adams' "Use io_uring for sockets on Linux" | **Closed, not merged.** `merged:false`, closed 2026-04-24 by the stale-PR bot. On 2026-03-25 the author wrote it was "not beating epoll yet" after testing with @sebastienros. | [dotnet/runtime#124374](https://github.com/dotnet/runtime/pull/124374) |
| Perf-test request for that PR | Open, no results posted | [aspnet/Benchmarks#2153](https://github.com/aspnet/Benchmarks/issues/2153) |
| "Use io_uring instead of epoll when supported" | Open, milestone **Future** (uncommitted) | [dotnet/runtime#753](https://github.com/dotnet/runtime/issues/753) |
| io_uring in dotnet/runtime source | **0 matches** for `io_uring`, `IoUring`, `UseIoUring`, `IORING_` across all of `src/` on `main` (f0c7a08), `release/11.0` (bc123d0) and the RC2 tag (474ed96). Linux sockets still call `epoll_create1`. | `src/native/libs/System.Native/pal_networking.c` |
| io_uring in dotnet/aspnetcore | **0 matches** (`main`, `release/11.0`). Kestrel's transport uses plain `Socket`. | `src/Servers/Kestrel/Transport.Sockets/` |
| .NET 11 release notes (Preview 1 → RC2) and "What's new in .NET 11" | **0 mentions** across all of `dotnet/core` `release-notes/11.0/**` | [dotnet/core release notes](https://github.com/dotnet/core/tree/main/release-notes/11.0) |

The unmerged PR proposed `DOTNET_SYSTEM_NET_SOCKETS_IO_URING=1` and `System.Net.Sockets.UseIoUring`. **Those knobs do
not exist in any shipped build.** Secondary articles that say ".NET 11 uses io_uring" (including the Phoronix news item of
2026-02-17) describe that PR's *expected* gains, not a shipped feature.

Caveat: absence across *every historical commit and branch* of dotnet/runtime cannot be proven (GitHub commit search
covers only the default branch). The claim above is scoped to the shipping branches and tags listed.

## 2. Empirical check on the RC2 runtime

Environment: SDK/runtime `11.0.100-rc.2.26504.105` / `11.0.0-rc.2.26504.105`, Linux 6.17. A `net11.0` app consuming
the library's **net10.0** asset (no NuGet or SDK errors), run once with `UseIoUring()` and once without:

| Mode | `anon_inode:[io_uring]` fds | epoll fds | `io_uring_setup` syscalls | `io_uring_enter` syscalls | `epoll_create1` syscalls |
|---|---:|---:|---:|---:|---:|
| Stock SocketTransport (no `UseIoUring`) | **0** | 1 | **0** | **0** | 1 |
| `UseIoUring()` | 1 | 1 | 2 (support probe + real ring) | 116 | 1 |

Reproduce:

```bash
# Is any io_uring ring open in the server process?
ls -l /proc/<pid>/fd | grep -c io_uring

# Does the process ever call io_uring at all?
strace -f -qq -e trace=io_uring_setup,io_uring_enter,io_uring_register,epoll_create1 -o st.txt dotnet app.dll
grep -c io_uring st.txt
```

Use the same check in any benchmark harness to prove which transport actually served the traffic.
`IoUringTransportFactory.IsUsingIoUring` reports kernel *capability* only, so it is not proof of activation.

## 3. Using the library from .NET 11

A `net11.0` application can reference this package today: NuGet selects the `net10.0` asset, and the unversioned
`Microsoft.AspNetCore.App` framework reference resolves against the **application's** target framework. The library
therefore does not need a `net11.0` target. Adding one would only be useful for .NET 11-specific APIs and would require
the .NET 11 SDK for every build of the library.

Installing the RC2 SDK into user space (does not touch a system install):

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- \
  --version 11.0.100-rc.2.26504.105 --architecture x64 \
  --install-dir "$HOME/.dotnet-11" --no-path
export DOTNET_ROOT="$HOME/.dotnet-11" PATH="$HOME/.dotnet-11:$PATH"
```

The archive is ~201 MiB (~524 MiB unpacked; budget ~1.3 GiB while installing). It contains the shared ASP.NET Core
runtime and reference pack, but not the `Microsoft.AspNetCore.App.Runtime.linux-x64` pack needed for self-contained publish.
