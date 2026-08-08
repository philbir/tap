using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tap.Studio.Variables;
using Tap.Workspace.Variables;

namespace Tap.Studio;

/// <summary>
/// Persistent store for user-level Tap settings. The file lives at
/// <c>&lt;SystemDir&gt;/system.json</c> — <c>SystemDir</c> comes from the <c>TAP_SYSTEM_DIR</c>
/// env var, falling back to <c>~/.tap</c>. Two sections:
/// <list type="bullet">
///   <item><b>Providers</b> — system-scope variable provider configs. The Studio merges these
///     with workspace-level providers when building the resolution registry (workspace
///     providers shadow same-named system ones).</item>
///   <item><b>Variables</b> — flat name/value/secret entries exposed through the built-in
///     <c>system</c> variable provider (always registered).</item>
///  </list>
///
/// <para>The file is treated as user-private. Values (including those flagged secret) are
/// stored verbatim — relying on the user-profile directory permissions instead of at-rest
/// encryption. Per-provider mechanisms (e.g. the <c>file</c> provider's AES envelope) still
/// apply when those providers are configured.</para>
///
/// <para>On first construction the file is created if missing, seeded with whatever providers
/// were declared in <c>StudioOptions.SystemProviders</c> (appsettings). After that the file is
/// the source of truth; the appsettings entries are ignored.</para>
/// </summary>
public sealed class SystemSettingsStore
{
    private readonly string _path;
    private readonly ILogger<SystemSettingsStore> _logger;
    private readonly Lock _gate = new();
    private SystemSettingsFile _state;
    private DateTime _loadedWriteTimeUtc;

    public SystemSettingsStore(StudioOptions options, ILogger<SystemSettingsStore> logger)
    {
        _logger = logger;
        AtomicStateFile.CreateDirectory(options.SystemDir);
        _path = Path.Combine(options.SystemDir, "system.json");
        SystemDir = options.SystemDir;

        var (outcome, loaded) = TryLoad(_path);
        if (outcome is LoadOutcome.Corrupt)
        {
            // Every system-scope secret lives in this file. Seeding empty state and writing it
            // back — which is what the old "loaded is null" branch did — turns one truncated
            // write plus one restart into permanent, silent data loss. Quarantine instead.
            var quarantined = AtomicStateFile.MoveAsideCorrupt(_path);
            if (quarantined is not null)
            {
                _logger.LogError(
                    "Could not parse '{Path}'. Moved it to '{Quarantine}' and started from empty settings — "
                    + "recover any secrets from that copy before deleting it.", _path, quarantined);
            }
            else
            {
                _logger.LogError(
                    "Could not parse '{Path}', and could not move it aside either. Running with empty settings; "
                    + "the file is left untouched so nothing is lost. Fix or remove it and restart.", _path);
            }
        }

        if (loaded is null)
        {
            _state = new SystemSettingsFile
            {
                VariableProviders = [.. options.SystemProviders.Select(StoredProvider.From)],
                Variables = new Dictionary<string, StoredVariable>(StringComparer.Ordinal),
            };
            // Only seed the file when there genuinely wasn't one. After a parse failure the
            // original may still be sitting there, and startup must never be the thing that
            // overwrites it.
            if (outcome is LoadOutcome.Missing) Persist();
        }
        else
        {
            _state = loaded;
        }

        _loadedWriteTimeUtc = SafeWriteTimeUtc(_path);
    }

    /// <summary>Re-reads <c>system.json</c> when its on-disk timestamp moved past what we
    /// loaded. Several Studio processes routinely share <c>~/.tap</c> (desktop app + a dev
    /// instance); without this, an instance keeps serving — and on the next save,
    /// <b>writes back</b> — a stale snapshot, silently discarding providers/variables another
    /// instance persisted in the meantime. Must be called under <see cref="_gate"/>.</summary>
    private void ReloadIfChangedLocked()
    {
        var mtime = SafeWriteTimeUtc(_path);
        if (mtime == _loadedWriteTimeUtc) return;
        var (outcome, fresh) = TryLoad(_path);
        if (outcome is LoadOutcome.Corrupt)
        {
            // Keep the in-memory copy rather than adopting garbage, and move the watermark so
            // this warns once per on-disk change instead of once per property read. No
            // quarantine here — another process may simply be mid-write.
            _loadedWriteTimeUtc = mtime;
            _logger.LogWarning("'{Path}' changed on disk but could not be parsed; keeping the in-memory settings.", _path);
            return;
        }
        if (fresh is not null)
        {
            _state = fresh;
            _loadedWriteTimeUtc = mtime;
        }
    }

    private static DateTime SafeWriteTimeUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    public string SystemDir { get; }

