# ANAN Core Native Desktop DSP / VST3 Host — Implementation Plan

**Status:** Architecture and implementation handoff  
**Date:** 2026-09-30  
**Primary target:** ANAN Core running on an ANAN G2 / G2 Ultra, with a native macOS/Windows/Linux desktop client that embeds the existing `zeus-web` UI and performs native desktop DSP/plugin hosting locally.  
**Initial plugin target:** VST3.  
**Secondary plugin target:** Audio Unit on macOS.  
**Reference prior work:** `ChasingCoffee/Thetis-VST` (reuse concepts/code where licensing and architecture permit).

---

## 1. Executive summary

Build a **network-only native desktop client for ANAN Core** that:

1. embeds the existing ANAN Core browser UI (`zeus-web`),
2. connects that UI to the ANAN Core server running on the radio,
3. terminates RX/TX audio on the desktop instead of relying on browser audio,
4. hosts desktop-native VST3 plugins (and later AU on macOS), and
5. keeps all radio ownership, Protocol 2, WDSP, PureSignal, RF safety, band/mode state, and TX authority on the ANAN Core server.

The desired architecture is **not** “run a VST host on the Raspberry Pi.” It is:

```text
                  ANAN G2 / G2 Ultra
          ┌───────────────────────────────┐
          │ ANAN Core server / engine     │
          │                               │
Radio <──>│ Protocol 2 + WDSP             │
          │ Radio state / TX authority    │
          │ RX audio egress               │
          │ TX mic audio ingress          │
          └──────────────┬────────────────┘
                         │ LAN
                         │ existing or minimal audio protocol
                         ▼
          ┌───────────────────────────────┐
          │ Native desktop client         │
          │                               │
          │ Embedded zeus-web             │
          │   └─ normal radio controls    │
          │                               │
          │ Native audio engine           │
          │   ├─ RX VST3/AU chain         │
          │   ├─ TX VST3/AU chain         │
          │   ├─ local input/output       │
          │   └─ plugin editor windows    │
          └───────────────────────────────┘
```

The **first architectural rule** is to avoid adding a new server-side DSP/plugin mechanism until the existing browser audio data path has been traced. If the browser already receives suitable post-WDSP RX PCM and uploads suitable pre-WDSP TX microphone PCM, the native client should implement that same protocol directly. Server changes should be minimal and only added where the existing protocol cannot support a correct native audio client.

The **second architectural rule** is to avoid treating a remote desktop VST as a synchronous server plugin callback. Do not put LAN/network latency inside the server's real-time DSP callback. The desktop is an audio endpoint/client, not a remote function call from WDSP.

---

## 2. Current upstream context to verify before coding

The current Apache Labs ANAN Core source is at:

- <https://github.com/abhishekprakash22/zeus>
- Current development branch observed while this plan was written: `freedv-in-core`

The repository currently contains the major seams relevant to this project:

```text
OpenhpsdrZeus/
Zeus.Contracts/
Zeus.Dsp/
Zeus.Plugins.Contracts/
Zeus.Plugins.Host/
Zeus.Plugins.VstHostStub/
Zeus.Protocol1/
Zeus.Protocol2/
Zeus.Server.Hosting/
zeus-web/
zeus-mobile/
```

The root README describes ANAN Core as GPL-2.0-or-later with **engine, server, and client in one source tree**. The G2/G2 Ultra is the reference platform.

`zeus-mobile` is especially important. It already establishes the pattern we want for a native network-only client:

- bundle the existing `zeus-web` frontend,
- run it inside a native WebView,
- store a server URL,
- rewrite `/api/*`, `/ws`, and `/hub/*` traffic to the selected ANAN Core server,
- use the same browser microphone/audio behavior as the web client.

This project should reuse that model rather than inventing a second UI stack.

### Mandatory repository rules

Before editing ANAN Core, the implementing agent must read:

```text
CLAUDE.md
AGENTS.md
```

At the time this plan was written, those files establish several important constraints:

- cross-platform behavior is a hard requirement,
- architecture changes, new dependencies, wire-format changes, and signal-routing restructures require maintainer review,
- PureSignal is explicitly protected and must not be changed without approval,
- backend dev port is `6060`,
- Vite dev port is `5173`,
- Thetis is the reference implementation for DSP/protocol behavior,
- the repository uses `bd`/Beads for task tracking.

**Do not bypass those project-specific rules just because this plan recommends an architecture.** The repository's current instructions take precedence.

---

## 3. Project goal

Create a desktop application that feels like a native ANAN Core client but reuses the official browser UI.

The application should eventually provide:

- ANAN Core UI embedded in the desktop application,
- connection to a G2/G2 Ultra running ANAN Core server-side,
- local native audio device selection,
- local RX audio playback,
- local microphone capture,
- VST3 processing on RX,
- VST3 processing on TX,
- AU processing on macOS as a later extension,
- native plugin scanning,
- native plugin editor windows,
- chain ordering and per-plugin bypass,
- master bypass,
- saved plugin chains/profiles,
- plugin-state persistence,
- meters and diagnostics,
- clean fallback to the ordinary browser audio path when native DSP is unavailable.

The target user experience is:

```text
Open ANAN Core Native
    ↓
Select / discover radio server
    ↓
ANAN Core UI appears exactly as expected
    ↓
Native DSP capability is detected
    ↓
Choose local audio input/output
    ↓
Add VST3/AU plugins to RX or TX chain
    ↓
Operate radio normally from the embedded ANAN Core UI
```

---

## 4. Non-goals for the first implementation

Do **not** begin by doing any of the following:

