#!/usr/bin/env bash
# Builds, tests and publishes the OpenVersus .NET client from Linux (and maybe macOS).
#
#   dotnet/build.sh                 build the host libraries, run the tests, publish OpenVersus_<version>.asi
#   dotnet/build.sh test            build and run the tests only (no Windows toolchain needed)
#   dotnet/build.sh publish         publish OpenVersus_<version>.asi only
#   dotnet/build.sh harness         run the hooking layer end to end under Wine
#   dotnet/build.sh clean           remove every bin/ and obj/
#
# Options:
#   --accept-license   accept the Visual Studio Build Tools license for the Windows SDK sysroot
#                      that the first publish downloads (about 2.4 GB into ~/.cache/xwin);
#                      without it the script asks, and refuses when there is no terminal to ask on
#   --rwx              publish with trampoline pages read-write-execute for their whole life,
#                      as the C++ client did (default: read-execute except while a stub is written)
#   --install DIR      copy the published OpenVersus_<version>.asi into DIR (the game's plugins
#                      folder), renaming any OpenVersus*.asi already there to .bak, since the
#                      ASI loader would otherwise load both
#   --skip-tests       do not run the tests in the default command
#
# Needs the .NET 10 SDK. Publishing a Windows binary from Linux also needs lld-link (package
# "lld") and xwin on PATH; the tests and the host build need neither. On Windows use build.ps1.
# xwin can be downloaded from: https://github.com/Jake-Shadle/xwin

set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
solution="$here/OpenVersus.slnx"
project="$here/OpenVersus/OpenVersus.csproj"
version=$(tr -d '[:space:]' < "$here/../VERSION")
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
        build|test|publish|harness|clean) command=$1 ;;
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
        for old in "$install_dir"/OpenVersus*.asi; do
            [ -f "$old" ] || continue
            mv -f "$old" "$old.bak"
            echo "kept the previous plugin as $old.bak"
        done
        cp -f "$published" "$install_dir/"
        say "installed to $install_dir/$(basename "$published")"
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
    test) do_build; do_test ;;
    publish) do_publish ;;
    harness) do_harness ;;
    clean) do_clean ;;
esac

