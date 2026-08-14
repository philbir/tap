using Tap.Studio.Cli;

namespace Tap.Tests.Cli;

/// <summary>
/// How a CI job gets values into a run. Worth pinning down because the failure mode is silent:
/// a variable file that doesn't load the way the author expects produces a run against the
/// wrong data that still reports green.
/// </summary>
public class VariableInputsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tap-cli-vars").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Inline_assignments_are_read()
    {
        Assert.True(VariableInputs.TryCollect(null, ["customer=cus_ci", "sku=ABC-1"], out var vars, out _));
        Assert.Equal("cus_ci", vars["customer"]);
        Assert.Equal("ABC-1", vars["sku"]);
    }

    [Fact]
    public void A_value_may_contain_equals_signs()
    {
        // Base64 and connection strings both do; splitting on the last '=' would corrupt them.
        Assert.True(VariableInputs.TryCollect(null, ["token=abc=def=="], out var vars, out _));
        Assert.Equal("abc=def==", vars["token"]);
    }

    [Fact]
    public void An_empty_value_is_legal()
    {
        Assert.True(VariableInputs.TryCollect(null, ["empty="], out var vars, out _));
        Assert.Equal(string.Empty, vars["empty"]);
    }

    [Theory]
    [InlineData("novalue")]
    [InlineData("=orphan")]
    public void A_malformed_assignment_is_rejected(string assignment)
    {
        Assert.False(VariableInputs.TryCollect(null, [assignment], out _, out var error));
        Assert.Contains("--var", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Dotenv_files_are_read()
    {
        var file = Write("ci.env", """
            # a comment
            NAME=value
            export EXPORTED=works

            QUOTED="with spaces"
            SINGLE='single'
            """);

        Assert.True(VariableInputs.TryCollect([file], null, out var vars, out var error), error);
        Assert.Equal("value", vars["NAME"]);
        Assert.Equal("works", vars["EXPORTED"]);
        Assert.Equal("with spaces", vars["QUOTED"]);
        Assert.Equal("single", vars["SINGLE"]);
        Assert.DoesNotContain("# a comment", vars.Keys);
    }

    [Fact]
    public void Json_files_are_read_and_scalars_stringified()
    {
        var file = Write("ci.json", """
            { "name": "value", "count": 3, "enabled": true, "missing": null }
            """);

        Assert.True(VariableInputs.TryCollect([file], null, out var vars, out var error), error);
        Assert.Equal("value", vars["name"]);
        Assert.Equal("3", vars["count"]);
        Assert.Equal("true", vars["enabled"]);
        Assert.Equal(string.Empty, vars["missing"]);
    }

    [Fact]
    public void A_nested_json_value_is_rejected_rather_than_flattened()
    {
        var file = Write("nested.json", """{ "outer": { "inner": 1 } }""");
        Assert.False(VariableInputs.TryCollect([file], null, out _, out var error));
        Assert.Contains("flat name/value pairs", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Later_files_win_and_inline_beats_every_file()
    {
        var first = Write("first.env", "shared=from-first\nonly-first=1");
        var second = Write("second.env", "shared=from-second");

        Assert.True(VariableInputs.TryCollect([first, second], ["shared=from-cli"], out var vars, out _));
        Assert.Equal("from-cli", vars["shared"]);
        Assert.Equal("1", vars["only-first"]);
    }

    [Fact]
    public void A_missing_file_is_an_error_not_an_empty_set()
    {
        // Silently continuing would run the tests against whatever the files declare, and
        // report green for a run that never received its inputs.
        Assert.False(VariableInputs.TryCollect([Path.Combine(_dir, "nope.env")], null, out _, out var error));
        Assert.Contains("does not exist", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_malformed_dotenv_line_names_the_line()
    {
        var file = Write("bad.env", "GOOD=1\nthis is not an assignment\n");
        Assert.False(VariableInputs.TryCollect([file], null, out _, out var error));
        Assert.Contains("line 2", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_json_is_reported_as_such()
    {
        var file = Write("bad.json", "{ not json");
        Assert.False(VariableInputs.TryCollect([file], null, out _, out var error));
        Assert.Contains("not valid JSON", error, StringComparison.Ordinal);
    }
}
