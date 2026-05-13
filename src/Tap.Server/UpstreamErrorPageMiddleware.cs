using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using Yarp.ReverseProxy.Forwarder;

namespace Tap.Server;

public sealed class UpstreamErrorPageMiddleware
{
    private readonly RequestDelegate _next;
    private readonly InspectorIngressEntry[] _ingress;
    private readonly ILogger<UpstreamErrorPageMiddleware> _logger;

    public UpstreamErrorPageMiddleware(
        RequestDelegate next,
        InspectorIngressEntry[] ingress,
        ILogger<UpstreamErrorPageMiddleware> logger)
    {
        _next = next;
        _ingress = ingress;
        _logger = logger;
    }

    private static readonly HashSet<int> ProxyFailureStatusCodes =
    [
        StatusCodes.Status502BadGateway,
        StatusCodes.Status503ServiceUnavailable,
        StatusCodes.Status504GatewayTimeout,
    ];

    public async Task InvokeAsync(HttpContext ctx)
    {
        await _next(ctx);

        var forwarderError = ctx.GetForwarderErrorFeature();
        if (forwarderError is null ||
            !ProxyFailureStatusCodes.Contains(ctx.Response.StatusCode) ||
            ctx.Response.HasStarted)
        {
            return;
        }

        if (ctx.Response.Body is CapturingResponseStream capture &&
            !capture.TryResetBufferedResponse())
        {
            return;
        }

        var ingress = ResolveIngress(ctx.Request.Host.Host);
        var upstream = ingress?.Upstream ?? "unknown upstream";
        var statusCode = ctx.Response.StatusCode;
        var statusTitle = ReasonPhrases.GetReasonPhrase(statusCode);
        var errorKind = forwarderError.Error.ToString();
        var errorMessage = forwarderError.Exception?.GetBaseException().Message ?? errorKind;

        _logger.LogWarning(
            "Proxy failure: {Method} {Path} -> {StatusCode} {Error}: {Message}",
            ctx.Request.Method,
            ctx.Request.Path.Value ?? "/",
            statusCode,
            errorKind,
            errorMessage);

        ctx.Response.Headers.Clear();
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";

        await ctx.Response.WriteAsync(Render(statusCode, statusTitle, errorKind, ingress, upstream), ctx.RequestAborted);
    }

