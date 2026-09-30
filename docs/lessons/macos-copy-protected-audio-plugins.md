# macOS: copy-protected audio plug-ins hang inside .NET

Some commercial VST3 / AU / CLAP plug-ins (seen with the Softube / Summit Audio
family) never finish loading inside the Zeus process on macOS, yet load in
under a second in any native host. Bookmark this before "fixing" a plug-in
that times out.

## TL;DR

Start Zeus with `PAL_MachExceptionMode=2`. The app bundle's launcher
(`zeus-launch`), the Server.app scripts and `launchSettings.json` all set it;
a bare `dotnet OpenhpsdrZeus.dll` or a hand-rolled launcher does not.

## The symptom

- A scan reports the plug-in as "did not respond within N s".
- macOS writes a crash report for each probe (`OpenhpsdrZeus-*.ips`) with
  `EXC_BREAKPOINT (SIGTRAP)` inside the plug-in's protection code
  (e.g. `GrandChannel_VST_AU_Protect`), terminated by `Killed: 9` from the
  parent (the probe timeout).
- The same bundle reads instantly with `zeus-plugin-probe describe <path>`.

## The cause

The protection code executes a breakpoint on purpose while it starts and
expects its own `SIGTRAP` handler to answer. The .NET runtime on macOS
registers a Mach exception port for `EXC_BREAKPOINT` on its threads, so the
exception goes to the runtime instead of becoming a signal; the plug-in's
handler never runs and the loading thread waits forever. In the running app
that thread is the main thread (the VST bridge loads on it), so the whole UI
freezes.

`PAL_MachExceptionMode=2` ("suppress debugging exceptions") leaves
`EXC_BREAKPOINT` to the kernel's default Unix signal path. The runtime still
handles `SIGTRAP` through its signal handler, so managed behaviour does not
change. The variable is read once when the runtime starts: setting it from
managed code is too late, which is why it lives in the launchers.

Measured on macOS 27 / .NET 10: Summit Audio Grand Channel, Transient
Shaper, Tape and Bus Processor went from hanging to loading in about 1 s.
Mode `4` (suppress bad-access) does not help; mode `1` makes the probe die
at once.

## Defences in the code

- Scans read class lists with the native `zeus-plugin-probe` (built with the
  VST bridge, shipped in `runtimes/<rid>/native/`), which never has this
  problem and keeps plug-in crashes out of the app's crash reports.
- Trial loads (`ProbingPluginLoadGuard`) run in a child of the app that
  inherits its environment, so a plug-in that would hang the app is refused
  before it is loaded, and on macOS the refusal names the missing variable.
