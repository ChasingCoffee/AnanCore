# ANAN Core Audio Plugins (VST3 / AU / CLAP) — Working Plan

**Status:** Phase 1 done (PR #1); Phase 2 done (PR #2); Phase 3 next — see the status notes under each phase
**Date:** 2026-09-30
**Branching:** `feature/<name>` off `freedv-in-core` in the ChasingCoffee fork; contributed upstream to Apache Labs once each phase is proven.
**Companion doc:** [`ANAN_Core_Native_VST3_Host_Plan.md`](ANAN_Core_Native_VST3_Host_Plan.md) (the original proposal). This plan supersedes its framework/architecture choice (Sections 8–9, 19) because discovery found a partial in-process host already in the tree; its safety rules, TX invariants, and non-goals (Sections 4, 13, 15–16, 26, 35) still apply.

---

## 1. Goal

Third-party audio plugins — VST3 on macOS/Windows/Linux, Audio Units on macOS, CLAP on all three — in the RX chain (post-demod) and the TX chain (mic, pre-WDSP), in **both** deployments:

| Deployment | Where ANAN Core's server runs | Where plugins run |
|---|---|---|
| **Local** | On the operator's Mac/PC (desktop mode, talking to the G2 over LAN, like Thetis) | In-process with the server (today's path) → later out-of-process |
| **Remote** | On the G2 Ultra's Raspberry Pi 5 | On the operator's Mac/PC, in `OpenhpsdrZeus --client` |

One native plugin engine and one .NET chain layer serve both. The remote client is the existing `OpenhpsdrZeus` binary in a new mode, not a separate native application.

## 2. Guardrails (from `CLAUDE.md` / `AGENTS.md` and the original proposal)

- **PureSignal is untouched.** No change to PS logic, state, persistence, or feedback routing.
- **TX authority stays on the server.** MOX / PTT / TUNE / drive / SWR protection are never owned by a plugin host or the remote client.
- **Failure means silence.** A crashed/hung plugin or dropped client must produce silence or pass-through, never stale or repeated TX audio.
- **Cross-platform on every change.** macOS, Windows, Linux x64 + arm64, Raspberry Pi. A native bridge that cannot build on a platform must degrade to "plugins unavailable" there, not break the build.
- **Browser-only operation is unchanged.** Every plugin surface is additive and capability-gated.
- **Red-light items** (new dependencies, architecture, `Zeus.Contracts` wire format, operator-felt defaults) are called out per phase so the upstream PR can flag them for maintainer review.
- No repo-visible mention of assistants/vendors in commits, PRs, or code comments.

---

## 3. What already exists (audit, 2026-09-30)

### 3.1 Native bridges

**`native/zeus-vst-bridge`** — C ABI v3 (`include/zvst.h:42`), Steinberg vst3sdk hosting sources, P/Invoked by `Zeus.Plugins.Host/Audio/VstBridgeNative.cs`.

| Capability | State | Where |
|---|---|---|
| Load / process / unload | Real. Planar f32, 1–2 ch with mono↔stereo bridging, 44.1–192 kHz, blocks 32–4096 | `src/bridge.cpp:441-464`, `:1134-1136`, `:1193-1254` |
| Describe (scan) | Real, **in-process** | `:1352-1386` |
| Latency | Read once at load | `:499`, `:1278` |
| Editor | Windows (HWND thread) and Linux (X11 embed) real; **macOS returns `ZVST_NOT_IMPLEMENTED`** | `:586-878`, `:880-1112`, `:1409-1411` |
| State get/set | **Missing** from the ABI | — |
| Per-plugin bypass | **Missing** | — |
| Load by class UID | **Missing** — always first audio-effect class, so shell plugins can't select a sub-plugin | `:389-401` |
| 64-bit processing | Refused (`ZVST_UNSUPPORTED_PRECISION`) | `:469` |
| Output parameter changes (plugin meters) | Discarded every block | `:1256-1260` |
| **Buildability** | **Broken.** vst3sdk submodule removed (`f988881`); `third_party/` absent. CMake sets `ZEUS_VST_BRIDGE_STUB` (`CMakeLists.txt:116`) but `bridge.cpp` never checks it, so the "stub" fallback does not compile either | — |

**`native/zeus-au-bridge`** — C ABI v2 (`include/zau.h:53`), Objective-C++.

