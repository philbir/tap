using Microsoft.OpenApi;
using Tap.Studio.Importing;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;

namespace Tap.Studio.OpenApi;

/// <summary>
/// Runs a re-sync: diff a tracked collection against a newer document, then apply the decisions
/// the user made about each operation.
///
/// <para>This is the only part of re-sync that touches disk. The diff
/// (<see cref="OpenApiResyncPlanner"/>) and the merge (<see cref="OpenApiResyncMerger"/>) are pure
/// and tested on their own.</para>
/// </summary>
public sealed class OpenApiResyncService(WorkspaceService svc)
{
    /// <summary>What to do with one operation. Anything not named is left alone.</summary>
    public sealed record Decision(string OpKey, DecisionAction Action);

    public enum DecisionAction
    {
        /// <summary>Leave the local file exactly as it is.</summary>
        Skip,

        /// <summary>Create the request for an operation that is new upstream.</summary>
        Add,

        /// <summary>Rewrite the fields the importer owns, keeping assertions, vars, auth and the
        /// rest. Used for both a clean update and a resolved conflict.</summary>
        Update,

        /// <summary>Tag the request <c>deprecated</c> and note it in the body. The default for an
        /// operation that vanished upstream — deleting someone's request because a server response
        /// changed is the one unrecoverable act in this feature.</summary>
        Deprecate,

        /// <summary>Drop it from the lock, leave the file. "Stop managing this."</summary>
        Untrack,
    }

    public sealed record ApplyResult(
        int Added, int Updated, int Deprecated, int Untracked, int Skipped,
        IReadOnlyList<string> WrittenPaths, IReadOnlyList<string> Warnings);

    /// <summary>Diffs without writing anything.</summary>
    public OpenApiResyncPlanner.Plan Diff(OpenApiLock lockFile, OpenApiDocument document)
        => OpenApiResyncPlanner.Diff(lockFile, OpenApiOperationMapper.Map(document), ReadLocal);

