using Tap.Workspace.Model;

namespace Tap.Workspace.Asserts;

/// <summary>One binding attempt: the variable, what it bound, or why it couldn't.</summary>
/// <param name="Var">The variable name the extraction declared.</param>
/// <param name="Value">The bound value, or null when nothing was bound.</param>
/// <param name="Error">Why nothing was bound, when that is a failure. Null both when the bind
/// succeeded and when the extraction was optional and simply found nothing.</param>
public sealed record ExtractedValue(string Var, string? Value, string? Error)
{
    public bool Ok => Error is null;

    /// <summary>True when a value actually entered the run bag. An optional extraction that
    /// found nothing is <see cref="Ok"/> but bound nothing.</summary>
    public bool Bound => Error is null && Value is not null;
}

/// <summary>
/// Turns a flow step's <c>extract:</c> declarations into values for the run's variable bag.
/// Pure and synchronous, over the same <see cref="ResponseReader"/> the assertion evaluator
/// uses — so <c>$.order.id</c> means one thing whether you assert on it or bind it.
///
/// <para>Unlike an assertion, a failed extraction is a real failure rather than an annotation:
/// the next step is about to send <c>{{orderId}}</c>, and saying so here beats reporting a
/// strange URL two steps later. <c>default:</c> and <c>required: false</c> are the two ways to
/// declare that a value is genuinely optional.</para>
/// </summary>
public static class ValueExtractor
{
    /// <summary>Cap on a reported bound value. A whole-body extraction is legitimate; echoing
    /// 2 MiB of it back into a run result is not.</summary>
    public const int ValuePreviewLimit = 4096;

    public static IReadOnlyList<ExtractedValue> Extract(
        IReadOnlyList<ExtractSpec> extractions, ResponseSnapshot response)
    {
        if (extractions.Count == 0) return [];

        var reader = new ResponseReader(response);
        var results = new List<ExtractedValue>(extractions.Count);
        foreach (var extraction in extractions) results.Add(ExtractOne(extraction, reader));
        return results;
    }

    private static ExtractedValue ExtractOne(ExtractSpec spec, ResponseReader reader)
    {
        var read = reader.Read(spec.Source, spec.Selector, spec.Group);

        if (read.Error is { } error)
        {
            // A body that isn't JSON or a selector that doesn't parse is a defect in the flow,
            // not a missing optional value — `default:` doesn't paper over it.
            return new ExtractedValue(spec.Var, null, error);
        }

        if (!read.Present)
        {
            if (spec.Default is { } fallback) return new ExtractedValue(spec.Var, fallback, null);
            if (!spec.Required) return new ExtractedValue(spec.Var, null, null);
            return new ExtractedValue(spec.Var, null,
                $"{Capitalize(read.Subject)} did not match anything in the response, so '{spec.Var}' has no value. " +
                "Add a 'default:' or set 'required: false' if that is expected.");
        }

        // Several nodes have no defined "the" value. Picking the first silently is how a flow
        // starts passing against the wrong order two months from now.
        if (read.Count > 1)
        {
            return new ExtractedValue(spec.Var, null,
                $"{Capitalize(read.Subject)} matched {read.Count} nodes — an extraction binds one value. " +
                "Narrow the expression (e.g. add an index).");
        }

        return new ExtractedValue(spec.Var, Preview(read.Text ?? string.Empty), null);
    }

    private static string Preview(string value)
        => value.Length <= ValuePreviewLimit
            ? value
            : value[..ValuePreviewLimit];

    private static string Capitalize(string subject)
        => subject.Length == 0 ? subject : char.ToUpperInvariant(subject[0]) + subject[1..];
}
