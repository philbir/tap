using Tap.Studio.Contracts;
using Tap.Studio.Specs;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;

namespace Tap.Studio.OpenApi;

/// <summary>
/// Rewrites a tracked request from a newer version of its operation, keeping everything the
/// importer does not own.
///
/// <para><b>The rule that makes this safe:</b> we never regenerate a request from scratch and
/// write it over the old one. We read what is on disk, project it to its spec, and overwrite only
/// the handful of fields the importer authored. Assertions, variables, auth, transport, protocol
/// and the file's <c>id</c> are preserved <i>structurally</i> — not by remembering to copy them,
/// but because nothing in this file ever assigns them.</para>
///
/// <para>Two fields are treated more cautiously when the user has edited the file:
/// <c>name</c> and the markdown body. Those are what a human customises for readability, and
/// losing a hand-written description to a one-word upstream summary change is a bad trade. When
/// the file is untouched they are refreshed like everything else.</para>
/// </summary>
public static class OpenApiResyncMerger
{
    /// <summary>Merges into an existing <c>.req.tap</c>, returning its new content.</summary>
    public static string MergeRequest(
        RequestFile existing,
        MappedOperation operation,
        OpenApiImportPlanner.Options options,
        bool preserveProse)
    {
        var current = RequestSpecProjection.ToSpec(existing);
        var generated = OpenApiImportPlanner.BuildRequestSpec(operation, existing.RelativePath, options);

        var merged = current with
        {
            // Owned: these describe the call itself, which is precisely what the document defines.
            Url = generated.Url,
            RequestBody = generated.RequestBody,
            Headers = MergeHeaders(current.Headers, generated.Headers),
            Vars = MergeVars(current.Vars, generated.Vars),
            Tags = MergeTags(current.Tags, generated.Tags),

            // Cautious when the user has been in here; refreshed when they haven't.
            Name = preserveProse ? current.Name : generated.Name,
            Body = preserveProse ? current.Body : generated.Body,

            // Everything else — Assertions, Auth, Transport, Protocol, Id — falls through from
            // `current` untouched. That is the whole point of merging onto the existing spec.
        };

        return RequestSpecEmitter.ToFileSource(merged);
    }

    /// <summary>
    /// Headers merge by name and are never removed. A header the importer writes
    /// (<c>Content-Type</c>, <c>Accept</c>, declared header parameters) is refreshed; anything else
    /// is the user's and survives.
    ///
    /// <para>Not removing a header the document dropped is deliberate: the lock does not record
    /// which headers we authored, and deleting one the user depends on is unrecoverable, while
    /// leaving a stale one is visible and trivially fixed.</para>
    /// </summary>
    private static IReadOnlyList<HttpHeaderSpecDto> MergeHeaders(
        IReadOnlyList<HttpHeaderSpecDto>? current, IReadOnlyList<HttpHeaderSpecDto>? generated)
    {
        var merged = new List<HttpHeaderSpecDto>(current ?? []);

        foreach (var header in generated ?? [])
        {
            var index = merged.FindIndex(h => string.Equals(h.Name, header.Name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) merged[index] = header;
            else merged.Add(header);
        }

        return merged;
    }

    /// <summary>
    /// Variables are unioned: a parameter added upstream shows up, and an existing entry keeps
    /// whatever value the user filled in. Nothing is removed — a variable may be referenced from
    /// somewhere this code cannot see.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? MergeVars(
        IReadOnlyDictionary<string, string>? current, IReadOnlyDictionary<string, string>? generated)
    {
        if (generated is not { Count: > 0 }) return current;

        var merged = new Dictionary<string, string>(current ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var (name, value) in generated)
        {
            if (!merged.ContainsKey(name)) merged[name] = value;
        }
        return merged.Count > 0 ? merged : null;
    }

    private static IReadOnlyList<string>? MergeTags(IReadOnlyList<string>? current, IReadOnlyList<string>? generated)
    {
        var merged = new List<string>(current ?? []);
        foreach (var tag in generated ?? [])
        {
            if (!merged.Contains(tag, StringComparer.OrdinalIgnoreCase)) merged.Add(tag);
        }
        return merged.Count > 0 ? merged : null;
    }
}

/// <summary>
/// Edits a <c>.http</c> file one <c>###</c> section at a time.
///
/// <para>Sections are located by the <c># tap-openapi &lt;opKey&gt;</c> marker the emitter writes,
/// so a section survives being renamed, reordered, or edited. Untouched sections are copied
/// byte-for-byte: re-syncing three operations in a twenty-request file must produce a three-section
/// diff, not a whole-file rewrite.</para>
/// </summary>
public static class HttpFileSurgeon
{
    /// <summary>Splits a file into its leading header and its <c>###</c> sections.</summary>
    public static (string Header, IReadOnlyList<(string? OpKey, string Text)> Sections) Split(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var header = new List<string>();
        var sections = new List<(string?, string)>();
        List<string>? current = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("###", StringComparison.Ordinal))
            {
                if (current is not null) sections.Add((MarkerOf(current), string.Join('\n', current)));
                current = [line];
                continue;
            }
            (current ?? header).Add(line);
        }
        if (current is not null) sections.Add((MarkerOf(current), string.Join('\n', current)));

        return (string.Join('\n', header), sections);
    }

    /// <summary>Replaces the section carrying <paramref name="opKey"/>, or appends when absent.</summary>
    public static string ReplaceSection(string content, string opKey, string replacement)
    {
        var (header, sections) = Split(content);
        var rebuilt = new List<string>();
        var replaced = false;

        foreach (var (key, text) in sections)
        {
            if (!replaced && string.Equals(key, opKey, StringComparison.Ordinal))
            {
                rebuilt.Add(replacement.TrimEnd('\n'));
                replaced = true;
            }
            else
            {
                rebuilt.Add(text.TrimEnd('\n'));
            }
        }

        if (!replaced) rebuilt.Add(replacement.TrimEnd('\n'));

        return header.TrimEnd('\n') + "\n\n" + string.Join("\n\n", rebuilt) + "\n";
    }

    /// <summary>The current text of one section, or null when the marker is not in the file.</summary>
    public static string? ReadSection(string content, string opKey)
    {
        var (_, sections) = Split(content);
        foreach (var (key, text) in sections)
        {
            if (string.Equals(key, opKey, StringComparison.Ordinal)) return text.TrimEnd('\n') + "\n";
        }
        return null;
    }

    private static string? MarkerOf(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith(HttpFileEmitter.OperationMarkerPrefix, StringComparison.Ordinal))
                return trimmed[HttpFileEmitter.OperationMarkerPrefix.Length..].Trim();
        }
        return null;
    }
}
