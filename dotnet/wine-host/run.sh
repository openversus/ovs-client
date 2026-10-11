#!/usr/bin/env bash
# End-to-end test of the hooking layer: cross-compiles host.c, publishes the harness plugin, and
# runs them together under Wine. The host calls three assembly sites before and after loading
# the plugin; the plugin redirects a call and a jmp into managed code, patches a byte, makes
# one hook throw to prove the guard returns the fallback, and swaps a pointer in read-only data.
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
# The rollback node, when the server repo has published its Linux build: the plugin starts it the way
# the mod does under Proton (start /unix) and this script checks that it ends when the keepalives stop.
node_src=${NODE_LINUX:-$repo/../ovs-rollback-server/out/node-linux-x64}
node_test=false
if [ -x "$node_src/OVS.Rollback.Node" ]; then
    mkdir -p "$work/node/linux-x64"; cp -r "$node_src/." "$work/node/linux-x64/"; node_test=true
    echo "--- node: testing with $node_src"
else
    echo "--- node: no Linux build at $node_src; the node step is skipped"
fi

cd "$work"
export WINEPREFIX="$work/prefix" WINEDEBUG=-all
status=0
wine host.exe > host.out 2>wine.err || status=$?
echo "--- host output (exit $status)"; cat host.out
echo "--- OpenVersus.HookTest.log"; cat OpenVersus.HookTest.log 2>/dev/null || echo "(no log written)"
grep -q 'process-exit hook fired' OpenVersus.HookTest.log 2>/dev/null && echo "EXIT HOOK: fires under NativeAOT in a DLL" || echo "EXIT HOOK: did not fire (the next-launch archive covers it)"
node_ok=true
if $node_test; then
    if grep -q 'node: port [0-9]* reported' OpenVersus.HookTest.log; then
        echo "NODE: started through start /unix and reported $(grep -o 'node: port [0-9]*' OpenVersus.HookTest.log | head -1 | cut -d' ' -f3)"
    else
        echo "NODE: FAILED to report a port"; node_ok=false
    fi
    # Through start.exe the node is not the plugin's child; the keepalives stopping must end it (its timeout is 10 s).
    # By process name, which the kernel keeps to 15 characters (pgrep -x on the full name matches nothing).
    node_alive() { ps -eo comm= | grep -qx 'OVS.Rollback.No'; }
    # The plugin kept it alive for 15 s, past its 10 s timeout: alive here means the keepalives reached it.
    node_alive && echo "NODE: alive after 15 s of keepalives (timeout 10 s): the keepalives reach it" || { echo "NODE: FAILED: gone right after the plugin stopped it; the keepalives did not keep it alive past its timeout"; node_ok=false; }
    for i in $(seq 1 30); do node_alive || break; sleep 1; done
    if node_alive; then echo "NODE: FAILED to exit within 30 s of the keepalives stopping"; node_ok=false; else echo "NODE: exited within $i s of the keepalives stopping"; fi
    ls "$work"/node/port-*.txt >/dev/null 2>&1 && { echo "NODE: FAILED to remove its port file"; node_ok=false; } || true
fi
if [ $status = 0 ] && $node_ok && tr -d '\r' < host.out | grep -q '^PASS$' && grep -q 'guarded read: ok' OpenVersus.HookTest.log && grep -q 'trampoline page .* (as expected)' OpenVersus.HookTest.log && grep -q 'stale swap refused, slot unchanged' OpenVersus.HookTest.log; then
    echo "WINE TEST: passed"
else
    echo "WINE TEST: FAILED (see $work/wine.err)"; exit 1
fi