    /// <summary>Currently configured default writable variable provider, or <c>null</c> when
    /// none is set. Mirrors the workspace manifest's <c>defaultVariableProvider:</c> field —
    /// the registry uses this as the system-wide fallback when the active workspace doesn't
    /// declare its own.</summary>
    public string? DefaultVariableProvider
    {
        get
        {
            lock (_gate)
            {
                ReloadIfChangedLocked();
                return _state.DefaultVariableProvider;
            }
        }
    }

    /// <summary>The persisted system-scope provider configs, including the implicit
    /// <c>system</c> provider that exposes <see cref="Variables"/>. Returned fresh each call
    /// so callers always see the latest on-disk state.</summary>
    public IReadOnlyList<VariableProviderConfig> GetProviderConfigs()
    {
        lock (_gate)
        {
            ReloadIfChangedLocked();
            var list = new List<VariableProviderConfig>(_state.VariableProviders.Count + 1)
            {
                // Implicit, always-on provider. Kept first so its variables resolve before
                // any user-configured provider on unprefixed lookups.
                new VariableProviderConfig
                {
                    Name = SystemVariableProvider.ProviderName,
                    Type = SystemVariableProvider.ProviderType,
                    Settings = new Dictionary<string, string?>(),
                    Origin = ProviderOrigin.System,
                },
            };
            foreach (var p in _state.VariableProviders)
            {
                list.Add(new VariableProviderConfig
                {
                    Name = p.Name,
                    Type = p.Type,
                    Settings = p.Settings ?? new Dictionary<string, string?>(),
                    Origin = ProviderOrigin.System,
                });
            }
            return list;
        }
    }

    /// <summary>Snapshot of the stored variables — name to (value, isSecret).</summary>
    public IReadOnlyDictionary<string, StoredVariable> GetVariables()
    {
        lock (_gate)
        {
            ReloadIfChangedLocked();
            return new Dictionary<string, StoredVariable>(_state.Variables, StringComparer.Ordinal);
        }
    }

    /// <summary>The persisted AI assistant settings (provider + CLI paths + default model),
    /// or <c>null</c> when the user hasn't configured AI yet.</summary>
    public StoredAiSettings? GetAiSettings()
    {
        lock (_gate)
        {
            ReloadIfChangedLocked();
            return _state.Ai;
        }
    }

    /// <summary>Replace the AI settings block. Pass <c>null</c> to clear it (revert to
    /// auto-detected defaults).</summary>
    public void SetAiSettings(StoredAiSettings? ai)
    {
        lock (_gate)
        {
            ReloadIfChangedLocked();
            _state = _state with { Ai = ai };
            Persist();
        }
        Changed?.Invoke();
    }

