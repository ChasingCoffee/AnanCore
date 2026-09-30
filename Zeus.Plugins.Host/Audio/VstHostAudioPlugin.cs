using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Plugins.Host.Audio;

/// <summary>
/// <see cref="IAudioPlugin"/> implementation that hosts a single VST3
/// effect via <see cref="IVstBridgeNative"/>. Synthesised by the host
/// when a plugin's manifest declares <c>audio.vst3Path</c> — plugin
/// authors don't write this themselves.
/// </summary>
public sealed class VstHostAudioPlugin : IAudioPlugin, IAsyncDisposable
{
    private readonly IVstBridgeNative _bridge;
    private readonly string _loadIdentity;
    private readonly string? _classUid;
    private readonly bool _isAudioUnit;
    private readonly string _pluginRootPath;
    private readonly string _slot;
    private readonly ILogger? _log;
    private readonly string? _pluginId;
    private readonly IPluginStateStore? _stateStore;
    private readonly IPluginLoadGuard? _loadGuard;
    // Serialises the control-thread handle operations (state, editor, unload)
    // so an autosave can never race an unload onto a freed handle. The
    // realtime Process path never takes it.
    private readonly object _ctl = new();
    private nint _handle;
    private int _latencySamples;
    // SHA-256 of the last state blob saved or restored, so unchanged state is
    // never rewritten.
    private byte[]? _lastStateHash;

    public VstHostAudioPlugin(
        IVstBridgeNative bridge,
        AudioBlock manifestAudio,
        string pluginRootPath,
        string displayName,
        ILogger? log = null,
        string? pluginId = null,
        IPluginStateStore? stateStore = null,
        IPluginLoadGuard? loadGuard = null)
    {
        _loadGuard = loadGuard;
        _bridge = bridge;
        _pluginRootPath = pluginRootPath;
        _log = log;
        _pluginId = pluginId;
        _stateStore = pluginId is null ? null : stateStore;
        _slot = manifestAudio.Slot;
        _classUid = string.IsNullOrWhiteSpace(manifestAudio.Vst3Uid) ? null : manifestAudio.Vst3Uid;
        DisplayName = displayName;
        Requirements = new AudioPluginRequirements(
            SampleRate: manifestAudio.SampleRate,
            Channels: manifestAudio.Channels,
            BlockSize: manifestAudio.Slot.StartsWith("rx.", StringComparison.OrdinalIgnoreCase) ? 2048 : 1024);

        // Format selects the load identity. "au" loads a macOS Audio Unit by
        // its type:subtype:manufacturer triple (resolved from the OS registry,
        // not a file); anything else (default "vst3") loads from a VST3 path.
        // AudioPluginBridge picks the matching IVstBridgeNative backend; this
        // class stays backend-agnostic.
        _isAudioUnit = string.Equals(manifestAudio.Format, "au", StringComparison.OrdinalIgnoreCase);
        _loadIdentity = _isAudioUnit
            ? (manifestAudio.AuComponentId
                ?? throw new ArgumentException("audio.auComponentId is required when audio.format is \"au\""))
            : (manifestAudio.Vst3Path
                ?? throw new ArgumentException("audio.vst3Path is required for VstHostAudioPlugin"));
    }

    public string DisplayName { get; }
    public AudioPluginRequirements Requirements { get; }

    /// <summary>
    /// The plugin's reported processing latency in samples at its loaded
    /// geometry (0 if zero-latency, not natively loaded, or the bridge does
    /// not report it). Captured once at load.
    /// </summary>
    public int ReportedLatencySamples => _latencySamples;

    /// <summary>
    /// Load gate for in-process native plugin hosting (VST3 + Audio Unit). Both
    /// RX and TX native load default ON — TX was enabled by KB2UKA after
    /// bench-verifying AU TX hosting (audio confirmed changing under the editor)
    /// on 2026-06-26; it had previously been opt-in.
    ///
    /// Kill switches fall back to the crash-isolated out-of-process engine:
    /// <c>ZEUS_DISABLE_VST_LOAD=1</c> (all slots), <c>ZEUS_DISABLE_RX_VST_LOAD=1</c>
    /// (RX only), <c>ZEUS_DISABLE_TX_VST_LOAD=1</c> (TX only).
    /// <c>ZEUS_ENABLE_VST_LOAD=1</c> force-enables everything even if a per-side
    /// disable is set. Precedence: global disable &gt; force-enable &gt; per-side
    /// disable. See native/zeus-vst-bridge.
    /// </summary>
    /// <summary>Test override for <see cref="NativeLoadEnabled"/>; null = use the env var.</summary>
    internal static bool? NativeLoadEnabledOverride;

