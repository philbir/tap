using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Tap.Studio.Importing;

/// <summary>
/// One parsed document held between the preview call that produced it and the import that
/// follows.
///
/// <para><see cref="SpecVersion"/> is whatever dialect string the format uses — <c>3.1</c> for
/// OpenAPI, <c>1.1</c> for WSDL — and is carried here only so the import can record it in the
/// collection's lock without re-parsing.</para>
/// </summary>
public sealed record StagedDocument<TDocument>(
    string DocumentId,
    TDocument Document,
    string RawText,
    string ContentHash,
    string SpecVersion,
    string? SourceUrl,
    string? SourceFileName);

/// <summary>
/// Holds parsed documents between the preview call and the import that follows it.
///
/// <para><b>Why staging rather than resending the spec.</b> The wizard shows a list of operations,
/// the user picks from it, and only then does anything get written. If the second call carried the
/// document again, preview and import could disagree — a URL re-fetched a minute later may not
/// return the same bytes — and every call would push a document that can reach megabytes through
/// the model binder. Staging makes the two phases provably see the same document and fetches
/// once.</para>
///
/// <para>Bounded on every axis, because it is filled by user input: an entry count, a total byte
/// budget, and a sliding expiry. Eviction is oldest-first. Generic over the parsed document so
/// the OpenAPI and WSDL wizards share one bounded cache rather than two that can drift apart on
/// exactly the limits that make it safe.</para>
///
/// <para>Parse diagnostics are deliberately <b>not</b> kept here. They are rendered into the
/// staging response the wizard already has in hand, and nothing downstream reads them back — a
/// copy on every entry would only be one more thing to keep bounded.</para>
/// </summary>
public abstract class StagedDocumentCache<TDocument>
{
    private const int MaxEntries = 8;
    private const long MaxTotalBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed record Entry(StagedDocument<TDocument> Value, long SizeBytes)
    {
        public DateTimeOffset LastTouched { get; set; } = DateTimeOffset.UtcNow;
    }

    public StagedDocument<TDocument> Add(
        TDocument document,
        string rawText,
        string specVersion,
        string? sourceUrl,
        string? sourceFileName)
    {
        Prune();

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawText)));
        var staged = new StagedDocument<TDocument>(
            DocumentId: Guid.CreateVersion7().ToString("n"),
            Document: document,
            RawText: rawText,
            ContentHash: hash,
            SpecVersion: specVersion,
            SourceUrl: sourceUrl,
            SourceFileName: sourceFileName);

        _entries[staged.DocumentId] = new Entry(staged, Encoding.UTF8.GetByteCount(rawText));
        Prune();
        return staged;
    }

    public StagedDocument<TDocument>? Get(string? documentId)
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