- moving WDSP from the G2 to the desktop,
- moving Protocol 2 ownership from the G2 to the desktop,
- replacing the ANAN Core server,
- replacing `zeus-web`,
- rewriting the radio UI natively,
- modifying PureSignal,
- modifying RF safety behavior,
- making the native client responsible for MOX/PTT state,
- introducing virtual audio cables as the primary architecture,
- sending a synchronous VST processing request from a server DSP callback across the network,
- solving WAN audio before LAN audio works correctly,
- implementing every plugin format at once,
- implementing a general third-party ANAN Core plugin framework.

The first useful product is **ANAN Core UI + local desktop audio + one local VST3 in the RX path**.

---

## 5. Core architectural decision

### Recommended model

Treat the desktop application as a **specialized audio client** of the existing ANAN Core server.

The server remains authoritative for:

- radio discovery/connection,
- Protocol 1/2,
- WDSP,
- demodulation,
- transmit modulation,
- filters,
- CFC and other built-in DSP,
- PureSignal,
- RF safety,
- meters,
- VFO/band/mode state,
- MOX/PTT state.

The desktop application owns:

- local microphone device,
- local speaker/headphone device,
- local VST3/AU discovery,
- local plugin instances,
- local plugin editor windows,
- local plugin profiles/state,
- RX post-demod plugin processing,
- TX pre-WDSP plugin processing,
- network jitter/clock adaptation for its audio endpoint.

### Preferred RX path

```text
RF
 ↓
ANAN Core / Protocol 2
 ↓
WDSP RX / demodulation
 ↓
post-WDSP RX PCM
 ↓  LAN
Native desktop client
 ↓
RX VST3/AU chain
 ↓
local output device
```

### Preferred TX path

```text
local microphone
 ↓
Native desktop client
 ↓
TX VST3/AU chain
 ↓  LAN
ANAN Core mic ingress
 ↓
WDSP TX processing / CFC / modulation
 ↓
Protocol 2
 ↓
RF
```

This keeps the same important signal ordering used by the existing Zeus/ANAN Core Audio Suite concept: third-party TX processing is upstream of the built-in WDSP TX chain.

---

## 6. Critical discovery phase — do this before implementing architecture

The implementing agent's first work item is a **read-only trace of the current audio paths**.

Do not start by creating new endpoints.

### 6.1 Trace browser RX audio end-to-end

Find the exact path from WDSP output to browser speakers.

Document:

- server class/method producing RX audio,
- where samples leave WDSP,
- sample type (`float`, `int16`, etc.),
- sample rate,
- channel count,
- block/frame size,
- transport used (raw WebSocket, SignalR, WebRTC, another stream),
- framing/header structure,
- sequence/timestamp behavior,
- client-side jitter buffering,
- client-side resampling,
- final Web Audio API call/site,
- whether multiple RX receivers have separate streams,
- whether mute/volume is server-side or client-side.

Search likely areas:

```text
Zeus.Server.Hosting/
Zeus.Dsp/
zeus-web/src/
```

Look for terms such as:

```text
audio
pcm
rxAudio
speaker
playback
AudioContext
AudioWorklet
WebSocket
StreamingHub
hub
opus
webrtc
```

### 6.2 Trace browser TX microphone end-to-end

Find the exact path from `getUserMedia()` to WDSP TX input.

Document:

- browser capture API,
- browser sample format and sample rate,
- resampling behavior,
- packet/frame size,
- transport,
- server receive endpoint,
- queue/ring implementation,
- underrun behavior,
- point where mic PCM enters WDSP,
- relationship to MOX/PTT,
- silence/timeout behavior if the client disconnects,
- whether TX audio transport exists while MOX is false.

Search likely areas for:

```text
getUserMedia
microphone
mic
mic uplink
txAudio
AudioWorklet
MediaStream
```

### 6.3 Trace existing VST/plugin hooks

Inspect:

```text
Zeus.Plugins.Contracts/
Zeus.Plugins.Host/
Zeus.Plugins.VstHostStub/
```

Answer:

- What interface does the VST host stub implement?
- Is that interface intended for server-local in-process audio only?
- What buffers/sample rates does it expect?
- Where is it called from TX/RX DSP?
- Is there already an abstraction that separates plugin metadata/control from real-time processing?
- Does the UI already know about VST availability/state?
- Is there already a platform capability flag?

**Important:** do not assume these hooks are the right mechanism for remote native DSP merely because they exist.

### 6.4 Trace the mobile wrapper pattern

Inspect:

```text
zeus-mobile/
zeus-web/src/serverUrl.ts
```

Understand exactly how the mobile WebView redirects:

```text
/api/*
/ws
/hub/*
```

to a remote ANAN Core server.

The native desktop client should, where practical, use this exact frontend connection model.

### 6.5 Inspect the prior Thetis VST work

Reference repo:

- <https://github.com/ChasingCoffee/Thetis-VST>

Find the VST-support branch/history and inventory reusable pieces:

- plugin scanning,
- plugin identity model,
- plugin chain model,
- state serialization,
- editor-window hosting,
- bypass handling,
- real-time processing wrapper,
- native bridge/process boundary,
- crash handling,
- licensing/dependencies.

Do not mechanically transplant Thetis-specific audio plumbing. Reuse the host technology and data model where useful; rebuild the ANAN Core transport boundary around ANAN Core's current audio protocol.

### 6.6 Discovery deliverable

Before feature code, produce a short design note with this table completed:

