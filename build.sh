#!/usr/bin/env bash
# Builds, tests and publishes the OpenVersus .NET client from Linux (and maybe macOS).
#
#   build.sh                 build the host libraries, run the tests, publish OpenVersus_<version>.asi
#   build.sh test            build and run the tests only (no Windows toolchain needed)
#   build.sh publish         publish OpenVersus_<version>.asi only
#   build.sh harness         run the hooking layer end to end under Wine
#   build.sh package         build, test and publish, build the rollback node for every platform (the server
#                                   repo's build.sh node), and zip them for players: out/OpenVersus_<version>.zip
#                                   holding plugins/OpenVersus/ with the .asi, OpenVersus.toml (the default file, pointing
#                                   at prod, with LogLevel trace: tools/package-config.cs) and
#                                   node/{win-x64,linux-x64,osx-x64,osx-arm64} with node/OVS.Rollback.Node.sh, the launcher
#                                   that picks a Mac's node. PACKAGE_SERVER=testing makes a testing
#                                   package instead: both ServerUrl lines switched to the testing server, the prod line
#                                   commented out above each. The testing server is the node's own
#                                   (NODE_REPO/pki/testing/server-url.txt), and the node always trusts both.
#   build.sh clean           remove every bin/ and obj/
#
# Options:
#   --accept-license   accept the Visual Studio Build Tools license for the Windows SDK sysroot
#                      that the first publish downloads (about 2.4 GB into ~/.cache/xwin);
#                      without it the script asks, and refuses when there is no terminal to ask on
#   --rwx              publish with trampoline pages read-write-execute for their whole life,
#                      as the C++ client did (default: read-execute except while a stub is written)
#   --install DIR      copy the published OpenVersus_<version>.asi into DIR/OpenVersus (DIR is the
#                      game's plugins folder), renaming any OpenVersus*.asi in DIR or DIR/OpenVersus
#                      to .bak, since the ASI loader would otherwise load both. Also copies the rollback
#                      node builds into DIR/OpenVersus/node/{win-x64,linux-x64,osx-x64,osx-arm64} when the server repo has
#                      published them (NODE_OUT, default <repo>/../ovs-rollback-server/out), replacing
#                      what is there; without them the node folder is left alone and a note says so
#   --skip-tests       do not run the tests in the default command or package
#
# Needs the .NET 10 SDK. Publishing a Windows binary from Linux also needs lld-link (package
# "lld") and xwin on PATH; the tests and the host build need neither. On Windows use build.ps1.
# xwin can be downloaded from: https://github.com/Jake-Shadle/xwin

set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
solution="$here/OpenVersus.slnx"
project="$here/OpenVersus/OpenVersus.csproj"
version=$(tr -d '[:space:]' < "$here/VERSION")
published="$here/OpenVersus/bin/Release/net10.0/win-x64/publish/OpenVersus_$version.asi"

command=build
accept_license=${ACCEPT_VS_BUILD_TOOLS_LICENSE:-false}
rwx=false
install_dir=""
skip_tests=false

usage() {
    sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'
}

while [ $# -gt 0 ]; do
    case "$1" in
        build|rebuild|test|publish|harness|clean|package) command=$1 ;;
        --accept-license) accept_license=true ;;
        --rwx) rwx=true ;;
        --install) shift; install_dir=${1:-}; [ -n "$install_dir" ] || { echo "--install needs a directory" >&2; exit 2; } ;;
        --install=*) install_dir=${1#--install=} ;;
        --skip-tests) skip_tests=true ;;
        -h|--help) usage; exit 0 ;;
        *) echo "unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done

say() { printf '\033[1m== %s\033[0m\n' "$*"; }
fail() { echo "error: $*" >&2; exit 1; }

need_dotnet() {
    command -v dotnet > /dev/null || fail "dotnet is not on PATH; install the .NET 10 SDK from https://dotnet.microsoft.com/download"
    local major
    major=$(dotnet --version 2>/dev/null | cut -d. -f1)
    [ "${major:-0}" -ge 10 ] || fail ".NET SDK 10 or newer is required (found $(dotnet --version 2>/dev/null || echo none))"
}

