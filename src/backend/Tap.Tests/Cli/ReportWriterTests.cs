using System.Xml.Linq;
using Tap.Execution.Contracts;
using Tap.Studio.Cli.Output;

namespace Tap.Tests.Cli;

/// <summary>
/// The reports CI ingests. These are a contract with tooling nobody here controls, so the
/// shape matters more than the prose: a wrong count or a wrong encoding means a build that
/// looks green in one system and broken in another.
/// </summary>
public class ReportWriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tap-cli-report").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static TestStepResultDto Step(bool ok, string name = "step", params AssertResultDto[] assertions) => new(
        Index: 0, Name: name, RequestPath: "collections/demo/a.req.md",
        Method: "GET", Url: "https://example.test/a", Status: ok ? 200 : 500, StatusText: "OK",
        ContentType: "application/json", ResponseBody: "{}", ResponseBodyBytes: 2, DurationMs: 12,
        Assertions: assertions,
        AssertSummary: new AssertSummaryDto(ok, assertions.Count(a => a.Ok), assertions.Count(a => !a.Ok), 0),
        Extracted: [], Ok: ok, Skipped: false, Error: null);

    private static TestEntryResultDto Entry(int index, string name, bool ok, bool skipped = false, string? error = null)
        => new(index, name, "request", "collections/demo/a.req.md",
            skipped ? [] : [Step(ok, name, new AssertResultDto(0, "status = 200", ok, false, ok ? "200" : "500", "200", ok ? null : "expected 200, got 500"))],
            ok, skipped, 12, error);

    private static TestRunResultDto Run(params TestEntryResultDto[] entries) => new(
        Path: "tests/orders.test.md", Kind: "test", Name: "Order API", Entries: entries,
        Ok: entries.All(e => e.Ok), Passed: entries.Count(e => e.Ok && !e.Skipped),
        Failed: entries.Count(e => !e.Ok && !e.Skipped), Skipped: entries.Count(e => e.Skipped),
        DurationMs: 42, Error: null);

    private XDocument WriteAndParse(string format, TestRunResultDto run)
    {
        var path = Path.Combine(_dir, $"out.{format}");
        Assert.True(ReportWriter.TryWrite(format, path, run, out var written, out var error), error);
        Assert.Equal(path, written);
        return XDocument.Load(written);
    }

    [Fact]
    public void JUnit_counts_match_the_run()
    {
        var doc = WriteAndParse("junit", Run(
            Entry(0, "passes", true),
            Entry(1, "fails", false),
            Entry(2, "skipped", true, skipped: true)));

        var suite = doc.Root!.Element("testsuite")!;
        Assert.Equal("3", suite.Attribute("tests")!.Value);
        Assert.Equal("1", suite.Attribute("failures")!.Value);
        Assert.Equal("1", suite.Attribute("skipped")!.Value);
        Assert.Equal(3, suite.Elements("testcase").Count());
    }

    [Fact]
    public void A_failing_case_carries_a_failure_element_naming_the_assertion()
    {
        var doc = WriteAndParse("junit", Run(Entry(0, "fails", false)));
        var failure = doc.Descendants("failure").Single();

        Assert.Contains("status = 200", failure.Value, StringComparison.Ordinal);
        Assert.Contains("GET https://example.test/a", failure.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void A_passing_case_carries_no_failure_element()
    {
        var doc = WriteAndParse("junit", Run(Entry(0, "passes", true)));
        Assert.Empty(doc.Descendants("failure"));
        // …but still records what it hit, so a green run is auditable.
        Assert.Contains("GET https://example.test/a", doc.Descendants("system-out").Single().Value, StringComparison.Ordinal);
    }

    [Fact]
    public void A_skipped_case_is_marked_skipped_not_failed()
    {
        var doc = WriteAndParse("junit", Run(Entry(0, "skipped", true, skipped: true)));
        Assert.Single(doc.Descendants("skipped"));
        Assert.Empty(doc.Descendants("failure"));
    }

    [Fact]
    public void The_declared_encoding_matches_the_bytes_on_disk()
    {
        // XmlWriter takes the declaration from its writer; a plain StringWriter claims UTF-16
        // while the file is written UTF-8, and strict parsers reject the mismatch.
        var path = Path.Combine(_dir, "encoding.xml");
        Assert.True(ReportWriter.TryWrite("junit", path, Run(Entry(0, "passes", true)), out _, out _));
        Assert.Contains("encoding=\"utf-8\"", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Trx_reports_outcomes_and_lines_results_up_with_definitions()
    {
        var doc = WriteAndParse("trx", Run(Entry(0, "passes", true), Entry(1, "fails", false)));
        var ns = doc.Root!.Name.Namespace;

        var outcomes = doc.Descendants(ns + "UnitTestResult")
            .Select(r => r.Attribute("outcome")!.Value)
            .ToArray();
        Assert.Equal(["Passed", "Failed"], outcomes);

        var resultIds = doc.Descendants(ns + "UnitTestResult").Select(r => r.Attribute("testId")!.Value).ToHashSet();
        var definitionIds = doc.Descendants(ns + "UnitTest").Select(t => t.Attribute("id")!.Value).ToHashSet();
        Assert.Equal(resultIds, definitionIds);
    }

    [Fact]
    public void Trx_ids_are_stable_across_runs()
    {
        // A CI system tracks a test across builds by its id; a fresh GUID each run makes
        // history useless.
        var first = WriteAndParse("trx", Run(Entry(0, "passes", true)));
        var second = WriteAndParse("trx", Run(Entry(0, "passes", true)));
        var ns = first.Root!.Name.Namespace;

        Assert.Equal(
            first.Descendants(ns + "UnitTest").Single().Attribute("id")!.Value,
            second.Descendants(ns + "UnitTest").Single().Attribute("id")!.Value);
    }

    [Fact]
    public void Json_wraps_the_engine_shape_in_a_totals_envelope()
    {
        var path = Path.Combine(_dir, "out.json");
        Assert.True(ReportWriter.TryWrite("json", path, Run(Entry(0, "passes", true), Entry(1, "fails", false)), out _, out _));

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal(1, root.GetProperty("passed").GetInt32());
        Assert.Equal(1, root.GetProperty("failed").GetInt32());

        // Each element is the engine's own result, unmodified.
        var run = root.GetProperty("runs").EnumerateArray().Single();
        Assert.Equal("Order API", run.GetProperty("name").GetString());
        Assert.Equal(2, run.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public void Json_is_an_envelope_even_for_one_target()
    {
        // A script shouldn't have to branch on whether the caller happened to pass --tag.
        var path = Path.Combine(_dir, "single.json");
        Assert.True(ReportWriter.TryWrite("json", path, Run(Entry(0, "passes", true)), out _, out _));

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(System.Text.Json.JsonValueKind.Array, document.RootElement.GetProperty("runs").ValueKind);
    }

    [Fact]
    public void JUnit_gives_each_target_its_own_suite()
    {
        var first = Run(Entry(0, "passes", true));
        var second = Run(Entry(0, "fails", false)) with { Path = "tests/other.test.md", Name = "Other" };

        var path = Path.Combine(_dir, "multi.xml");
        Assert.True(ReportWriter.TryWrite("junit", path, new[] { first, second }, out _, out var error), error);
        var doc = XDocument.Load(path);

        var suites = doc.Root!.Elements("testsuite").ToArray();
        Assert.Equal(2, suites.Length);
        Assert.Equal(["Order API", "Other"], suites.Select(s => s.Attribute("name")!.Value));

        // Root totals span every target, so a CI badge reads the run, not the first file.
        Assert.Equal("2", doc.Root.Attribute("tests")!.Value);
        Assert.Equal("1", doc.Root.Attribute("failures")!.Value);
    }

    [Fact]
    public void Trx_merges_targets_into_one_run_with_unique_ids()
    {
        var first = Run(Entry(0, "passes", true));
        var second = Run(Entry(0, "passes", true)) with { Path = "tests/other.test.md", Name = "Other" };

        var path = Path.Combine(_dir, "multi.trx");
        Assert.True(ReportWriter.TryWrite("trx", path, new[] { first, second }, out _, out var error), error);
        var doc = XDocument.Load(path);
        var ns = doc.Root!.Name.Namespace;

        // Same entry index and name in two files must not collide on one id.
        var ids = doc.Descendants(ns + "UnitTest").Select(t => t.Attribute("id")!.Value).ToArray();
        Assert.Equal(2, ids.Length);
        Assert.Equal(2, ids.Distinct().Count());
        Assert.Equal("2", doc.Descendants(ns + "Counters").Single().Attribute("total")!.Value);
    }

    // --- Markdown ----------------------------------------------------------------------

    private string WriteMarkdown(params TestRunResultDto[] runs)
    {
        var path = Path.Combine(_dir, $"out-{runs.Length}-{Guid.NewGuid():N}.md");
        Assert.True(ReportWriter.TryWrite("markdown", path, runs, out _, out var error), error);
        return File.ReadAllText(path);
    }

    [Fact]
    public void Markdown_leads_with_the_verdict_and_totals()
    {
        var md = WriteMarkdown(Run(Entry(0, "passes", true), Entry(1, "fails", false)));
        var first = md.Split('\n')[0];

        Assert.Equal("# Order API", first);
        Assert.Contains("❌", md, StringComparison.Ordinal);
        Assert.Contains("**1 passed**", md, StringComparison.Ordinal);
        Assert.Contains("**1 failed**", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_puts_failures_above_the_tables()
    {
        // Someone opens a job summary because a build went red; the reason belongs on the
        // screen they land on, not behind a scroll.
        var md = WriteMarkdown(Run(Entry(0, "passes", true), Entry(1, "fails", false)));

        var failures = md.IndexOf("## Failures", StringComparison.Ordinal);
        var table = md.IndexOf("| | Test |", StringComparison.Ordinal);
        Assert.True(failures >= 0, "expected a Failures section");
        Assert.True(failures < table, "failures should precede the table");

        Assert.Contains("status = 200", md, StringComparison.Ordinal);
        Assert.Contains("expected 200, got 500", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_has_no_failure_section_when_everything_passed()
    {
        var md = WriteMarkdown(Run(Entry(0, "passes", true)));
        Assert.DoesNotContain("## Failures", md, StringComparison.Ordinal);
        Assert.Contains("✅", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_collapses_a_passing_target_and_leaves_a_failing_one_open()
    {
        Assert.Contains("<details>", WriteMarkdown(Run(Entry(0, "passes", true))), StringComparison.Ordinal);
        Assert.DoesNotContain("<details>", WriteMarkdown(Run(Entry(0, "fails", false))), StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_leaves_a_blank_line_after_every_summary_tag()
    {
        // GitHub only renders Markdown inside <details> when a blank line follows <summary> —
        // without it the table below comes out as literal pipes.
        var second = Run(Entry(0, "passes", true)) with { Path = "tests/other.test.md", Name = "Other" };
        var lines = WriteMarkdown(Run(Entry(0, "passes", true)), second).Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("<summary>", StringComparison.Ordinal)) continue;
            Assert.True(i + 1 < lines.Length && lines[i + 1].Length == 0,
                $"line {i + 1} after <summary> must be blank");
        }
        Assert.Equal(
            lines.Count(l => l.Trim() == "<details>"),
            lines.Count(l => l.Trim() == "</details>"));
    }

    [Fact]
    public void Markdown_gives_each_target_a_section()
    {
        var second = Run(Entry(0, "passes", true)) with { Path = "tests/other.test.md", Name = "Other" };
        var md = WriteMarkdown(Run(Entry(0, "passes", true)), second);

        Assert.StartsWith("# 2 test sets", md, StringComparison.Ordinal);
        Assert.Contains("Order API", md, StringComparison.Ordinal);
        Assert.Contains("Other", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_marks_a_skipped_test_distinctly()
    {
        var md = WriteMarkdown(Run(Entry(0, "skipped", true, skipped: true)));
        Assert.Contains("⚪", md, StringComparison.Ordinal);
        Assert.Contains("1 skipped", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_escapes_a_name_that_would_otherwise_be_markup()
    {
        var md = WriteMarkdown(Run(Entry(0, "a|b _c_ *d*", true)));

        // An unescaped pipe would end the table row early and shift every later column.
        Assert.Contains(@"a\|b \_c\_ \*d\*", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_does_not_escape_inside_code_spans()
    {
        // Content in a code span is literal — escaping it prints the backslashes, so a path
        // with an underscore would render as `my\_tests/...`.
        var run = Run(Entry(0, "passes", true)) with { Path = "tests/my_tests/orders.test.md" };
        var md = WriteMarkdown(run);

        Assert.Contains("`tests/my_tests/orders.test.md`", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_is_accepted_under_its_short_name()
    {
        var path = Path.Combine(_dir, "short.md");
        Assert.True(ReportWriter.TryWrite("md", path, Run(Entry(0, "passes", true)), out var written, out var error), error);
        Assert.Equal(path, written);
        Assert.StartsWith("# Order API", File.ReadAllText(written), StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_defaults_to_a_md_extension()
    {
        Assert.True(ReportWriter.TryWrite("markdown", null, Run(Entry(0, "passes", true)), out var written, out _));
        Assert.EndsWith("test-results.md", written, StringComparison.Ordinal);
        File.Delete(written);
    }

    [Fact]
    public void An_unknown_format_is_rejected_with_the_options()
    {
        Assert.False(ReportWriter.TryWrite("xunit", null, Run(), out _, out var error));
        Assert.Contains("junit, trx, json, or markdown", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_parent_directories_are_created()
    {
        var path = Path.Combine(_dir, "nested", "deeper", "out.xml");
        Assert.True(ReportWriter.TryWrite("junit", path, Run(Entry(0, "passes", true)), out _, out var error), error);
        Assert.True(File.Exists(path));
    }
}