| Question | Current implementation | Native-client consequence |
|---|---|---|
| RX sample rate | TBD | TBD |
| RX sample format | TBD | TBD |
| RX transport | TBD | TBD |
| RX client jitter strategy | TBD | TBD |
| TX sample rate | TBD | TBD |
| TX sample format | TBD | TBD |
| TX transport | TBD | TBD |
| TX underrun behavior | TBD | TBD |
| Mic insertion point | TBD | TBD |
| Existing VST host contract | TBD | TBD |
| Native client can reuse protocol without server changes? | TBD | yes/no |

This is the first decision gate.

---

## 7. Decision hierarchy after discovery

Choose the **highest** option that works.

### Option A — preferred: no server audio changes

If the browser's existing RX/TX transport is appropriate for a native client:

- implement the same wire protocol in native code,
- leave the server audio routing untouched,
- leave `Zeus.Plugins.*` untouched for the MVP,
- use the native bridge only for UI/plugin management.

This is the best outcome.

### Option B — acceptable: minimal dedicated native-audio transport

If the browser protocol is too coupled to Web Audio, inefficient for PCM, or lacks required timing metadata:

Add a small, explicit audio-client transport to `Zeus.Server.Hosting`.

It should expose audio endpoint semantics, not VST semantics.

For example:

```text
/native-audio/control
/native-audio/stream
```

or equivalent existing project naming.

Keep the transport generic enough that a future native client without VST could use it.

### Option C — avoid unless maintainer explicitly wants it

Implement a server `RemoteVstHost` that blocks server DSP while waiting for processed audio from a desktop machine.

This design couples real-time DSP to network latency/jitter and creates poor failure behavior. Do not choose it simply because the current plugin contract appears convenient.

---

## 8. Native desktop application architecture

### 8.1 Application layers

```text
NativeDesktopApp
├── Application shell
│   ├── lifecycle
│   ├── server selection
│   ├── preferences
│   └── update/version info
│
├── Web UI host
│   ├── embedded zeus-web build
│   ├── serverUrl configuration
│   ├── navigation restrictions
│   └── JS/native bridge
│
├── ANAN Core network client
│   ├── RX audio receiver
│   ├── TX audio sender
│   ├── connection health
│   ├── sequence/timestamp tracking
│   └── reconnect
│
├── Native audio engine
│   ├── input device
│   ├── output device
│   ├── jitter/ring buffers
│   ├── resampling / clock adaptation
│   └── metering
│
├── Plugin host
│   ├── VST3 discovery
│   ├── AU discovery (macOS later)
│   ├── RX chain
│   ├── TX chain
│   ├── plugin editor windows
│   └── plugin state/presets
│
└── Persistence
    ├── app settings
    ├── device selections
    ├── plugin inventory cache
    ├── RX/TX chain definitions
    └── plugin state blobs
```

### 8.2 Reuse `zeus-web`; do not rewrite the UI

The native application should bundle the compiled `zeus-web` assets the same way `zeus-mobile` does, rather than rendering a new SDR UI.

Recommended flow:

```text
build zeus-web
   ↓
copy dist into native application resources
   ↓
load local bundled UI in native WebView
   ↓
set zeus.serverUrl = http://<g2>:6060
   ↓
existing serverUrl.ts routes APIs/WebSockets/hubs to G2
```

This is preferable to loading arbitrary HTTP UI directly from the radio because:

- it follows an existing ANAN Core client pattern,
- native bridge access can be restricted to bundled/trusted app content,
- arbitrary LAN pages do not get access to native plugin APIs,
- the native client and its UI can be versioned/tested together.

A later compatibility mechanism may allow the desktop shell to use server-served frontend assets, but that should not be the first version.

---

## 9. Native framework choice

Do not choose a framework before inspecting the user's existing `Thetis-VST` hosting code and its dependencies.

### Preferred decision criteria

The framework must support:

- VST3 hosting on macOS and Windows,
- native audio devices,
- native plugin editor windows,
- real-time-safe audio callbacks,
- CMake or another reproducible build system,
- embedded WebView,
- Apple Silicon,
- Windows x64,
- preferably Linux x64,
- crash-safe plugin scanning,
- plugin state serialization.

### JUCE

JUCE is technically a strong fit because it provides:

- `AudioPluginFormatManager`,
- VST3 hosting,
- AU hosting on macOS,
- audio-device abstraction,
- plugin editor hosting,
- `AudioProcessorGraph`,
- mature desktop application support.

However, **perform a license review before committing to JUCE**. ANAN Core is GPL-2.0-or-later and JUCE has its own licensing model. The implementation must have a redistribution/source strategy that is actually compatible with the intended upstream/distribution model.

If the existing Thetis VST work already solved hosting with another library or bridge, strongly consider reusing that approach.

### Do not let framework selection block the protocol proof

The first native proof can be a minimal command-line or test app that receives RX PCM and plays it. Plugin hosting can be added after the ANAN Core audio boundary is proven.

---

## 10. Web/native bridge design

The browser UI needs only a **small capabilities/control bridge**. Do not expose general filesystem/process execution to JavaScript.

Suggested conceptual API:

```ts
interface AnanNativeBridge {
  getCapabilities(): Promise<NativeCapabilities>;

  audio: {
    getDevices(): Promise<AudioDeviceInfo[]>;
    getState(): Promise<NativeAudioState>;
    setInputDevice(id: string): Promise<void>;
    setOutputDevice(id: string): Promise<void>;
    getStats(): Promise<AudioStats>;
  };

  plugins: {
    scan(): Promise<PluginDescriptor[]>;
    list(): Promise<PluginDescriptor[]>;
    getChain(kind: "rx" | "tx"): Promise<PluginChainState>;
    add(kind: "rx" | "tx", pluginId: string): Promise<void>;
    remove(kind: "rx" | "tx", instanceId: string): Promise<void>;
    reorder(kind: "rx" | "tx", instanceIds: string[]): Promise<void>;
    setBypass(instanceId: string, bypass: boolean): Promise<void>;
    setMasterBypass(kind: "rx" | "tx", bypass: boolean): Promise<void>;
    openEditor(instanceId: string): Promise<void>;
    closeEditor(instanceId: string): Promise<void>;
  };
}
```

