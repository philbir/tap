using System.Text.RegularExpressions;

namespace Tap.Workspace.Variables.Providers;

/// <summary>
/// Read-only provider that exposes process environment variables matching the operator's
/// allowlists. The lists are configured on the host process, not the workspace: that keeps
/// secret reachability out of files anyone can read.
///
/// <para>Two env vars drive membership:</para>
/// <list type="bullet">
///   <item><c>TAP_VARS_ALLOWED</c> — comma-separated glob list of names exposed as plain
///     variables (<see cref="VariableValue.IsSecret"/> = false).</item>
///   <item><c>TAP_SECRETS_ALLOWED</c> — same syntax; values flow through but stay masked
///     (<see cref="VariableValue.IsSecret"/> = true).</item>
/// </list>
///
/// <para>A name on both lists is treated as secret (stricter wins). When neither var is
/// set, the provider exposes nothing — deny-by-default is the safe default for anything
/// reaching into the host's environment.</para>
///
/// <para>Settings: this provider ignores <see cref="VariableProviderConfig.Settings"/>; the
/// allowlist mechanism lives on the host. Always <see cref="ProviderMode.Read"/>.</para>
/// </summary>
public sealed class EnvVariableProvider(VariableProviderConfig config) : IVariableProvider
{
    private const string VarsEnvName = "TAP_VARS_ALLOWED";
    private const string SecretsEnvName = "TAP_SECRETS_ALLOWED";

    public string Name => config.Name;
    public ProviderMode Mode => ProviderMode.Read;  // static — env reads from the host, never writes back.
    public VariableProviderConfig Config => config;

    public ValueTask<VariableValue?> GetAsync(string name, CancellationToken ct)
    {
        var vars = ParsePatterns(Environment.GetEnvironmentVariable(VarsEnvName));
        var secrets = ParsePatterns(Environment.GetEnvironmentVariable(SecretsEnvName));

        var isSecret = Matches(name, secrets);
        var isPlain = Matches(name, vars);
        if (!isSecret && !isPlain) return ValueTask.FromResult<VariableValue?>(null);

        var raw = Environment.GetEnvironmentVariable(name);
        if (raw is null) return ValueTask.FromResult<VariableValue?>(null);
        return ValueTask.FromResult<VariableValue?>(new VariableValue(name, raw, isSecret, Name));
    }

    public ValueTask<IReadOnlyList<VariableValue>> ListAsync(CancellationToken ct)
    {
        var vars = ParsePatterns(Environment.GetEnvironmentVariable(VarsEnvName));
        var secrets = ParsePatterns(Environment.GetEnvironmentVariable(SecretsEnvName));

        var result = new List<VariableValue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables())
        {
            if (kv.Key is not string name || kv.Value is not string value) continue;
            var matchSecret = Matches(name, secrets);
            var matchPlain = Matches(name, vars);
            if (!matchSecret && !matchPlain) continue;
            if (!seen.Add(name)) continue;
            result.Add(new VariableValue(name, value, matchSecret, Name));
        }

        result.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return ValueTask.FromResult<IReadOnlyList<VariableValue>>(result);
    }

    public ValueTask SetAsync(string name, string value, bool isSecret, CancellationToken ct)
        => throw new NotSupportedException("Env provider is read-only. Set the variable in the host environment and add it to TAP_VARS_ALLOWED / TAP_SECRETS_ALLOWED.");

    public ValueTask<bool> DeleteAsync(string name, CancellationToken ct)
        => throw new NotSupportedException("Env provider is read-only. Unset the variable in the host environment instead.");

    private static IReadOnlyList<Regex> ParsePatterns(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        var entries = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var regex = new List<Regex>(entries.Length);
        foreach (var entry in entries)
        {
            var pattern = "^" + Regex.Escape(entry).Replace("\\*", ".*") + "$";
            regex.Add(new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant));
        }
        return regex;
    }

    private static bool Matches(string name, IReadOnlyList<Regex> patterns)
    {
        for (var i = 0; i < patterns.Count; i++)
            if (patterns[i].IsMatch(name)) return true;
        return false;
    }
}

public sealed class EnvVariableProviderFactory : IVariableProviderFactory
{
    public string Type => "env";

    public ProviderTypeDescriptor Descriptor { get; } = new()
    {
        Type = "env",
        DisplayName = "Host environment",
        Icon = "terminal",
        Description = "Reads allow-listed environment variables from the Studio host process.",
        Mode = ProviderMode.Read,
        // No per-instance settings on purpose: the allowlists (TAP_VARS_ALLOWED /
        // TAP_SECRETS_ALLOWED) live on the host so files can't widen secret reachability.
        Fields = [],
    };

    public IVariableProvider Create(VariableProviderConfig config, ProviderFactoryContext context)
        => new EnvVariableProvider(config);
}
