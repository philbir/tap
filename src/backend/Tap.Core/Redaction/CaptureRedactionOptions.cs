namespace Tap.Core.Redaction;

/// <summary>
/// Per-inspector tuning for <see cref="CaptureRedactor"/>. Every knob here only ever makes
/// the redactor hide <em>more</em>: there is deliberately no way to switch a layer off, and
/// no way to ask for a value back. See the "no reveal" rule in
/// <c>docs/inspector-agent-plan.md</c>.
/// </summary>
public sealed class CaptureRedactionOptions
{
    /// <summary>Extra header names to treat as credential carriers, on top of the built-in
    /// list. Bound from <c>Inspector:Agent:ExtraSensitiveHeaders</c>.</summary>
    public IReadOnlyCollection<string> ExtraSensitiveHeaders { get; init; } = [];

    /// <summary>Extra JSON/form/query/cookie key names to treat as secret-shaped, on top of
    /// the built-in list. Bound from <c>Inspector:Agent:ExtraSecretKeys</c>.</summary>
    public IReadOnlyCollection<string> ExtraSecretKeys { get; init; } = [];

    /// <summary>
    /// Cookie values at least this long are masked whatever they are called. Session cookies
    /// are effectively always longer than this; the display preferences worth keeping
    /// (<c>theme=dark</c>, <c>locale=de-CH</c>) are not.
    /// </summary>
    public int CookieKeepMaxLength { get; init; } = 20;

    /// <summary>
    /// Values shorter than this are never fingerprinted. A short value's hash is a much
    /// better guessing oracle than a long one's, and a two-character "secret" is not worth
    /// correlating anyway. <see cref="Tap.Workspace"/>'s <c>SecretRedactor</c> draws the same
    /// kind of line at 4 for a related reason.
    /// </summary>
    public int MinFingerprintLength { get; init; } = 8;

    /// <summary>
    /// Which JWT claims appear in a token's preview. Registered claims only by default —
    /// private claims are where identity providers put <c>email</c>, <c>name</c>, and
    /// <c>phone_number</c>, and those are the reader's problem to justify, not the redactor's
    /// to leak.
    /// </summary>
    public JwtClaimPolicy JwtClaims { get; init; } = JwtClaimPolicy.Registered;

    public static CaptureRedactionOptions Default { get; } = new();
}
