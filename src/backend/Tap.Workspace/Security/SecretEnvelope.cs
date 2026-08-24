using System.Security.Cryptography;
using System.Text;

namespace Tap.Workspace.Security;

/// <summary>
/// The at-rest format Tap writes anything it must be able to read back but nobody else should:
/// <c>enc:v1:&lt;iv-b64&gt;:&lt;ciphertext-b64&gt;:&lt;tag-b64&gt;</c>, AES-256-GCM under a key
/// derived from the machine passphrase with PBKDF2-HMAC-SHA256.
///
/// <para>Extracted from the <c>file</c> variable provider, which invented it and remains its
/// biggest user, so the second thing that needs at-rest encryption — request history — inherits
/// a format that has already been reviewed rather than inventing a near-identical one. Two
/// envelope formats is one too many to reason about when the question is "can an attacker who
/// has the repo read this".</para>
///
/// <para>The salt is a per-store label (<c>tap-file-provider:&lt;name&gt;</c>,
/// <c>tap-history:&lt;workspace&gt;</c>), so a single machine key still yields a distinct
/// derived key per store. Derivation is deliberately expensive; callers cache the result via
/// <see cref="DerivedKey"/> rather than re-deriving per value.</para>
/// </summary>
public static class SecretEnvelope
{
    /// <summary>On-disk marker for an encrypted payload. Public so callers can detect one
    /// without attempting a decrypt.</summary>
    public const string Prefix = "enc:v1:";

    private const int Pbkdf2Iterations = 200_000;
    private const int KeyBytes = 32;
    private const int IvBytes = 12;
    private const int TagBytes = 16;

    /// <summary>True when <paramref name="text"/> is in the v1 envelope.</summary>
    public static bool IsEnvelope(string? text)
        => text is not null && text.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Derives the AES key for one store. Expensive by design (~100 ms) — hold the
    /// result in a <see cref="DerivedKey"/> instead of calling per value.</summary>
    public static byte[] DeriveKey(string passphrase, string salt)
        => Rfc2898DeriveBytes.Pbkdf2(
            passphrase, Encoding.UTF8.GetBytes(salt), Pbkdf2Iterations, HashAlgorithmName.SHA256, KeyBytes);

    /// <summary>Wraps <paramref name="clear"/> in the v1 envelope under <paramref name="key"/>.</summary>
    public static string Protect(string clear, byte[] key)
    {
        var iv = RandomNumberGenerator.GetBytes(IvBytes);
        var plain = Encoding.UTF8.GetBytes(clear);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(iv, plain, cipher, tag);
        return Prefix
            + Convert.ToBase64String(iv) + ":"
            + Convert.ToBase64String(cipher) + ":"
            + Convert.ToBase64String(tag);
    }

    /// <summary>
    /// Unwraps a v1 envelope. Throws <see cref="CryptographicException"/> for anything that
    /// isn't one, is malformed, or fails the GCM tag check — the three failures are reported
    /// distinctly because they mean different things: not-an-envelope is a format mistake,
    /// a failed tag is the wrong key or a tampered file.
    /// </summary>
    public static string Unprotect(string stored, byte[] key)
    {
        if (!IsEnvelope(stored))
            throw new CryptographicException("Value is not in the v1 encryption envelope.");

        var parts = stored[Prefix.Length..].Split(':');
        if (parts.Length != 3)
            throw new CryptographicException("Malformed v1 encryption envelope.");

        var iv = Convert.FromBase64String(parts[0]);
        var cipher = Convert.FromBase64String(parts[1]);
        var tag = Convert.FromBase64String(parts[2]);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(iv, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}

/// <summary>
/// A store's derived key, re-derived only when the machine passphrase changes.
///
/// <para>That last part is the whole reason this is a class and not a cached field: the Studio
/// is long-lived, and someone who clicks "generate a key" in Settings expects the very next
/// write to encrypt — without restarting the host, and without a stale key silently producing
/// files the new key cannot read.</para>
/// </summary>
public sealed class DerivedKey(IEncryptionKeySource source, string salt)
{
    private readonly Lock _gate = new();
    private (string Passphrase, byte[] Key)? _cached;

    /// <summary>
    /// The key for this store, or null when the machine has no passphrase.
    ///
    /// <para><paramref name="create"/> is the encrypt/decrypt asymmetry. Storing something is a
    /// request to protect it, so a machine with no key gets one made for it. Reading is not:
    /// no key there means the ciphertext was written under a key that is gone, and minting a
    /// fresh one would answer "your data is unreadable" with a key that still cannot read it.</para>
    /// </summary>
    public byte[]? Get(bool create = false)
    {
        var passphrase = create ? source.EnsurePassphrase() : source.GetPassphrase();
        if (string.IsNullOrEmpty(passphrase)) return null;

        lock (_gate)
        {
            if (_cached is { } c && string.Equals(c.Passphrase, passphrase, StringComparison.Ordinal))
                return c.Key;
            var key = SecretEnvelope.DeriveKey(passphrase, salt);
            _cached = (passphrase, key);
            return key;
        }
    }
}
