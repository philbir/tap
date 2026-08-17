using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.OpenApi;

namespace Tap.Studio.OpenApi;

/// <summary>
/// Holds parsed documents between the preview call and the import that follows it.
///
/// <para><b>Why staging rather than resending the spec.</b> The wizard shows an operation list,
/// the user picks from it, and only then does anything get written. If the second call carried the
/// document again, preview and import could disagree — a URL re-fetched a minute later may not
/// return the same bytes — and every call would push a document that can reach 16 MB through the
/// model binder. Staging makes the two phases provably see the same document and fetches once.</para>
///
/// <para>Bounded on every axis, because it is filled by user input: an entry count, a total byte
/// budget, and a sliding expiry. Eviction is oldest-first.</para>
/// </summary>
public sealed class OpenApiDocumentCache
{
    private const int MaxEntries = 8;
    private const long MaxTotalBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public sealed record Staged(
        string DocumentId,
        OpenApiDocument Document,
        string RawText,
        string ContentHash,
        string SpecVersion,
        string? SourceUrl,
        string? SourceFileName,
        IReadOnlyList<OpenApiDocumentReader.Diagnostic> Diagnostics);

    private sealed record Entry(Staged Value, long SizeBytes)
    {
        public DateTimeOffset LastTouched { get; set; } = DateTimeOffset.UtcNow;
    }

    public Staged Add(
        OpenApiDocument document,
        string rawText,
        string specVersion,
        string? sourceUrl,
        string? sourceFileName,
        IReadOnlyList<OpenApiDocumentReader.Diagnostic> diagnostics)
    {
        Prune();

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawText)));
        var staged = new Staged(
            DocumentId: Guid.CreateVersion7().ToString("n"),
            Document: document,
            RawText: rawText,
            ContentHash: hash,
            SpecVersion: specVersion,
            SourceUrl: sourceUrl,
            SourceFileName: sourceFileName,
            Diagnostics: diagnostics);

        _entries[staged.DocumentId] = new Entry(staged, Encoding.UTF8.GetByteCount(rawText));
        Prune();
        return staged;
    }

    public Staged? Get(string? documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId)) return null;
        if (!_entries.TryGetValue(documentId, out var entry)) return null;

        if (DateTimeOffset.UtcNow - entry.LastTouched > Ttl)
        {
            _entries.TryRemove(documentId, out _);
            return null;
        }

        entry.LastTouched = DateTimeOffset.UtcNow; // sliding: a slow wizard session stays valid
        return entry.Value;
    }

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, entry) in _entries)
        {
            if (now - entry.LastTouched > Ttl) _entries.TryRemove(id, out _);
        }

        while (_entries.Count > MaxEntries || _entries.Values.Sum(e => e.SizeBytes) > MaxTotalBytes)
        {
            var oldest = _entries.OrderBy(kv => kv.Value.LastTouched).FirstOrDefault();
            if (oldest.Key is null) break;
            _entries.TryRemove(oldest.Key, out _);
        }
    }
}
