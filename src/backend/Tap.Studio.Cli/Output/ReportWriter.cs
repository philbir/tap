using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using Tap.Execution.Contracts;

namespace Tap.Studio.Cli.Output;

/// <summary>
/// Writes a run to a file CI can ingest.
///
/// <para>JUnit XML first, because GitHub Actions, GitLab, Azure DevOps, and Jenkins all read it
/// without a plugin — it is the one format that makes a failed assertion show up as a failed
/// test in a UI rather than a line in a log. TRX for Azure DevOps' native reporting, raw JSON
/// for anyone scripting against the engine's own shape, and Markdown for the places a human
/// reads rather than a parser: a GitHub job summary, a PR comment, an artifact someone
/// opens.</para>
/// </summary>
public static class ReportWriter
{
    public static bool TryWrite(
        string format, string? path, TestRunResultDto result, out string writtenTo, out string error)
        => TryWrite(format, path, [result], out writtenTo, out error);

    /// <param name="runs">One entry per target. <c>--tag</c> can select several, and JUnit's
    /// shape already accounts for that — many <c>&lt;testsuite&gt;</c> inside one
    /// <c>&lt;testsuites&gt;</c> is exactly what the format is for.</param>
    public static bool TryWrite(
        string format, string? path, IReadOnlyList<TestRunResultDto> runs, out string writtenTo, out string error)
    {
        writtenTo = string.Empty;
        error = string.Empty;

        var normalized = format.Trim().ToLowerInvariant();
        // `md` is accepted alongside `markdown` because both are what people type.
        if (normalized == "md") normalized = "markdown";

        var extension = normalized switch
        {
            "junit" => ".xml",
            "trx" => ".trx",
            "json" => ".json",
            "markdown" => ".md",
            _ => null,
        };
        if (extension is null)
        {
            error = $"--output '{format}' is not a known format. Use junit, trx, json, or markdown.";
            return false;
        }

        writtenTo = Path.GetFullPath(path ?? $"test-results{extension}");
        try
        {
            var directory = Path.GetDirectoryName(writtenTo);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var content = normalized switch
            {
                "junit" => JUnit(runs),
                "trx" => Trx(runs),
                "markdown" => Markdown(runs),
                _ => Json(runs),
            };
            File.WriteAllText(writtenTo, content);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Could not write '{writtenTo}': {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// One <c>&lt;testsuite&gt;</c> for the run, one <c>&lt;testcase&gt;</c> per test.
    ///
    /// <para>A flow's steps are folded into their test's failure text rather than becoming
    /// testcases of their own. JUnit has no nesting a CI viewer renders usefully, and flattening
    /// steps into siblings would report a five-step flow as five tests — inflating counts and
    /// making a single logical failure look like several.</para>
    /// </summary>
    private static string JUnit(IReadOnlyList<TestRunResultDto> runs)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new System.Text.UTF8Encoding(false) };
        var buffer = new Utf8StringWriter();
        using (var writer = XmlWriter.Create(buffer, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("testsuites");
            writer.WriteAttributeString("name", Title(runs));
            writer.WriteAttributeString("tests", Total(runs, r => r.Passed + r.Failed + r.Skipped));
            writer.WriteAttributeString("failures", Total(runs, r => r.Failed));
            writer.WriteAttributeString("skipped", Total(runs, r => r.Skipped));
            writer.WriteAttributeString("time", Seconds(runs.Sum(r => r.DurationMs)));

            foreach (var result in runs)
            {
            writer.WriteStartElement("testsuite");
            writer.WriteAttributeString("name", result.Name);
            writer.WriteAttributeString("tests", Count(result));
            writer.WriteAttributeString("failures", result.Failed.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("skipped", result.Skipped.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("time", Seconds(result.DurationMs));
            writer.WriteAttributeString("file", result.Path);

            foreach (var entry in result.Entries)
            {
                writer.WriteStartElement("testcase");
                writer.WriteAttributeString("name", entry.Name);
                writer.WriteAttributeString("classname", result.Name);
                writer.WriteAttributeString("time", Seconds(entry.DurationMs));

                if (entry.Skipped)
                {
                    writer.WriteStartElement("skipped");
                    writer.WriteAttributeString("message", entry.Error ?? "skipped");
                    writer.WriteEndElement();
                }
                else if (!entry.Ok)
                {
                    writer.WriteStartElement("failure");
                    writer.WriteAttributeString("message", Flatten(entry.Error ?? "failed"));
                    writer.WriteAttributeString("type", "AssertionFailure");
                    writer.WriteCData(Detail(entry));
                    writer.WriteEndElement();
                }

                // The request lines make a passing case auditable too — which endpoint did this
                // actually hit, and how long did it take.
                var output = Trace(entry);
                if (output.Length > 0)
                {
                    writer.WriteStartElement("system-out");
                    writer.WriteCData(output);
                    writer.WriteEndElement();
                }

                writer.WriteEndElement(); // testcase
            }

            writer.WriteEndElement(); // testsuite
            }

            writer.WriteEndElement(); // testsuites
            writer.WriteEndDocument();
        }
        return buffer.ToString();
    }

    /// <summary>Visual Studio's TRX. Minimal but valid: Azure DevOps needs the results, the
    /// definitions, and the entries to line up by test id. Several runs merge into one
    /// TestRun — TRX has no notion of sibling runs, and a consumer wants one document.</summary>
    private static string Trx(IReadOnlyList<TestRunResultDto> runs)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new System.Text.UTF8Encoding(false) };
        var buffer = new Utf8StringWriter();

        // Deterministic ids from the entry's identity: re-running the same set produces the
        // same ids, so a CI system can track one test across runs.
        var cases = runs
            .SelectMany(run => run.Entries.Select(entry => (Run: run, Entry: entry)))
            .Select(c => (c.Run, c.Entry, Id: Deterministic($"{c.Run.Path}#{c.Entry.Index}#{c.Entry.Name}")))
            .ToArray();

        var duration = runs.Sum(r => r.DurationMs);
        var finished = DateTimeOffset.UtcNow;
        var started = finished.AddMilliseconds(-duration);

        using (var writer = XmlWriter.Create(buffer, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("TestRun", "http://microsoft.com/schemas/VisualStudio/TeamTest/2010");
            writer.WriteAttributeString("id", Deterministic(string.Join('|', runs.Select(r => r.Path))).ToString());
            writer.WriteAttributeString("name", Title(runs));

            writer.WriteStartElement("Times");
            writer.WriteAttributeString("creation", started.ToString("O"));
            writer.WriteAttributeString("start", started.ToString("O"));
            writer.WriteAttributeString("finish", finished.ToString("O"));
            writer.WriteEndElement();

            writer.WriteStartElement("Results");
            foreach (var (run, entry, id) in cases)
            {
                writer.WriteStartElement("UnitTestResult");
                writer.WriteAttributeString("testId", id.ToString());
                writer.WriteAttributeString("testName", entry.Name);
                writer.WriteAttributeString("duration", TimeSpan.FromMilliseconds(entry.DurationMs).ToString("c"));
                writer.WriteAttributeString("outcome", entry.Skipped ? "NotExecuted" : entry.Ok ? "Passed" : "Failed");
                writer.WriteAttributeString("testType", "13cdc9d9-ddb5-4fa4-a97d-d965ccfc6d4b");
                writer.WriteAttributeString("testListId", "8c84fa94-04c1-424b-9868-57a2d4851a1d");

                if (!entry.Ok && !entry.Skipped)
                {
                    writer.WriteStartElement("Output");
                    writer.WriteStartElement("ErrorInfo");
                    writer.WriteElementString("Message", Flatten(entry.Error ?? "failed"));
                    writer.WriteElementString("StackTrace", Detail(entry));
                    writer.WriteEndElement();
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
            writer.WriteEndElement(); // Results

            writer.WriteStartElement("TestDefinitions");
            foreach (var (run, entry, id) in cases)
            {
                writer.WriteStartElement("UnitTest");
                writer.WriteAttributeString("id", id.ToString());
                writer.WriteAttributeString("name", entry.Name);
                writer.WriteStartElement("TestMethod");
                writer.WriteAttributeString("codeBase", run.Path);
                writer.WriteAttributeString("className", run.Name);
                writer.WriteAttributeString("name", entry.Name);
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
            writer.WriteEndElement(); // TestDefinitions

            writer.WriteStartElement("ResultSummary");
            writer.WriteAttributeString("outcome", runs.All(r => r.Ok) ? "Completed" : "Failed");
            writer.WriteStartElement("Counters");
            writer.WriteAttributeString("total", Total(runs, r => r.Passed + r.Failed + r.Skipped));
            writer.WriteAttributeString("executed", Total(runs, r => r.Passed + r.Failed));
            writer.WriteAttributeString("passed", Total(runs, r => r.Passed));
            writer.WriteAttributeString("failed", Total(runs, r => r.Failed));
            writer.WriteEndElement();
            writer.WriteEndElement();

            writer.WriteEndElement(); // TestRun
            writer.WriteEndDocument();
        }
        return buffer.ToString();
    }

    /// <summary>
    /// An envelope carrying the engine's own result shape per target, in the same camelCase the
    /// Studio's API emits — so a script that reads this and a script that reads the SSE stream
    /// see one set of field names.
    ///
    /// <para>Always an envelope, even for one target. A script shouldn't have to branch on
    /// whether the caller happened to pass <c>--tag</c>. Public because <c>test --json</c>
    /// prints this same document to stdout; keep it on the default encoder so
    /// <see cref="Tap.Workspace.Rendering.SecretRedactor"/>'s JSON-escaped variants match.</para>
    /// </summary>
    public static string Json(IReadOnlyList<TestRunResultDto> runs)
        => JsonSerializer.Serialize(
            new JsonReport(
                Ok: runs.All(r => r.Ok),
                Passed: runs.Sum(r => r.Passed),
                Failed: runs.Sum(r => r.Failed),
                Skipped: runs.Sum(r => r.Skipped),
                DurationMs: runs.Sum(r => r.DurationMs),
                Runs: runs),
            new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });

    private sealed record JsonReport(
        bool Ok, int Passed, int Failed, int Skipped, double DurationMs,
        IReadOnlyList<TestRunResultDto> Runs);

    /// <summary>Everything a reader needs to act on a failure, without opening the workspace:
    /// which step, which request, and which assertion said what.</summary>
    private static string Detail(TestEntryResultDto entry)
    {
        var lines = new List<string>();
        if (entry.Error is { } error) lines.Add(error);

        foreach (var step in entry.Steps)
        {
            if (step.Skipped)
            {
                lines.Add($"- {step.Name}: {step.Error ?? "skipped"}");
                continue;
            }

            lines.Add($"- {step.Name}: {step.Method} {step.Url} -> {step.Status} ({step.DurationMs:F0}ms)");
            foreach (var assertion in step.Assertions.Where(a => !a.Ok && !a.Skipped))
            {
                var why = assertion.Message ?? $"expected {assertion.Expected ?? "—"}, got {assertion.Actual ?? "nothing"}";
                lines.Add($"    assertion failed: {assertion.Name} — {why}");
            }
            foreach (var bound in step.Extracted.Where(b => b.Error is not null))
                lines.Add($"    extraction failed: {bound.Error}");
            if (step.Error is { } stepError) lines.Add($"    {stepError}");
        }
        return string.Join('\n', lines);
    }

    private static string Trace(TestEntryResultDto entry)
        => string.Join('\n', entry.Steps
            .Where(s => !s.Skipped && s.Method != "—")
            .Select(s => $"{s.Method} {s.Url} -> {s.Status} ({s.DurationMs:F0}ms)"));

    /// <summary>
    /// A report for people rather than parsers — a GitHub job summary, a PR comment, an
    /// artifact someone opens.
    ///
    /// <para>Failures come first, in full, before the per-target tables. Someone opening a
    /// summary because a build went red wants the reason on the screen they land on, not
    /// behind a scroll; and when everything passed there is no failure section at all, so the
    /// document stays short exactly when there is nothing to say.</para>
    ///
    /// <para>Passing targets collapse into a <c>&lt;details&gt;</c>; failing ones stay open.
    /// GitHub needs a blank line after <c>&lt;summary&gt;</c> before it will render Markdown
    /// inside, which is why the spacing below looks deliberate — it is.</para>
    /// </summary>
    private static string Markdown(IReadOnlyList<TestRunResultDto> runs)
    {
        var passed = runs.Sum(r => r.Passed);
        var failed = runs.Sum(r => r.Failed);
        var skipped = runs.Sum(r => r.Skipped);
        var ok = runs.All(r => r.Ok);

        var md = new StringBuilder();
        md.Append("# ").Append(Text(Title(runs))).Append("\n\n");

        var totals = new List<string>();
        if (passed > 0) totals.Add($"**{passed} passed**");
        if (failed > 0) totals.Add($"**{failed} failed**");
        if (skipped > 0) totals.Add($"{skipped} skipped");
        if (totals.Count == 0) totals.Add("**nothing ran**");

        md.Append(ok ? "✅ " : "❌ ")
          .Append(string.Join(" · ", totals))
          .Append(" — ").Append(Duration(runs.Sum(r => r.DurationMs)))
          .Append("\n");

        // A run that couldn't start at all has no tables worth printing.
        if (runs.FirstOrDefault(r => r.Error is not null)?.Error is { } fatal)
        {
            md.Append("\n> ").Append(Text(Flatten(fatal))).Append("\n");
            return md.ToString();
        }

        if (failed > 0)
        {
            md.Append("\n## Failures\n");
            foreach (var run in runs)
            {
                foreach (var entry in run.Entries.Where(e => !e.Ok && !e.Skipped))
                {
                    AppendFailure(md, run, entry);
                }
            }
        }

        foreach (var run in runs)
        {
            AppendTarget(md, run, single: runs.Count == 1);
        }

        return md.ToString();
    }

    private static void AppendFailure(StringBuilder md, TestRunResultDto run, TestEntryResultDto entry)
    {
        md.Append("\n### ").Append(Text(entry.Name)).Append("\n\n");
        md.Append("In ").Append(Code(run.Path)).Append("\n");

        foreach (var step in entry.Steps.Where(s => !s.Ok && !s.Skipped))
        {
            md.Append('\n');
            if (step.Method != "—")
            {
                md.Append(Code($"{step.Method} {step.Url}")).Append(" → ")
                  .Append(Code(step.Status > 0 ? step.Status.ToString(CultureInfo.InvariantCulture) : "—"))
                  .Append(' ').Append(Duration(step.DurationMs)).Append("\n\n");
            }

            foreach (var assertion in step.Assertions.Where(a => !a.Ok && !a.Skipped))
            {
                var why = assertion.Message
                    ?? $"expected {assertion.Expected ?? "—"}, got {assertion.Actual ?? "nothing"}";
                md.Append("- ❌ ").Append(Text(assertion.Name)).Append(" — ").Append(Text(Flatten(why))).Append("\n");
            }
            foreach (var bound in step.Extracted.Where(b => b.Error is not null))
                md.Append("- ❌ ").Append(Text(Flatten(bound.Error!))).Append("\n");
            if (step.Error is { } stepError)
                md.Append("- ❌ ").Append(Text(Flatten(stepError))).Append("\n");
        }

        // A failure with no failing step — the entry itself couldn't start.
        if (!entry.Steps.Any(s => !s.Ok && !s.Skipped) && entry.Error is { } entryError)
            md.Append("\n- ❌ ").Append(Text(Flatten(entryError))).Append("\n");
    }

    private static void AppendTarget(StringBuilder md, TestRunResultDto run, bool single)
    {
        var collapse = run.Ok;
        md.Append('\n');

        if (single)
        {
            // The document is already titled after this target, so no second identical
            // heading — but when the failure section sits above, the table still needs
            // something to separate it from the last bullet.
            if (collapse) md.Append("<details>\n<summary>").Append(Text(Counts(run))).Append("</summary>\n\n");
            else md.Append("## Tests\n\n");
        }
        else if (collapse)
        {
            md.Append("<details>\n<summary>")
              .Append(run.Ok ? "✅ " : "❌ ").Append(Text(run.Name))
              .Append(" — ").Append(Text(Counts(run)))
              .Append("</summary>\n\n");
        }
        else
        {
            md.Append("## ").Append(run.Ok ? "✅ " : "❌ ").Append(Text(run.Name)).Append("\n\n");
        }

        md.Append("| | Test | Assertions | Time |\n|---|---|--:|--:|\n");
        foreach (var entry in run.Entries)
        {
            var mark = entry.Skipped ? "⚪" : entry.Ok ? "✅" : "❌";
            md.Append("| ").Append(mark)
              .Append(" | ").Append(Cell(entry.Name))
              .Append(" | ").Append(Assertions(entry))
              .Append(" | ").Append(entry.Skipped ? "—" : Duration(entry.DurationMs))
              .Append(" |\n");
        }

        if (run.Entries.Count == 0) md.Append("| ⚪ | _no tests_ | — | — |\n");

        md.Append('\n').Append(Code(run.Path)).Append('\n');
        if (collapse) md.Append("\n</details>\n");
    }

    /// <summary>Assertion tally across an entry's steps — what a reader actually wants from
    /// that column, rather than a step count.</summary>
    private static string Assertions(TestEntryResultDto entry)
    {
        var passed = 0;
        var total = 0;
        foreach (var step in entry.Steps)
        {
            if (step.AssertSummary is not { } summary) continue;
            passed += summary.Passed;
            total += summary.Passed + summary.Failed;
        }
        return total == 0 ? "—" : $"{passed}/{total}";
    }

    private static string Counts(TestRunResultDto run)
    {
        var parts = new List<string>();
        if (run.Passed > 0) parts.Add($"{run.Passed} passed");
        if (run.Failed > 0) parts.Add($"{run.Failed} failed");
        if (run.Skipped > 0) parts.Add($"{run.Skipped} skipped");
        return parts.Count == 0 ? "nothing ran" : string.Join(", ", parts);
    }

    private static string Duration(double milliseconds)
        => milliseconds >= 1000
            ? $"{milliseconds / 1000d:F2} s"
            : $"{milliseconds:F0} ms";

    /// <summary>Neutralises the characters that would otherwise be read as Markdown. Names and
    /// assertion messages come from workspace files and from upstream responses — a backtick or
    /// an underscore in either must not restyle half the report.</summary>
    private static string Text(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '|' or '#') escaped.Append('\\');
            escaped.Append(ch);
        }
        return escaped.ToString();
    }

    /// <summary>A table cell additionally can't contain a newline — the row would end early.</summary>
    private static string Cell(string value) => Text(Flatten(value));

    /// <summary>
    /// Wraps a value in a code span. Content inside one is literal, so running it through
    /// <see cref="Text"/> would print the backslashes — a path like <c>my_api/get.req.md</c>
    /// would come out as <c>my\_api/…</c>. The only thing that needs handling is a backtick in
    /// the content, which is fenced by a longer run per CommonMark.
    /// </summary>
    private static string Code(string value)
    {
        var flat = Flatten(value);

        var longest = 0;
        var current = 0;
        foreach (var ch in flat)
        {
            if (ch == '`') longest = Math.Max(longest, ++current);
            else current = 0;
        }

        var fence = new string('`', longest + 1);
        // A span starting or ending with a backtick needs padding spaces, which the renderer
        // then strips.
        var pad = flat.StartsWith('`') || flat.EndsWith('`') ? " " : string.Empty;
        return fence + pad + flat + pad + fence;
    }

    /// <summary>A name for the whole report: the single run's, or a count when several.</summary>
    private static string Title(IReadOnlyList<TestRunResultDto> runs)
    {
        if (runs.Count == 1) return runs[0].Name;
        if (runs.All(r => r.Kind == "test")) return $"{runs.Count} test sets";
        if (runs.All(r => r.Kind == "flow")) return $"{runs.Count} flows";
        return $"{runs.Count} targets";
    }

    private static string Total(IReadOnlyList<TestRunResultDto> runs, Func<TestRunResultDto, int> select)
        => runs.Sum(select).ToString(CultureInfo.InvariantCulture);

    private static string Count(TestRunResultDto result)
        => (result.Passed + result.Failed + result.Skipped).ToString(CultureInfo.InvariantCulture);

    private static string Seconds(double milliseconds)
        => (milliseconds / 1000d).ToString("F3", CultureInfo.InvariantCulture);

    private static string Flatten(string value)
        => value.Replace('\n', ' ').Replace('\r', ' ').Trim();

    /// <summary>A <see cref="StringWriter"/> that admits to UTF-8. <see cref="XmlWriter"/>
    /// writes the declaration from its underlying writer's encoding; the default StringWriter
    /// claims UTF-16, which would put <c>encoding="utf-16"</c> at the top of a file we then
    /// write as UTF-8 — and strict parsers reject exactly that.</summary>
    private sealed class Utf8StringWriter() : StringWriter(CultureInfo.InvariantCulture)
    {
        public override System.Text.Encoding Encoding => new System.Text.UTF8Encoding(false);
    }

    /// <summary>A stable GUID from a string, so ids survive across runs of the same set.</summary>
    private static Guid Deterministic(string value)
    {
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(hash);
    }
}
