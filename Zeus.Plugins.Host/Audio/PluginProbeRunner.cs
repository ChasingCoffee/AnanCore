// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Zeus.Plugins.Host.Audio;

public enum ProbeOutcome
{
    /// <summary>The plugin behaved.</summary>
    Ok,
    /// <summary>The plugin was refused cleanly (not an effect, won't activate…).</summary>
    Failed,
    /// <summary>The probe process died while touching the plugin.</summary>
    Crashed,
    /// <summary>The plugin hung past the probe's time limit.</summary>
    TimedOut,
}

public sealed record ProbeResult(ProbeOutcome Outcome, string Message, PluginProbeReply? Reply)
{
    public bool Ok => Outcome == ProbeOutcome.Ok;
}

/// <summary>
/// Parent side of <see cref="PluginProbe"/>: runs the host binary in probe
/// mode, with a time limit, and reads its one-line JSON reply. A probe that
/// exits without a reply crashed; one that overruns is killed.
/// </summary>
public sealed class PluginProbeRunner
{
    /// <summary>Environment override: the executable to run probes with.</summary>
    public const string ExecutableEnvVar = "ZEUS_PLUGIN_PROBE";

    private readonly string _executable;
    private readonly IReadOnlyList<string> _prefixArgs;

    public PluginProbeRunner(string executable, IReadOnlyList<string>? prefixArgs = null)
    {
        _executable = executable;
        _prefixArgs = prefixArgs ?? [];
    }

    public TimeSpan DescribeTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The runner for this process: <see cref="ExecutableEnvVar"/> if set,
    /// else the Zeus host binary we are running as (or that sits next to us).
    /// Null when no probe host can be found — e.g. inside a test runner —
    /// in which case callers fall back to in-process handling.
    /// </summary>
    public static PluginProbeRunner? CreateDefault()
    {
        var env = Environment.GetEnvironmentVariable(ExecutableEnvVar);
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return new PluginProbeRunner(env);

        var exeName = OperatingSystem.IsWindows() ? "OpenhpsdrZeus.exe" : "OpenhpsdrZeus";
        var self = Environment.ProcessPath;
        if (self is not null && string.Equals(Path.GetFileName(self), exeName, StringComparison.OrdinalIgnoreCase))
            return new PluginProbeRunner(self);
        var sibling = Path.Combine(AppContext.BaseDirectory, exeName);
        return File.Exists(sibling) ? new PluginProbeRunner(sibling) : null;
    }

    public ProbeResult Describe(string path, CancellationToken ct = default) =>
        Run(["describe", path], DescribeTimeout, ct);

    public ProbeResult TrialLoad(string format, string identity, string? classUid, CancellationToken ct = default)
    {
        var fmt = string.Equals(format, "au", StringComparison.OrdinalIgnoreCase) ? "au" : "vst3";
        List<string> args = ["load", fmt, identity];
        if (fmt == "vst3" && !string.IsNullOrEmpty(classUid)) args.Add(classUid);
        return Run(args, LoadTimeout, ct);
    }

    private ProbeResult Run(IReadOnlyList<string> command, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (var a in _prefixArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(PluginProbe.Flag);
        foreach (var a in command) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return new ProbeResult(ProbeOutcome.Failed, $"could not start the plugin probe: {ex.Message}", null);
        }
        proc.StandardInput.Close();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        bool exited;
        using (ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } }))
            exited = proc.WaitForExit(timeout);
        if (!exited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            proc.WaitForExit();
            ct.ThrowIfCancellationRequested();
            return new ProbeResult(ProbeOutcome.TimedOut,
                $"the plugin did not respond within {timeout.TotalSeconds:0} s", null);
        }
        proc.WaitForExit(); // drain the async readers
        ct.ThrowIfCancellationRequested();

        PluginProbeReply? reply = null;
        string text;
        lock (stdout) text = stdout.ToString();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith(PluginProbe.ReplyMarker, StringComparison.Ordinal)) continue;
            try { reply = JsonSerializer.Deserialize<PluginProbeReply>(line[PluginProbe.ReplyMarker.Length..], PluginProbe.Json); }
            catch (JsonException) { /* malformed: treat as no reply */ }
        }
        if (reply is null)
            return new ProbeResult(ProbeOutcome.Crashed,
                $"the plugin crashed its probe process (exit code {proc.ExitCode})", null);
        return reply.Ok
            ? new ProbeResult(ProbeOutcome.Ok, "ok", reply)
            : new ProbeResult(ProbeOutcome.Failed, reply.Error ?? $"status {reply.Status}", reply);
    }
}