- Real: enumerate effects (aufx + aumf), load by `type:subtype:manufacturer`, process with mono/stereo bridging, editor (Cocoa view → `AUGenericView` fallback) dispatched to the main queue (`src/bridge.mm:391-408`, `:488-538`, `:596-700`).
- Missing: state (ClassInfo), latency property, bypass. Never built by CI.

**Committed binaries** (`Zeus.Plugins.Host/runtimes/`): `win-x64/zeus-vst-bridge.dll` (ABI 3) and `osx-arm64/libzeus-au-bridge.dylib` (ABI 2) only. No VST3 bridge for macOS/Linux/win-arm64; no AU bridge for osx-x64. CI bridge jobs were removed (`build-native-libs.yml:637-649`); `release.yml:313`, `:787-793` are gated to the upstream org repo.

### 3.2 .NET host (`Zeus.Plugins.Host`)

- `IVstBridgeNative` → `VstBridgeNative` (VST3) / `AuBridgeNative` (AU; degrades gracefully when the dylib is missing). Loader probes `runtimes/<rid>/native` (`Audio/VstBridgeNativeLoader.cs`, `Audio/NativeBridgeResolver.cs`).
- Scan: `VstDirectoryScanService.cs` calls `zvst_describe` **inside the server process**; a crashing plugin kills the server. Its no-bridge fallback is a Windows PE check that rejects every macOS bundle (`:318`, `:440-460`). AU scan: `AuComponentScanService.cs` (macOS only).
- Each scanned plugin becomes a generated `plugin.json` + `Zeus.Plugins.VstHostStub` assembly under `PluginRoot` (`~/Library/Application Support/Zeus/plugins` on macOS). IDs `com.openhpsdr.zeus.{vst,rxvst,au,rxau}.<slug>`; manifest pins `channels=1`, `sampleRate=48000`, `vst3Uid=null`.
- Chain: `Audio/AudioChain.cs` — 8 slots (`:28`), master bypass, NaN/Inf zeroing after each slot. Per-slot bypass exists but the server never calls it; UI "bypass" means parking the plugin out of the chain.
- Wrapper: `Audio/VstHostAudioPlugin.cs` (both formats); non-OK process status → pass-through. Kill switches: `ZEUS_DISABLE_{,RX_,TX_}VST_LOAD`.
- Persistence (`zeus-prefs.db`): `audio_chain_order`, `rx_audio_chain_order` (active + parked IDs), `audio_chain_settings` (master bypasses). **Plugin parameter state is never saved.** `TxAudioProfile.VstPluginStates` (`TxAudioProfileStore.cs:139`) exists but nothing applies it.
- Retired external engine (`VstEngineController` / `VstEngineProcess` / `VstEngineBridge`, commit `31d38be`) is dead code; its designs (`docs/designs/vst-out-of-process-engine.md`, `vst-engine-bridge-protocol.md`) are useful reference for Phase 4.

### 3.3 Signal-path insertion points

| Chain | Call site | Position | Thread | Format |
|---|---|---|---|---|
| RX | `DspPipelineService.Tick` → `_rxAudioPluginHandler` (`DspPipelineService.cs:7663-7665`) → `AudioPluginBridge.ProcessRxBlock` (`AudioPluginBridge.cs:1314`) | After WDSP demod/AGC/AF gain and FreeDV decode; before adaptive squelch and sidetone | RX IQ thread (inline tick) or 30 Hz fallback timer | Mono f32, 48 kHz, ≤2048 frames |
| TX | `WdspDspEngine.ProcessTxBlock` → `_txAudioPluginHandler` (`WdspDspEngine.cs:4017`) → `AudioPluginBridge.Process` (`AudioPluginBridge.cs:529`) | Mic audio **before** `fexchange2` (WDSP TXA/CFC); skipped for digital modes and roger beep | TxAudioIngest caller (WS receive thread, or `NativeMicCapture`) | Mono f32, 48 kHz, 512 (P2) / 1024 (P1) |

**A hung `process()` blocks the RX or TX thread indefinitely; a plugin crash takes down the server.** No watchdog exists.

### 3.4 Frontend

