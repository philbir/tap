using Tap.Workspace.Model;

namespace Tap.Workspace.Testing;

/// <summary>
/// Composes the variable bag a test run hands to the renderer as its override tier.
///
/// <para>Pure, and deliberately separate from the runner that uses it: the precedence order is
/// the subtlest thing about the whole feature — get it wrong and a flow silently tests the
/// wrong data — so it belongs somewhere it can be pinned down by tests without an HTTP server.
/// A future headless runner composes its bag through exactly this.</para>
///
/// <para>Tiers, later winning (§10.5 of <c>docs/workspace-format.md</c>): test-set vars, the
/// test entry's vars, flow vars, values bound by <c>extract:</c>, the step's own vars.</para>
/// </summary>
public static class RunVariables
{
    /// <summary>The literal tier: a file's declared variable defaults plus any per-run
    /// overrides the caller supplied. Variables with no default contribute nothing — they are
    /// a declaration that something is needed, not a value.</summary>
    public static Dictionary<string, string> Literals(
        IReadOnlyDictionary<string, VarSpec> vars, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var bag = new Dictionary<string, string>(StringComparer.Ordinal);
        MergeLiterals(bag, vars);
        if (overrides is not null)
        {
            foreach (var (name, value) in overrides) bag[name] = value;
        }
        return bag;
    }

    /// <summary>Folds another file's declared defaults into an existing bag — a flow's own
    /// variables joining the test set's.</summary>
    public static void MergeLiterals(Dictionary<string, string> bag, IReadOnlyDictionary<string, VarSpec> vars)
    {
        foreach (var (name, spec) in vars)
        {
            if (spec.Default is { } value) bag[name] = value;
        }
    }

    /// <summary>
    /// The names in a bag whose value came out of a <c>vars:</c> block — as opposed to a per-run
    /// override the caller typed or a value a step bound with <c>extract:</c>.
    ///
    /// <para>The distinction is what lets a declared <c>'{{file:api.token}}'</c> resolve while a
    /// value that arrived in a response never does: only a file's own declaration is a template.
    /// The renderer takes this set as its <c>declaredOverrides</c>.</para>
    /// </summary>
    public static HashSet<string> DeclaredNames(
        IReadOnlyDictionary<string, VarSpec> vars, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        MergeDeclaredNames(names, vars);
        if (overrides is not null)
        {
            // An override wins the value, so it also decides what the value is: data, not a template.
            foreach (var name in overrides.Keys) names.Remove(name);
        }
        return names;
    }

    /// <summary>Adds another file's declared names — the counterpart to
    /// <see cref="MergeLiterals"/>, applied to the same vars in the same order.</summary>
    public static void MergeDeclaredNames(HashSet<string> names, IReadOnlyDictionary<string, VarSpec> vars)
    {
        foreach (var (name, spec) in vars)
        {
            if (spec.Default is not null) names.Add(name);
        }
    }

    /// <summary>
    /// Orders the template-valued overrides for one step: the enclosing entry's variables,
    /// then the step's own, which win.
    ///
    /// <para>An ordered list rather than a dictionary because each of these is expanded against
    /// the ones before it — order is semantics here, not presentation. A name declared at both
    /// levels appears once, carrying the step's value.</para>
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>>? Templates(
        IReadOnlyDictionary<string, string>? entry, IReadOnlyDictionary<string, string>? step)
    {
        var entryCount = entry?.Count ?? 0;
        var stepCount = step?.Count ?? 0;
        if (entryCount == 0 && stepCount == 0) return null;

        var ordered = new List<KeyValuePair<string, string>>(entryCount + stepCount);
        if (entry is not null)
        {
            foreach (var pair in entry)
            {
                if (step is not null && step.ContainsKey(pair.Key)) continue;
                ordered.Add(pair);
            }
        }
        if (step is not null)
        {
            foreach (var pair in step) ordered.Add(pair);
        }
        return ordered;
    }
}
