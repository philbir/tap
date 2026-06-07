namespace Tap.Workspace.Model;

/// <summary>
/// A variable declaration. May be a literal default value or a structured spec with
/// description / required / example fields. See spec §5.1.
/// </summary>
public sealed record VarSpec
{
    public string? Default { get; init; }
    public string? Description { get; init; }
    public bool Required { get; init; }
    public string? Example { get; init; }

    /// <summary>
    /// Marks the variable as secret-bearing. The Studio UI redacts the value in catalog
    /// listings, autocomplete, and the variables panel. Templates resolve secret variables
    /// the same way as plain ones — there's no special <c>${{…}}</c> syntax in user-typed
    /// fields; the flag is the only marker.
    /// </summary>
    public bool Secret { get; init; }

    public static VarSpec FromLiteral(string value) => new() { Default = value };
}