`NativeCapabilities` should be versioned:

```json
{
  "bridgeVersion": 1,
  "nativeAudio": true,
  "vst3": true,
  "audioUnit": true,
  "clap": false,
  "platform": "macos-arm64"
}
```

### Security rules for the bridge

- only bundled/trusted `zeus-web` content may call it,
- external navigation must not retain bridge access,
- validate every argument in native code,
- plugin scanning must use predetermined/user-approved plugin locations,
- never expose arbitrary shell execution,
- never expose arbitrary file read/write APIs,
- bridge version must be explicit,
- the native layer must tolerate an older/newer UI gracefully.

---

## 11. Web UI integration strategy

### Phase-1 UI

Do not immediately modify the full ANAN Core Audio Suite UI.

For the first working build, expose native DSP via one of:

- a small native menu,
- a minimal native DSP settings window,
- a developer/debug panel.

The goal is proving signal flow.

### Integrated UI after signal flow is stable

Add a frontend capability check:

```ts
const native = await detectNativeBridge();
```

Then the existing Audio Suite or Audio Tools UI can display something like:

```text
Native DSP: Connected
Platform: macOS arm64
RX VST3: available
TX VST3: available
AU: available
```

Prefer a provider abstraction rather than hard-coding platform logic throughout React:

```text
AudioPluginProvider
├── ServerPluginProvider
└── NativePluginProvider
```

The React UI should manipulate a logical chain; the provider decides where plugin operations are executed.

### Native plugin editors

Do not attempt to render arbitrary VST3/AU editors into HTML.

When the user clicks **Open Editor**:

```text
React button
  ↓ JS/native bridge
native host
  ↓
plugin's native editor window
```

Closing the editor must not unload the plugin or stop processing.

---

## 12. Audio transport design

### 12.1 Prefer the existing transport

If the existing web audio path already transports PCM with acceptable framing and semantics, reproduce it exactly in native code first.

Do not create a new wire format merely to make the native implementation aesthetically cleaner.

### 12.2 If a dedicated PCM protocol is required

Use binary frames, not JSON/base64 audio.

A conceptual frame can contain:

```text
magic/version
stream kind (rx1/rx2/tx/etc.)
sequence number
timestamp/sample counter
sample rate
channel count
sample format
frame count
payload
```

Example conceptual C structure:

```c
struct AudioFrameHeader {
    uint32_t magic;
    uint16_t version;
    uint16_t stream_id;
    uint64_t sequence;
    uint64_t sample_counter;
    uint32_t sample_rate;
    uint16_t channels;
    uint16_t format;
    uint32_t frames;
};
```

Do not freeze this structure until existing ANAN Core wire conventions have been inspected.

### 12.3 LAN first

For LAN operation, uncompressed PCM is preferred unless the existing client path dictates otherwise.

At 48 kHz mono float32:

```text
48,000 samples/s × 4 bytes ≈ 192 KB/s
```

Even bidirectional PCM is trivial on normal Ethernet/Wi-Fi compared with IQ streaming.

WAN/Opus/WebRTC can be a later transport mode.

---

## 13. Clocking, buffering, and resampling

This is a first-class design issue, not polish.

The G2/radio audio clock and the desktop audio-device clock will not be perfectly identical even when both report 48 kHz. A simple FIFO will eventually underrun or overflow.

### RX

The desktop output callback should be the local playback clock master.

Recommended model:

```text
network RX thread
   ↓
SPSC ring / jitter buffer
   ↓
adaptive resampler / rate matcher
   ↓
RX plugin chain
   ↓
local output callback
```

Maintain a target ring occupancy. Apply very small asynchronous sample-rate correction to keep occupancy centered.

### TX

The local input callback is the microphone clock source.

Recommended model:

```text
local mic callback
   ↓
TX plugin chain
   ↓
rate adaptation to server-required rate
   ↓
SPSC transmit queue
   ↓
network sender
```

If the server already performs robust mic resampling/clock adaptation for browsers, reuse its expectations rather than duplicating behavior incorrectly.

### Requirements

- no heap allocation in the real-time audio callback,
- no mutex waiting in the real-time audio callback,
- no network calls from the real-time audio callback,
- no JSON serialization in the real-time audio callback,
- bounded rings,
- explicit underrun/overflow counters,
- click-free recovery where possible,
- silence on fatal TX audio failure.

---

## 14. Plugin processing engine

### 14.1 Separate RX and TX chains

```text
RX chain
  plugin 1
  plugin 2
  ...

TX chain
  plugin 1
  plugin 2
  ...
```

Each chain requires:

- ordered instances,
- per-instance bypass,
- master bypass,
- input/output metering,
- persistent state,
- plugin latency reporting,
- editor open/close state independent of processing.

### 14.2 Plugin scanning

Plugin discovery can crash due to broken third-party plugins. Do not scan unknown plugins in the main UI/audio process if the chosen host framework supports safer scanning.

Preferred behavior:

```text
main app
  ↓ spawn
scanner child process
  ↓
scan one plugin / small batch
  ↓
return descriptor
```

Cache successful scan results.

Record failures so the application does not crash-loop on startup.

### 14.3 Plugin processing lifecycle

For each instance:

