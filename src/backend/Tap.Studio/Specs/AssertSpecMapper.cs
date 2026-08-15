using Tap.Studio.Contracts;
using Tap.Workspace.Asserts;
using Tap.Workspace.Model;
using Tap.Execution.Contracts;

namespace Tap.Studio.Specs;

/// <summary>
/// Converts between the wire <see cref="AssertSpecDto"/> and the parsed
/// <see cref="AssertSpec"/>. The inbound direction re-runs
/// <see cref="AssertSpec.Validate"/>, so an assertion posted by a client is held to the
/// same rules as one typed into a file — the editor can't smuggle in a combination the
/// parser would reject and leave the workspace with a file that no longer loads.
/// </summary>
public static class AssertSpecMapper
{
    public static IReadOnlyList<AssertSpecDto> ToDto(IReadOnlyList<AssertSpec> specs)
    {
        if (specs.Count == 0) return [];
        var list = new List<AssertSpecDto>(specs.Count);
        foreach (var spec in specs)
        {
            list.Add(new AssertSpecDto
            {
                Name = spec.Name,
                Source = spec.Source.ToWire(),
                Selector = spec.Selector,
                Op = spec.Op.ToWire(),
                Expected = spec.Expected,
                ExpectedList = spec.ExpectedList,
                IgnoreCase = spec.IgnoreCase,
                Skip = spec.Skip,
            });
        }
        return list;
    }

    public static IReadOnlyList<AssertSpec> ToModel(IReadOnlyList<AssertSpecDto>? dtos, string? relativePath = null)
    {
        if (dtos is null || dtos.Count == 0) return [];

        var list = new List<AssertSpec>(dtos.Count);
        for (var i = 0; i < dtos.Count; i++)
        {
            var (spec, error) = Convert(dtos[i], i + 1);
            if (error is not null) throw Invalid(error, relativePath);
            list.Add(spec!);
        }
        return list;
    }

    /// <summary>
    /// Converts each entry independently, reporting per-entry problems instead of failing the
    /// batch. Used by the interactive re-check endpoint: an assertion someone is halfway
    /// through typing shouldn't blank out the verdicts on the ones already finished.
    /// The save path stays strict — see <see cref="ToModel"/>.
    /// </summary>
    public static IReadOnlyList<(AssertSpec Spec, string? Error)> ToModelTolerant(IReadOnlyList<AssertSpecDto>? dtos)
    {
        if (dtos is null || dtos.Count == 0) return [];

        var list = new List<(AssertSpec, string?)>(dtos.Count);
        for (var i = 0; i < dtos.Count; i++)
        {
            var dto = dtos[i];
            var (spec, error) = Convert(dto, i + 1);
            list.Add((spec ?? Placeholder(dto), error));
        }
        return list;
    }

    /// <summary>A stand-in for an entry that didn't convert, carrying enough identity for the
    /// row to keep its label in the results list.</summary>
    private static AssertSpec Placeholder(AssertSpecDto dto) => new()
    {
        Name = Trimmed(dto.Name) ?? $"{dto.Source} {dto.Op}".Trim(),
        Source = AssertWire.ParseSource(dto.Source) ?? AssertSource.Status,
        Selector = Trimmed(dto.Selector),
        Op = AssertWire.ParseOp(dto.Op) ?? AssertOp.Equals,
        Expected = dto.Expected,
    };

    private static (AssertSpec? Spec, string? Error) Convert(AssertSpecDto dto, int ordinal)
    {
        if (AssertWire.ParseSource(dto.Source) is not { } source)
            return (null, $"Assertion #{ordinal}: '{dto.Source}' is not a known extractor.");
        if (AssertWire.ParseOp(dto.Op) is not { } op)
            return (null, $"Assertion #{ordinal}: '{dto.Op}' is not a known matcher.");

        var selector = source.TakesSelector() ? Trimmed(dto.Selector) : null;
        if (source.TakesSelector() && string.IsNullOrEmpty(selector))
        {
            var what = source == AssertSource.Header ? "a header name" : "an expression";
            return (null, $"Assertion #{ordinal}: '{source.ToWire()}' needs {what}.");
        }

        var spec = new AssertSpec
        {
            Name = Trimmed(dto.Name),
            Source = source,
            Selector = selector,
            Op = op,
            Expected = op.TakesList() ? null : dto.Expected,
            ExpectedList = op.TakesList() ? dto.ExpectedList : null,
            IgnoreCase = dto.IgnoreCase,
            Skip = dto.Skip,
        };

        return AssertSpec.Validate(spec) is { } error
            ? (null, $"Assertion #{ordinal}: {error}")
            : (spec, null);
    }

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

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static WorkspaceParseException Invalid(string message, string? relativePath)
        => new(new WorkspaceError(WorkspaceErrorCode.E_ASSERT_INVALID, message, relativePath));
}