    private static bool NativeLoadEnabled(string slot)
    {
        if (NativeLoadEnabledOverride is { } forced) return forced;
        if (Environment.GetEnvironmentVariable("ZEUS_DISABLE_VST_LOAD") == "1") return false;
        if (Environment.GetEnvironmentVariable("ZEUS_ENABLE_VST_LOAD") == "1") return true;
        if (slot.StartsWith("rx.", StringComparison.OrdinalIgnoreCase))
            return Environment.GetEnvironmentVariable("ZEUS_DISABLE_RX_VST_LOAD") != "1";
        // TX native load now defaults on (KB2UKA-approved 2026-06-26);
        // ZEUS_DISABLE_TX_VST_LOAD=1 is the TX-side kill switch.
        return Environment.GetEnvironmentVariable("ZEUS_DISABLE_TX_VST_LOAD") != "1";
    }

    /// <summary>
    /// Editor-guard signal ONLY — whether the TX editor-open fallback should
    /// treat TX as in-process-hostable when a plugin is <em>not</em> already
    /// natively loaded. Deliberately <b>decoupled</b> from the TX <em>load</em>
    /// default (<see cref="NativeLoadEnabled"/>, which now defaults TX on as of
    /// 2026-06-26): it stays pinned to the pre-flip explicit <c>ZEUS_ENABLE_VST_LOAD</c>
    /// opt-in so flipping the load default does not move the cross-platform
    /// editor engine-redirect UX (Windows in-process TX editor hosting is
    /// unverified). A TX plugin that actually loaded in-process is detected via
    /// <c>AudioPluginBridge.HostsPlugin</c> upstream and bypasses the guard
    /// regardless; this governs only the not-hosted fallback message. Mirrors the
    /// pre-flip <c>TxNativeLoadEnabled</c> semantics exactly.
    /// </summary>
    public static bool TxNativeEditorOptIn
    {
        get
        {
            if (NativeLoadEnabledOverride is { } forced) return forced;
            if (Environment.GetEnvironmentVariable("ZEUS_DISABLE_VST_LOAD") == "1") return false;
            return Environment.GetEnvironmentVariable("ZEUS_ENABLE_VST_LOAD") == "1";
        }
    }