1. instantiate off the audio thread,
2. configure sample rate/block-size expectations,
3. restore state off the audio thread,
4. atomically insert into chain,
5. process only from real-time thread,
6. remove using a safe graph swap/suspend mechanism,
7. destroy off the audio thread.

### 14.4 Reconfiguration

Changing plugin order, loading a preset, or changing audio device must not mutate a live processing graph unsafely.

Use either:

- immutable graph + atomic swap, or
- controlled suspend/reconfigure/resume with a short silence window.

### 14.5 Plugin latency

Query/report plugin latency where available.

For the first serial chain, full plugin-delay compensation is not essential because there is no dry parallel branch that must stay phase-aligned. However:

- display total reported plugin latency,
- include it in diagnostics,
- account for it when measuring end-to-end latency,
- avoid silently claiming “zero-latency” operation.

---

## 15. TX safety invariants

These are mandatory.

### Radio authority remains on the server

The native app must **not** become authoritative for:

- MOX,
- PTT,
- TUNE,
- drive,
- band limits,
- TX timeout,
- SWR protection,
- PureSignal arm state.

The embedded ANAN Core UI continues to request those operations from the server using the normal control plane.

### Native DSP is audio-only

The native client may provide microphone samples only when the server is in the correct TX state.

The server must remain safe if:

- the native app crashes,
- Wi-Fi drops,
- the native app stalls,
- a VST hangs,
- a plugin emits NaNs,
- a plugin emits huge amplitude,
- the TX audio stream disappears.

Expected failure behavior is **silence / server-side timeout**, never “keep transmitting stale audio.”

### PureSignal boundary

Do not change PureSignal processing, timing, state, persistence, or feedback routing as part of this project.

Plugin TX audio belongs upstream of the ordinary WDSP TX chain. PureSignal remains server/radio-side.

---

## 16. Audio sanitation

Before samples leave the native plugin chain:

- detect/replace NaN and Inf,
- meter peak/RMS,
- optionally guard against pathological amplitude according to existing ANAN Core conventions,
- do not add an unapproved “sound-changing” limiter as a hidden default,
- master bypass must provide a transparent path.

Any protective clamp/limiter that changes normal operator audio is an operator-visible default and therefore must follow ANAN Core maintainer rules.

---

## 17. Persistence model

Native plugin state should initially be **desktop-local**, not stored as authoritative radio state.

Why:

- VST installations are machine-specific,
- plugin identifiers/paths differ by OS,
- an AU may exist only on macOS,
- the G2 should remain usable without the desktop client,
- server profiles should not become invalid merely because a desktop plugin is absent.

Suggested state:

```json
{
  "schemaVersion": 1,
  "serverId": "...",
  "audio": {
    "inputDevice": "...",
    "outputDevice": "..."
  },
  "rxChain": [
    {
      "instanceId": "...",
      "pluginId": "...",
      "format": "VST3",
      "bypass": false,
      "stateBlob": "..."
    }
  ],
  "txChain": []
}
```

Prefer a stable radio/server identity over raw IP address when one is available.

Later, the ANAN Core UI can expose named local profiles such as:

```text
RX Clean
RX Broadcast
TX Ragchew
TX DX
```

but the actual third-party plugin state remains on the desktop machine.

---

## 18. Observability and diagnostics

Implement diagnostics early. Audio bugs become much easier to solve when the endpoint reports actual queue state.

Expose at least:

### Network

- connection state,
- reconnect count,
- RX/TX sequence gaps,
- bytes/sec,
- RTT if available.

### Audio

- input device sample rate,
- output device sample rate,
- server-required rate,
- RX ring occupancy,
- TX ring occupancy,
- underrun count,
- overflow count,
- adaptive resampler ratio,
- current block size.

### Plugins

- plugin name/version,
- format,
- processing enabled/bypassed,
- reported latency samples,
- average/max processing time per block,
- exception/failure state if the host framework exposes one.

### End-to-end latency

Provide a debug method for measuring:

```text
server RX audio arrival → local speaker callback
local mic callback → server TX audio acceptance
```

Do not rely on subjective “feels fast” testing alone.

---

## 19. Recommended repository layout

Do not create this structure until maintainers approve whether it belongs in-tree, but a clean standalone shape would be:

```text
zeus-native/
├── CMakeLists.txt
├── README.md
├── src/
│   ├── app/
│   │   ├── Application.*
│   │   └── Settings.*
│   ├── web/
│   │   ├── WebUiHost.*
│   │   └── NativeBridge.*
│   ├── net/
│   │   ├── ServerConnection.*
│   │   ├── RxAudioClient.*
│   │   └── TxAudioClient.*
│   ├── audio/
│   │   ├── AudioEngine.*
│   │   ├── RingBuffer.*
│   │   ├── ClockAdapter.*
│   │   └── Metering.*
│   ├── plugins/
│   │   ├── PluginManager.*
│   │   ├── PluginScanner.*
│   │   ├── PluginChain.*
│   │   └── PluginEditorHost.*
│   └── state/
│       └── NativeStateStore.*
├── tests/
│   ├── audio/
│   ├── protocol/
│   └── state/
└── resources/
    └── web/        # built zeus-web assets
```

If upstream wants the native host out-of-tree initially, keep the network protocol/client library separable so it can later move in-tree cleanly.

---

## 20. Milestone plan

No milestone should silently expand into the next one.

### Milestone 0 — architecture trace

Deliverables:

- completed RX/TX trace,
- exact browser audio wire protocol documented,
- current VST stub contract documented,
- existing mobile client routing documented,
- decision: Option A or Option B from Section 7,
- inventory of reusable `Thetis-VST` code.

Acceptance:

- another developer can point to the exact server method and frontend method for every audio boundary.

### Milestone 1 — native WebView client only