/// <summary>
/// Remembers probe verdicts between runs, keyed by plugin identity and
/// invalidated when the plugin's files change — so a bad plugin is probed
/// once and then skipped, and a good one isn't re-probed on every start.
/// Stored as JSON beside the scanned plugin packages.
/// </summary>
public sealed class PluginProbeCache
{
    private readonly string? _file;
    private readonly object _sync = new();
    private Dictionary<string, Entry>? _entries;

    public sealed record Entry(string Stamp, ProbeOutcome Outcome, string Message, DateTime ProbedUtc);

    /// <param name="file">JSON file path; null keeps the cache in memory only.</param>
    public PluginProbeCache(string? file) => _file = file;

    public Entry? Get(string key, string stamp)
    {
        lock (_sync)
        {
            var e = Entries().GetValueOrDefault(key);
            return e is not null && e.Stamp == stamp ? e : null;
        }
    }

    public void Put(string key, Entry entry)
    {
        lock (_sync)
        {
            Entries()[key] = entry;
            Save();
        }
    }

    public void Forget(string key)
    {
        lock (_sync)
        {
            if (Entries().Remove(key)) Save();
        }
    }

    private Dictionary<string, Entry> Entries()
    {
        if (_entries is not null) return _entries;
        _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (_file is null || !File.Exists(_file)) return _entries;
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_file), PluginProbe.Json);
            if (loaded is not null) _entries = new Dictionary<string, Entry>(loaded, StringComparer.Ordinal);
        }
        catch (Exception) { /* a corrupt cache is only a lost optimisation */ }
        return _entries;
    }

    private void Save()
    {
        if (_file is null || _entries is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, PluginProbe.Json));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception) { /* best effort */ }
    }

    /// <summary>
    /// A cheap "has this plugin changed" stamp: the newest write time among
    /// the plugin file / bundle and the files inside it (bounded walk). AU
    /// identities have no path; their stamp is constant, so a failed AU stays
    /// refused until its cache entry is forgotten (a rescan).
    /// </summary>
    public static string StampFor(string identity)
    {
        try
        {
            if (File.Exists(identity))
            {
                var fi = new FileInfo(identity);
                return $"{fi.LastWriteTimeUtc.Ticks}:{fi.Length}";
            }
            if (Directory.Exists(identity))
            {
                long newest = Directory.GetLastWriteTimeUtc(identity).Ticks;
                int seen = 0;
                foreach (var f in Directory.EnumerateFiles(identity, "*", new EnumerationOptions
                         { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true }))
                {
                    newest = Math.Max(newest, File.GetLastWriteTimeUtc(f).Ticks);
                    if (++seen >= 500) break;
                }
                return newest.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        catch (Exception) { /* fall through */ }
        return "";
    }
}

/// <summary>Decides whether a plugin may be loaded into this process.</summary>
public interface IPluginLoadGuard
{
    /// <summary>Allowed, or a reason the plugin must not be loaded in-process.</summary>
    (bool Allowed, string? Reason) Check(string format, string identity, string? classUid);
}

/// <summary>
/// Before a plugin is loaded into the server for the first time (or after its
/// files change), trial-load it in a probe process. Plugins that crash, hang
/// or refuse to load there are not loaded here. Without a probe host (tests,
/// unusual layouts) every plugin is allowed — the pre-existing behaviour.
/// </summary>
public sealed class ProbingPluginLoadGuard : IPluginLoadGuard
{
    private readonly PluginProbeRunner? _runner;
    private readonly PluginProbeCache _cache;
    private readonly ILogger? _log;

    public ProbingPluginLoadGuard(PluginProbeRunner? runner, PluginProbeCache cache, ILogger? log = null)
    {
        _runner = runner;
        _cache = cache;
        _log = log;
    }

    public static string KeyFor(string format, string identity, string? classUid) =>
        $"load|{format.ToLowerInvariant()}|{identity}|{classUid ?? ""}";

    public (bool Allowed, string? Reason) Check(string format, string identity, string? classUid)
    {
        if (_runner is null) return (true, null);
        var key = KeyFor(format, identity, classUid);
        var stamp = PluginProbeCache.StampFor(identity);
        var cached = _cache.Get(key, stamp);
        if (cached is not null)
            return cached.Outcome == ProbeOutcome.Ok ? (true, null) : (false, cached.Message);

        var result = _runner.TrialLoad(format, identity, classUid);
        _cache.Put(key, new PluginProbeCache.Entry(stamp, result.Outcome, result.Message, DateTime.UtcNow));
        if (result.Ok) return (true, null);
        _log?.LogWarning("Plugin {Identity} failed its safety check ({Outcome}): {Message}. Not loading it.",
            identity, result.Outcome, result.Message);
        return (false, result.Message);
    }
}
