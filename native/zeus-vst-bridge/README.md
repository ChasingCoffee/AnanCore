# zeus-vst-bridge

Native, in-process VST3 host for Openhpsdr-Zeus. Linked as a shared
library and called via P/Invoke from `Zeus.Plugins.Host.Audio.VstBridgeNative`.

## Status

Real VST3 hosting via [Steinberg `vst3sdk`](https://github.com/steinbergmedia/vst3sdk)
(MIT since October 2025), vendored under `third_party/vst3sdk` as a git
submodule pinned to a release tag (currently `v3.8.1_build_84`):
`Module::create(path)` → factory walk → instantiate `kVstAudioEffectClass` →
`initialize` / `setBusArrangements` (every bus: main + any sidechain/aux) /
`setupProcessing` / `setActive` / `setProcessing` / `process` (see
`src/bridge.cpp`).

The C ABI in `include/zvst.h` is stable; the .NET P/Invoke side is
`Zeus.Plugins.Host.Audio.VstBridgeNative`, exercised end to end by
`VstBridgeTestPluginTests` (against the in-tree test plug-in, below) and
`VstBridgeNativeRealTests`.

If the `vst3sdk` submodule is not initialised, CMake builds
`src/bridge_stub.cpp` instead: the same ABI, but every load and describe
returns `ZVST_NOT_IMPLEMENTED`, so the rest of the tree builds and the host
simply finds no plug-ins. Staging and CI builds pass `-DZEUS_VST_REQUIRE_SDK=ON`
so a missing submodule fails loudly instead of shipping the stub.

CLAP support ([CLAP SDK](https://github.com/free-audio/clap), MIT) is a
planned addition in the same library. VST2 is **not** in scope
(Steinberg withdrew distribution rights for new hosts in 2024 — see
`docs/proposals/plugin-system-v2.md`).

## Build

Initialise the vendored SDK first (the submodule itself has nested
submodules — `base`, `pluginterfaces`, `public.sdk` — that the hosting
sources need; `vstgui` and the samples are not required):

```bash
git submodule update --init native/zeus-vst-bridge/third_party/vst3sdk
git -C native/zeus-vst-bridge/third_party/vst3sdk \
    submodule update --init base pluginterfaces public.sdk cmake
```

Then build:

```bash
cd native/zeus-vst-bridge
cmake -B build -DCMAKE_BUILD_TYPE=Release      # Windows: add -G "Visual Studio 17 2022" -A x64
cmake --build build --config Release
```

Output:

- Linux:   `build/libzeus-vst-bridge.so`
- macOS:   `build/libzeus-vst-bridge.dylib` (minimum macOS 11.0)
- Windows: `build/Release/zeus-vst-bridge.dll`
- Test plug-in: `build/test-plugin/ZeusTestPlugin.vst3`
  (`-DZEUS_VST_BUILD_TEST_PLUGIN=OFF` to skip)

`dotnet test` picks up a local build automatically: the
`Zeus.Plugins.Host.Tests` project copies the bridge into its output, and
`VstBridgeTestPluginTests` finds the test plug-in under `build/test-plugin/`.

## Staging for shipping

The loader (`VstBridgeNativeLoader`) probes
`Zeus.Plugins.Host/runtimes/<rid>/native/` first, and
`Zeus.Plugins.Host.csproj` copies `runtimes/**` into the host output. To
refresh the committed binaries:

- macOS / Linux: `tools/stage-plugin-bridges.sh [--init-submodules]`
  (macOS builds arm64 + x64, plus the AU bridge; Linux builds the host arch)
- Windows: `tools/stage-windows-vst-bridge.ps1 -Arch x64|arm64 [-InitSubmodules]`
- Every RID at once: run the **Build Plugin Bridges** workflow
  (`.github/workflows/build-plugin-bridges.yml`) and commit its
  `plugin-bridges-<rid>` artifacts.

## Test plug-in

`test-plugin/zeus_test_plugin.cpp` builds `ZeusTestPlugin.vst3`, a
deterministic two-class effect ("Zeus Test Gain", "Zeus Test Invert") with a
stereo sidechain bus, a bypass parameter and component state. It is never
shipped. The environment variable `ZEUS_TEST_VST_FAULT` makes it misbehave on
purpose (`crash-scan`, `hang-scan`, `crash-process`, `hang-process`,
`nan-process`, `latency-N`) for isolation tests.

## ABI

`include/zvst.h` is the single source of truth. The .NET side checks
`ZVST_ABI` on init via `zvst_init` and refuses to proceed on mismatch.
Bump `ZVST_ABI` in lockstep with any breaking change.

## License

GPL-2.0-or-later (matches Zeus core). Statically links the MIT-licensed
`vst3sdk` (and, once added, the CLAP SDK).