`components/AudioSuiteWindow.tsx`: plugin browser, favorites, drag-to-chain, park (×), master bypass per chain, Scan VSTs / Scan AU / + Add VST folder. Default scan folders are Windows-only (`:1400-1403`). `GenericVstPanel.tsx` + `useVstEditor.ts`: Open/Close Editor only. Platform flags come from `ZeusEndpoints.cs:511-513` (`inProcessHostSupported`, `auSupported`). e2e: `zeus-web/e2e/vst-in-process.spec.ts` (stubbed backend, never loads a real plugin).

### 3.5 Bottom line today

On macOS arm64 desktop mode, **AU works** on RX and TX (settings lost on restart). **VST3 does not** (no macOS bridge, no macOS editor, SDK missing). CLAP does not exist.

---

## 4. Audio transport facts for the remote client

The existing `/ws` protocol is sufficient; **no server changes are needed for audio.**

| | RX | TX |
|---|---|---|
| Rate / format | 48 kHz, mono f32le | 48 kHz, mono f32le |
| Framing | `MsgType 0x02`: 16-byte header `[type u8][flags u8][len u16][seq u32][tsUnixMs f64]` + `[rxId u8][ch u8][rate u32][count u16][samples]` (`Zeus.Contracts/WireFormat.cs`, `AudioFrame.cs:52-89`) | `MsgType 0x20`: `[0x20][960 × f32le]`, exactly 3841 bytes, no header (`StreamingHub.cs:433-475`) |
| Block size | Variable, ~1600 samples (one ~30 Hz tick), cap 2048 | Exactly 960 (20 ms); anything else dropped |
| Receivers | All RX (and Kiwi) mixed server-side into one `rxId 0` stream (`DspPipelineService.cs:7550-7572`) | — |
| Server buffering | Per-client send queue of 4, drop-oldest (`StreamingHub.cs:57`) | Push-driven; WDSP TXA runs per arriving block; ring serves zero IQ when starved (`TxIqRing.cs:169-174`) |
| Gating | `WebSocketAudioSink` ignores master mute (`/api/audio/mute`) | Server drops blocks unless MOX or TX monitor (`TxAudioIngest.cs:808-826`); disconnect does **not** un-key |
| Auth | None on `/ws`; `/api` POST gate is off in standalone builds | — |

Browser client (`zeus-web/src/audio/audio-client.ts`) has **no clock-drift correction** — scheduled `AudioBufferSource`s with an adaptive 100–350 ms cushion. The native client must do better (Section 7, Phase 5).

Alternative: TCI (`Zeus.Server.Hosting/Tci/`) already carries RX/TX PCM with demand-driven TX pacing, but binds to 127.0.0.1 by default and adds a second protocol. Kept as a fallback, not the plan.

---

## 5. Reuse from Thetis-VST (`ChasingCoffee/Thetis-VST`, branch `vst-support`)

Author-owned code (relicensable GPL-2.0-or-later with SPDX headers added on transplant). VST3 SDK 3.8.0 is MIT. No JUCE, no AU/CLAP, Windows-only throughout. Architecture reference: `Documentation/VST_ARCHITECTURE.md`.

| Idea / component | Source | Use in ANAN Core |
|---|---|---|
| VST3 state blob: `magic \| compSize \| ctrlSize` + component + controller streams; restore order `component->setState` → `controller->setComponentState` → `controller->setState` | `VstHostBridge/vst_runtime.cpp` | **Transplant** into `zvst` (Phase 2) |
| Dirty detection (`performEdit`, `restartComponent(kParamValuesChanged)`, `setDirty`) → debounced save (750/1000 ms) | `vst_runtime.cpp`, `Console/vsthost.cs` | **Adapt** (Phase 2) |
| Output-parameter cache drained to the controller at ~30 Hz (plugin meters move) | `vst_runtime.cpp` | **Transplant** (Phase 2) |
| Try-lock on the audio path; pass-through if a state/editor op holds the lock | `vst_runtime.cpp`, `vst_chain.cpp` | **Adapt** with `std::atomic` / `std::shared_mutex` (Phase 2) |
| 64-bit processing when supported, else 32-bit | `vst_runtime.cpp` | **Adapt** (Phase 2) |
| Child-process scanner: timeout per plugin, `moduleinfo.json` first, unavailable/blacklist cached by mtime | `VstPluginScanner/Program.cs`, `vsthost.cs` | **Re-implement** in .NET 10 as `OpenhpsdrZeus --plugin-probe` (Phase 2) |
| Chains start bypassed; hosting opt-in; chain bypass flushes transport | `vst_chain.cpp`, `cmaster.c` hooks | **Adopt as policy** (Phase 2) |
| Out-of-process host: shared-memory audio ring, fixed-size control packets with timeouts, adaptive latency, restart + chain replay, generation counter | `VstHostBridge/vst_host_bridge.cpp`, `VstCommon/vst_ipc.h`, `VstAudioHost/host_process.cpp` | **Portable rewrite** (Phase 4) |
| Plugin snapshot artwork (`Resources/Snapshots/<CID>_snapshot.png`) | `Console/VstPluginArt.cs` | **Transplant** later for the Audio Suite rack |
| Rack / chain manager UX | WinForms forms | Reference only |

