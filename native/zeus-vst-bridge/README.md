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

What the host does per plug-in (ABI v4):

- **Class selection** — `zvst_load_vst3_class` loads one effect out of a
  multi-class module by the UID `zvst_describe` reports.
- **State** — `zvst_get_state` / `zvst_set_state` carry a versioned blob
  (`ZVS1` | component length | controller length | component state |
  controller state). Restoring holds a lock the audio thread only
  try-locks, so blocks pass through unprocessed instead of waiting.
  Controller state that arrives before the editor exists is held and
  applied when the controller is created.
- **Editors** — Windows (HWND, per-plug-in UI thread), Linux (X11 embed,
  editor thread) and macOS (NSView in an NSWindow on the main thread, via
  `src/mac_ui.mm`). On macOS every non-audio call runs on the main thread
  when the host runs an AppKit loop (desktop mode); headless, editors are
  unavailable. Plug-in output parameters (meters) are forwarded to the
  editor at ~30 Hz.
- **Precision** — 32-bit float end to end; a plug-in that only processes
  64-bit samples is hosted through a conversion at the boundary.
- **Buses** — every declared bus gets buffers; sidechain / aux inputs read
  silence.

The C ABI in `include/zvst.h` is stable; the .NET P/Invoke side is
`Zeus.Plugins.Host.Audio.VstBridgeNative`, exercised by the native ctest
suite (`tests/bridge_tests.cpp`), `VstBridgeTestPluginTests` (against the
in-tree test plug-in, below) and `VstBridgeNativeRealTests`.

If the `vst3sdk` submodule is not initialised, CMake builds
`src/bridge_stub.cpp` instead: the same ABI, but every load and describe
returns `ZVST_NOT_IMPLEMENTED`, so the rest of the tree builds and the host
simply finds no plug-ins. Staging and CI builds pass `-DZEUS_VST_REQUIRE_SDK=ON`
so a missing submodule fails loudly instead of shipping the stub.

## CLAP

The same library hosts CLAP plug-ins (`src/clap_bridge.cpp`, C ABI
`include/zclap.h`, .NET side `Zeus.Plugins.Host.Audio.ClapBridgeNative`)
with the header-only [CLAP SDK](https://github.com/free-audio/clap) (MIT),
a submodule at `third_party/clap` pinned to `1.2.10`. The ABI mirrors zvst
function for function and reuses its status codes, so the .NET host drives
both formats through one interface.

- **Main thread.** Each instance gets a host loop that is the plug-in's
  main thread: a dedicated thread with an event loop (plug-in callbacks,
  `timer-support`, and on Linux `posix-fd-support` plus the editor's X
  connection; on Windows the editor's message pump), or on macOS the
  process main thread when the host runs an AppKit loop. `init`, `activate`,
  state, parameters and the GUI all run there; `process` runs on the
  caller's audio thread, `start_processing` / `stop_processing` too.
- **Ports.** Every declared audio port gets buffers; the main ports are
  bridged to the host's mono/stereo geometry, other inputs read silence.
  Plug-ins without a 1- or 2-channel main input and output (instruments)
  are refused.
- **Parameters** arrive as `CLAP_EVENT_PARAM_VALUE` events, the normalised
  value mapped onto the parameter's plain range.
- **State** is the plug-in's `clap.state` stream in a `ZCS1` blob;
  **latency** from `clap.latency`; **editors** from `clap.gui` — embedded in
  a bridge window where supported, otherwise the plug-in's floating window.
- Not supported: `request_restart` (the plug-in keeps its current setup).

Without the CLAP submodule, `src/clap_stub.cpp` exports the same ABI and
loads nothing.

VST2 is **not** in scope (Steinberg withdrew distribution rights for new
hosts in 2024 — see `docs/proposals/plugin-system-v2.md`).

## Build

Initialise the vendored SDKs first (vst3sdk itself has nested submodules —
`base`, `pluginterfaces`, `public.sdk` — that the hosting sources need;
`vstgui` and the samples are not required):

```bash
git submodule update --init native/zeus-vst-bridge/third_party/vst3sdk native/zeus-vst-bridge/third_party/clap
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
- Test plug-ins: `build/test-plugin/ZeusTestPlugin.vst3` and
  `build/test-plugin/ZeusTestPlugin.clap` (`-DZEUS_VST_BUILD_TEST_PLUGIN=OFF`
  to skip)

Native tests: `ctest --test-dir build -C Release --output-on-failure`.

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

## Test plug-ins

`test-plugin/zeus_test_plugin.cpp` builds `ZeusTestPlugin.vst3`, a
deterministic two-class effect ("Zeus Test Gain", "Zeus Test Invert") with a
stereo sidechain bus, a bypass parameter and component state.
`test-plugin/zeus_test_clap.c` builds `ZeusTestPlugin.clap`: the same two
effects plus an instrument the scanner must skip, and state that records
whether the host kept CLAP's threading contract. Neither is shipped. The
environment variable `ZEUS_TEST_VST_FAULT` makes them misbehave on purpose
(`crash-scan`, `hang-scan`, `crash-process`, `hang-process`, `nan-process`,
`latency-N`, VST3 only: `only-64`) for isolation tests.

## ABI

`include/zvst.h` is the single source of truth. The .NET side checks
`ZVST_ABI` on init via `zvst_init` and refuses to proceed on mismatch.
Bump `ZVST_ABI` in lockstep with any breaking change.

## License

GPL-2.0-or-later (matches Zeus core). Statically links the MIT-licensed
`vst3sdk` and compiles against the MIT-licensed CLAP SDK headers.