Build a desktop app that:

- embeds bundled `zeus-web`,
- lets the user set the G2 server URL,
- routes `/api`, `/ws`, `/hub` correctly,
- renders panadapter/waterfall,
- changes frequency/mode,
- receives server state,
- has no native audio/VST functionality yet.

Acceptance:

- embedded UI behaves like opening ANAN Core in a normal browser.

### Milestone 2 — native RX audio, no plugins

Implement:

```text
G2 post-WDSP RX audio
   ↓
native client
   ↓
local speaker/headphone
```

Do not add VST3 yet.

Acceptance:

- clean continuous RX audio,
- mute/volume behavior is understood,
- reconnect works,
- underrun/overflow stats exist,
- clock drift does not cause an eventual failure during a long run.

### Milestone 3 — one RX VST3

Add:

- VST3 scanning,
- one selected VST3 instance,
- bypass,
- native editor window,
- state save/restore.

Acceptance:

```text
G2 RX → VST3 → local output
```

works continuously and master bypass is transparent.

This is the first major proof of the concept.

### Milestone 4 — RX plugin chain

Add:

- multiple plugins,
- reorder,
- remove,
- per-plugin bypass,
- chain persistence,
- plugin CPU/latency stats.

Acceptance:

- graph changes do not crash/glitch catastrophically,
- state restores across application restarts.

### Milestone 5 — native TX audio, bypass only

Before TX plugins, prove the native mic path with no effects:

```text
local mic
   ↓
native client
   ↓
ANAN Core mic ingress
   ↓
WDSP TX
```

Use a dummy load/low-risk bench setup as appropriate for real-radio testing.

Acceptance:

- server retains all TX authority,
- disconnect causes silence/timeout,
- no stale audio repeats,
- native app cannot key RF by itself,
- server TX behavior matches browser-mic behavior.

### Milestone 6 — TX VST3 chain

Add TX plugins before server WDSP/CFC.

Acceptance:

```text
mic → VST3 chain → server → WDSP/CFC → RF
```

works with:

- bypass,
- plugin editor,
- saved state,
- gain staging visible,
- no modification to PureSignal logic.

### Milestone 7 — web UI integration

Integrate native capabilities into the ANAN Core Audio Tools / Audio Suite UI.

Add:

- Native DSP status,
- scan button,
- available plugins,
- RX/TX chains,
- reorder,
- bypass,
- open editor,
- device selection or link to native audio settings,
- diagnostics.

Acceptance:

- ordinary browser mode remains functional,
- native controls appear only when the bridge exists,
- no native-only assumptions leak into normal web operation.

### Milestone 8 — macOS AU support

If desired and licensing/host framework permits:

- scan Audio Units,
- instantiate/process,
- open native editors,
- persist AU state,
- allow mixed VST3/AU chains if the host abstraction supports it safely.

### Milestone 9 — Windows support

Validate:

- Windows x64 VST3,
- audio devices,
- WebView implementation,
- native editor ownership/focus,
- installer packaging.

### Milestone 10 — Linux desktop support

Validate Linux x64 where practical.

Do not let Linux VST support delay the macOS/Windows proof if upstream agrees to staged platform delivery; however, any in-tree ANAN Core changes must continue to honor the repository's cross-platform build rules.

### Milestone 11 — WAN/remote DSP mode

Only after LAN is stable, investigate whether existing ANAN Core WebRTC/remote audio can feed the native DSP client.

Possible WAN path:

```text
G2 WDSP RX
 ↓
Opus/WebRTC
 ↓
native client decode
 ↓
VST3
 ↓
local output
```

TX:

```text
local mic
 ↓
VST3
 ↓
Opus/WebRTC
 ↓
G2 server
 ↓
WDSP TX
```

Do not introduce a second WAN transport unless the existing remote stack cannot satisfy the native client.

---

## 21. Testing strategy

### 21.1 Unit tests

Cover:

- ring buffer behavior,
- sequence handling,
- jitter-buffer target logic,
- resampler/controller logic,
- plugin-chain serialization,
- plugin reorder/remove,
- state migrations,
- bridge argument validation,
- NaN/Inf sanitation.

### 21.2 Synthetic audio integration tests

The native client must be testable without transmitting RF.

Use generated:

- sine wave,
- impulse,
- white/pink noise,
- deterministic sample counters.

Verify:

- channel correctness,
- no endian/format mistakes,
- no block duplication/drop under normal LAN conditions,
- plugin bypass bit-perfect or near-bit-perfect where format conversion permits,
- state restores deterministically.

### 21.3 Network fault tests

Inject:

- packet delay,
- jitter,
- disconnect/reconnect,
- sequence gaps,
- server restart,
- client sleep/wake,
- interface change Wi-Fi ↔ Ethernet.

Verify bounded recovery and useful diagnostics.

### 21.4 Plugin fault tests

Test:

- plugin that fails scan,
- plugin with no editor,
- plugin with very large reported latency,
- plugin with unusual block-size preferences,
- plugin that changes latency,
- plugin that emits NaN,
- plugin that is CPU-heavy,
- plugin missing after restart.

The application must remain usable even if a third-party plugin is bad.

### 21.5 TX bench tests

Validate TX with conservative real-radio procedures.

At minimum verify:

- no TX without explicit server-side MOX/PTT,
- stream drop produces silence,
- plugin bypass restores clean mic audio,
- server CFC remains downstream,
- server TX protection remains active,
- PureSignal behavior is unchanged.

---

## 22. Performance goals

Measure before optimizing.

Track separately:

```text
network/buffer latency
native audio-device latency
plugin-reported latency
plugin CPU time
server DSP latency
```

