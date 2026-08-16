using System.Text.RegularExpressions;

namespace Tap.Workspace.Variables.Providers;

/// <summary>
/// Resolves <c>{{aspire:&lt;resource-name&gt;}}</c> to a resource's allocated URL, so a collection
/// can declare <c>baseUrl: {{aspire:orders-api}}</c> and hit whatever port the AppHost handed
/// out this run.
///
/// <para><b>It reads the standard service-discovery convention, not an Aspire API.</b> Aspire
/// injects <c>services__{name}__{scheme}__{index}</c> into every resource that has a reference,
/// and this provider reads exactly those. That choice is what makes the same workspace runnable
/// in CI with no Aspire and no code change — export
/// <c>services__orders-api__https__0=https://staging.example.com</c> and
/// <c>tap-studio test</c> resolves the identical token. A provider bound to Aspire's own types
/// would have made the AppHost a runtime dependency of the test suite.</para>
///
/// <para>Values are never secret: an allocated URL is not a credential, and marking it secret
/// would redact the one field you most need to read in a failure report.</para>
/// </summary>
public sealed partial class AspireVariableProvider(VariableProviderConfig config)
    : IVariableProvider, IExplainsMissingValues
{
    public const string TypeName = "aspire";

    /// <summary>The env-var convention: <c>services__{name}__{scheme}__{index}</c>.</summary>
    private const string Prefix = "services__";

    /// <summary>Schemes in preference order. https first because a resource that offers both is
    /// telling you which one it would rather you used.</summary>
    private static readonly string[] SchemePreference = ["https", "http"];

    public string Name => config.Name;
    public ProviderMode Mode => ProviderMode.Read;
    public VariableProviderConfig Config => config;

    public ValueTask<VariableValue?> GetAsync(string name, CancellationToken ct)
        => ValueTask.FromResult(Resolve(name, ReadEnvironment()));

    public ValueTask<IReadOnlyList<VariableValue>> ListAsync(CancellationToken ct)
    {
        var env = ReadEnvironment();
        var result = new List<VariableValue>();

        foreach (var resource in ResourceNames(env))
        {
            if (Resolve(resource, env) is { } value) result.Add(value);
        }

        result.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return ValueTask.FromResult<IReadOnlyList<VariableValue>>(result);
    }

    /// <summary>A miss here is almost always a missing environment variable rather than a typo,
    /// so name the exact one — that turns "resolved to null" into a one-line fix.</summary>
    public string ExplainMiss(string name)
        => $"No endpoint for resource '{name}'. Under an Aspire AppHost, reference it "
         + $"(WithReference) so it is injected; elsewhere set services__{name}__https__0 "
         + $"(or __http__0) to the URL — that is how CI runs the same workspace.";

    public ValueTask SetAsync(string name, string value, bool isSecret, CancellationToken ct)
        => throw new NotSupportedException(
            "The aspire provider is read-only — endpoints are allocated by the AppHost. "
            + "Set services__<resource>__<scheme>__0 in the environment to override one.");

    /// <summary>
    /// Picks the best URL for a resource: preferred scheme first, then lowest index. Returns null
    /// when the resource has no endpoint in the environment, which the registry turns into
    /// <c>E_PROVIDER_RESOLUTION_FAILED</c> naming the variable we looked for.
    /// </summary>
    public static VariableValue? Resolve(string resourceName, IReadOnlyDictionary<string, string> env)
    {
        if (string.IsNullOrWhiteSpace(resourceName)) return null;

        foreach (var scheme in SchemePreference)
        {
            var best = default(string);
            var bestIndex = int.MaxValue;
            var head = $"{Prefix}{resourceName}__{scheme}__";

            foreach (var (key, value) in env)
            {
                if (!key.StartsWith(head, StringComparison.OrdinalIgnoreCase)) continue;
                if (!int.TryParse(key[head.Length..], out var index)) continue;
                if (index >= bestIndex || string.IsNullOrWhiteSpace(value)) continue;
                bestIndex = index;
                best = value;
            }

            if (best is not null)
                return new VariableValue(resourceName, best.TrimEnd('/'), IsSecret: false, ProviderName: TypeName);
        }

        return null;
    }

    /// <summary>Every resource name the environment mentions, for the vars view.</summary>
    public static IEnumerable<string> ResourceNames(IReadOnlyDictionary<string, string> env)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in env.Keys)
        {
            var match = KeyPattern().Match(key);
            if (match.Success) names.Add(match.Groups["name"].Value);
        }
        return names;
    }

    /// <summary>Resource names carry dashes (<c>orders-api</c>), which is why this is a pattern
    /// rather than a split on <c>__</c>.</summary>
    [GeneratedRegex(@"^services__(?<name>.+?)__(?<scheme>[a-z]+)__(?<index>\d+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    private static Dictionary<string, string> ReadEnvironment()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables())
        {
            if (kv.Key is string key && kv.Value is string value
                && key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                map[key] = value;
            }
        }
        return map;
    }
}

public sealed class AspireVariableProviderFactory : IVariableProviderFactory
{
    public string Type => AspireVariableProvider.TypeName;

    public ProviderTypeDescriptor Descriptor { get; } = new()
    {
        Type = AspireVariableProvider.TypeName,
        DisplayName = "Aspire service discovery",
        Icon = "plug-connected",
        Description =
            "Resolves {{aspire:<resource>}} to a resource's allocated URL, read from the standard "
            + "services__<resource>__<scheme>__<index> environment variables. Works under an Aspire "
            + "AppHost, and in CI by exporting those variables yourself.",
        Mode = ProviderMode.Read,
        Fields = [],
    };

    public IVariableProvider Create(VariableProviderConfig config, ProviderFactoryContext context)
        => new AspireVariableProvider(config);
}
