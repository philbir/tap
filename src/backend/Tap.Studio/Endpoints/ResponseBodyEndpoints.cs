using Tap.Studio.Contracts;
using Tap.Execution.Http;

namespace Tap.Studio.Endpoints;

/// <summary>
/// <c>/api/execute/body/*</c> — the retained copy of a response the panel showed truncated.
///
/// <para>Two ways to ask for it, because the two asks are different. <c>/text</c> returns a
/// longer decoded prefix for the body viewer, which has a practical limit on what it can
/// render; the bare route streams the file as an attachment, which does not. Both read the
/// same spool — the request is never sent a second time, which is the whole point: a POST
/// you can't safely repeat is exactly the one whose 40 MB response you want to read.</para>
/// </summary>
public static class ResponseBodyEndpoints
{
    /// <summary>Hard ceiling on one <c>/text</c> reply, independent of what the workspace
    /// retains. Past this the honest answer is "download it" — no code editor renders a
    /// hundred megabytes of JSON.</summary>
    private const long MaxTextBytes = 32L * 1024 * 1024;

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/execute/body");

        g.MapGet("/{id}/text", async (string id, long? max, ResponseBodyStore store, CancellationToken ct) =>
        {
            if (store.Get(id) is not { } body) return Results.NotFound();

            var wanted = Math.Clamp(max ?? body.RetainedBytes, 0, Math.Min(body.RetainedBytes, MaxTextBytes));
            var bytes = await ReadPrefixAsync(body.Path, wanted, ct).ConfigureAwait(false);

            // Decoded through the same path the inline body took, so the viewer gets the same
            // treatment (and the same "…truncated" marker) for a longer prefix.
            var text = HttpExecutionHelpers.TryDecodeBody(bytes, body.ContentType, body.TotalBytes);
            return Results.Ok(new ResponseBodyTextDto(text, bytes.LongLength, body.TotalBytes, body.RetainedBytes));
        });

        g.MapGet("/{id}", (string id, string? name, ResponseBodyStore store) =>
        {
            if (store.Get(id) is not { } body) return Results.NotFound();

            var stream = new FileStream(body.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            return Results.File(
                stream,
                contentType: body.ContentType ?? "application/octet-stream",
                fileDownloadName: SafeFileName(name) ?? $"response-{id}.bin");
        });
    }

    /// <summary>Read the first <paramref name="count"/> bytes of the spool.</summary>
    private static async Task<byte[]> ReadPrefixAsync(string path, long count, CancellationToken ct)
    {
        if (count <= 0) return [];
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var buffer = new byte[count];
        var read = await file.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        return read == buffer.Length ? buffer : buffer[..read];
    }

    /// <summary>Reduce a client-supplied download name to a bare filename. The value only ever
    /// reaches a <c>Content-Disposition</c> header, but a name carrying a path separator or a
    /// newline has no business being echoed into one.</summary>
    private static string? SafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = new string(name.Trim().Where(c => !char.IsControl(c)).ToArray());
        trimmed = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed is "." or "..") return null;
        return trimmed.Length > 120 ? trimmed[^120..] : trimmed;
    }
}