    /// <summary>
    /// Reads the current text a tracked operation occupies: the whole file for <c>.req.tap</c>, or
    /// just its <c>###</c> section for <c>.http</c>. Null when it is gone — which is how the
    /// planner detects an orphan.
    /// </summary>
    private string? ReadLocal(OpenApiLockOperation tracked)
    {
        try
        {
            var content = svc.ReadSource(tracked.RelativePath);
            if (content is null) return null;
            return tracked.Fragment is null
                ? content
                : HttpFileSurgeon.ReadSection(content, tracked.OpKey);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <param name="documentHash">
    /// Hash of the document being synced to. Recorded only when nothing actionable was left
    /// behind — otherwise the next preview would claim the document is unchanged while the
    /// changes the user deferred are still outstanding.
    /// </param>
    public ApplyResult Apply(
        string slug,
        OpenApiLock lockFile,
        OpenApiDocument document,
        OpenApiImportPlanner.Options options,
        IReadOnlyDictionary<string, DecisionAction> decisions,
        string? documentHash = null)
    {
        var plan = Diff(lockFile, document);
        var warnings = new List<string>();
        var written = new List<string>();
        int added = 0, updated = 0, deprecated = 0, untracked = 0, skipped = 0;
        var deferred = 0;

        // Start from the tracked set and mutate as decisions are applied, so the lock we write at
        // the end describes exactly what is now on disk.
        var tracking = lockFile.Operations.ToDictionary(o => o.OpKey, StringComparer.Ordinal);

        foreach (var change in plan.Changes)
        {
            var action = decisions.TryGetValue(change.OpKey, out var chosen)
                ? chosen
                : DecisionAction.Skip;

            if (action == DecisionAction.Skip)
            {
                skipped++;
                // An unchanged row being "skipped" is not deferred work; a real change is.
                if (change.Kind != OpenApiResyncPlanner.ChangeKind.Unchanged) deferred++;
                continue;
            }

            switch (action)
            {
                case DecisionAction.Untrack:
                    tracking.Remove(change.OpKey);
                    untracked++;
                    break;

                case DecisionAction.Deprecate when change.Tracked is { } trackedOp:
                    if (Deprecate(trackedOp, warnings) is { } deprecatedPath)
                    {
                        written.Add(deprecatedPath);
                        deprecated++;
                    }
                    tracking.Remove(change.OpKey);
                    break;

                case DecisionAction.Add when change.Operation is { } newOp:
                    if (AddOperation(slug, newOp, lockFile, options, warnings) is { } addition)
                    {
                        written.Add(addition.RelativePath);
                        tracking[addition.OpKey] = addition;
                        added++;
                    }
                    break;

                case DecisionAction.Update when change is { Operation: { } op, Tracked: { } tracked }:
                    if (UpdateOperation(op, tracked, change.LocallyEdited, options, warnings) is { } update)
                    {
                        written.Add(update.RelativePath);
                        tracking[update.OpKey] = update;
                        updated++;
                    }
                    break;

                default:
                    skipped++;
                    break;
            }
        }

        var store = new OpenApiLockStore(svc.RootDirectory);
        store.Write(slug, lockFile with
        {
            Source = lockFile.Source with
            {
                FetchedAt = DateTimeOffset.UtcNow,
                ApiVersion = document.Info?.Version ?? lockFile.Source.ApiVersion,
                // Only claim we are level with this document when nothing was left undone.
                DocumentHash = deferred == 0 && documentHash is { Length: > 0 }
                    ? documentHash
                    : lockFile.Source.DocumentHash,
            },
            Operations = tracking.Values.ToArray(),
        });

        svc.ReloadNow();

        return new ApplyResult(added, updated, deprecated, untracked, skipped, written, warnings);
    }

    private OpenApiLockOperation? AddOperation(
        string slug,
        MappedOperation operation,
        OpenApiLock lockFile,
        OpenApiImportPlanner.Options options,
        List<string> warnings)
    {
        var collectionDir = $"{ImportWriter.CollectionsRoot}/{slug}";

        if (lockFile.Layout == "http")
        {
            // Land it in the file its tag already uses, so a new operation joins its siblings
            // rather than starting a stray file.
            var tag = operation.Tags.Count > 0 ? operation.Tags[0] : "api";
            var target = lockFile.Operations
                .Select(o => o.RelativePath)
                .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p)
                    .Equals(RequestSlug.ForTag(tag), StringComparison.OrdinalIgnoreCase))
                ?? $"{collectionDir}/{RequestSlug.ForTag(tag)}{KindResolver.HttpExtension}";

            var section = HttpFileEmitter.EmitWithSections([operation], new HttpFileEmitter.FileOptions(
                null, null, null, null)).Sections.Single();

            var existing = svc.ReadSource(target) ?? HttpFileEmitter.Emit([], new HttpFileEmitter.FileOptions(
                null, null, null, tag));
            var merged = HttpFileSurgeon.ReplaceSection(existing, operation.OpKey, section.Text);

            if (!TrySave(target, merged, warnings)) return null;

            return new OpenApiLockOperation(
                operation.OpKey, operation.OperationId, operation.Method, operation.Path,
                operation.SourceHash, OpenApiImportPlanner.HashContent(section.Text),
                null, target, section.Name);
        }

        var folder = operation.Tags.Count > 0 ? RequestSlug.ForTag(operation.Tags[0]) : string.Empty;
        var dir = folder.Length > 0 ? $"{collectionDir}/{folder}" : collectionDir;
        var siblings = new HashSet<string>(
            lockFile.Operations
                .Where(o => (Path.GetDirectoryName(o.RelativePath) ?? string.Empty).Replace('\\', '/') == dir)
                .Select(o => StripSuffix(Path.GetFileName(o.RelativePath))),
            StringComparer.OrdinalIgnoreCase);

        var fileSlug = ImportSlug.UniqueSlug(RequestSlug.For(operation), siblings, "request");
        var relPath = $"{dir}/{KindResolver.FileNameFor(WorkspaceKind.Request, fileSlug)}";

        var spec = OpenApiImportPlanner.BuildRequestSpec(operation, relPath, options);
        var content = Specs.RequestSpecEmitter.ToFileSource(spec);
        if (!TrySave(relPath, content, warnings)) return null;

        return new OpenApiLockOperation(
            operation.OpKey, operation.OperationId, operation.Method, operation.Path,
            operation.SourceHash, OpenApiImportPlanner.HashContent(content), spec.Id, relPath, null);
    }

