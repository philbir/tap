using Spectre.Console;
using Tap.Execution.Contracts;

namespace Tap.Studio.Cli.Output;

/// <summary>
/// Prints a run as it happens.
///
/// <para>Lines are emitted when each step and entry completes rather than redrawn as a live
/// tree. A tree looks better in a terminal for about ten seconds and is actively worse
/// everywhere else: CI captures stdout as a log, and a log of cursor-repositioning escape
/// codes is unreadable. Incremental lines are the same in both places, and in both places they
/// tell you which request the run is currently sitting on.</para>
///
/// <para>Colour and glyphs degrade on their own — <see cref="AnsiConsole"/> follows
/// <c>NO_COLOR</c> and a redirected stdout, and the verdict marks fall back to ASCII when the
/// terminal can't render the shapes.</para>
/// </summary>
public sealed class ConsoleReporter(IAnsiConsole console, bool verbose)
{
    private readonly Dictionary<int, string> _entryNames = new();

    private bool _anyStarted;

    public void Started(TestRunStartDto start)
    {
        // Blank line between targets, none before the first — so a single run stays compact.
        if (_anyStarted) console.WriteLine();
        _anyStarted = true;

        var kind = start.Kind == "flow" ? "flow" : "test set";
        console.MarkupLine($"[bold]{Escape(start.Name)}[/] [dim]({kind}, {start.Entries.Count} {Plural(start.Entries.Count, "entry", "entries")})[/]");

        var scope = new List<string> { Escape(start.Path) };
        if (start.Env is { Length: > 0 }) scope.Add($"env {Escape(start.Env)}");
        console.MarkupLine($"[dim]{string.Join(" · ", scope)}[/]");
        console.WriteLine();

        foreach (var entry in start.Entries) _entryNames[entry.Index] = entry.Name;
    }

    /// <summary>A step finished. For a flow this is the interesting granularity — it is where
    /// the run actually spends its time, and where a bound value first appears.</summary>
    public void Step(TestRunStepEventDto e)
    {
        var step = e.Step;
        var indent = "    ";

        if (step.Skipped)
        {
            console.MarkupLine($"{indent}[dim]{Mark.Skip} {Escape(step.Name)} — {Escape(step.Error ?? "skipped")}[/]");
            return;
        }

        var mark = step.Ok ? $"[green]{Mark.Pass}[/]" : $"[red]{Mark.Fail}[/]";
        var status = step.Status > 0 ? $" [dim]{step.Status}[/]" : string.Empty;
        var timing = step.DurationMs > 0 ? $" [dim]{step.DurationMs:F0}ms[/]" : string.Empty;
        var asserts = step.AssertSummary is { } s && s.Passed + s.Failed > 0
            ? $" [dim]{s.Passed}/{s.Passed + s.Failed}[/]"
            : string.Empty;

        console.MarkupLine($"{indent}{mark} {Escape(step.Name)}{status}{asserts}{timing}");

        if (verbose && step.Method != "—")
            console.MarkupLine($"{indent}  [dim]{Escape(step.Method)} {Escape(step.Url)}[/]");

        foreach (var bound in step.Extracted)
        {
            if (bound.Error is { } boundError)
                console.MarkupLine($"{indent}  [red]{Mark.Fail} {Escape(boundError)}[/]");
            else if (verbose && bound.Value is { } value)
                console.MarkupLine($"{indent}  [dim]bound {Escape(bound.Var)} = {Escape(Truncate(value, 120))}[/]");
        }

        // Failures always explain themselves; passes only when asked. A green run should be
        // quiet enough that the failures in a mostly-green run are the thing you see.
        foreach (var assertion in step.Assertions)
        {
            if (assertion.Skipped) continue;
            if (assertion.Ok)
            {
                if (verbose) console.MarkupLine($"{indent}  [dim]{Mark.Pass} {Escape(assertion.Name)}[/]");
                continue;
            }
            console.MarkupLine($"{indent}  [red]{Mark.Fail} {Escape(assertion.Name)}[/]");
            console.MarkupLine($"{indent}    [dim]{Escape(Explain(assertion))}[/]");
        }

        if (step.Error is { } error)
            console.MarkupLine($"{indent}  [red]{Escape(error)}[/]");
    }

