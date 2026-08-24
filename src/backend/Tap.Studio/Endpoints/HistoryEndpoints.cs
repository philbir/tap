using Tap.Studio.History;

namespace Tap.Studio.Endpoints;

/// <summary>
/// Reads (and clears) what <see cref="HistoryRecorder"/> wrote. Nothing here records — recording
/// is a side effect of executing, and lives with the execute endpoints.
///
/// <para>The listings deliberately return summaries rather than whole entries: a timeline row
/// needs a status, a duration and a URL, and shipping every recorded body to draw one would make
/// the cheap part of this feature the expensive part.</para>
/// </summary>
public static class HistoryEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/history");

        // The workspace-wide timeline. `collection` and `status` filter after the fact rather
        // than in the enumeration, because the newest-N cut has to happen on the whole set — a
        // filter applied first would let one chatty collection push everything else out of range.
        g.MapGet("/", (
            HistoryStoreProvider stores, WorkspaceService svc,
            int? limit = null, string? collection = null, string? status = null, bool includeOrphans = true) =>
        {
            var take = Math.Clamp(limit ?? 100, 1, 500);
            var rows = stores.Current.ListRecent(svc.Current, take);
            if (!string.IsNullOrEmpty(collection))
                rows = [.. rows.Where(r => string.Equals(r.Collection, collection, StringComparison.OrdinalIgnoreCase))];
            if (!includeOrphans)
                rows = [.. rows.Where(r => !r.Orphaned)];
            rows = FilterByStatus(rows, status);
            return Results.Ok(rows);
        });

        g.MapGet("/request/{requestId}", (
            string requestId, HistoryStoreProvider stores, WorkspaceService svc, int? limit = null) =>
            Results.Ok(stores.Current.ListForRequest(requestId, svc.Current, Math.Clamp(limit ?? 100, 1, 500))));

        g.MapGet("/entry/{requestId}/{entryId}", (string requestId, string entryId, HistoryStoreProvider stores) =>
        {
            var entry = stores.Current.Read(requestId, entryId);
            if (entry is not null) return Results.Ok(entry);
            // "Encrypted and this machine has no key" is a different answer from "gone", and the
            // only one the user can act on — it names the key they need rather than implying the
            // entry was deleted.
            return stores.Current.IsLocked(requestId, entryId)
                ? Results.Problem(
                    title: "Entry is encrypted",
                    detail: "This entry was written with history.encrypt on, and this machine has no key that opens it.",
                    statusCode: StatusCodes.Status423Locked)
                : Results.NotFound();
        });

        g.MapDelete("/entry/{requestId}/{entryId}", (string requestId, string entryId, HistoryStoreProvider stores) =>
            stores.Current.DeleteEntry(requestId, entryId) ? Results.NoContent() : Results.NotFound());

        g.MapDelete("/request/{requestId}", (string requestId, HistoryStoreProvider stores) =>
            stores.Current.DeleteRequest(requestId) ? Results.NoContent() : Results.NotFound());

        g.MapDelete("/orphans", (HistoryStoreProvider stores, WorkspaceService svc) =>
            Results.Ok(new { deleted = stores.Current.DeleteOrphans(svc.Current) }));

        // Whether the encrypt toggle can be honoured, so the settings UI can offer to generate a
        // key instead of letting the user turn on an option that then quietly records nothing.
        g.MapGet("/status", (HistoryStoreProvider stores) =>
            Results.Ok(new HistoryStatusDto(stores.Current.Root, stores.Current.HasKey)));
    }

    /// <summary>Filters by outcome class: <c>ok</c>, <c>failed</c> (any non-2xx/3xx, an error, or
    /// a failing assertion), or a status-code family like <c>4xx</c>.</summary>
    private static IReadOnlyList<HistorySummary> FilterByStatus(IReadOnlyList<HistorySummary> rows, string? status)
    {
        if (string.IsNullOrEmpty(status)) return rows;
        if (status.Equals("ok", StringComparison.OrdinalIgnoreCase)) return [.. rows.Where(r => r.Ok)];
        if (status.Equals("failed", StringComparison.OrdinalIgnoreCase)) return [.. rows.Where(r => !r.Ok)];
        if (status.Length == 3 && char.IsAsciiDigit(status[0]) && status.EndsWith("xx", StringComparison.OrdinalIgnoreCase))
        {
            var family = status[0] - '0';
            return [.. rows.Where(r => r.Status is { } s && s / 100 == family)];
        }
        return int.TryParse(status, out var exact) ? [.. rows.Where(r => r.Status == exact)] : rows;
    }
}

/// <summary>Where history lives and whether encrypted entries are readable here.</summary>
public sealed record HistoryStatusDto(string Directory, bool HasEncryptionKey);
