using System.Globalization;
using System.Security.Cryptography;

namespace Tap.Workspace.Rendering;

/// <summary>
/// The <c>$</c>-prefixed tokens every tool in the <c>.http</c> ecosystem provides:
/// <c>{{$guid}}</c>, <c>{{$timestamp}}</c>, and friends. Generated at render time rather than
/// looked up, so they need no provider and no configuration.
///
/// <para>Deliberately small. Each of REST Client, JetBrains, and httpyac ships a different and
/// much larger set, and guessing at the union would mean silently producing a value where another
/// tool produced something else. These five are the ones all of them agree on; anything else
/// <c>$</c>-prefixed is reported as an unknown variable, which is honest.</para>
///
/// <para>A token resolves once per render (<see cref="Interpolation"/> caches by token text), so
/// <c>{{$guid}}</c> used in a header and again in the body is the <em>same</em> guid. That is
/// what makes it usable as a correlation id, which is the main reason to reach for it.</para>
/// </summary>
public static class DynamicVariables
{
    /// <summary>Resolves a <c>$</c>-prefixed token, or returns false if the name isn't one of ours.</summary>
    public static bool TryResolve(string name, out string value)
    {
        var space = name.IndexOf(' ');
        var head = space < 0 ? name : name[..space];
        var arguments = space < 0 ? string.Empty : name[(space + 1)..].Trim();

        switch (head.ToLowerInvariant())
        {
            case "$guid":
            case "$uuid":
                value = Guid.NewGuid().ToString();
                return true;

            case "$timestamp":
                value = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                return true;

            case "$isotimestamp":
                value = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                return true;

            case "$randomint":
                value = RandomInt(arguments);
                return true;

            default:
                value = string.Empty;
                return false;
        }
    }

    /// <summary>True when the name looks like one of ours — used to keep dynamic tokens out of
    /// the "variables this request needs" view, where they would read as unresolved inputs.</summary>
    public static bool IsDynamic(string name) => TryResolve(name, out _);

    /// <summary><c>$randomInt</c> with optional <c>min max</c> arguments, defaulting to the
    /// ecosystem's 0–1000. Bad arguments fall back to the default range rather than failing the
    /// render — a malformed token is not worth blocking a request over.</summary>
    private static string RandomInt(string arguments)
    {
        var min = 0;
        var max = 1000;

        var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2
            && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var parsedMin)
            && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var parsedMax)
            && parsedMin < parsedMax)
        {
            min = parsedMin;
            max = parsedMax;
        }

        return RandomNumberGenerator.GetInt32(min, max).ToString(CultureInfo.InvariantCulture);
    }
}
