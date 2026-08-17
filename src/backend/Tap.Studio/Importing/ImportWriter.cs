using Tap.Execution.Workspace;
using Tap.Studio.Contracts;
using Tap.Workspace.Model;

namespace Tap.Studio.Importing;

/// <summary>
/// Writes an <see cref="ImportPlan"/> to disk. Shared by every importer so they cannot drift on
/// the parts that matter: slug validation, the overwrite guard, and the rule that every file
/// goes through <see cref="WorkspaceService.Save"/> rather than <c>File.WriteAllText</c>.
/// </summary>
public static class ImportWriter
{
    public const string CollectionsRoot = "collections";

    /// <summary>What to do when the target collection directory already has content.</summary>
    public enum ExistingCollection
    {
        /// <summary>Fail with <c>collection-exists</c>. The default, because a silent merge turns
        /// requests renamed upstream into orphans nobody notices.</summary>
        Reject,

        /// <summary>Delete the directory first. Correct for "replace this import"; leftovers from
        /// a previous run would otherwise linger in the explorer as stale requests.</summary>
        Replace,

        /// <summary>Write over the files in the plan and leave everything else alone. This is the
        /// re-sync mode — wiping here would destroy the hand-written assertions and extra requests
        /// that re-sync exists to preserve.</summary>
        Merge,
    }

    /// <summary>Failure carries the wire error the endpoint should return; success carries null.</summary>
    public sealed record WriteResult(WorkspaceErrorDto? Error)
    {
        public bool Ok => Error is null;
        public static readonly WriteResult Success = new((WorkspaceErrorDto?)null);
    }

    public static WriteResult Write(WorkspaceService svc, ImportPlan plan, ExistingCollection onExisting)
    {
        if (!IsValidSlug(plan.Slug))
            return new WriteResult(new WorkspaceErrorDto("invalid-slug",
                $"Derived slug '{plan.Slug}' is not valid. Pass an explicit slug.", null, null));

        if (!WorkspacePaths.TryResolve(svc.RootDirectory, $"{CollectionsRoot}/{plan.Slug}",
                out var collectionDirAbs, out var dirErr))
            return new WriteResult(new WorkspaceErrorDto("invalid-slug", dirErr, null, null));

        var exists = Directory.Exists(collectionDirAbs)
            && Directory.EnumerateFileSystemEntries(collectionDirAbs).Any();

        switch (onExisting)
        {
            case ExistingCollection.Reject when exists:
                return new WriteResult(new WorkspaceErrorDto(
                    "collection-exists",
                    $"Collection '{plan.Slug}' already exists. Add to it, replace it, or pick another name.",
                    $"{CollectionsRoot}/{plan.Slug}",
                    null));
            case ExistingCollection.Replace when Directory.Exists(collectionDirAbs):
                Directory.Delete(collectionDirAbs, recursive: true);
                break;
        }

        foreach (var file in plan.Files)
        {
            try
            {
                svc.Save(file.RelativePath, file.Content);
            }
            catch (WorkspaceParseException ex)
            {
                return new WriteResult(new WorkspaceErrorDto(
                    ex.Error.Code, ex.Error.Message, ex.Error.RelativePath, ex.Error.Line));
            }
            catch (InvalidOperationException ex)
            {
                return new WriteResult(new WorkspaceErrorDto("import-failed", ex.Message, file.RelativePath, null));
            }
        }

        return WriteResult.Success;
    }

    public static bool IsValidSlug(string slug)
        => !string.IsNullOrWhiteSpace(slug)
        && slug.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_');
}
