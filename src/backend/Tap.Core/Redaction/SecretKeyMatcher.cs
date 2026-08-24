namespace Tap.Core.Redaction;

/// <summary>
/// Decides whether a key name announces a secret — for JSON properties, form fields, query
/// parameters, and cookie names.
///
/// <para>Names are normalised before matching (lower-cased, with <c>_</c>, <c>-</c> and
/// <c>.</c> removed), so <c>client_secret</c>, <c>Client-Secret</c> and <c>clientSecret</c>
/// are one key.</para>
///
/// <para>Query strings get a wider net than request bodies. <c>?code=</c> is an OAuth
/// authorization code and <c>?key=</c> is an API key, but <c>{"code": "CH"}</c> and
/// <c>{"key": "region"}</c> are ordinary data — masking those would cost real debugging
/// value for no gain, so the extra names apply only where they actually mean something.</para>
/// </summary>
internal sealed class SecretKeyMatcher
{
    /// <summary>Whole names, after normalisation.</summary>
    private static readonly string[] Exact =
    [
        "pwd", "pass", "passwd", "password", "passphrase",
        "secret", "clientsecret", "apisecret",
        "token", "accesstoken", "refreshtoken", "idtoken", "bearer", "jwt",
        "apikey", "credential", "credentials", "privatekey",
        "auth", "authorization",
        "sid", "session", "sessionid",
        "otp", "pin", "cvv", "cvc", "ssn",
    ];

    /// <summary>Substrings, after normalisation. Catches <c>csrfToken</c>,
    /// <c>stripeApiKey</c>, <c>userPassword</c> without enumerating them.</summary>
    private static readonly string[] Contains =
    [
        "password", "passwd", "passphrase", "secret", "token",
        "apikey", "privatekey", "credential",
    ];

    /// <summary>Prefixes, after normalisation — <c>cardNumber</c>, <c>cardholder</c>.</summary>
    private static readonly string[] Prefixes = ["card"];

    /// <summary>Names that mean a credential in a query string and nothing much anywhere
    /// else.</summary>
    /// <summary><c>state</c> is deliberately absent: an OAuth state parameter is a CSRF nonce,
    /// not a credential, and it is frequently the thing you are trying to trace.</summary>
    private static readonly string[] QueryOnly = ["code", "key", "sig", "signature", "ticket"];

    private readonly HashSet<string> _exact;
    private readonly HashSet<string> _queryOnly;

    public SecretKeyMatcher(IReadOnlyCollection<string> extraKeys)
    {
        _exact = new HashSet<string>(Exact, StringComparer.Ordinal);
        foreach (var key in extraKeys)
        {
            var normalised = Normalise(key);
            if (normalised.Length > 0) _exact.Add(normalised);
        }

        _queryOnly = new HashSet<string>(QueryOnly, StringComparer.Ordinal);
    }

    /// <summary>True for a JSON property, form field, or cookie name that names a secret.</summary>
    public bool IsSecret(string key)
    {
        var name = Normalise(key);
        if (name.Length == 0) return false;
        if (_exact.Contains(name)) return true;
        foreach (var fragment in Contains)
        {
            if (name.Contains(fragment, StringComparison.Ordinal)) return true;
        }

        foreach (var prefix in Prefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>As <see cref="IsSecret"/>, plus the names that only carry credentials in a
    /// query string.</summary>
    public bool IsSecretQueryKey(string key)
        => IsSecret(key) || _queryOnly.Contains(Normalise(key));

    private static string Normalise(string key)
    {
        Span<char> buffer = key.Length <= 128 ? stackalloc char[key.Length] : new char[key.Length];
        var length = 0;
        foreach (var c in key)
        {
            if (c is '_' or '-' or '.') continue;
            buffer[length++] = char.ToLowerInvariant(c);
        }

        return new string(buffer[..length]);
    }
}
