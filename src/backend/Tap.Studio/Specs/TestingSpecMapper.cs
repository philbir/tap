using Tap.Studio.Contracts;
using Tap.Workspace.Model;

namespace Tap.Studio.Specs;

/// <summary>
/// Converts between the wire DTOs for flows / test sets and their parsed models. The inbound
/// direction re-runs the same validation the YAML parser applies, so a step posted by the
/// editor is held to exactly the rules a hand-written file is — the editor can't smuggle in a
/// shape that would leave the workspace with a file it can no longer load.
/// </summary>
public static class TestingSpecMapper
{
    // ---- Flows ---------------------------------------------------------------------------

    public static IReadOnlyList<FlowStepSpecDto> ToDto(IReadOnlyList<FlowStep> steps)
    {
        if (steps.Count == 0) return [];
        var list = new List<FlowStepSpecDto>(steps.Count);
        foreach (var step in steps)
        {
            list.Add(new FlowStepSpecDto
            {
                Request = step.Request.SourceText,
                Name = step.Name,
                Vars = step.Vars.Count == 0 ? null : step.Vars,
                Extract = ToDto(step.Extract),
                Assertions = AssertSpecMapper.ToDto(step.Assertions),
                ContinueOnFailure = step.ContinueOnFailure,
                Skip = step.Skip,
            });
        }
        return list;
    }

    public static IReadOnlyList<FlowStep> ToModel(IReadOnlyList<FlowStepSpecDto>? dtos, string? relativePath)
    {
        if (dtos is null || dtos.Count == 0) return [];

        var list = new List<FlowStep>(dtos.Count);
        for (var i = 0; i < dtos.Count; i++)
        {
            var dto = dtos[i];
            var where = $"Step #{i + 1}: ";

            var request = ParseRef(dto.Request)
                ?? throw FlowInvalid($"{where}'request' is required.", relativePath);

            list.Add(new FlowStep
            {
                Request = request,
                Name = Trimmed(dto.Name),
                Vars = CleanVars(dto.Vars),
                Extract = ToModel(dto.Extract, where, relativePath),
                Assertions = AssertSpecMapper.ToModel(dto.Assertions, relativePath),
                ContinueOnFailure = dto.ContinueOnFailure,
                Skip = dto.Skip,
            });
        }
        return list;
    }

    public static IReadOnlyList<ExtractSpecDto> ToDto(IReadOnlyList<ExtractSpec> extractions)
    {
        if (extractions.Count == 0) return [];
        var list = new List<ExtractSpecDto>(extractions.Count);
        foreach (var extract in extractions)
        {
            list.Add(new ExtractSpecDto
            {
                Var = extract.Var,
                Source = extract.Source.ToWire(),
                Selector = extract.Selector,
                Group = extract.Group,
                Default = extract.Default,
                Required = extract.Required,
            });
        }
        return list;
    }

    private static IReadOnlyList<ExtractSpec> ToModel(
        IReadOnlyList<ExtractSpecDto>? dtos, string where, string? relativePath)
    {
        if (dtos is null || dtos.Count == 0) return [];

        var list = new List<ExtractSpec>(dtos.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < dtos.Count; i++)
        {
            var dto = dtos[i];
            if (ExtractWire.ParseSource(dto.Source) is not { } source)
                throw FlowInvalid($"{where}extraction #{i + 1}: '{dto.Source}' is not a known source.", relativePath);

            var spec = new ExtractSpec
            {
                Var = (dto.Var ?? string.Empty).Trim(),
                Source = source,
                Selector = source.TakesSelector() ? Trimmed(dto.Selector) : null,
                Group = source == ExtractSource.Regex ? dto.Group : null,
                Default = dto.Default,
                Required = dto.Required,
            };

            if (ExtractSpec.Validate(spec) is { } error)
                throw FlowInvalid($"{where}extraction #{i + 1}: {error}", relativePath);

            if (!seen.Add(spec.Var))
            {
                throw FlowInvalid(
                    $"{where}extraction #{i + 1} binds '{spec.Var}', which an earlier extraction in the same step already binds.",
                    relativePath);
            }

            list.Add(spec);
        }
        return list;
    }

    // ---- Test sets -----------------------------------------------------------------------

    public static IReadOnlyList<TestEntrySpecDto> ToDto(IReadOnlyList<TestEntry> tests)
    {
        if (tests.Count == 0) return [];
        var list = new List<TestEntrySpecDto>(tests.Count);
        foreach (var test in tests)
        {
            list.Add(new TestEntrySpecDto
            {
                Name = test.Name,
                Request = test.Request?.SourceText,
                Flow = test.Flow?.SourceText,
                Vars = test.Vars.Count == 0 ? null : test.Vars,
                Assertions = AssertSpecMapper.ToDto(test.Assertions),
                Skip = test.Skip,
            });
        }
        return list;
    }

    public static IReadOnlyList<TestEntry> ToModel(IReadOnlyList<TestEntrySpecDto>? dtos, string? relativePath)
    {
        if (dtos is null || dtos.Count == 0) return [];

        var list = new List<TestEntry>(dtos.Count);
        for (var i = 0; i < dtos.Count; i++)
        {
            var dto = dtos[i];
            var where = $"Test #{i + 1}: ";

            var request = ParseRef(dto.Request);
            var flow = ParseRef(dto.Flow);

            if (request is not null && flow is not null)
                throw TestInvalid($"{where}names both a request and a flow. A test runs one or the other.", relativePath);
            if (request is null && flow is null)
                throw TestInvalid($"{where}names neither a request nor a flow.", relativePath);

            list.Add(new TestEntry
            {
                Name = Trimmed(dto.Name),
                Request = request,
                Flow = flow,
                Vars = CleanVars(dto.Vars),
                Assertions = AssertSpecMapper.ToModel(dto.Assertions, relativePath),
                Skip = dto.Skip,
            });
        }
        return list;
    }

    public static TestFailureMode ParseOnFailure(string? value, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(value)) return TestFailureMode.Continue;
        return TestFailureModeWire.Parse(value)
            ?? throw TestInvalid($"'onFailure: {value}' is not recognized. Expected 'continue' or 'stop'.", relativePath);
    }

    // ---- Shared --------------------------------------------------------------------------

    /// <summary>Mirrors <c>YamlExt.Ref</c> so a ref typed into the editor and one typed into a
    /// file normalize identically.</summary>
    private static WorkspaceRef? ParseRef(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        return trimmed.StartsWith("id:", StringComparison.OrdinalIgnoreCase)
            ? WorkspaceRef.FromId(trimmed[3..])
            : WorkspaceRef.FromPath(trimmed);
    }

    /// <summary>Drops blank-named overrides. The editor keeps an empty row around while the
    /// user is typing into it; that row is not a variable and shouldn't reach the file.</summary>
    private static IReadOnlyDictionary<string, string> CleanVars(IReadOnlyDictionary<string, string>? vars)
    {
        if (vars is null || vars.Count == 0) return new Dictionary<string, string>();
        var clean = new Dictionary<string, string>(vars.Count, StringComparer.Ordinal);
        foreach (var (k, v) in vars)
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            clean[k.Trim()] = v ?? string.Empty;
        }
        return clean;
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static WorkspaceParseException FlowInvalid(string message, string? relativePath)
        => new(new WorkspaceError(WorkspaceErrorCode.E_FLOW_INVALID, message, relativePath));

    private static WorkspaceParseException TestInvalid(string message, string? relativePath)
        => new(new WorkspaceError(WorkspaceErrorCode.E_TEST_INVALID, message, relativePath));
}
