namespace Tap.Core.Redaction;

/// <summary>
/// One thing <see cref="CaptureRedactor"/> hid, and why.
///
/// <para>Redaction on the inspector's agent surface is deliberately <em>reported</em> rather
/// than silent. An agent told "the password field was hidden, reason known-key" knows to ask
/// a human; an agent handed a quietly-stripped payload invents a story about the missing
/// field. That makes this a correctness feature before it is an audit one.</para>
///
/// <para><paramref name="Fingerprint"/> is the salted short hash of the hidden value, or
/// <c>null</c> when the value was too short to fingerprint safely. Two notes carrying the
/// same fingerprint hid the same bytes — which is how an agent answers "is the 401 sending a
/// different token than the 200?" without ever seeing either token.</para>
/// </summary>
/// <param name="Location">Where it was, e.g. <c>header:Authorization</c>,
/// <c>query:access_token</c>, <c>body:$.user.password</c>.</param>
/// <param name="Reason">Which rule fired — see <see cref="RedactionReason"/>.</param>
public sealed record RedactionNote(string Location, string Reason, string? Fingerprint);

/// <summary>The rule that caused a <see cref="RedactionNote"/>. Stable strings: agents and
/// the UI branch on them, so they are part of the contract.</summary>
public static class RedactionReason
{
    /// <summary>A header that carries credentials by definition.</summary>
    public const string SensitiveHeader = "sensitive-header";

    /// <summary>A JSON/form/query key whose name announces a secret.</summary>
    public const string KnownKey = "known-key";

    /// <summary>A cookie that failed the "obviously boring" test.</summary>
    public const string Cookie = "cookie";

    /// <summary>Content we will not render at all — binary, or a multipart part.</summary>
    public const string Binary = "binary";

    /// <summary>Structured content that would not parse, so it was masked whole rather than
    /// scanned optimistically.</summary>
    public const string Unparseable = "unparseable";

    /// <summary>A detector exceeded its time budget on hostile input; the payload was masked
    /// whole rather than shipped unscanned.</summary>
    public const string ScanTimeout = "scan-timeout";

    /// <summary>A value matched a shape detector — <c>pattern:jwt</c>, <c>pattern:pan</c>, …</summary>
    public static string Pattern(string kind) => "pattern:" + kind;
}

/// <summary>Redacted text plus what was taken out of it.</summary>
public sealed record RedactedText(string Text, IReadOnlyList<RedactionNote> Notes);

/// <summary>Redacted headers plus what was taken out of them. Header order and casing are
/// preserved: both are occasionally the bug.</summary>
public sealed record RedactedHeaders(
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyList<RedactionNote> Notes);

/// <summary>
/// A body safe to show an agent.
///
/// <para><paramref name="Text"/> is <c>null</c> when nothing renderable survived — a binary
/// payload, or a multipart upload. <paramref name="Sha256"/> identifies the captured bytes
/// even then, so an agent can say "the retry sent the same payload" about content it is not
/// allowed to read.</para>
/// </summary>
/// <param name="Kind">What the body was treated as: <c>json</c>, <c>form</c>,
/// <c>multipart</c>, <c>text</c>, <c>binary</c>, or <c>empty</c>.</param>
public sealed record RedactedBody(
    string? Text,
    string Kind,
    long OriginalSize,
    bool Truncated,
    string? Sha256,
    IReadOnlyList<RedactionNote> Notes);