    /// <summary>Replace the providers list and the default-provider pointer atomically. The
    /// implicit <c>system</c> provider is filtered out — callers can't (and don't need to)
    /// redeclare it. Pass <c>null</c> for <paramref name="defaultProviderName"/> to clear the
    /// default. Empty / whitespace strings are normalized to <c>null</c>.</summary>
    public void ReplaceProviders(string? defaultProviderName, IEnumerable<StoredProvider> providers)
    {
        lock (_gate)
        {
            // Base the wholesale replace on fresh disk state so the untouched sections
            // (variables, AI settings) can't revert another instance's writes.
            ReloadIfChangedLocked();
            var clean = providers
                .Where(p => !string.IsNullOrEmpty(p.Name)
                    && !string.IsNullOrEmpty(p.Type)
                    && !string.Equals(p.Name, SystemVariableProvider.ProviderName, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(p.Type, SystemVariableProvider.ProviderType, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Reject duplicate provider names — would otherwise produce silently shadowed
            // entries the user can't tell apart in the UI.
            var dupes = clean.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            if (dupes.Count > 0)
                throw new InvalidOperationException($"Duplicate provider name(s): {string.Join(", ", dupes)}.");

            // Default-provider sanity: ignore if it doesn't match a declared provider. We
            // deliberately don't error — workspace-level defaults shadow this anyway, and a
            // stale string in the JSON shouldn't block a save.
            var normalizedDefault = string.IsNullOrWhiteSpace(defaultProviderName) ? null : defaultProviderName.Trim();
            if (normalizedDefault is not null
                && !clean.Any(p => string.Equals(p.Name, normalizedDefault, StringComparison.OrdinalIgnoreCase)))
            {
                normalizedDefault = null;
            }

            _state = _state with { DefaultVariableProvider = normalizedDefault, VariableProviders = clean };
            Persist();
        }
        Changed?.Invoke();
    }

    public void ReplaceVariables(IEnumerable<KeyValuePair<string, StoredVariable>> variables)
    {
        lock (_gate)
        {
            ReloadIfChangedLocked();
            var dict = new Dictionary<string, StoredVariable>(StringComparer.Ordinal);
            foreach (var (k, v) in variables)
            {
                if (string.IsNullOrEmpty(k)) continue;
                dict[k] = v;
            }
            _state = _state with { Variables = dict };
            Persist();
        }
        Changed?.Invoke();
    }

    /// <summary>Upsert one variable. Used by the system variable provider's <c>SetAsync</c>
    /// so request execution can write back to <c>system.json</c> via
    /// <c>{{system:NAME}}</c> targets.</summary>
    public void SetVariable(string name, string value, bool isSecret)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Variable name is required.", nameof(name));

        lock (_gate)
        {
            ReloadIfChangedLocked();
            var dict = new Dictionary<string, StoredVariable>(_state.Variables, StringComparer.Ordinal)
            {
                [name] = new StoredVariable(value, isSecret),
            };
            _state = _state with { Variables = dict };
            Persist();
        }
        Changed?.Invoke();
    }

    /// <summary>Fired after any successful write. The provider-registry rebuild path picks
    /// fresh state on every <c>CreateRegistry()</c> call anyway, but UI clients use this to
    /// invalidate the Settings page.</summary>
    public event Action? Changed;

    private void Persist()
    {
        AtomicStateFile.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicStateFile.WriteAllText(_path, JsonSerializer.Serialize(_state, SystemSettingsJson.Default.SystemSettingsFile));
        _loadedWriteTimeUtc = SafeWriteTimeUtc(_path);
    }

    private enum LoadOutcome { Missing, Loaded, Corrupt }

    /// <summary>Reads <c>system.json</c>, keeping "not written yet" and "there but
    /// unreadable" apart. Collapsing the two is what made a truncated write unrecoverable:
    /// the caller saw <c>null</c>, seeded empty state, and persisted over the damage.</summary>
    private static (LoadOutcome Outcome, SystemSettingsFile? Settings) TryLoad(string path)
    {
        if (!File.Exists(path)) return (LoadOutcome.Missing, null);
        try
        {
            // Read into the transitional shape so older files using the legacy `providers`
            // key still load. The first subsequent save rewrites under the canonical
            // `variableProviders` key.
            var raw = JsonSerializer.Deserialize(File.ReadAllText(path), SystemSettingsJson.Default.SystemSettingsFileRaw);
            if (raw is null) return (LoadOutcome.Corrupt, null);
            return (LoadOutcome.Loaded, new SystemSettingsFile
            {
                DefaultVariableProvider = string.IsNullOrWhiteSpace(raw.DefaultVariableProvider) ? null : raw.DefaultVariableProvider,
                VariableProviders = raw.VariableProviders ?? raw.Providers ?? [],
                Variables = raw.Variables ?? new Dictionary<string, StoredVariable>(StringComparer.Ordinal),
                Ai = raw.Ai,
            });
        }
        catch
        {
            // Refuse to crash the host over a corrupt user-config file. The caller logs and
            // quarantines; the Settings page shows the underlying dir so the user can fix it.
            return (LoadOutcome.Corrupt, null);
        }
    }
}

public sealed record SystemSettingsFile
{
    /// <summary>Name of the writable provider that receives writes when callers don't name
    /// one explicitly. Mirrors the workspace manifest's <c>defaultVariableProvider:</c>
    /// field. <c>null</c> means "no system default" — fall through to the first ReadWrite
    /// provider in registration order.</summary>
    public string? DefaultVariableProvider { get; init; }
    public required IReadOnlyList<StoredProvider> VariableProviders { get; init; }
    public required IReadOnlyDictionary<string, StoredVariable> Variables { get; init; }

    /// <summary>AI assistant configuration (provider + CLI overrides + default model).
    /// <c>null</c> when AI hasn't been configured — providers fall back to auto-detection.</summary>
    public StoredAiSettings? Ai { get; init; }
}

/// <summary>Read-only shape used during deserialization to absorb files written by older
/// builds (legacy <c>providers:</c> key) without losing data. Never serialized — the
/// canonical <see cref="SystemSettingsFile"/> is what hits disk.</summary>
internal sealed record SystemSettingsFileRaw
{
    public string? DefaultVariableProvider { get; init; }
    public IReadOnlyList<StoredProvider>? VariableProviders { get; init; }
    /// <summary>Legacy key. Used only when <see cref="VariableProviders"/> is absent.</summary>
    public IReadOnlyList<StoredProvider>? Providers { get; init; }
    public IReadOnlyDictionary<string, StoredVariable>? Variables { get; init; }
    public StoredAiSettings? Ai { get; init; }
}

public sealed record StoredProvider
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public IReadOnlyDictionary<string, string?>? Settings { get; init; }

    public static StoredProvider From(VariableProviderConfig cfg) => new()
    {
        Name = cfg.Name,
        Type = cfg.Type,
        Settings = new Dictionary<string, string?>(cfg.Settings),
    };
}

public sealed record StoredVariable(string Value, bool Secret);

/// <summary>Persisted AI assistant configuration. All fields optional — absence means the
/// provider auto-detects the CLI on PATH and uses its default model.</summary>
public sealed record StoredAiSettings
{
    /// <summary><c>copilot</c> or <c>claude-code</c>.</summary>
    public string? Provider { get; init; }
    /// <summary>Default model id (e.g. <c>claude-sonnet-4.5</c>, <c>sonnet</c>).</summary>
    public string? Model { get; init; }
    /// <summary>Explicit path to the <c>copilot</c> binary, overriding auto-detection.</summary>
    public string? CopilotCliPath { get; init; }
    /// <summary>Explicit path to the <c>claude</c> binary, overriding auto-detection.</summary>
    public string? ClaudeCliPath { get; init; }
}

[JsonSerializable(typeof(SystemSettingsFile))]
[JsonSerializable(typeof(SystemSettingsFileRaw))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class SystemSettingsJson : JsonSerializerContext
{
}

/// <summary>
/// Crash-safe, owner-private writes for the JSON files Studio keeps under <c>~/.tap</c>
/// (<c>system.json</c>, <c>auth-tokens.json</c>, <c>workspaces.json</c>). Two failure modes are
/// being prevented:
///
/// <para>Truncation. <c>File.WriteAllText</c> opens the real file with <c>FileMode.Create</c>,
/// so a crash or a full disk mid-write leaves a zero-length file where a working one used to
/// be. Everything here lands in a sibling temp file and is renamed over the target, which is
/// atomic on both NTFS and POSIX filesystems.</para>
///
/// <para>Leaked secrets. The default creation mode is 0666 &amp; umask — world-readable on most
/// Linux boxes — and these files hold plaintext secret-flagged variables and OAuth refresh
/// tokens. The temp file is created 0600 up front rather than chmod'd afterwards, so the
/// content never exists on disk under a permissive mode.</para>
///
/// <para>This mirrors <c>Tap.Core.IO.AtomicFile</c>. Tap.Studio deliberately does not reference
/// Tap.Core (that assembly carries the Aspire/OIDC hosting surface), so the handful of lines is
/// duplicated rather than dragging in the dependency.</para>
/// </summary>
internal static class AtomicStateFile
{
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode OwnerOnlyDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Atomically replaces <paramref name="path"/> with <paramref name="contents"/>
    /// (UTF-8, no BOM), leaving the file readable only by the current user on Unix.</summary>
    public static void WriteAllText(string path, string contents)
    {
        var tmp = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnlyFile;

            using (var fs = new FileStream(tmp, options))
            {
                fs.Write(Encoding.UTF8.GetBytes(contents));
                // Flush to the platter before the rename: otherwise a power loss can commit
                // the directory entry while the data blocks are still in cache, which is
                // exactly the truncated-file outcome the rename is meant to prevent.
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }

        // The rename carries 0600 across on POSIX; re-applying costs one syscall and covers
        // filesystems that keep the destination's own mode.
        RestrictToOwner(path);
    }

    /// <summary>Creates <paramref name="path"/> and any missing parents, restricting newly
    /// created directories to the current user (0700 on Unix). Existing directories keep
    /// whatever mode they already have.</summary>
    public static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }
        Directory.CreateDirectory(path, OwnerOnlyDirectory);
    }

    /// <summary>Best-effort tightening of an existing file to owner-only read/write (0600 on
    /// Unix). No-op on Windows, where the file already sits inside the user profile and ACLs
    /// are managed differently. Silent on failure — a permissive file beats a crashed
    /// host.</summary>
    public static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, OwnerOnlyFile);
        }
        catch
        {
            // ignore — the file is still owned by the current user
        }
    }

    /// <summary>Renames an unparseable file to <c>&lt;name&gt;.corrupt-&lt;utc&gt;</c> and
    /// returns the new path, or <c>null</c> when the move itself failed. Callers use this
    /// instead of overwriting: a file we cannot read may still hold the only copy of a
    /// secret.</summary>
    public static string? MoveAsideCorrupt(string path)
    {
        var quarantine = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}";
        try
        {
            File.Move(path, quarantine, overwrite: true);
            return quarantine;
        }
        catch
        {
            return null;
        }
    }
}