    public void Entry(TestEntryResultDto entry)
    {
        // A flow run has a single wrapping entry whose steps already reported everything.
        if (entry.Steps.Count == 1 && !entry.Skipped) return;

        if (entry.Skipped)
        {
            console.MarkupLine($"[dim]{Mark.Skip} {Escape(entry.Name)} — skipped[/]");
            return;
        }

        var mark = entry.Ok ? $"[green]{Mark.Pass}[/]" : $"[red]{Mark.Fail}[/]";
        console.MarkupLine($"{mark} {Escape(entry.Name)} [dim]{entry.DurationMs:F0}ms[/]");
        if (!entry.Ok && entry.Error is { } error)
            console.MarkupLine($"  [red]{Escape(error)}[/]");
    }

    /// <summary>The line a human scans for and CI puts in a job summary. Totals across every
    /// target, so a <c>--tag</c> run ends with one verdict rather than a verdict per file the
    /// reader has to add up.</summary>
    public void Finished(IReadOnlyList<TestRunResultDto> results)
    {
        console.WriteLine();

        if (results.FirstOrDefault(r => r.Error is not null)?.Error is { } fatal)
        {
            console.MarkupLine($"[red]{Mark.Fail} {Escape(fatal)}[/]");
            return;
        }

        var passed = results.Sum(r => r.Passed);
        var failed = results.Sum(r => r.Failed);
        var skipped = results.Sum(r => r.Skipped);
        var duration = results.Sum(r => r.DurationMs);

        var parts = new List<string>();
        if (passed > 0) parts.Add($"[green]{passed} passed[/]");
        if (failed > 0) parts.Add($"[red]{failed} failed[/]");
        if (skipped > 0) parts.Add($"[dim]{skipped} skipped[/]");
        if (parts.Count == 0) parts.Add("[dim]nothing ran[/]");

        var across = results.Count > 1
            ? $"  [dim]across {results.Count} {Kinds(results)}[/]"
            : string.Empty;

        var verdict = results.All(r => r.Ok) ? "[green]PASS[/]" : "[red]FAIL[/]";
        console.MarkupLine($"{verdict}  {string.Join(" · ", parts)}{across}  [dim]{duration:F0}ms[/]");
    }

    public void Error(string message) => console.MarkupLine($"[red]{Mark.Fail} {Escape(message)}[/]");

    /// <summary>What to call a mixed selection. A <c>--tag</c> run can pull in both kinds, and
    /// "3 test sets" would be wrong when one of them is a flow.</summary>
    private static string Kinds(IReadOnlyList<TestRunResultDto> results)
    {
        if (results.All(r => r.Kind == "test")) return "test sets";
        if (results.All(r => r.Kind == "flow")) return "flows";
        return "targets";
    }

    /// <summary>Why an assertion failed, in the words the evaluator used where it had them.</summary>
    private static string Explain(AssertResultDto assertion)
        => assertion.Message
        ?? $"expected {assertion.Expected ?? "—"}, got {assertion.Actual ?? "nothing"}";

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    private static string Escape(string value) => Markup.Escape(value);

    private static string Plural(int count, string one, string many) => count == 1 ? one : many;

    /// <summary>Verdict glyphs, with an ASCII fallback for terminals (and CI log viewers) that
    /// can't render the shapes.</summary>
    private static class Mark
    {
        private static readonly bool Unicode = OperatingSystem.IsWindows()
            ? Console.OutputEncoding.CodePage is 65001
            : true;

        public static string Pass => Unicode ? "✔" : "+";
        public static string Fail => Unicode ? "✘" : "x";
        public static string Skip => Unicode ? "○" : "-";
    }
}