A useful engineering target for LAN is that the native transport/buffering should add only a small number of audio blocks beyond the existing browser path, excluding latency intentionally introduced by plugins.

Do not achieve low latency by making the system fragile to ordinary scheduling/network jitter. Provide a user-selectable safe/low-latency buffer policy later if needed.

---

## 23. Compatibility/versioning

The native client and G2 server may update independently.

Add explicit feature/version negotiation before depending on native-specific server behavior.

Conceptually:

```json
{
  "nativeAudioProtocol": 1,
  "rxPcm": true,
  "txPcm": true,
  "sampleRates": [48000]
}
```

If the client is incompatible:

- keep the web UI usable,
- disable native DSP cleanly,
- show a clear compatibility message,
- never partially enable TX native audio.

---

## 24. Packaging and platform priorities

Recommended implementation order:

1. macOS Apple Silicon first,
2. Windows x64 second,
3. Linux x64 after the host abstraction is proven.

This is an implementation order, not permission to break ANAN Core's existing cross-platform builds.

### macOS

Eventually address:

- app bundle,
- code signing/notarization,
- microphone permission,
- local-network permission if required by the chosen shell,
- AU discovery,
- plugin editor window behavior,
- Apple Silicon plugin compatibility.

### Windows

Eventually address:

- WebView2/runtime strategy,
- WASAPI/ASIO choice as appropriate,
- VST3 default locations,
- plugin editor DPI/scaling,
- installer.

### Linux

Eventually address:

- WebKit/WebView dependency,
- ALSA/PipeWire/JACK policy via chosen framework,
- native VST3 editor requirements,
- packaging format.

---

## 25. Licensing checklist

Complete this before upstreaming or distributing binaries.

### ANAN Core

Current repository states GPL-2.0-or-later.

### Thetis-VST reuse

Before copying code from `ChasingCoffee/Thetis-VST`:

- inspect `LICENSE`,
- inspect `LICENSE-DUAL-LICENSING`,
- identify which modifications are original to the user's fork,
- preserve required notices/headers,
- confirm dependency licenses.

### Plugin host framework

For JUCE or any alternative:

- document the exact license used,
- confirm compatibility with the planned source/binary distribution,
- document whether recipients can rebuild the complete application.

### VST3 SDK

Verify the exact current Steinberg SDK license used by the selected host implementation rather than relying on historical assumptions.

### Audio Units

Document Apple framework usage and deployment requirements.

**Do not treat licensing as a release-day cleanup task.**

---

## 26. Explicitly rejected shortcuts

### Virtual audio cable as product architecture

Useful for debugging, not the target product. It creates routing complexity and loses the integrated ANAN Core experience.

### Run desktop VST binaries on the Pi

Wrong platform/architecture for most users' plugin libraries and defeats the goal.

### Move all of WDSP to desktop immediately

Possible future architecture, but vastly increases scope and duplicates server responsibilities before native plugins are proven.

### Browser-to-VST via generic local HTTP on every audio block

Not a real-time design.

### Base64 PCM in JSON

Avoid for high-rate audio.

### Let JavaScript directly own plugin objects

Keep real-time/native ownership in the native layer. JavaScript is control/UI only.

### Let the native client own MOX/PTT

Radio TX authority stays server-side.

---

## 27. Suggested first implementation sequence for an agent

Use this order exactly unless source discovery disproves an assumption.

### Step 1 — read project instructions

```text
CLAUDE.md
AGENTS.md
README.md
zeus-mobile/README.md
```

Run whatever repository bootstrap/`bd prime` workflow current instructions require.

### Step 2 — trace RX browser audio

Produce a file/method call graph from WDSP output to browser speakers.

### Step 3 — trace TX browser mic

Produce a file/method call graph from `getUserMedia` to WDSP TX input.

### Step 4 — inspect plugin contracts/stub

Document exactly what `Zeus.Plugins.VstHostStub` is replacing.

### Step 5 — inspect `Thetis-VST`

Identify reusable host components and dependency/license implications.

### Step 6 — decide Option A vs B

Prefer **Option A: native client implements existing browser audio protocol**.

Stop and request maintainer architectural approval before changing server signal routing if Option B is needed, because the current repository rules classify architecture/signal-routing changes as red-light.

### Step 7 — build web shell

Create the smallest desktop shell that bundles `zeus-web` and points it at the G2 server.

### Step 8 — build RX PCM test client

No plugins. Prove clean audio and clock stability.

### Step 9 — add one VST3

Only after RX audio is stable.

### Step 10 — add TX bypass path

No effects. Prove safety and server authority.

### Step 11 — add TX plugins

Then integrate UI/state/polish.

---

## 28. Questions the implementing agent must answer, not guess

1. What exact audio transport does current `zeus-web` use for RX?
2. What exact transport does current `zeus-web` use for TX microphone uplink?
3. Are RX samples post-WDSP and immediately suitable for speaker playback?
4. Is TX mic ingress upstream of CFC/normal WDSP TX processing?
5. Does existing browser audio already have timestamps/sequence numbers?
6. How does the browser solve clock mismatch today?
7. Can a native client reuse existing endpoints without pretending to be a browser?
8. Is there one audio stream per receiver?
9. How is RX2 handled?
10. What does `Zeus.Plugins.VstHostStub` actually abstract?
11. Is the plugin stub called in a real-time server path that must never block?
12. Does `zeus-web` already have UI for native plugin status that can be reused?
13. Can the current desktop wrapper run as a client of a remote server, or does it always start a local server?
14. Is creating a new native shell preferable to extending the current desktop wrapper?
15. What VST host implementation was used in `Thetis-VST`, and can it be extracted cleanly?
16. What licensing model permits the chosen host to be contributed/distributed with ANAN Core?

