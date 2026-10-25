#!/bin/bash
# Installs everything the profile needs into the PTS install directory ($HOME == install dir):
#   pts-source/      the Kestrel.Transport.IoUring source bundle (server app + runtime scripts)
#   dotnet10/ dotnet11/   user-space SDKs (no system .NET required, nothing outside this directory is touched)
#   wrk-4.2.0/       load generator (same version as the nginx/apache profiles)
#   wwwroot/         test.html from the PTS http-test-files package
#   publish/net10, publish/net11   the ASP.NET Core server built for each runtime
#
# Set PTS_SKIP_NET11=1 to skip the .NET 11 SDK/publish (the ".NET 11 RC2" option then fails fast in pre.sh).
# dotnet publish restores a few NuGet packages (for example SourceLink), so the install needs nuget.org access.

fail() {
    echo "ERROR: $*"
    echo 1 > ~/install-exit-status
    exit 1
}

cd ~ || fail "cannot cd to the install directory"

SRC_BUNDLE="aspnet-kestrel-iouring-src.tar.gz"
SDK10="dotnet-sdk-10.0.402-linux-x64.tar.gz"
SDK11="dotnet-sdk-11.0.100-rc.2.26504.105-linux-x64.tar.gz"

for f in "$SRC_BUNDLE" "$SDK10" wrk-4.2.0.tar.gz http-test-files-1.tar.xz; do
    [ -f "$f" ] || fail "missing download $f"
done

# 1. Source bundle (also carries the runtime scripts used by pre.sh, post.sh and the run wrapper).
rm -rf pts-source && mkdir pts-source || fail "cannot create pts-source"
tar -xzf "$SRC_BUNDLE" -C pts-source || fail "cannot unpack $SRC_BUNDLE"

# 2. SDKs.
rm -rf dotnet10 && mkdir dotnet10 && tar -xzf "$SDK10" -C dotnet10 || fail "cannot unpack $SDK10"
if [ "${PTS_SKIP_NET11:-0}" != "1" ]; then
    [ -f "$SDK11" ] || fail "missing download $SDK11"
    rm -rf dotnet11 && mkdir dotnet11 && tar -xzf "$SDK11" -C dotnet11 || fail "cannot unpack $SDK11"
fi

# 3. wrk.
rm -rf wrk-4.2.0 && tar -xzf wrk-4.2.0.tar.gz || fail "cannot unpack wrk"
( cd wrk-4.2.0 && make -j "${NUM_CPU_CORES:-2}" ) || fail "wrk build failed"
[ -x wrk-4.2.0/wrk ] || fail "wrk binary missing after build"

# 4. Static file served by the 'Static File' workload.
rm -rf http-test-files wwwroot && tar -xJf http-test-files-1.tar.xz || fail "cannot unpack test files"
mkdir wwwroot && cp http-test-files/test.html wwwroot/ || fail "test.html missing"
rm -rf http-test-files

# 5. Build the server once per runtime.
export DOTNET_CLI_HOME="$HOME" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export NUGET_PACKAGES="$HOME/.nuget/packages" MSBUILDDISABLENODEREUSE=1 DOTNET_gcServer=
PROJECT="pts-source/benchmarks/Kestrel.Transport.IoUring.PtsServer/Kestrel.Transport.IoUring.PtsServer.csproj"

publish() {
    local rt="$1"
    echo "Publishing PtsServer for .NET $rt"
    DOTNET_ROOT="$HOME/dotnet$rt" "$HOME/dotnet$rt/dotnet" publish "$PROJECT" -c Release \
        -p:PtsTargetFramework="net$rt.0" -p:PtsLibraryTfm=net10.0 -o "publish/net$rt" \
        > "publish-net$rt.log" 2>&1 || { tail -n 30 "publish-net$rt.log"; fail "dotnet publish for net$rt failed"; }
    [ -f "publish/net$rt/PtsServer.dll" ] || fail "publish/net$rt/PtsServer.dll missing"
}

rm -rf publish
publish 10
[ "${PTS_SKIP_NET11:-0}" = "1" ] || publish 11

# 6. Run wrapper (PTS runs ./aspnet-kestrel-iouring <options> for every repetition).
cat > aspnet-kestrel-iouring <<'EOF'
#!/bin/sh
exec bash "$HOME/pts-source/phoronix/runtime/run-test.sh" "$@"
EOF
chmod +x aspnet-kestrel-iouring

echo 0 > ~/install-exit-status
