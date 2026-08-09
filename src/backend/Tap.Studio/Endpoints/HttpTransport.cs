using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Tap.Studio.Contracts;
using Tap.Workspace.Rendering;

namespace Tap.Studio.Endpoints;

internal static class HttpTransport
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

    public static async Task<TlsDiagnosisDto> DiagnoseAsync(Uri uri, CancellationToken ct)
    {
        if (uri.Scheme is not ("https" or "wss"))
            return new TlsDiagnosisDto(uri.ToString(), false, "TLS diagnosis requires an HTTPS URL.", [], []);

        var errors = SslPolicyErrors.None;
        var certificates = new List<TlsCertificateDto>();
        var statuses = new List<string>();
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync(uri.DnsSafeHost, uri.Port > 0 ? uri.Port : 443, ct).ConfigureAwait(false);
            await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false,
                (_, certificate, chain, policyErrors) =>
                {
                    errors = policyErrors;
                    if (chain is not null)
                    {
                        foreach (var element in chain.ChainElements.Cast<X509ChainElement>())
                        {
                            certificates.Add(new TlsCertificateDto(
                                element.Certificate.Subject,
                                element.Certificate.Issuer,
                                element.Certificate.Thumbprint,
                                element.Certificate.NotBefore,
                                element.Certificate.NotAfter,
                                element.Certificate.SerialNumber));
                        }
                        statuses.AddRange(chain.ChainStatus.Where(s => s.Status != X509ChainStatusFlags.NoError)
                            .Select(s => $"{s.Status}: {s.StatusInformation.Trim()}"));
                    }
                    else if (certificate is not null)
                    {
                        using var leaf = new X509Certificate2(certificate);
                        certificates.Add(new TlsCertificateDto(
                            leaf.Subject, leaf.Issuer, leaf.Thumbprint, leaf.NotBefore, leaf.NotAfter, leaf.SerialNumber));
                    }
                    return true;
                });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = uri.DnsSafeHost,
            }, ct).ConfigureAwait(false);

            if (errors != SslPolicyErrors.None) statuses.Add($"Policy: {errors}");
            return new TlsDiagnosisDto(uri.ToString(), errors == SslPolicyErrors.None && statuses.Count == 0, null, certificates, statuses);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or System.Net.Sockets.SocketException)
        {
            return new TlsDiagnosisDto(uri.ToString(), false, DescribeException(ex), certificates, statuses);
        }
    }

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