Where `zvst` is already ahead: cross-platform CMake, Linux X11 editor, mono↔stereo bridging, `getLatencySamples`, ABI version check.

---

## 6. Target architecture

```text
                        ┌──────────────────────────── native plugin engine ─┐
                        │  zeus-vst-bridge (C ABI)                          │
                        │    VST3 (vst3sdk 3.8, MIT)                        │
                        │    CLAP (clap SDK, MIT)            ← Phase 3      │
                        │  zeus-au-bridge (macOS)                           │
                        └───────────────────────▲──────────────────────────┘
                                                │ P/Invoke (Phases 1–3)
                                                │ or shm + control IPC via a
                                                │ host process (Phase 4)
                        ┌───────────────────────┴──────────────────────────┐
                        │ Zeus.Plugins.Host: scan (child process), chain,   │
                        │ state persistence, editor control                 │
                        └──────────▲─────────────────────────▲──────────────┘
                                   │                         │
            Local deployment       │                         │   Remote deployment
   ┌───────────────────────────────┴───┐     ┌───────────────┴────────────────────────┐
   │ OpenhpsdrZeus --desktop (Mac/PC)  │     │ OpenhpsdrZeus --client http://g2:6060  │
   │  full server + WDSP + Protocol 2  │     │  loopback proxy → Pi (UI, /api, /ws)   │
   │  RX chain: DspPipelineService.Tick│     │  local routes: plugins, audio devices  │
   │  TX chain: ProcessTxBlock (pre-TXA)│    │  /ws 0x02 → RX chain → miniaudio out   │
   └───────────────────────────────────┘     │  miniaudio mic → TX chain → /ws 0x20   │
                                             └────────────────────┬───────────────────┘
                                                                  │ LAN
                                                   ANAN Core server on the G2's Pi
                                                   (unchanged; owns MOX/PTT/PS/RF)
```

Rule from the original proposal (Section 35) holds: radio/RF/WDSP/protocol/protection stays on the server; desktop audio devices and desktop plugins stay on the desktop; the network carries audio and control state, never synchronous DSP callbacks.

---

## 7. Phases

Each phase ends with a PR into `freedv-in-core` in the fork, green on macOS/Windows/Linux CI, and is independently useful. No phase silently expands into the next.

### Phase 1 — VST3 builds again, everywhere