    public Task InitializeAudioAsync(IAudioHost host, CancellationToken ct)
    {
        if (!NativeLoadEnabled(_slot))
        {
            _log?.LogInformation(
                "VST host '{Name}' registered but native load is disabled "
                + "(clear ZEUS_DISABLE_VST_LOAD / ZEUS_DISABLE_TX_VST_LOAD / "
                + "ZEUS_DISABLE_RX_VST_LOAD to re-enable); passing audio through.",
                DisplayName);
            return Task.CompletedTask; // _handle stays 0 → Process passes through
        }

        // Bridge init is idempotent — the native side ref-counts. (The AU
        // bridge ignores the abi argument and validates against its own
        // ZAU_ABI; VstBridgeAbi.Current is the VST3 ABI but the AU backend's
        // Init clamps to AuBridgeAbi.Current internally.)
        var initStatus = _bridge.Init(VstBridgeAbi.Current);
        if (initStatus != VstBridgeStatus.Ok)
            throw new PluginLoadException(
                $"audio bridge init failed (status={initStatus}); is the native bridge installed?");

        // For an Audio Unit the load identity is a registry triple, not a
        // filesystem path — pass it through unchanged and skip the file check.
        string loadIdentity;
        if (_isAudioUnit)
        {
            loadIdentity = _loadIdentity;
        }
        else
        {
            loadIdentity = Path.IsPathRooted(_loadIdentity)
                ? _loadIdentity
                : Path.Combine(_pluginRootPath, _loadIdentity);

            if (!File.Exists(loadIdentity) && !Directory.Exists(loadIdentity))
                throw new PluginLoadException($"VST3 path not found: {loadIdentity}");
        }

        // Never load a plugin into this process that crashed, hung or refused
        // to load in a probe process (cached per plugin version).
        if (_loadGuard is not null)
        {
            var (allowed, reason) = _loadGuard.Check(_isAudioUnit ? "au" : "vst3", loadIdentity, _classUid);
            if (!allowed)
                throw new PluginLoadException($"'{DisplayName}' was not loaded: {reason}");
        }

        var blockSize = Math.Max(1, host.CurrentBlockSize);
        // Load at the host's ACTUAL processing rate, not the manifest's
        // declared rate — the plugin must run at the stream rate it will be
        // fed, or its time-based DSP (filters, modulation) is detuned. Falls
        // back to the manifest rate only if the host reports nothing usable.
        var sampleRate = host.CurrentSampleRate > 0 ? host.CurrentSampleRate : Requirements.SampleRate;
        // A class UID (from the scan) selects one plugin out of a multi-class
        // module; without one the bridge loads the module's first effect.
        nint handle;
        var status = _classUid is not null && !_isAudioUnit
            ? _bridge.LoadVst3Class(loadIdentity, _classUid, Requirements.Channels, sampleRate, blockSize, out handle)
            : _bridge.LoadVst3(loadIdentity, Requirements.Channels, sampleRate, blockSize, out handle);

        if (status != VstBridgeStatus.Ok || handle == 0)
            throw new PluginLoadException(
                $"{(_isAudioUnit ? "Audio Unit" : "VST3")} load failed for {loadIdentity} (status={status})");

        lock (_ctl)
        {
            _handle = handle;
            RestoreSavedStateLocked();
        }

        // Capture the plugin's reported processing latency (ABI v3). 0 for
        // zero-latency effects; the host sums these to report total insert
        // latency. Defaulted bridges (test fakes) return 0.
        _latencySamples = _bridge.GetLatencySamples(_handle);

        _log?.LogInformation(
            "{Kind} host loaded {Id} (channels={Channels} sr={SampleRate} block={Block} latency={Latency}smp)",
            _isAudioUnit ? "AU" : "VST", loadIdentity,
            Requirements.Channels, sampleRate, blockSize, _latencySamples);
        return Task.CompletedTask;
    }

    // Apply the operator's saved settings right after a native load, so a
    // plugin comes back exactly as it was left. Caller holds _ctl.
    private void RestoreSavedStateLocked()
    {
        if (_stateStore is null || _pluginId is null || _handle == 0) return;
        byte[]? saved;
        try { saved = _stateStore.Load(_pluginId); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Reading saved state for '{Name}' failed; starting from defaults.", DisplayName);
            return;
        }
        if (saved is not { Length: > 0 }) return;
        var st = _bridge.SetState(_handle, saved);
        if (st == VstBridgeStatus.Ok)
        {
            _lastStateHash = System.Security.Cryptography.SHA256.HashData(saved);
            _log?.LogInformation("Restored saved settings for '{Name}' ({Bytes} bytes).", DisplayName, saved.Length);
        }
        else
        {
            _log?.LogWarning("Saved settings for '{Name}' were not accepted (status={Status}); plugin defaults kept.",
                DisplayName, st);
        }
    }

    /// <summary>
    /// The plugin's current native state blob, or null when it isn't loaded or
    /// its backend can't serialise state. Control thread only.
    /// </summary>
    public byte[]? CaptureState()
    {
        lock (_ctl)
        {
            if (_handle == 0) return null;
            return _bridge.GetState(_handle, out var state) == VstBridgeStatus.Ok ? state : null;
        }
    }

