#!/usr/bin/env bash
# End-to-end test of the hooking layer: cross-compiles host.c, publishes the harness plugin, and
# runs them together under Wine. The host calls three assembly sites before and after loading
# the plugin; the plugin redirects a call and a jmp into managed code, patches a byte, and makes
# one hook throw to prove the guard returns the fallback.
#
#   dotnet/wine-host/run.sh [workdir]        (default: <repo>/local/wine-host)
#
# Needs wine, mingw-w64, and the cross-publish toolchain (lld-link, xwin). Uses its own
# WINEPREFIX inside the workdir, so it never touches the default one.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
work=${1:-$repo/local/wine-host}

# Refuse to delete anything that is not this script's own output directory.
if [ -e "$work" ] && [ ! -e "$work/.ovs-wine-host" ]; then
	echo "$work exists and was not created by this script; pass a different workdir" >&2
	exit 2
fi
rm -rf "$work"; mkdir -p "$work"; touch "$work/.ovs-wine-host"
x86_64-w64-mingw32-gcc -O1 -o "$work/host.exe" "$here/host.c"
dotnet publish "$here/../OpenVersus.HookTest/OpenVersus.HookTest.csproj" -c Release -r win-x64 \
	-p:AcceptVSBuildToolsLicense=true --nologo -v quiet
cp "$here/../OpenVersus.HookTest/bin/Release/net10.0/win-x64/publish/OpenVersus.HookTest.asi" "$work/"

cd "$work"
export WINEPREFIX="$work/prefix" WINEDEBUG=-all
status=0
wine host.exe > host.out 2>wine.err || status=$?
echo "--- host output (exit $status)"; cat host.out
echo "--- OpenVersus.HookTest.log"; cat OpenVersus.HookTest.log 2>/dev/null || echo "(no log written)"
if [ $status = 0 ] && tr -d '\r' < host.out | grep -q '^PASS$' && grep -q 'guarded read: ok' OpenVersus.HookTest.log && grep -q 'trampoline page .* (as expected)' OpenVersus.HookTest.log; then
	echo "WINE TEST: passed"
else
	echo "WINE TEST: FAILED (see $work/wine.err)"; exit 1
fi