need_cross_toolchain() {
    case "$(uname -s)" in
        Linux|Darwin)
            command -v lld-link > /dev/null || fail "lld-link is not on PATH; install the \"lld\" package (it links the Windows binary)"
            command -v xwin > /dev/null || fail "xwin is not on PATH; see https://github.com/Jake-Shadle/xwin (it provides the Windows SDK sysroot)"
            ;;
    esac
}

confirm_license() {
    [ "$accept_license" = true ] && return
    if [ ! -d "${XWinCache:-$HOME/.cache/xwin}" ]; then
        cat <<'NOTICE'

Publishing a Windows binary from Linux links against the Windows SDK, which xwin downloads
from Microsoft (about 2.4 GB, once, into ~/.cache/xwin). Using it means accepting the
Visual Studio Build Tools license: https://go.microsoft.com/fwlink/?LinkId=2086102

NOTICE
        if [ -t 0 ]; then
            read -r -p "Accept the license and continue? [y/N] " answer
            case "$answer" in y|Y|yes|YES) ;; *) fail "license not accepted; nothing published" ;; esac
        else
            fail "no terminal to ask on; pass --accept-license (or set ACCEPT_VS_BUILD_TOOLS_LICENSE=true) to accept the Visual Studio Build Tools license"
        fi
    fi
    accept_license=true
}

do_build() {
    say "building for the host"
    dotnet build "$solution" --nologo -v quiet
}

do_test() {
    say "running the tests"
    dotnet test "$solution" --nologo -v quiet
}

do_publish() {
    need_cross_toolchain
    confirm_license
    say "publishing OpenVersus_$version.asi (NativeAOT, win-x64$([ "$rwx" = true ] && echo ', RWX trampolines'))"
    dotnet publish "$project" -c Release -r win-x64 --nologo -v quiet \
        -p:AcceptVSBuildToolsLicense="$accept_license" \
        $([ "$rwx" = true ] && echo "-p:RwxTrampolines=true")
    [ -f "$published" ] || fail "publish finished but $published is missing"
    say "published $published ($(du -h "$published" | cut -f1))"
    if [ -n "$install_dir" ]; then
        [ -d "$install_dir" ] || fail "$install_dir is not a directory"
        home="$install_dir/OpenVersus"
        mkdir -p "$home"
        for old in "$install_dir"/OpenVersus*.asi "$home"/OpenVersus*.asi; do
            [ -f "$old" ] || continue
            mv -f "$old" "$old.bak"
            echo "kept the previous plugin as $old.bak"
        done
        cp -f "$published" "$home/"
        say "installed to $home/$(basename "$published")"
        # The rollback node the mod starts beside the game (see README, "Rollback node"): published by
        # ovs-rollback-server's build.sh (node), one folder per platform.
        node_out=${NODE_OUT:-$here/../ovs-rollback-server/out}
        if [ -f "$node_out/OVS.Rollback.Node.sh" ]; then
            install -m 755 "$node_out/OVS.Rollback.Node.sh" "$home/node/OVS.Rollback.Node.sh"
            say "installed the node launcher (picks a Mac's node) to $home/node/OVS.Rollback.Node.sh"
        fi
        for rid in win-x64 linux-x64 osx-x64 osx-arm64; do
            if [ -d "$node_out/node-$rid" ]; then
                rm -rf "$home/node/$rid"
                mkdir -p "$home/node/$rid"
                cp -r "$node_out/node-$rid/." "$home/node/$rid/"
                say "installed the $rid rollback node to $home/node/$rid"
            else
                echo "no $rid rollback node at $node_out/node-$rid; $home/node/$rid left as it is (build it with ovs-rollback-server/build.sh node)"
            fi
        done
    fi
}

do_harness() {
    command -v wine > /dev/null || fail "wine is not on PATH"
    command -v x86_64-w64-mingw32-gcc > /dev/null || fail "x86_64-w64-mingw32-gcc is not on PATH (package mingw64-gcc or mingw-w64)"
    need_cross_toolchain
    confirm_license
    say "running the Wine harness"
    ACCEPT_VS_BUILD_TOOLS_LICENSE=true "$here/wine-host/run.sh"
}

