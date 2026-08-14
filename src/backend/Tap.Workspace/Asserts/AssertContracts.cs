using Tap.Workspace.Model;

namespace Tap.Workspace.Asserts;

/// <summary>
/// Everything <see cref="AssertEvaluator"/> is allowed to look at. Deliberately a plain
/// value type rather than an <c>HttpResponseMessage</c>: the same snapshot can be rebuilt
/// from a stored response, which is what lets the Studio re-check edited assertions against
/// the last result without sending the request again — and what will let a future CLI
/// runner evaluate a recorded exchange.
/// </summary>
public sealed record ResponseSnapshot
{
    public required int Status { get; init; }

    /// <summary>Response headers. A repeated header appears once per value; the
    /// <see cref="AssertSource.Header"/> extractor reads the first match.</summary>
    public required IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; }

    /// <summary>Decoded response body, or null when the response had no body.</summary>
    public string? BodyText { get; init; }

    /// <summary>True when the body was cut off at the capture cap. Body-family assertions
    /// refuse to run against a partial body rather than matching a prefix and reporting a
    /// pass that a full response might not have produced.</summary>
    public bool BodyTruncated { get; init; }

    public required double DurationMs { get; init; }
}

/// <summary>
/// An <see cref="AssertSpec"/> whose selector and expected values have been through
/// variable expansion, ready to evaluate.
/// </summary>
/// <param name="Spec">The expanded assertion.</param>
/// <param name="ExpectedSecret">True when expansion pulled a value marked secret into the
/// expected side. The reported <see cref="AssertResult.Expected"/> is masked in that case,
/// so a results pane (or a CI log) never becomes a place secrets leak.</param>
/// <param name="RenderError">Set when this assertion couldn't be prepared at all — it didn't
/// validate, or expanding its expected value hit an unknown variable. The evaluator reports
/// it as a failure carrying this message. Half-finished assertions are the normal state
/// while someone is typing one, so a batch containing one still evaluates the rest.</param>
public sealed record ResolvedAssert(AssertSpec Spec, bool ExpectedSecret = false, string? RenderError = null);

/// <summary>Outcome of evaluating one assertion.</summary>
public sealed record AssertResult
{
    /// <summary>Position in the request's assertion list — the UI pairs results back to rows with this.</summary>
    public required int Index { get; init; }

    /// <summary>The assertion's label: its <c>name:</c> when set, otherwise
    /// <see cref="AssertSpec.Describe"/>.</summary>
    public required string Name { get; init; }

    public required bool Ok { get; init; }

    /// <summary>True when the assertion was declared <c>skip: true</c> or the protocol does
    /// not support assertions yet. Skipped assertions count as neither passed nor failed.</summary>
    public bool Skipped { get; init; }

    /// <summary>What was actually extracted, truncated for display. Null when nothing could be extracted.</summary>
    public string? Actual { get; init; }

    /// <summary>What was expected, masked when <see cref="ResolvedAssert.ExpectedSecret"/> was set.</summary>
    public string? Expected { get; init; }

    /// <summary>Why it failed, when "expected X, got Y" doesn't tell the whole story —
    /// a malformed selector, a body that isn't JSON, a multi-node match.</summary>
    public string? Message { get; init; }
}

/// <summary>Roll-up of a run's <see cref="AssertResult"/> list.</summary>
/// <param name="Ok">True when nothing failed. A request with no assertions is Ok.</param>
public sealed record AssertSummary(bool Ok, int Passed, int Failed, int Skipped)
{
    public static AssertSummary From(IReadOnlyList<AssertResult> results)
    {
        var passed = 0;
        var failed = 0;
        var skipped = 0;
        foreach (var r in results)
        {
            if (r.Skipped) skipped++;
            else if (r.Ok) passed++;
            else failed++;
        }
        return new AssertSummary(failed == 0, passed, failed, skipped);
    }
}