**Scope**
1. Re-add `native/zeus-vst-bridge/third_party/vst3sdk` as a submodule pinned to a 3.8.x tag (MIT); init only `base`, `pluginterfaces`, `public.sdk`, `cmake`. Restore the `.gitmodules` stanza.
2. Make the stub build real: guard SDK includes/implementation in `bridge.cpp` with `ZEUS_VST_BRIDGE_STUB` so a tree without the submodule still compiles (every load returns "unavailable", every block passes through).
3. CI (`build-native-libs.yml`): VST3 bridge for `osx-arm64`, `osx-x64`, `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`; AU bridge for `osx-arm64` + `osx-x64` (or one universal dylib). Stage into `Zeus.Plugins.Host/runtimes/<rid>/native/`. Ungate the relevant `release.yml` steps from the upstream org name.
4. Replace the committed prebuilt binaries with CI-built ones (or document the local build + staging script for dev).
5. Per-OS default VST3 folders in `AudioSuiteWindow.tsx` (macOS `/Library/Audio/Plug-Ins/VST3`, `~/Library/Audio/Plug-Ins/VST3`; Linux `~/.vst3`, `/usr/lib/vst3`, `/usr/local/lib/vst3`; Windows as today). Better: serve defaults from the server so the UI stays platform-agnostic.
6. Fix `VstDirectoryScanService` so macOS `.vst3` bundles are recognised when the bridge is absent (no PE-header rejection).
7. Get `VstBridgeNativeRealTests` running in CI against a small MIT/test VST3 (e.g. the SDK's `again` sample, built in CI).

**Acceptance:** on a Mac in local desktop mode, scan finds VST3s in the standard folders, one loads into RX and TX and audibly processes; bridge binaries exist for all six RIDs from CI; a checkout without submodules still builds and runs with plugins reported unavailable.

**Red-light for upstream:** new submodule dependency (vst3sdk), CI matrix change.

**Status — done.** vst3sdk pinned at `v3.8.1_build_84`; stub build; `build-plugin-bridges.yml` (6 VST3 RIDs + AU macOS, export check, test-plug-in load) and `build-test.yml` build the bridge first; CI-built binaries committed for every RID. Discovery during the phase, fixed in the bridge: sidechain plug-ins (FabFilter Pro-Q/Pro-C, most dynamics) never loaded because `setBusArrangements` named only the main bus; `setActive` was called before `initialize` and crashed u-he plug-ins; the committed AU dylib required macOS 26. Item 6 (PE-heuristic rejecting macOS bundles) needed no change: with the bridge present, non-PE bundles go through `describe`. A sweep of 392 installed macOS VST3s loads every audio effect; instruments are refused; one plug-in (MPC 3) hangs in load — Phase 2's probing.

### Phase 2 — Make the host solid (VST3 + AU)

**Scope**
1. **macOS VST3 editor:** `IPlugView` with `kPlatformTypeNSView` in an `NSWindow`, dispatched to the main queue exactly as `zau` does. Handle `IPlugFrame::resizeView`. Document that editors require desktop mode (the Photino loop owns the main thread); headless mode reports "editor unavailable" instead of silently succeeding (also fix the AU path).
2. **State:** add `zvst_get_state` / `zvst_set_state` (Thetis blob format, versioned) and `zau_get_state` / `zau_set_state` (`kAudioUnitProperty_ClassInfo` plist). Bump both ABIs. Persist per chain slot in `zeus-prefs.db` (new collection keyed by plugin ID + slot instance ID); restore off the audio thread before insert. Dirty-driven debounced save. Decide whether `TxAudioProfile.VstPluginStates` becomes the storage or is retired.
3. **Per-plugin bypass:** VST3 `kIsBypass` parameter when exposed, else host-side bypass; AU `kAudioUnitProperty_BypassEffect`. Wire the existing `AudioChain` per-slot bypass through an endpoint; UI toggle distinct from "park".
4. **Load by class UID** (`zvst_load_vst3` gains a UID argument; manifest `vst3Uid` populated by scan) so shell/multi-class modules work.
5. **AU latency** (`kAudioUnitProperty_Latency`) and **64-bit VST3 processing** fallback.
6. **Output parameter cache** → controller at ~30 Hz so in-editor meters move.
7. **Audio-path try-lock:** state/editor operations never block `process()`; contention → pass-through for that block.
8. **Crash-safe scanning:** `OpenhpsdrZeus --plugin-probe <path|au-id>` child process prints JSON (reads `moduleinfo.json` first); per-plugin timeout; failures cached as unavailable, keyed by path + mtime; UI shows unavailable plugins with a rescan option. The server never loads an unscanned module in-process.
9. **Policy:** new chain insertions start bypassed on TX (operator enables deliberately); document. *(Operator-felt default → flag for review.)*

**Acceptance:** VST3 + AU editors open on macOS/Windows/Linux(X11); plugin settings survive restart; a deliberately crashing test plugin fails its scan without affecting the server; per-plugin bypass is transparent.

**Red-light for upstream:** ABI changes (internal, but new persistence collection), TX default-bypassed policy.

**Status — done, with these decisions:**
- **Bypass is host-side** for every format: the chain skips the slot (bit-identical pass-through) and the plugin stays loaded in position. Uniform across VST3/AU/CLAP; a plug-in's own `kIsBypass` is not driven. Persisted per plugin id (`audio_plugin_bypass`), toggled from a Power button on each chain chip (UX addition — flag for review).
- **State** lives in `zeus-prefs.db` (`audio_plugin_state`, keyed by plugin id), restored right after each native load. Saved when it changes: on editor close (from the UI or the window's own close button), on unload/shutdown, and every 2 s while an editor is open. TX and RX **profiles** now carry each hosted plugin's state (the previously unused `VstPluginStates` / RX `PluginStates`), so applying a profile restores plugin settings too.
- **Crash-safe probing** runs the host binary as `OpenhpsdrZeus --plugin-probe …`: VST3 scanning describes each module in a child process (4 in parallel), and a plugin is trial-loaded in a child process before its first in-process load. Crashes, hangs (20–30 s limit, process killed) and refusals are cached per plugin version (`<plugin-root>/.probe-cache.json`). Without a probe host (e.g. a test runner) behaviour falls back to in-process.
- **Multi-class modules** register one plugin per effect class, pinned by class UID; instrument classes are skipped.
- **Not done: "new TX inserts start bypassed."** Scanned plugins already arrive parked, so adding one to the chain is the deliberate act; a second gate would only confuse.
- ABI: zvst v4, zau v3. Native ctest suite (`native/zeus-vst-bridge/tests`) runs in CI.

### Phase 3 — CLAP

**Scope:** vendor `free-audio/clap` (MIT, header-only) under the VST bridge (or a sibling `zclap` target inside the same library); scan (`clap_entry` → factory → descriptors, in the probe process); load by plugin ID; `activate` / `start_processing` / `process` with f32 audio ports (mono/stereo negotiation via `clap.audio-ports`); state via `clap.state`; latency via `clap.latency`; bypass host-side; editor via `clap.gui` (cocoa / win32 / x11 embedded, floating fallback); `clap.params` flush when not processing. Manifest `format: "clap"`, IDs `com.openhpsdr.zeus.{clap,rxclap}.<slug>`. Default folders: macOS `/Library/Audio/Plug-Ins/CLAP`, `~/Library/Audio/Plug-Ins/CLAP`; Windows `%COMMONPROGRAMFILES%\CLAP`, `%LOCALAPPDATA%\Programs\Common\CLAP`; Linux `~/.clap`, `/usr/lib/clap`.

**Acceptance:** a CLAP effect loads in RX/TX on all three OSes with editor, state, and bypass parity with VST3.

**Red-light for upstream:** new dependency (CLAP SDK).

### Phase 4 — Plugin fault isolation (out-of-process host)

Direction: out-of-process, per the Thetis-VST design, made portable. Details to be discussed before starting.

**Sketch**
- `zeus-plugin-host` process per chain (RX, TX), built from the same native bridge sources; the .NET chain talks to it instead of P/Invoking directly. In-process remains as a fallback/debug mode.
- Audio: shared-memory SPSC ring (POSIX `shm_open` / Windows file mapping) with fixed block slots; control: length-prefixed packets over a Unix-domain socket / named pipe, every call with a timeout.
- Real-time policy: the server's audio thread never waits. RX late block → repeat-last or silence (decide); **TX late block → pass-through unprocessed mic audio**, never stale processed audio.
- Supervisor: detect crash/hang, restart once, replay chain + state (generation counter prevents saving half-applied chains), surface status in the Audio Suite; repeated failure → chain auto-bypassed with a visible reason.
- Editors: owned by the host process (`NSWindow` / HWND / X11); on macOS the host process needs its own main-thread run loop, which also removes the "editors only in desktop mode" limitation.
- Reference: retired engine designs in `docs/designs/vst-out-of-process-engine.md` / `vst-engine-bridge-protocol.md`, and Thetis-VST `VstCommon/vst_ipc.h`, `VstHostBridge/vst_host_bridge.cpp`.

**Open questions:** latency budget per chain (Thetis uses 8 blocks RX / 2 TX adaptive); one process per chain vs per plugin; process priority / MMCSS / `pthread` RT on each OS; whether the Phase 5 client uses the same host process.

**Red-light for upstream:** architecture (new process, IPC, signal routing).

### Phase 5 — Remote client mode (server on the G2's Pi)

**Scope**
1. `OpenhpsdrZeus --client <url>` (and a server picker in the shell): starts **no** radio/DSP stack. Runs a loopback Kestrel that:
   - reverse-proxies everything (SPA assets, `/api/*`, `/ws`) to the Pi, so the UI version always matches the server and the SPA is same-origin (the server has no CORS);
   - serves locally: plugin scan/chain/editor/state routes, audio-device routes, and a `capabilities` response that reports native audio so the embedded SPA does not start Web Audio playback or `getUserMedia`.
   Photino loads the loopback URL. The exact local-route list comes from the Audio Suite's API surface; aim for **zero SPA changes**.
2. **Audio endpoint** (a dedicated `/ws` connection to the Pi, independent of the UI's socket):
   - RX: parse `0x02` frames → jitter ring → **adaptive resampler** (target ring occupancy, ±few hundred ppm correction) → RX chain → miniaudio output (reuse `NativeAudioSink` ring/prebuffer where possible).
   - TX: miniaudio mic (reuse `NativeMicCapture`) → TX chain → sanitize (NaN/Inf) → 960-sample `0x20` frames, **sent only while the server's state push (`0x3C`) shows MOX or TX monitor**. On disconnect the client stops sending; the server's ring starves to zero IQ.
   - Honour master mute locally (the server's `WebSocketAudioSink` does not).
3. Diagnostics: ring occupancy, resampler ratio, underrun/overflow counts, sequence gaps, per-plugin process time — exposed on a local route and shown in the Audio Suite.
4. Version/capability negotiation: the client checks the server's version/capabilities and disables native audio (keeping the UI usable) if incompatible.

**Acceptance:** with the server on a G2 Ultra, a Mac client plays RX through a VST3/AU/CLAP chain to a local device for hours without drift failure; TX mic through a plugin chain reaches WDSP only under server MOX; killing the client mid-transmission yields silence; browser-only operation of the same server is unchanged.

**Open questions:** multiple simultaneous clients sending mic (server mixes all senders into one accumulator today); TX auto-gain the browser applies client-side (`mic-uplink-session.ts:56-58`) needs a native equivalent or explicit omission; whether any server change becomes necessary (if so, it is a generic native-audio endpoint, not "VST over the network").

**Red-light for upstream:** new host mode (architecture).

### Later (not scheduled)

Generic parameter UI in React, plugin artwork in the rack, per-receiver RX chains, WAN/WebRTC audio for the remote client, MIDI mapping to plugin parameters, plugin-delay reporting in end-to-end latency diagnostics.

---

## 8. Test strategy

- **Unit (.NET):** chain ordering/bypass/state serialization, scan cache + blacklist, manifest generation per format, resampler controller, `/ws` frame parse/build, mic gating on MOX state.
- **Native:** bridge tests against SDK sample plugins (VST3 `again`, CLAP `clap-plugins` gain) built in CI; state round-trip bit-exactness; bypass transparency.
- **Fault plugins:** a tiny in-repo test plugin that can crash in scan, hang in `process`, emit NaN, report huge latency — used by Phase 2 (scan isolation) and Phase 4 (runtime isolation).
- **e2e:** extend `zeus-web/e2e/vst-in-process.spec.ts` beyond the stubbed backend once CI-built bridges exist.
- **Bench (RF):** TX phases verified into a dummy load on the G2; confirm no keying without server MOX, silence on client/plugin failure, CFC still downstream, PureSignal behaviour unchanged.

## 9. Upstream contribution notes

- Keep each phase a separate, reviewable PR; Phases 1–3 touch no server signal routing.
- Call out every red-light item in the PR description (dependency, ABI, default, architecture) for Apache Labs maintainer review.
- Keep the plugin engine optional: ANAN Core must build and run with no native bridge present.
- Add SPDX `GPL-2.0-or-later` headers to anything transplanted from Thetis-VST; record provenance in `ATTRIBUTIONS.md`.
- Record the vst3sdk and CLAP SDK licenses (MIT) in `ATTRIBUTIONS.md`; follow Steinberg's VST trademark usage guidelines in UI text.

## 10. Unrelated issue found during discovery

`MsgType.VfoState` and `MsgType.MidiLearn` are both `0x3B` (`Zeus.Contracts/MsgType.cs:257`, `:301`). `ws-client.ts` checks MIDI learn first (`:609`), so binary VFO-state frames are parsed as MIDI JSON and dropped; the `0x3C` state push likely masks it. Fix is a wire-format change → separate PR, flagged for review.
