using Tap.Workspace.Model;
using Tap.Workspace.Testing;

namespace Tap.Tests.Testing;

/// <summary>
/// The precedence order of a run's variable bag. Subtle and consequential: get it wrong and a
/// flow quietly tests the wrong data while every assertion still passes.
/// </summary>
public class RunVariablesTests
{
    private static Dictionary<string, VarSpec> Vars(params (string Name, string? Default)[] entries)
        => entries.ToDictionary(e => e.Name, e => new VarSpec { Default = e.Default });

    [Fact]
    public void Literals_take_declared_defaults()
    {
        var bag = RunVariables.Literals(Vars(("sku", "ABC-1"), ("customer", "cus_demo")));
        Assert.Equal("ABC-1", bag["sku"]);
        Assert.Equal("cus_demo", bag["customer"]);
    }

    [Fact]
    public void A_variable_with_no_default_contributes_nothing()
    {
        // `required: true` with no default is a declaration that something is needed, not a
        // value — binding it to "" would satisfy the requirement with nonsense.
        Assert.Empty(RunVariables.Literals(Vars(("sku", null))));
    }

    [Fact]
    public void Per_run_overrides_beat_declared_defaults()
    {
        var bag = RunVariables.Literals(
            Vars(("sku", "ABC-1")),
            new Dictionary<string, string> { ["sku"] = "OVERRIDE" });
        Assert.Equal("OVERRIDE", bag["sku"]);
    }

    [Fact]
    public void A_flows_variables_join_the_sets()
    {
        var bag = RunVariables.Literals(Vars(("customer", "cus_demo"), ("sku", "from-set")));
        RunVariables.MergeLiterals(bag, Vars(("sku", "from-flow")));

        Assert.Equal("cus_demo", bag["customer"]);
        Assert.Equal("from-flow", bag["sku"]);
    }

    [Fact]
    public void Templates_are_ordered_entry_then_step()
    {
        // Order is semantics: each template expands against the ones before it, so a step var
        // reading {{fromEntry}} only works if the entry's landed first.
        var ordered = RunVariables.Templates(
            new Dictionary<string, string> { ["fromEntry"] = "e" },
            new Dictionary<string, string> { ["fromStep"] = "{{fromEntry}}-s" })!;

        Assert.Collection(ordered,
            first => Assert.Equal("fromEntry", first.Key),
            second => Assert.Equal("fromStep", second.Key));
    }

    [Fact]
    public void A_step_variable_wins_over_the_entrys_and_appears_once()
    {
        var ordered = RunVariables.Templates(
            new Dictionary<string, string> { ["id"] = "from-entry", ["other"] = "kept" },
            new Dictionary<string, string> { ["id"] = "from-step" })!;

        var id = Assert.Single(ordered, p => p.Key == "id");
        Assert.Equal("from-step", id.Value);
        Assert.Contains(ordered, p => p.Key == "other");
        Assert.Equal(2, ordered.Count);
    }

    [Fact]
    public void No_templates_is_null_rather_than_an_empty_list()
    {
        // The renderer skips the whole expansion pass on null; an empty list would mean
        // "expand nothing", which is the same result by a slower route.
        Assert.Null(RunVariables.Templates(null, null));
        Assert.Null(RunVariables.Templates(new Dictionary<string, string>(), null));
    }

    [Fact]
    public void Either_side_alone_is_carried_through()
    {
        var entryOnly = RunVariables.Templates(new Dictionary<string, string> { ["a"] = "1" }, null)!;
        Assert.Equal("a", Assert.Single(entryOnly).Key);

        var stepOnly = RunVariables.Templates(null, new Dictionary<string, string> { ["b"] = "2" })!;
        Assert.Equal("b", Assert.Single(stepOnly).Key);
    }

    // -------------------------------------------------------------------------------------
    // Which of the bag's names are templates. A file's own declaration may reference another
    // variable; a value the run produced may not — see DeclaredReferenceTests.
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Declared_names_are_the_ones_a_file_wrote()
    {
        var names = RunVariables.DeclaredNames(Vars(("sku", "ABC-1"), ("customer", "cus_demo")));
        Assert.Equal(["customer", "sku"], names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_variable_with_no_default_declares_no_value_to_expand()
    {
        Assert.Empty(RunVariables.DeclaredNames(Vars(("sku", null))));
    }

    [Fact]
    public void A_per_run_override_takes_the_name_back()
    {
        // `--var sku='{{file:sku}}'` is the caller's literal, not the file's template — and the
        // caller's value is the one in the bag.
        var names = RunVariables.DeclaredNames(
            Vars(("sku", "ABC-1"), ("customer", "cus_demo")),
            new Dictionary<string, string> { ["sku"] = "OVERRIDE" });

        Assert.Equal("customer", Assert.Single(names));
    }

    [Fact]
    public void A_flows_declarations_join_the_sets()
    {
        var names = RunVariables.DeclaredNames(Vars(("customer", "cus_demo")));
        RunVariables.MergeDeclaredNames(names, Vars(("sku", "from-flow"), ("empty", null)));

        Assert.Equal(["customer", "sku"], names.Order(StringComparer.Ordinal));
    }
}
