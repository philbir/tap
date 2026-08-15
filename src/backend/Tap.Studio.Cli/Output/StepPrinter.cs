using Spectre.Console;
using Tap.Execution.Contracts;

namespace Tap.Studio.Cli.Output;

/// <summary>Human rendering of a single step result — shared by <c>send</c> and <c>call</c>
/// so an ad-hoc request and a saved one report identically.</summary>
public static class StepPrinter
{
    public static void Print(IAnsiConsole console, TestStepResultDto step, bool showBody)
    {
        var status = step.Status > 0 ? step.Status.ToString() : "—";
        var colour = step.Status is >= 200 and < 300 ? "green" : step.Status >= 400 ? "red" : "yellow";
        console.MarkupLine($"[dim]{Markup.Escape(step.Method)}[/] {Markup.Escape(step.Url)}");
        console.MarkupLine($"[{colour}]{status}[/] {Markup.Escape(step.StatusText ?? string.Empty)} [dim]{step.DurationMs:F0}ms · {step.ResponseBodyBytes:N0} bytes[/]");

        if (step.Error is { } error)
        {
            console.MarkupLine($"[red]{Markup.Escape(error)}[/]");
            return;
        }

        foreach (var assertion in step.Assertions)
        {
            if (assertion.Skipped)
            {
                console.MarkupLine($"  [dim]○ {Markup.Escape(assertion.Name)}[/]");
                continue;
            }
            if (assertion.Ok)
            {
                console.MarkupLine($"  [green]✔[/] {Markup.Escape(assertion.Name)}");
                continue;
            }
            var why = assertion.Message ?? $"expected {assertion.Expected ?? "—"}, got {assertion.Actual ?? "nothing"}";
            console.MarkupLine($"  [red]✘[/] {Markup.Escape(assertion.Name)}");
            console.MarkupLine($"    [dim]{Markup.Escape(why)}[/]");
        }

        if (showBody && step.ResponseBody is { Length: > 0 } body)
        {
            console.WriteLine();
            console.WriteLine(body);
        }
    }
}
