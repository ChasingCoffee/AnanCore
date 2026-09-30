#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Build the native audio-plugin bridges for this host OS and stage them into
# Zeus.Plugins.Host/runtimes/<rid>/native/, where VstBridgeNativeLoader /
# AuBridgeNativeLoader probe first. macOS + Linux; Windows uses
# tools/stage-windows-vst-bridge.ps1.
#
#   tools/stage-plugin-bridges.sh [--init-submodules] [--arch arm64|x64]...
#
# macOS: builds libzeus-vst-bridge.dylib and libzeus-au-bridge.dylib for each
#        --arch given (default: arm64 and x64), minimum macOS 11.0.
# Linux: builds libzeus-vst-bridge.so for the host architecture only
#        (cross builds belong to CI; see .github/workflows/build-plugin-bridges.yml).
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
vst_root="$repo_root/native/zeus-vst-bridge"
au_root="$repo_root/native/zeus-au-bridge"
runtimes="$repo_root/Zeus.Plugins.Host/runtimes"

init_submodules=0
archs=()
while [ $# -gt 0 ]; do
    case "$1" in
        --init-submodules) init_submodules=1 ;;
        --arch) shift; archs+=("$1") ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
    shift
done

if [ "$init_submodules" = 1 ]; then
    git -C "$repo_root" submodule update --init native/zeus-vst-bridge/third_party/vst3sdk native/zeus-vst-bridge/third_party/clap
    git -C "$vst_root/third_party/vst3sdk" submodule update --init base pluginterfaces public.sdk cmake
fi

jobs="$( (command -v nproc >/dev/null && nproc) || sysctl -n hw.ncpu 2>/dev/null || echo 4)"

build() { # <source dir> <build dir> [cmake args...]
    local src="$1" bld="$2"; shift 2
    cmake -S "$src" -B "$bld" -DCMAKE_BUILD_TYPE=Release "$@"
    cmake --build "$bld" --config Release --parallel "$jobs"
}

case "$(uname -s)" in
    Darwin)
        [ ${#archs[@]} -eq 0 ] && archs=(arm64 x64)
        for arch in "${archs[@]}"; do
            case "$arch" in
                arm64) osx_arch=arm64 ;;
                x64)   osx_arch=x86_64 ;;
                *) echo "unsupported macOS arch: $arch" >&2; exit 2 ;;
            esac
            rid="osx-$arch"
            dest="$runtimes/$rid/native"
            mkdir -p "$dest"
            build "$vst_root" "$vst_root/build-$rid" \
                -DCMAKE_OSX_ARCHITECTURES="$osx_arch" \
                -DZEUS_VST_REQUIRE_SDK=ON -DZEUS_VST_BUILD_TEST_PLUGIN=OFF
            build "$au_root" "$au_root/build-$rid" -DCMAKE_OSX_ARCHITECTURES="$osx_arch"
            cp -f "$vst_root/build-$rid/libzeus-vst-bridge.dylib" "$dest/"
            cp -f "$au_root/build-$rid/libzeus-au-bridge.dylib" "$dest/"
            for lib in "$dest/libzeus-vst-bridge.dylib" "$dest/libzeus-au-bridge.dylib"; do
                file "$lib"
                otool -l "$lib" | awk '/LC_BUILD_VERSION/{f=1} f&&/minos/{print "  minos", $2; exit}'
            done
        done
        ;;
    Linux)
        case "$(uname -m)" in
            x86_64)  rid=linux-x64 ;;
            aarch64) rid=linux-arm64 ;;
            *) echo "unsupported Linux arch: $(uname -m)" >&2; exit 2 ;;
        esac
        if [ ${#archs[@]} -gt 0 ]; then
            echo "--arch is ignored on Linux; building for $rid" >&2
        fi
        dest="$runtimes/$rid/native"
        mkdir -p "$dest"
        build "$vst_root" "$vst_root/build-$rid" \
            -DZEUS_VST_REQUIRE_SDK=ON -DZEUS_VST_BUILD_TEST_PLUGIN=OFF
        cp -f "$vst_root/build-$rid/libzeus-vst-bridge.so" "$dest/"
        file "$dest/libzeus-vst-bridge.so"
        ;;
    *)
        echo "use tools/stage-windows-vst-bridge.ps1 on Windows" >&2
        exit 2
        ;;
esac