    private InspectorIngressEntry? ResolveIngress(string requestHost)
    {
        InspectorIngressEntry? fallback = null;

        foreach (var entry in _ingress)
        {
            if (string.IsNullOrWhiteSpace(entry.Hostname))
            {
                fallback ??= entry;
                continue;
            }

            if (string.Equals(entry.Hostname, requestHost, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return fallback;
    }

    private static string Render(
        int statusCode,
        string statusTitle,
        string errorKind,
        InspectorIngressEntry? ingress,
        string upstream)
    {
        var safeStatusTitle = WebUtility.HtmlEncode(statusTitle);
        var safeUpstream = WebUtility.HtmlEncode(upstream);
        var safeErrorKind = WebUtility.HtmlEncode(errorKind);
        var upstreamLink = RenderUpstreamLink(upstream);
        var flow = RenderFlow(ingress, safeUpstream);

        return $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>{{statusCode}} {{safeStatusTitle}} - Tap upstream unavailable</title>
  <style>
    :root {
      color-scheme: light;
      --ink: #17142b;
      --muted: #655f83;
      --line: rgba(111, 92, 202, 0.22);
      --panel: rgba(255, 255, 255, 0.78);
      --violet: #7057e9;
      --cyan: #18c5d4;
      --green: #81c783;
      --orange: #ff8a1f;
      font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
    }

    * { box-sizing: border-box; }

    body {
      min-height: 100vh;
      margin: 0;
      display: grid;
      place-items: center;
      padding: 32px;
      color: var(--ink);
      background:
        radial-gradient(circle at 12% 18%, rgba(255, 255, 255, 0.95), transparent 22%),
        radial-gradient(circle at 78% 18%, rgba(139, 119, 236, 0.24), transparent 28%),
        radial-gradient(circle at 10% 88%, rgba(112, 190, 133, 0.28), transparent 24%),
        linear-gradient(145deg, #fbf7ff 0%, #f2ecff 46%, #f7fbf5 100%);
    }

    main {
      width: min(1320px, 100%);
      display: grid;
      grid-template-columns: minmax(300px, 0.8fr) minmax(620px, 1.2fr);
      gap: 30px 42px;
      align-items: center;
    }

    .status {
      font-size: clamp(92px, 16vw, 178px);
      line-height: 0.86;
      font-weight: 780;
      letter-spacing: 0;
      color: var(--violet);
      text-shadow: 0 18px 48px rgba(93, 68, 203, 0.22);
      margin: 0 0 22px;
    }

    h1 {
      margin: 0 0 12px;
      font-size: clamp(34px, 5vw, 62px);
      line-height: 1;
      letter-spacing: 0;
    }

    p {
      margin: 0;
      color: var(--muted);
      font-size: 18px;
      line-height: 1.55;
      max-width: 58ch;
    }

    .upstream {
      display: inline-block;
      margin-top: 18px;
      padding: 10px 12px;
      max-width: 100%;
      overflow-wrap: anywhere;
      border: 1px solid var(--line);
      border-radius: 8px;
      background: rgba(255, 255, 255, 0.56);
      color: #332b68;
      font-family: "SFMono-Regular", Consolas, "Liberation Mono", monospace;
      font-size: 15px;
    }

    .hint {
      margin-top: 14px;
      font-size: 15px;
    }

    .hint a {
      color: var(--violet);
      font-weight: 700;
      text-decoration: none;
    }

    .hint a:hover { text-decoration: underline; }

    .art {
      min-height: 410px;
      padding: 20px;
      border: 1px solid rgba(255, 255, 255, 0.72);
      border-radius: 8px;
      background: var(--panel);
      box-shadow: 0 24px 80px rgba(72, 58, 149, 0.18);
      backdrop-filter: blur(18px);
    }

    .art img {
      display: block;
      width: 100%;
      height: auto;
      border-radius: 6px;
    }

    .flow {
      grid-column: 1 / -1;
      display: flex;
      gap: 6px;
      align-items: center;
      width: 100%;
    }

    .node {
      flex: 1 1 0;
      min-width: 0;
      padding: 11px 8px;
      border: 1px solid var(--line);
      border-radius: 8px;
      background: rgba(255, 255, 255, 0.7);
      text-align: center;
      box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.9);
    }

    .node-status {
      display: inline-block;
      width: 8px;
      height: 8px;
      margin-bottom: 6px;
      border-radius: 999px;
      background: var(--green);
      box-shadow: 0 0 0 4px rgba(129, 199, 131, 0.16);
    }

    .node-status.bad {
      background: var(--orange);
      box-shadow: 0 0 0 4px rgba(255, 138, 31, 0.16);
    }

    .node strong {
      display: block;
      font-size: 13px;
      line-height: 1.2;
    }

    .node span {
      display: block;
      margin-top: 4px;
      overflow: hidden;
      color: var(--muted);
      font-family: "SFMono-Regular", Consolas, "Liberation Mono", monospace;
      font-size: 10px;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .arrow {
      flex: 0 0 28px;
      color: var(--violet);
      align-self: center;
      font-family: "SFMono-Regular", Consolas, "Liberation Mono", monospace;
      font-size: 13px;
      font-weight: 700;
      text-align: center;
    }

    .meta {
      margin-top: 14px;
      color: var(--muted);
      font-size: 12px;
      text-align: center;
    }

    @media (max-width: 760px) {
      body { padding: 22px; place-items: start center; }
      main { grid-template-columns: 1fr; gap: 28px; }
      .art { min-height: 0; padding: 14px; }
      .flow { flex-direction: column; }
      .node { width: 100%; flex-basis: auto; }
      .arrow { transform: rotate(90deg); justify-self: center; width: 28px; }
    }
  </style>
</head>
<body>
  <main>
    {{flow}}
    <section>
      <div class="status">{{statusCode}}</div>
      <h1>Broken tap here</h1>
      <p>Tap could receive the request, but the proxy could not reach the upstream application.</p>
      <div class="upstream">{{safeUpstream}}</div>
      {{upstreamLink}}
    </section>
    <section class="art" aria-label="Tap upstream failure diagram">
      <img src="/tap-error-broken.png" alt="A translucent Tap pipe cracked before reaching an upstream application">
      <div class="meta">YARP forwarding failed: {{safeErrorKind}}</div>
    </section>
  </main>
</body>
</html>
""";
    }

    private static string RenderUpstreamLink(string upstream)
    {
        if (!Uri.TryCreate(upstream, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return string.Empty;
        }

        var safeHref = WebUtility.HtmlEncode(uri.ToString());
        return $"""
      <p class="hint">Try the upstream directly: <a href="{safeHref}" target="_blank" rel="noreferrer">{safeHref}</a></p>
""";
    }

    private static string RenderFlow(InspectorIngressEntry? ingress, string safeUpstream)
    {
        if (!HasTunnel(ingress))
        {
            return $$"""
      <div class="flow">
        {{RenderNode("You", "browser", isBad: false)}}
        <div class="arrow">--&gt;</div>
        {{RenderNode("Tap", "proxy", isBad: false)}}
        <div class="arrow">--&gt;</div>
        {{RenderNode("Upstream", safeUpstream, isBad: true)}}
      </div>
""";
        }

        if (IsTailscale(ingress!.TunnelMode))
        {
            var ts = WebUtility.HtmlEncode(TailscaleEdgeSub(ingress));
            var daemonSub = WebUtility.HtmlEncode(TailscaledSub(ingress.TunnelMode));
            // Funnel = public, Serve = tailnet-only. We use PublicExpose to label the edge.
            var edgeLabel = ingress.PublicExpose ? "Tailscale Funnel" : "Tailscale Serve";
            return $$"""
      <div class="flow">
        {{RenderNode("You", "browser", isBad: false)}}
        <div class="arrow">--&gt;</div>
        {{RenderNode(edgeLabel, ts, isBad: false)}}
        <div class="arrow">--&gt;</div>
        {{RenderNode("tailscaled", daemonSub, isBad: false)}}
        <div class="arrow">--&gt;</div>
        {{RenderNode("Tap inspector", "proxy", isBad: false)}}
        <div class="arrow">--&gt;</div>
        {{RenderNode("Upstream", safeUpstream, isBad: true)}}
      </div>
""";
        }

        var cloudflareSub = WebUtility.HtmlEncode(CloudflareSub(ingress));
        var cloudflaredSub = WebUtility.HtmlEncode(CloudflaredSub(ingress.TunnelMode));

        return $$"""
      <div class="flow">
        {{RenderNode("You", "browser", isBad: false)}}
        <div class="arrow">--&gt;</div>
        {{RenderNode("Cloudflare", cloudflareSub, isBad: false)}}
        <div class="arrow">--&gt;</div>
        {{RenderNode("cloudflared", cloudflaredSub, isBad: false)}}
        <div class="arrow">--&gt;</div>
        {{RenderNode("Tap inspector", "proxy", isBad: false)}}
        <div class="arrow">--&gt;</div>
        {{RenderNode("Upstream", safeUpstream, isBad: true)}}
      </div>
""";
    }

    private static bool IsTailscale(string? tunnelMode) =>
        string.Equals(tunnelMode, "tailscale-system", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(tunnelMode, "tailscale-ephemeral", StringComparison.OrdinalIgnoreCase);

    private static string TailscaleEdgeSub(InspectorIngressEntry ingress)
    {
        if (!string.IsNullOrWhiteSpace(ingress.PublicUrl) &&
            Uri.TryCreate(ingress.PublicUrl, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }
        return string.IsNullOrWhiteSpace(ingress.Hostname) ? "ts.net" : ingress.Hostname;
    }

    private static string TailscaledSub(string? tunnelMode) =>
        string.Equals(tunnelMode, "tailscale-ephemeral", StringComparison.OrdinalIgnoreCase)
            ? "userspace · ephemeral"
            : "system daemon";

    private static string RenderNode(string label, string sub, bool isBad)
    {
        var statusClass = isBad ? "node-status bad" : "node-status";
        return $"""<div class="node"><i class="{statusClass}" aria-hidden="true"></i><strong>{WebUtility.HtmlEncode(label)}</strong><span>{sub}</span></div>""";
    }

    private static bool HasTunnel(InspectorIngressEntry? ingress) =>
        ingress?.TunnelMode is not null &&
        !string.Equals(ingress.TunnelMode, "local", StringComparison.OrdinalIgnoreCase);

    private static string CloudflareSub(InspectorIngressEntry ingress)
    {
        if (!string.IsNullOrWhiteSpace(ingress.PublicUrl) &&
            Uri.TryCreate(ingress.PublicUrl, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return string.IsNullOrWhiteSpace(ingress.Hostname) ? "edge" : ingress.Hostname;
    }

    private static string CloudflaredSub(string? tunnelMode) =>
        tunnelMode?.ToLowerInvariant() switch
        {
            "token" => "--token ...",
            "quick" => "--url ...",
            "api-managed" or "dynamic" => "config.yml",
            _ => "connector"
        };
}