Do not finalize architecture until questions 1–7 and 10–14 are answered from source.

---

## 29. Definition of MVP

The MVP is complete when all of this is true:

- ANAN Core server runs on the G2/G2 Ultra.
- Native macOS client embeds the real `zeus-web` UI.
- UI controls the remote ANAN Core server normally.
- RX audio is received by native code rather than Web Audio.
- Native code plays RX through the selected local output.
- One VST3 can be inserted in RX.
- The plugin's real native editor can be opened.
- Plugin bypass works.
- Plugin state survives application restart.
- Buffer underruns/overruns are visible in diagnostics.
- Long-running RX does not eventually fail from clock drift.
- Ordinary browser operation remains unchanged.
- No PureSignal code has been changed.
- No TX/RF behavior has been changed yet.

That is enough to prove the architecture before taking on TX risk and full Audio Suite integration.

---

## 30. Definition of v1

A practical v1 is complete when:

- macOS and Windows native clients exist,
- embedded `zeus-web` is the primary UI,
- native RX and TX audio work,
- VST3 works on RX and TX,
- AU works on macOS if selected as part of v1 scope,
- plugin editor windows work,
- plugin scanning is crash-resistant,
- chains are persistent,
- device selection is persistent,
- chain/master bypass exists,
- native DSP status is integrated into ANAN Core UI,
- the server stays authoritative for all RF/TX control,
- disconnect/crash fails safely,
- diagnostics expose buffer/network/plugin health,
- browser-only ANAN Core remains fully supported.

---

## 31. Future extensions after v1

Only after the v1 architecture is stable:

- CLAP hosting,
- plugin sandbox process,
- per-plugin crash recovery,
- multiple RX plugin chains,
- RX1/RX2 independent routing,
- local recording before/after plugins,
- native MIDI mapping to plugin parameters,
- plugin parameter surfaces rendered in the web UI,
- remote/WAN native DSP via existing WebRTC path,
- chain/profile sync metadata across desktop machines,
- optional server-advertised native-client discovery,
- headless native DSP companion without UI,
- external automation/control API.

A more radical future mode could run ANAN Core/WDSP on the desktop and use the G2 mainly as the radio interface, but that should be treated as a separate project rather than an extension of this MVP.

---

## 32. Notes for upstream contribution

Keep the feature easy for ANAN Core maintainers to accept:

- minimize server changes,
- reuse `zeus-web`,
- reuse existing audio wire semantics where possible,
- make native DSP capability optional,
- do not change normal browser behavior,
- do not add an RF-control dependency on the native client,
- do not touch PureSignal,
- keep the bridge narrow,
- isolate plugin-host dependencies from server code,
- make it possible to build/run ANAN Core without the native host toolchain.

If a new server transport is required, make it a generic **native audio endpoint** rather than “VST over the network.” That keeps the radio server useful to future native clients and keeps third-party plugin details out of the RF/DSP engine.

---

## 33. Reference links

### ANAN Core / Apache Labs

- ANAN Core source: <https://github.com/abhishekprakash22/zeus>
- Apache Labs ANAN Core announcement: <https://apache-labs.com/al-news/apache-labs-announces-anan-core-sdr-platform>
- ANAN Core mobile wrapper reference: <https://github.com/abhishekprakash22/zeus/tree/freedv-in-core/zeus-mobile>

### Prior Zeus / architectural reference

- Archived OpenHPSDR Zeus source/reference: <https://github.com/brianbruff/openhpsdr-zeus>
- Zeus SDR station-engine reference: <https://github.com/Zeus-SDR/station-engine>

### User's prior VST work

- Thetis-VST: <https://github.com/ChasingCoffee/Thetis-VST>

### JUCE reference if selected

- JUCE: <https://github.com/juce-framework/JUCE>
- JUCE AudioPluginHost example: <https://github.com/juce-framework/JUCE/tree/master/extras/AudioPluginHost>

---

## 34. Handoff prompt for the next agent

Use the following as the starting instruction for an implementation agent:

> Implement the project described in `ANAN_Core_Native_VST3_Host_Plan.md`, but do not start feature coding until you have completed the Discovery Phase. Read the repository's current `CLAUDE.md`, `AGENTS.md`, `README.md`, and `zeus-mobile/README.md` first. Trace the current RX audio path from WDSP to browser output and the current TX microphone path from browser `getUserMedia()` to WDSP. Inspect `Zeus.Plugins.Contracts`, `Zeus.Plugins.Host`, and `Zeus.Plugins.VstHostStub`, and inspect the user's `ChasingCoffee/Thetis-VST` implementation for reusable native VST-host components. Prefer implementing the existing browser audio protocol in the native client with zero server signal-routing changes. Do not modify PureSignal. Do not make the native client authoritative for PTT/MOX/TUNE or other RF safety state. The first coding milestone after discovery is a network-only native desktop shell that bundles `zeus-web` and connects it to the G2 server; the second is native RX playback with no plugins; the third is one RX VST3. Keep changes small, testable, and compatible with normal browser operation.

---

## 35. Architectural principle to preserve

The simplest way to decide where a new piece belongs is:

> **If it is about the radio, RF, WDSP, protocol, protection, or shared station state, it stays on the ANAN Core server. If it is about a desktop audio device or a desktop-native third-party plugin, it stays in the native desktop client. The network boundary carries audio and control state; it does not turn server DSP callbacks into remote procedure calls.**

Preserving that boundary is what makes this project useful on a G2/G2 Ultra without limiting plugins to Raspberry Pi/ARM builds.