    private OpenApiLockOperation? UpdateOperation(
        MappedOperation operation,
        OpenApiLockOperation tracked,
        bool locallyEdited,
        OpenApiImportPlanner.Options options,
        List<string> warnings)
    {
        if (tracked.Fragment is not null)
        {
            // .http: regenerate just this section and splice it back. Every other section in the
            // file is copied byte-for-byte, so the diff shows only what actually changed.
            var file = svc.ReadSource(tracked.RelativePath);
            if (file is null)
            {
                warnings.Add($"{tracked.RelativePath} is missing — skipped {tracked.OpKey}.");
                return null;
            }

            var section = HttpFileEmitter.EmitWithSections([operation], new HttpFileEmitter.FileOptions(
                null, null, null, null)).Sections.Single();
            var merged = HttpFileSurgeon.ReplaceSection(file, tracked.OpKey, section.Text);
            if (!TrySave(tracked.RelativePath, merged, warnings)) return null;

            return tracked with
            {
                OperationId = operation.OperationId,
                Method = operation.Method,
                Path = operation.Path,
                UpstreamHash = operation.SourceHash,
                GeneratedHash = OpenApiImportPlanner.HashContent(section.Text),
                Fragment = section.Name,
            };
        }

        // .req.tap: find it by id first so a renamed or moved file still updates in place.
        var request = Resolve(tracked);
        if (request is null)
        {
            warnings.Add($"Could not find the request for {tracked.OpKey} — skipped.");
            return null;
        }

        var content = OpenApiResyncMerger.MergeRequest(request, operation, options, preserveProse: locallyEdited);
        if (!TrySave(request.RelativePath, content, warnings)) return null;

        return tracked with
        {
            OperationId = operation.OperationId,
            Method = operation.Method,
            Path = operation.Path,
            UpstreamHash = operation.SourceHash,
            GeneratedHash = OpenApiImportPlanner.HashContent(content),
            RelativePath = request.RelativePath, // repair the hint when the file has moved
            FileId = request.Id ?? tracked.FileId,
        };
    }

    /// <summary>Marks a request the document no longer describes, rather than deleting it.</summary>
    private string? Deprecate(OpenApiLockOperation tracked, List<string> warnings)
    {
        if (tracked.Fragment is not null)
        {
            // A .http section is raw text the user owns; annotate it in place.
            var file = svc.ReadSource(tracked.RelativePath);
            if (file is null) return null;
            var section = HttpFileSurgeon.ReadSection(file, tracked.OpKey);
            if (section is null) return null;
            if (section.Contains("# @tap-tag deprecated", StringComparison.Ordinal)) return null;

            var annotated = section.TrimEnd('\n') + "\n";
            var marked = annotated.Replace(
                HttpFileEmitter.OperationMarkerPrefix + tracked.OpKey,
                HttpFileEmitter.OperationMarkerPrefix + tracked.OpKey
                    + "\n# @tap-tag deprecated\n# No longer present in the API description.",
                StringComparison.Ordinal);

            return TrySave(tracked.RelativePath, HttpFileSurgeon.ReplaceSection(file, tracked.OpKey, marked), warnings)
                ? tracked.RelativePath
                : null;
        }

        if (Resolve(tracked) is not { } request) return null;

        var spec = Specs.RequestSpecProjection.ToSpec(request);
        var tags = new List<string>(spec.Tags ?? []);
        if (!tags.Contains("deprecated", StringComparer.OrdinalIgnoreCase)) tags.Add("deprecated");

        const string note = "> **No longer in the API description.** Kept so nothing you wrote is lost.";
        var body = spec.Body is { Length: > 0 } existing && !existing.Contains(note, StringComparison.Ordinal)
            ? $"{existing}\n\n{note}"
            : spec.Body ?? note;

        var content = Specs.RequestSpecEmitter.ToFileSource(spec with { Tags = tags, Body = body });
        return TrySave(request.RelativePath, content, warnings) ? request.RelativePath : null;
    }

    /// <summary><c>id:</c> first, path second — the move endpoint rewrites no refs, so the path in
    /// the lock is a hint that goes stale the moment a request is dragged in the explorer.</summary>
    private RequestFile? Resolve(OpenApiLockOperation tracked)
    {
        if (tracked.FileId is { Length: > 0 } id)
        {
            var byId = svc.Current.Requests.FirstOrDefault(r =>
                string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            if (byId is not null) return byId;
        }
        return svc.Current.FindByPath(tracked.RelativePath) as RequestFile;
    }

    private bool TrySave(string relativePath, string content, List<string> warnings)
    {
        try
        {
            svc.Save(relativePath, content);
            return true;
        }
        catch (Exception e) when (e is WorkspaceParseException or InvalidOperationException)
        {
            warnings.Add($"{relativePath}: {e.Message}");
            return false;
        }
    }

    private static string StripSuffix(string fileName)
    {
        var suffix = KindResolver.SuffixFor(WorkspaceKind.Request);
        return fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^suffix.Length]
            : Path.GetFileNameWithoutExtension(fileName);
    }
}