# The zip for players. The node is built here rather than taken from the server repo's out/, so that the node in the zip
# is always one that trusts both servers the toml switches between; the published binaries are checked for both URLs.
do_package() {
    local node_repo=${NODE_REPO:-$here/../ovs-rollback-server}
    local testing_file="$node_repo/pki/testing/server-url.txt"
    [ -s "$testing_file" ] || fail "$testing_file is missing: the testing server the toml switches to comes from the node's own trust list"
    local testing prod
    testing=$(tr -d '[:space:]' < "$testing_file")
    prod=$(tr -d '[:space:]' < "$node_repo/pki/prod/server-url.txt")
    say "building the rollback node (the server repo's build.sh node)"
    NODE_PKI="prod testing" "$node_repo/build.sh" node win-x64 linux-x64 osx-x64 osx-arm64

    local stage="$here/out/package"
    local zip="$here/out/OpenVersus_$version.zip"
    rm -rf "$stage" "$zip"
    local home="$stage/plugins/OpenVersus"
    mkdir -p "$home/node"
    cp "$published" "$home/"
    # The launcher the mod starts on a Mac: it picks osx-arm64 or osx-x64 by the machine (the Linux node anywhere else).
    [ -f "$node_repo/out/OVS.Rollback.Node.sh" ] || fail "$node_repo/out/OVS.Rollback.Node.sh is missing: the server repo's build.sh node writes it"
    install -m 755 "$node_repo/out/OVS.Rollback.Node.sh" "$home/node/OVS.Rollback.Node.sh"
    for rid in win-x64 linux-x64 osx-x64 osx-arm64; do
        local exe
        exe=$(ls "$node_repo/out/node-$rid" | grep -E '^OVS\.Rollback\.Node(\.exe)?$') || fail "$node_repo/out/node-$rid has no node executable"
        grep -q -a -F "$prod " "$node_repo/out/node-$rid/$exe" && grep -q -a -F "$testing " "$node_repo/out/node-$rid/$exe" \
            || fail "the $rid node does not trust both $prod and $testing; the toml's switch would not reach it"
        mkdir -p "$home/node/$rid"
        # Everything the node runs with (its settings files, the Serilog config, the runtimeconfig), without debug symbols.
        (cd "$node_repo/out/node-$rid" && find . -type f ! -name '*.pdb' -exec cp --parents {} "$home/node/$rid/" \;)
    done
    # --file: from the repo root a bare path would build the C++ project there instead.
    if [ "${PACKAGE_SERVER:-prod}" = testing ]; then
        say "writing OpenVersus.toml (servers: $testing, prod commented out; LogLevel trace)"
        dotnet run --file "$here/tools/package-config.cs" -- "$home" "$testing"
    else
        say "writing OpenVersus.toml (servers: $prod; LogLevel trace)"
        dotnet run --file "$here/tools/package-config.cs" -- "$home"
    fi

    (cd "$stage" && zip -q -r -X "$zip" plugins) || fail "zip failed"
    # zip -X leaves out uid/gid extras, not the Unix mode: the Linux and macOS nodes and the launcher keep their
    # executable bit.
    for entry in linux-x64/OVS.Rollback.Node osx-x64/OVS.Rollback.Node osx-arm64/OVS.Rollback.Node OVS.Rollback.Node.sh; do
        unzip -Z "$zip" "plugins/OpenVersus/node/$entry" | grep -q '^-rwx' \
            || fail "node/$entry lost its executable bit in $zip"
    done
    say "packaged $zip ($(du -h "$zip" | cut -f1))"
    unzip -Z1 "$zip" | sed 's/^/    /'
}

do_clean() {
    say "removing bin/ and obj/"
    find "$here" -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
}

need_dotnet
case "$command" in
    build)
        do_build
        [ "$skip_tests" = true ] || do_test
        do_publish
        ;;
    rebuild)
        do_clean
        do_build
        [ "$skip_tests" = true ] || do_test
        do_publish
        ;;
    test) do_build; do_test ;;
    publish) do_publish ;;
    harness) do_harness ;;
    clean) do_clean ;;
    package)
        do_build
        [ "$skip_tests" = true ] || do_test
        do_publish
        do_package
        ;;
esac

