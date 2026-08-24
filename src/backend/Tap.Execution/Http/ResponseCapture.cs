using Tap.Workspace.Model;

namespace Tap.Execution.Http;

/// <summary>What a body capture produced, under one workspace's <see cref="ResponseLimits"/>.</summary>
/// <param name="Inline">The prefix that travels to the caller — at most the inline cap.</param>
/// <param name="TotalBytes">What the upstream actually sent, whether or not we kept it.</param>
/// <param name="RetainedBytes">How much is recoverable afterwards: the inline prefix when
/// nothing was retained, more when a retain sink took the overflow.</param>
public readonly record struct CapturedBody(byte[] Inline, long TotalBytes, long RetainedBytes)
{
    /// <summary>True when the inline copy is a prefix rather than the whole body.</summary>
    public bool Truncated => TotalBytes > Inline.LongLength;

    /// <summary>True when asking the host for more would actually return more.</summary>
    public bool HasMoreRetained => RetainedBytes > Inline.LongLength;
}

/// <summary>
/// The one place a response body is read off the wire under the workspace's caps. Every
/// front end funnels through here so "how big is too big" is answered identically for the
/// Studio's body pane, a CI test run, and the CLI's JSON document.
///
/// <para>The whole stream is drained regardless of the caps: the byte count in the status
/// strip is the size of the response, not the size of what we chose to keep, and a caller
/// that stopped reading early could report neither.</para>
/// </summary>
public static class ResponseCapture
{
    private const int ChunkSize = 64 * 1024;

    /// <summary>
    /// Read <paramref name="source"/> to its end, keeping the first <paramref name="maxInline"/>
    /// bytes in memory and copying the first <paramref name="maxRetained"/> into
    /// <paramref name="retain"/> when a sink is supplied. The sink receives the body from byte
    /// zero — it is a longer prefix of the same response, not the overflow on its own, so it
    /// can be served back as a file.
    /// </summary>
    public static async Task<CapturedBody> ReadAsync(
        Stream source, long maxInline, Stream? retain, long maxRetained, CancellationToken ct)
    {
        if (maxInline < 0) maxInline = 0;
        if (retain is null || maxRetained < maxInline) maxRetained = maxInline;

        using var inline = new MemoryStream();
        var buffer = new byte[ChunkSize];
        long total = 0;
        long retained = 0;
        int read;

        while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            total += read;

            if (inline.Length < maxInline)
            {
                var slack = (int)Math.Min(read, maxInline - inline.Length);
                inline.Write(buffer, 0, slack);
            }

            if (retain is not null && retained < maxRetained)
            {
                var slack = (int)Math.Min(read, maxRetained - retained);
                await retain.WriteAsync(buffer.AsMemory(0, slack), ct).ConfigureAwait(false);
                retained += slack;
            }
        }

        if (retain is null) retained = inline.Length;
        return new CapturedBody(inline.ToArray(), total, retained);
    }
}
