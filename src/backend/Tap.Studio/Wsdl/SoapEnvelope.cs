using System.Text;

namespace Tap.Studio.Wsdl;

/// <summary>
/// Assembles the SOAP envelope Tap writes into a generated request.
///
/// <para><b>The layout here is not arbitrary.</b> It is byte-for-byte what the Studio's SOAP body
/// editor produces when it serializes the same parts back out
/// (<c>serializeSoapBody</c> in <c>src/ui-studio/src/editors/body-mode.ts</c>): the same prefix,
/// the same two-space steps, the same self-closing rule for an operation with no payload. That
/// makes opening a generated request and saving it a no-op in the diff — which is the difference
/// between an import you can trust and one that rewrites every file the first time you look at
/// it.</para>
/// </summary>
public static class SoapEnvelope
{
    /// <summary>The prefix bound to the envelope namespace. <c>soap</c> for both versions, because
    /// that is the editor's default and a mismatch would show up as a diff on first save.</summary>
    public const string Prefix = "soap";

    private const string Indent = "  ";

    public static string NamespaceFor(SoapVersion version)
        => version == SoapVersion.Soap12 ? WsdlNs.Envelope12 : WsdlNs.Envelope11;

    /// <summary>
    /// The <c>Content-Type</c> for a request against this binding. SOAP 1.2 replaced the
    /// <c>SOAPAction</c> header with an <c>action</c> parameter on the media type, so the action
    /// has to reach one place or the other depending on the version — sending it in both, or in
    /// neither, is how a service answers with a blank 500.
    /// </summary>
    public static string ContentType(SoapVersion version, string? soapAction)
    {
        if (version == SoapVersion.Soap11) return "text/xml; charset=utf-8";
        return soapAction is { Length: > 0 } action
            ? $"application/soap+xml; charset=utf-8; action=\"{Escape(action)}\""
            : "application/soap+xml; charset=utf-8";
    }

    /// <summary>
    /// The <c>SOAPAction</c> header value, or null when the request must not carry one. Always
    /// quoted: an unquoted value is a protocol error, and <c>""</c> is a meaningful action that
    /// several stacks distinguish from an absent header.
    /// </summary>
    public static string? SoapActionHeader(SoapVersion version, string? soapAction)
        => version == SoapVersion.Soap11 && soapAction is not null ? $"\"{Escape(soapAction)}\"" : null;

    /// <summary>
    /// A WS-Security <c>UsernameToken</c> header with the credentials left as Tap variables.
    ///
    /// <para>The one header Tap authors, because it is the one nearly every legacy SOAP service
    /// asks for and the one that is otherwise a fiddly copy-paste. Everything else in
    /// WS-Security — timestamps, signatures, encryption — is the user's to add; the body editor
    /// keeps a hand-written header verbatim.</para>
    /// </summary>
    public static string UsernameTokenHeader(SoapVersion version)
    {
        // mustUnderstand is a boolean whose lexical form differs between the versions: SOAP 1.1
        // predates xs:boolean here and wants 1/0.
        var mustUnderstand = version == SoapVersion.Soap12 ? "true" : "1";

        // Three '$' so that `{{wsseUsername}}` is literal text and `{{{Prefix}}}` interpolates —
        // this block has to contain Tap's own doubled-brace variable syntax verbatim.
        return $$$"""
        <{{{Prefix}}}:Header>
          <wsse:Security {{{Prefix}}}:mustUnderstand="{{{mustUnderstand}}}" xmlns:wsse="{{{WsdlNs.WsSecurityExt}}}">
            <wsse:UsernameToken>
              <wsse:Username>{{wsseUsername}}</wsse:Username>
              <wsse:Password Type="{{{UsernameTokenPasswordText}}}">{{wssePassword}}</wsse:Password>
            </wsse:UsernameToken>
          </wsse:Security>
        </{{{Prefix}}}:Header>
        """;
    }

    private const string UsernameTokenPasswordText =
        "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordText";

    /// <summary>Variables the UsernameToken header refers to. Declared on the collection so the
    /// credentials are entered once rather than on every generated request.</summary>
    public const string UsernameVariable = "wsseUsername";
    public const string PasswordVariable = "wssePassword";

    /// <summary>
    /// The finished envelope. <paramref name="header"/> is a whole <c>&lt;soap:Header&gt;</c>
    /// block or null.
    /// </summary>
    public static string Build(
        SoapVersion version, string operation, string? namespaceUri, string payload, string? header)
    {
        var lines = new List<string>
        {
            $"<{Prefix}:Envelope xmlns:{Prefix}=\"{NamespaceFor(version)}\">",
        };

        if (header is { Length: > 0 } && header.Trim().Length > 0)
            lines.Add(IndentBlock(header.Trim(), 1));

        lines.Add($"{Indent}<{Prefix}:Body>");

        var name = operation.Trim();
        var body = payload.Trim();

        if (name.Length > 0)
        {
            var attributes = namespaceUri is { Length: > 0 } ns ? $" xmlns=\"{Escape(ns)}\"" : string.Empty;
            if (body.Length > 0)
            {
                lines.Add($"{Indent}{Indent}<{name}{attributes}>");
                lines.Add(IndentBlock(body, 3));
                lines.Add($"{Indent}{Indent}</{name}>");
            }
            else
            {
                lines.Add($"{Indent}{Indent}<{name}{attributes} />");
            }
        }
        else if (body.Length > 0)
        {
            lines.Add(IndentBlock(body, 2));
        }

        lines.Add($"{Indent}</{Prefix}:Body>");
        lines.Add($"</{Prefix}:Envelope>");

        return string.Join("\n", lines);
    }

    /// <summary>Shifts a block right without touching its internal shape — the payload arrives
    /// flush-left with its own nesting already in it, and re-indenting each line individually
    /// would flatten it.</summary>
    private static string IndentBlock(string xml, int levels)
    {
        var pad = string.Concat(Enumerable.Repeat(Indent, levels));
        return string.Join("\n", xml.Split('\n').Select(l => l.Trim().Length == 0 ? string.Empty : pad + l));
    }

    private static string Escape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
