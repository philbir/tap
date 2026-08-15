using Tap.Execution.Contracts;
using Tap.Workspace.Asserts;

namespace Tap.Execution.Asserts;

/// <summary>
/// Turns the evaluator's verdicts into the wire shape every front end reads.
///
/// <para>Only the result direction lives here. Mapping an assertion <em>spec</em> back and
/// forth is an editor concern — it belongs to whatever lets a human author one — and stays in
/// the Studio, which is the only thing that does.</para>
/// </summary>
public static class AssertResultMapper
{
    public static AssertResultDto ToDto(AssertResult result) => new(
        Index: result.Index,
        Name: result.Name,
        Ok: result.Ok,
        Skipped: result.Skipped,
        Actual: result.Actual,
        Expected: result.Expected,
        Message: result.Message);

    public static IReadOnlyList<AssertResultDto> ToDto(IReadOnlyList<AssertResult> results)
    {
        if (results.Count == 0) return [];
        var list = new List<AssertResultDto>(results.Count);
        foreach (var result in results) list.Add(ToDto(result));
        return list;
    }

    public static AssertSummaryDto ToDto(AssertSummary summary)
        => new(summary.Ok, summary.Passed, summary.Failed, summary.Skipped);
}
