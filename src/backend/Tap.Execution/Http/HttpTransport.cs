using System.Diagnostics;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Tap.Execution.Contracts;
using Tap.Workspace.Rendering;

namespace Tap.Execution.Http;

public static class HttpTransport
{
    public static HttpClient CreateClient(ResolvedRequest request)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        };
        if (request.Transport.IgnoreTlsErrors == true)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static CancellationTokenSource CreateTimeout(ResolvedRequest request, CancellationToken ct)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (request.Transport.TimeoutMs is > 0)
            linked.CancelAfter(request.Transport.TimeoutMs.Value);
        return linked;
    }

    /// <summary>
    /// Open a TLS connection purely to look at it. The validation callback always returns
    /// <c>true</c>: the point is to complete the handshake even when the certificate is bad, so
    /// the report can say *which* certificate is bad and what the negotiated protocol was —
    /// something a failed send can never tell you, because it aborts at the first fault.
    /// </summary>
    public static async Task<TlsDiagnosisDto> DiagnoseAsync(Uri uri, CancellationToken ct)
    {
        var port = uri.Port > 0 ? uri.Port : 443;
        if (uri.Scheme is not ("https" or "wss"))
        {
            return new TlsDiagnosisDto(uri.ToString(), false, "TLS diagnosis requires an HTTPS URL.", [], [],
                Host: uri.DnsSafeHost, Port: port);
        }

        var policy = SslPolicyErrors.None;
        var certificates = new List<TlsCertificateDto>();
        var statuses = new List<TlsStatusDto>();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync(uri.DnsSafeHost, port, ct).ConfigureAwait(false);
            await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false,
                (_, certificate, chain, policyErrors) =>
                {
                    policy = policyErrors;
                    if (chain is not null)
                    {
                        var index = 0;
                        foreach (var element in chain.ChainElements.Cast<X509ChainElement>())
                            certificates.Add(Describe(element.Certificate, index++, Statuses(element.ChainElementStatus)));
                        statuses.AddRange(Statuses(chain.ChainStatus));
                    }
                    else if (certificate is not null)
                    {
                        // Copy rather than cast: the handle belongs to the platform, and this
                        // callback has no business disposing it.
                        using var leaf = new X509Certificate2(certificate);
                        certificates.Add(Describe(leaf, 0, []));
                    }
                    return true;
                });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = uri.DnsSafeHost,
            }, ct).ConfigureAwait(false);
            stopwatch.Stop();

            var valid = policy == SslPolicyErrors.None && statuses.Count == 0;
            return new TlsDiagnosisDto(
                uri.ToString(), valid, null, certificates, statuses,
                Host: uri.DnsSafeHost,
                Port: port,
                Protocol: DescribeProtocol(ssl.SslProtocol),
                CipherSuite: ssl.NegotiatedCipherSuite == default ? null : ssl.NegotiatedCipherSuite.ToString(),
                Checks: BuildChecks(uri, policy, certificates, statuses, handshakeCompleted: true),
                HandshakeMs: stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or System.Net.Sockets.SocketException)
        {
            stopwatch.Stop();
            return new TlsDiagnosisDto(
                uri.ToString(), false, DescribeException(ex), certificates, statuses,
                Host: uri.DnsSafeHost,
                Port: port,
                Checks: BuildChecks(uri, policy, certificates, statuses, handshakeCompleted: false),
                HandshakeMs: stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// The named verdicts, in the order a reader wants them: can we talk to it at all, is it the
    /// host we asked for, is it in date, does it chain to a root we trust, is it revoked. Each is
    /// derived from the chain flags rather than re-implemented — the platform already decided,
    /// this only translates.
    /// </summary>
    private static IReadOnlyList<TlsCheckDto> BuildChecks(
        Uri uri,
        SslPolicyErrors policy,
        IReadOnlyList<TlsCertificateDto> certificates,
        IReadOnlyList<TlsStatusDto> chainStatus,
        bool handshakeCompleted)
    {
        var checks = new List<TlsCheckDto>
        {
            handshakeCompleted
                ? new TlsCheckDto("handshake", "TLS handshake", "ok", $"Completed with {uri.DnsSafeHost}.")
                : new TlsCheckDto("handshake", "TLS handshake", "fail", "The connection never got far enough to present a certificate."),
        };

        if (!handshakeCompleted && certificates.Count == 0) return checks;

        var codes = chainStatus.Select(s => s.Code).ToHashSet(StringComparer.Ordinal);
        var leaf = certificates.Count > 0 ? certificates[0] : null;

        // Hostname: the platform already ran the match (wildcards, SANs, the lot), so the
        // policy flag is the answer — re-deriving it here would only be a second opinion.
        checks.Add(policy.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)
            ? new TlsCheckDto("hostname", "Hostname match", "fail", $"The certificate is not valid for {uri.DnsSafeHost}.")
            : new TlsCheckDto("hostname", "Hostname match", "ok", $"Valid for {uri.DnsSafeHost}."));

        if (leaf is not null)
        {
            var now = DateTime.Now;
            if (now > leaf.NotAfter)
                checks.Add(new TlsCheckDto("expiry", "Validity period", "fail", $"Expired on {leaf.NotAfter:d}."));
            else if (now < leaf.NotBefore)
                checks.Add(new TlsCheckDto("expiry", "Validity period", "fail", $"Not valid until {leaf.NotBefore:d}."));
            else if ((leaf.NotAfter - now).TotalDays <= 14)
                checks.Add(new TlsCheckDto("expiry", "Validity period", "warn", $"Expires on {leaf.NotAfter:d}."));
            else
                checks.Add(new TlsCheckDto("expiry", "Validity period", "ok", $"Valid until {leaf.NotAfter:d}."));
        }

        var trustFaults = codes.Where(c => TrustFaults.ContainsKey(c)).ToList();
        if (trustFaults.Count > 0)
            checks.Add(new TlsCheckDto("trust", "Chain of trust", "fail", string.Join(" ", trustFaults.Select(f => TrustFaults[f]))));
        else if (certificates.Count > 0)
            checks.Add(new TlsCheckDto("trust", "Chain of trust", "ok",
                $"{certificates.Count} certificate{(certificates.Count == 1 ? "" : "s")} presented, chaining to {certificates[^1].CommonName ?? certificates[^1].Subject}."));

        if (codes.Contains("Revoked"))
            checks.Add(new TlsCheckDto("revocation", "Revocation", "fail", "The certificate has been revoked."));
        else if (codes.Contains("RevocationStatusUnknown") || codes.Contains("OfflineRevocation"))
            checks.Add(new TlsCheckDto("revocation", "Revocation", "unknown", "Revocation status could not be checked."));

        return checks;
    }

    /// <summary>Chain flags that mean "this doesn't lead anywhere I trust", each with the
    /// sentence that says so — the flag name alone is not a finding a reader can act on.</summary>
    private static readonly Dictionary<string, string> TrustFaults = new(StringComparer.Ordinal)
    {
        ["UntrustedRoot"] = "The chain ends at a root this machine doesn't trust.",
        ["PartialChain"] = "The server didn't send the intermediates needed to reach a trusted root.",
        ["NotSignatureValid"] = "A certificate in the chain isn't correctly signed by its issuer.",
        ["InvalidBasicConstraints"] = "A certificate in the chain is not permitted to sign the one below it.",
        ["NotValidForUsage"] = "The certificate is not authorized for server authentication.",
        ["CtlNotSignatureValid"] = "The trust list backing this chain is not correctly signed.",
        ["ExplicitDistrust"] = "This certificate has been explicitly distrusted on this machine.",
    };

    private static IReadOnlyList<TlsStatusDto> Statuses(IEnumerable<X509ChainStatus> status) =>
        status.Where(s => s.Status != X509ChainStatusFlags.NoError)
            .Select(s => new TlsStatusDto(s.Status.ToString(), s.StatusInformation.Trim()))
            .ToList();

    private static TlsCertificateDto Describe(X509Certificate2 certificate, int index, IReadOnlyList<TlsStatusDto> errors)
    {
        var (algorithm, size) = DescribeKey(certificate);
        return new TlsCertificateDto(
            certificate.Subject,
            certificate.Issuer,
            certificate.Thumbprint,
            certificate.NotBefore,
            certificate.NotAfter,
            certificate.SerialNumber,
            Index: index,
            CommonName: NullIfBlank(certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false)),
            DnsNames: DnsNames(certificate),
            SignatureAlgorithm: NullIfBlank(certificate.SignatureAlgorithm.FriendlyName) ?? certificate.SignatureAlgorithm.Value,
            KeyAlgorithm: algorithm,
            KeySizeBits: size,
            SelfSigned: string.Equals(certificate.Subject, certificate.Issuer, StringComparison.Ordinal),
            Errors: errors,
            Pem: ExportPem(certificate));
    }

    /// <summary>The certificate as PEM. Carried on the report so saving a chain is a click
    /// rather than a second connection with <c>openssl s_client</c> — and so what lands on disk
    /// is provably the bytes this handshake saw, including the chain a broken server sent.</summary>
    private static string? ExportPem(X509Certificate2 certificate)
    {
        try
        {
            return certificate.ExportCertificatePem();
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Nothing about a certificate we can't re-encode is worth failing the report over.
            return null;
        }
    }

    /// <summary>Subject alternative names, which is where the hostnames actually live — the
    /// subject CN has been advisory for years and is empty on plenty of modern certificates.</summary>
    private static IReadOnlyList<string> DnsNames(X509Certificate2 certificate)
    {
        var extension = certificate.Extensions["2.5.29.17"];
        if (extension is null) return [];
        try
        {
            var san = extension as X509SubjectAlternativeNameExtension
                ?? new X509SubjectAlternativeNameExtension(extension.RawData, extension.Critical);
            return san.EnumerateDnsNames().ToList();
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // A malformed SAN is itself worth nothing to the reader; the rest of the card stands.
            return [];
        }
    }

    private static (string? Algorithm, int? SizeBits) DescribeKey(X509Certificate2 certificate)
    {
        try
        {
            using var rsa = certificate.GetRSAPublicKey();
            if (rsa is not null) return ("RSA", rsa.KeySize);
            using var ecdsa = certificate.GetECDsaPublicKey();
            if (ecdsa is not null) return ("ECDSA", ecdsa.KeySize);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Unreadable key — fall through to whatever the OID says it is.
        }
        return (NullIfBlank(certificate.PublicKey.Oid.FriendlyName), null);
    }

    private static string? DescribeProtocol(SslProtocols protocol) => protocol switch
    {
        SslProtocols.Tls13 => "TLS 1.3",
        SslProtocols.Tls12 => "TLS 1.2",
#pragma warning disable SYSLIB0039 // Naming an obsolete protocol is the whole point of a diagnosis.
        SslProtocols.Tls11 => "TLS 1.1",
        SslProtocols.Tls => "TLS 1.0",
#pragma warning restore SYSLIB0039
        SslProtocols.None => null,
        _ => protocol.ToString(),
    };

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public static string DescribeException(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message)) messages.Add(current.Message);
        }
        var description = string.Join(" -> ", messages.Distinct(StringComparer.Ordinal));
        if (description.Contains("bad protocol version", StringComparison.OrdinalIgnoreCase))
        {
            description += " TLS negotiation failed before certificate validation. Verify that the URL and port serve TLS (try http:// if this is a plaintext endpoint), or update the server if it only supports obsolete TLS versions.";
        }
        return description;
    }

}
