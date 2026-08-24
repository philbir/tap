using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Tap.Workspace.Security;

namespace Tap.Studio.History;

/// <summary>
/// Hands out the <see cref="HistoryStore"/> for whichever workspace is currently active.
///
/// <para>The store can't simply be a singleton: it is rooted at a workspace folder and its
/// encryption key is salted with that folder's path, and the Studio switches workspaces without
/// restarting. Caching per root means switching back and forth doesn't re-derive a key that
/// costs ~100 ms to produce.</para>
///
/// <para>First sight of a workspace is also when orphaned history is swept — the natural moment,
/// because it is exactly when we have just learned which requests still exist.</para>
/// </summary>
public sealed class HistoryStoreProvider(
    WorkspaceService workspace,
    IEncryptionKeySource keys,
    ILogger<HistoryStoreProvider> logger) : IHistoryStores
{
    private readonly ConcurrentDictionary<string, HistoryStore> _byRoot = new(StringComparer.OrdinalIgnoreCase);

    public HistoryStore Current => _byRoot.GetOrAdd(workspace.RootDirectory, Create);

    private HistoryStore Create(string root)
    {
        var store = new HistoryStore(root, keys);
        try
        {
            var days = workspace.Current.HistoryDefaults.EffectiveOrphanRetentionDays;
            var swept = store.SweepOrphans(workspace.Current, days);
            if (swept > 0)
                logger.LogInformation("Swept {Count} orphaned history folder(s) older than {Days} day(s).", swept, days);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Housekeeping. A workspace we can't tidy is still a workspace we can record into.
            logger.LogDebug(ex, "Orphan sweep failed for {Root}.", root);
        }
        return store;
    }
}

/// <summary>
/// The one thing <see cref="HistoryRecorder"/> needs from the provider. Exists so the recorder
/// can be exercised against a store rooted at a temp folder without standing up a
/// <see cref="WorkspaceService"/> and its file watcher.
/// </summary>
public interface IHistoryStores
{
    HistoryStore Current { get; }
}
