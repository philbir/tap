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

    svg { width: 100%; height: auto; display: block; }

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
      <svg viewBox="0 0 920 430" role="img" aria-label="A broken Tap pipe between Cloudflare and upstream">
        <defs>
          <linearGradient id="pipe" x1="0" x2="1">
            <stop offset="0" stop-color="#7e70ef" stop-opacity="0.45"/>
            <stop offset="0.48" stop-color="#20d4e6" stop-opacity="0.78"/>
            <stop offset="1" stop-color="#7e70ef" stop-opacity="0.35"/>
          </linearGradient>
          <filter id="shadow" x="-20%" y="-30%" width="140%" height="160%">
            <feDropShadow dx="0" dy="18" stdDeviation="18" flood-color="#5745ba" flood-opacity="0.22"/>
          </filter>
          <filter id="hard-shadow" x="-20%" y="-20%" width="140%" height="140%">
            <feDropShadow dx="0" dy="12" stdDeviation="8" flood-color="#17142b" flood-opacity="0.18"/>
          </filter>
        </defs>
        <path d="M72 126 C90 70 138 45 193 61 C220 15 286 12 325 54 C356 48 389 66 404 97 C454 99 492 131 492 174 C492 222 452 252 402 252 L119 252 C71 252 34 218 34 173 C34 150 49 133 72 126Z" fill="#fff" opacity="0.88" filter="url(#shadow)"/>
        <path d="M658 103 C679 55 723 36 772 52 C795 17 850 15 880 54 C913 58 936 82 936 116 C936 158 900 184 856 184 L704 184 C665 184 635 158 635 123 C635 114 643 107 658 103Z" fill="#fff" opacity="0.58" filter="url(#shadow)"/>
        <g filter="url(#shadow)">
          <rect x="112" y="178" width="430" height="86" rx="43" fill="url(#pipe)" stroke="#ffffff" stroke-width="4"/>
          <rect x="152" y="199" width="318" height="9" rx="4.5" fill="#25e4ef" opacity="0.9"/>
          <rect x="188" y="227" width="236" height="9" rx="4.5" fill="#a6fff7" opacity="0.82"/>
          <circle cx="542" cy="221" r="48" fill="rgba(255,255,255,0.52)" stroke="#b6aafb" stroke-width="5"/>
          <path d="M574 190 C614 194 641 212 657 240" fill="none" stroke="url(#pipe)" stroke-width="82" stroke-linecap="round"/>
          <path d="M574 190 C614 194 641 212 657 240" fill="none" stroke="#ffffff" stroke-opacity="0.58" stroke-width="6" stroke-linecap="round"/>
          <path d="M688 259 C716 276 739 300 752 336" fill="none" stroke="url(#pipe)" stroke-width="72" stroke-linecap="round" opacity="0.82"/>
          <path d="M688 259 C716 276 739 300 752 336" fill="none" stroke="#ffffff" stroke-opacity="0.52" stroke-width="6" stroke-linecap="round"/>
          <path d="M638 222 L694 202 L667 247 L724 231 L671 304 L681 255 L626 273Z" fill="#fff8d9" stroke="#ff8a1f" stroke-width="6" stroke-linejoin="round"/>
          <path d="M615 176 L642 205 M630 169 L663 199 M640 246 L610 284 M662 254 L634 295" stroke="#ffffff" stroke-width="5" stroke-linecap="round" opacity="0.86"/>
        </g>
        <g transform="translate(672 96)">
          <rect width="210" height="170" rx="22" fill="#272060" stroke="#dcd5ff" stroke-width="7" filter="url(#shadow)"/>
          <rect x="22" y="24" width="166" height="122" rx="12" fill="#342879"/>
          <rect x="42" y="44" width="70" height="12" rx="6" fill="#24d0dc"/>
          <rect x="42" y="70" width="118" height="12" rx="6" fill="#72e4ee" opacity="0.86"/>
          <rect x="42" y="96" width="142" height="12" rx="6" fill="#8268ff"/>
          <rect x="42" y="122" width="92" height="12" rx="6" fill="#19c5d4"/>
        </g>
        <g transform="translate(82 286) rotate(-5)" filter="url(#hard-shadow)">
          <path d="M32 52 C48 33 65 33 81 52 S113 70 122 52" fill="none" stroke="#2dd4bf" stroke-linecap="round" stroke-width="18" opacity=".22"/>
          <path d="M32 52 C48 33 65 33 81 52 S113 70 122 52" fill="none" stroke="#7057e9" stroke-linecap="round" stroke-width="9"/>
          <path d="M75 22 h18 v23 h-18z" fill="#c9c2ea"/>
          <path d="M56 16 h56 v16 h-56z" fill="#ece8ff"/>
          <circle cx="56" cy="24" r="9" fill="#ffffff"/>
          <circle cx="112" cy="24" r="9" fill="#ffffff"/>
          <path d="M58 48 h52 a12 12 0 0 1 12 12 v4 a12 12 0 0 1 -12 12 h-18 v18 a7 7 0 0 1 -14 0 v-18 h-20 a12 12 0 0 1 -12 -12 v-4 a12 12 0 0 1 12 -12z" fill="#d9d4f8" stroke="#6d5ab7" stroke-width="3"/>
          <path d="M94 76 C117 82 128 96 128 120" fill="none" stroke="#2dd4bf" stroke-linecap="round" stroke-width="8"/>
          <circle cx="130" cy="124" r="5" fill="#ff8a1f"/>
        </g>
        <g transform="translate(280 286)" filter="url(#shadow)">
          <rect width="238" height="102" rx="18" fill="rgba(255,255,255,0.78)" stroke="#d8d0ff" stroke-width="3"/>
          <circle cx="32" cy="30" r="10" fill="#18b879"/>
          <circle cx="32" cy="62" r="10" fill="#18c5d4"/>
          <circle cx="32" cy="94" r="10" fill="#ff8a1f"/>
          <rect x="58" y="24" width="64" height="10" rx="5" fill="#b8acf2"/>
          <rect x="58" y="56" width="96" height="10" rx="5" fill="#b8acf2"/>
          <rect x="58" y="88" width="52" height="10" rx="5" fill="#b8acf2"/>
          <path d="M148 29 C162 20 174 40 190 29 C204 18 214 34 226 26" fill="none" stroke="#16c6d7" stroke-width="3"/>
          <path d="M148 62 C164 52 177 72 194 60 C207 50 217 68 228 57" fill="none" stroke="#7057e9" stroke-width="3"/>
          <path d="M148 94 C164 83 176 106 194 93 C208 82 218 101 228 90" fill="none" stroke="#ff8a1f" stroke-width="3"/>
        </g>
      </svg>
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

        var cloudflareSub = WebUtility.HtmlEncode(CloudflareSub(ingress!));
        var cloudflaredSub = WebUtility.HtmlEncode(CloudflaredSub(ingress!.TunnelMode));

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
