namespace Tap.Studio.OpenApi;

/// <summary>
/// Diffs a tracked collection against a freshly fetched document.
///
/// <para>Everything here is a pure function of (lock, new operations, what is on disk). It decides
/// nothing and writes nothing — it produces a plan the user approves, because the interesting
/// cases are exactly the ones where guessing would destroy work.</para>
/// </summary>
public static class OpenApiResyncPlanner
{
    public enum ChangeKind
    {
        /// <summary>In the document, not in the lock.</summary>
        Added,

        /// <summary>Upstream changed, and the local file is still exactly what we generated.
        /// Safe to regenerate wholesale.</summary>
        Changed,

        /// <summary>Upstream changed <i>and</i> the file was edited locally. The only case that
        /// needs a human.</summary>
        Conflict,

        /// <summary>Upstream is unchanged. Whether the user edited it locally is none of our
        /// business.</summary>
        Unchanged,

        /// <summary>Tracked and still in the document, but the file is gone — renamed out of the
        /// way, deleted, or moved to another collection.</summary>
        Orphaned,

        /// <summary>Tracked, still on disk, but no longer in the document.</summary>
        Removed,
    }

    public sealed record Change(
        ChangeKind Kind,
        string OpKey,
        string Method,
        string Path,
        string? Summary,
        /// <summary>Null for <see cref="ChangeKind.Added"/>.</summary>
        string? LocalPath,
        string? Fragment,
        /// <summary>Present for everything except <see cref="ChangeKind.Removed"/>.</summary>
        MappedOperation? Operation,
        /// <summary>Present when the operation is tracked.</summary>
        OpenApiLockOperation? Tracked,
        /// <summary>True when the file on disk no longer matches what we generated — i.e. the user
        /// edited it. Reported even for <see cref="ChangeKind.Unchanged"/> so the UI can say so.</summary>
        bool LocallyEdited);

    public sealed record Plan(IReadOnlyList<Change> Changes)
    {
        public int Added => Changes.Count(c => c.Kind == ChangeKind.Added);
        public int Changed => Changes.Count(c => c.Kind == ChangeKind.Changed);
        public int Conflicts => Changes.Count(c => c.Kind == ChangeKind.Conflict);
        public int Removed => Changes.Count(c => c.Kind == ChangeKind.Removed);
        public bool HasWork => Changes.Any(c => c.Kind != ChangeKind.Unchanged);
    }

    /// <param name="readLocal">
    /// Returns the current on-disk text for a tracked operation — the whole file for
    /// <c>.req.tap</c>, or just its <c>###</c> section for <c>.http</c> — or null when it is gone.
    /// Injected so the planner stays pure and testable.
    /// </param>
    public static Plan Diff(
        OpenApiLock lockFile,
        IReadOnlyList<MappedOperation> operations,
        Func<OpenApiLockOperation, string?> readLocal)
    {
        var changes = new List<Change>();
        var matched = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tracked in lockFile.Operations)
        {
            var operation = Match(tracked, operations, matched);

            var local = readLocal(tracked);
            if (local is null)
            {
                // The file is gone. If the operation also vanished upstream this is just tidy-up;
                // otherwise the user moved or deleted something we track.
                changes.Add(Build(
                    operation is null ? ChangeKind.Removed : ChangeKind.Orphaned,
                    tracked, operation, locallyEdited: false));
                continue;
            }

            var locallyEdited = !string.Equals(
                OpenApiImportPlanner.HashContent(local), tracked.GeneratedHash, StringComparison.Ordinal);

            if (operation is null)
            {
                changes.Add(Build(ChangeKind.Removed, tracked, null, locallyEdited));
                continue;
            }

            var upstreamChanged = !string.Equals(
                operation.SourceHash, tracked.UpstreamHash, StringComparison.Ordinal);

            var kind = (upstreamChanged, locallyEdited) switch
            {
                (false, _) => ChangeKind.Unchanged,
                (true, false) => ChangeKind.Changed,
                (true, true) => ChangeKind.Conflict,
            };

            changes.Add(Build(kind, tracked, operation, locallyEdited));
        }

        foreach (var operation in operations)
        {
            if (matched.Contains(operation.OpKey)) continue;
            changes.Add(new Change(
                ChangeKind.Added, operation.OpKey, operation.Method, operation.Path, operation.Summary,
                LocalPath: null, Fragment: null, Operation: operation, Tracked: null, LocallyEdited: false));
        }

        // Most-actionable first; the long tail of unchanged rows sinks to the bottom.
        return new Plan(changes
            .OrderBy(c => Rank(c.Kind))
            .ThenBy(c => c.Path, StringComparer.Ordinal)
            .ThenBy(c => c.Method, StringComparer.Ordinal)
            .ToArray());
    }

    /// <summary>
    /// Three passes, in this order, over what is left unmatched:
    /// <list type="number">
    ///   <item><c>operationId</c> equal on both sides — catches "the path moved, the id didn't"
    ///         (<c>/v1/pets</c> → <c>/v2/pets</c>).</item>
    ///   <item>method + path equal — catches "the id was renamed or newly added, the path didn't
    ///         move".</item>
    /// </list>
    /// Anything still unmatched is a genuine add or remove. Deliberately no fuzzy matching: on a
    /// CRUD-shaped API half the operations differ by one path segment, and a wrong guess silently
    /// rewrites the wrong request.
    /// </summary>
    private static MappedOperation? Match(
        OpenApiLockOperation tracked, IReadOnlyList<MappedOperation> operations, HashSet<string> matched)
    {
        if (tracked.OperationId is { Length: > 0 } id)
        {
            var byId = operations.FirstOrDefault(o =>
                !matched.Contains(o.OpKey) && string.Equals(o.OperationId, id, StringComparison.Ordinal));
            if (byId is not null) { matched.Add(byId.OpKey); return byId; }
        }

        var byShape = operations.FirstOrDefault(o =>
            !matched.Contains(o.OpKey)
            && string.Equals(o.Method, tracked.Method, StringComparison.OrdinalIgnoreCase)
            && string.Equals(o.Path, tracked.Path, StringComparison.Ordinal));
        if (byShape is not null) { matched.Add(byShape.OpKey); return byShape; }

        return null;
    }

    private static Change Build(
        ChangeKind kind, OpenApiLockOperation tracked, MappedOperation? operation, bool locallyEdited)
        => new(
            kind,
            tracked.OpKey,
            operation?.Method ?? tracked.Method,
            operation?.Path ?? tracked.Path,
            operation?.Summary,
            tracked.RelativePath,
            tracked.Fragment,
            operation,
            tracked,
            locallyEdited);

    private static int Rank(ChangeKind kind) => kind switch
    {
        ChangeKind.Conflict => 0,
        ChangeKind.Added => 1,
        ChangeKind.Changed => 2,
        ChangeKind.Removed => 3,
        ChangeKind.Orphaned => 4,
        _ => 5,
    };
}