    /// <summary>
    /// Apply a state blob from <see cref="CaptureState"/> (e.g. a profile's).
    /// When the plugin isn't loaded, the blob is stored so the next load
    /// restores it. Returns false only when a loaded plugin refused it.
    /// </summary>
    public bool RestoreState(byte[] state)
    {
        lock (_ctl)
        {
            if (_handle == 0)
            {
                if (_stateStore is not null && _pluginId is not null)
                    _stateStore.Save(_pluginId, _isAudioUnit ? "au" : "vst3", state);
                return true;
            }
            if (_bridge.SetState(_handle, state) != VstBridgeStatus.Ok) return false;
            _lastStateHash = System.Security.Cryptography.SHA256.HashData(state);
            _stateStore?.Save(_pluginId!, _isAudioUnit ? "au" : "vst3", state);
            return true;
        }
    }

    /// <summary>
    /// Persist the plugin's current state if it changed since it was last
    /// saved or restored. Cheap when nothing changed apart from the
    /// serialisation itself. Returns true when a new blob was written.
    /// </summary>
    public bool SaveStateIfChanged()
    {
        lock (_ctl) return SaveStateIfChangedLocked();
    }

    private bool SaveStateIfChangedLocked()
    {
        if (_stateStore is null || _pluginId is null || _handle == 0) return false;
        if (_bridge.GetState(_handle, out var state) != VstBridgeStatus.Ok || state.Length == 0) return false;
        var hash = System.Security.Cryptography.SHA256.HashData(state);
        if (_lastStateHash is not null && hash.AsSpan().SequenceEqual(_lastStateHash)) return false;
        try
        {
            _stateStore.Save(_pluginId, _isAudioUnit ? "au" : "vst3", state);
            _lastStateHash = hash;
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Saving settings for '{Name}' failed.", DisplayName);
            return false;
        }
    }

    public void Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx)
    {
        if (_handle == 0)
        {
            input.CopyTo(output); // safety: pass through if not initialised
            return;
        }

        var status = _bridge.Process(_handle, input, output, ctx.Frames);
        if (status != VstBridgeStatus.Ok)
        {
            // Realtime path: NEVER throw, NEVER log here (allocation).
            // Pass through on bridge failure — the operator will see a
            // status surface up via the next non-realtime poll.
            input.CopyTo(output);
        }
    }

    /// <summary>
    /// True once the VST is natively loaded (the editor can only open
    /// when there's a real plugin instance behind the handle). False when
    /// native load is gated off (<see cref="NativeLoadEnabled(string)"/>) or the
    /// load failed — in those states the slot passes audio through and
    /// has no GUI to show.
    /// </summary>
    public bool IsNativelyLoaded => _handle != 0;

    /// <summary>Whether the plugin's native editor window is currently open.</summary>
    public bool IsEditorOpen
    {
        get { lock (_ctl) return _handle != 0 && _bridge.EditorIsOpen(_handle); }
    }

    /// <summary>
    /// Open the plugin's native editor (its real GUI) in a bridge-owned
    /// OS window. Returns false if the VST isn't natively loaded or the
    /// bridge reports failure (e.g. editor unsupported on this platform).
    /// </summary>
    public bool OpenEditor()
    {
        lock (_ctl)
        {
            if (_handle == 0)
            {
                _log?.LogInformation(
                    "VST '{Name}' editor open requested but no native handle "
                    + "(native load disabled or load failed).", DisplayName);
                return false;
            }
            var status = _bridge.EditorOpen(_handle, DisplayName);
            if (status != VstBridgeStatus.Ok)
                _log?.LogWarning("VST '{Name}' editor open failed (status={Status}).", DisplayName, status);
            return status == VstBridgeStatus.Ok;
        }
    }

    /// <summary>Close the plugin's native editor window if open, keeping
    /// whatever the operator changed in it.</summary>
    public void CloseEditor()
    {
        lock (_ctl)
        {
            if (_handle == 0) return;
            _bridge.EditorClose(_handle);
            SaveStateIfChangedLocked();
        }
    }

    public Task ShutdownAudioAsync(CancellationToken ct)
    {
        lock (_ctl)
        {
            if (_handle != 0)
            {
                SaveStateIfChangedLocked();
                _bridge.Unload(_handle);
                _handle = 0;
            }
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (_ctl)
        {
            if (_handle != 0)
            {
                try { SaveStateIfChangedLocked(); } catch { /* best effort */ }
                try { _bridge.Unload(_handle); } catch { /* swallow */ }
                _handle = 0;
            }
        }
        return ValueTask.CompletedTask;
    }
}
