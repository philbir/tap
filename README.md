# tap

Aspire-friendly HTTP traffic inspector and Cloudflare tunnel integration.

## Packages

- **Tap.Hosting** — Aspire `AppHost` extensions: `AddCloudflaredTunnel`, `AddHttpInspector`, `WithCloudflareTunnel`, `WithInspector`.
- **Tap.Server** — standalone capture server: YARP reverse proxy + capture middleware + SSE feed + bundled React UI.

## Quick start

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var tunnel = builder.AddCloudflaredTunnel()
    .WithToken(builder.Configuration["Cloudflare:TunnelToken"]!)
    .WithHttpInspector();

builder.AddProject<Projects.Sample_Api>("api")
    .WithCloudflareTunnel(tunnel, "api-local.example.com");

builder.Build().Run();
```

The inspector UI is served at `http://localhost:5198` by default.

## Layout

```
src/
  Tap.Hosting/   Aspire integration (extensions for AppHost)
  Tap.Server/    Capture server + bundled React UI in wwwroot
ui/              Vite + React source for the inspector UI
samples/
  Sample.Api/    Tiny API to inspect
  Sample.AppHost/  Aspire host wiring tunnel + inspector + sample
```

## License

TBD.
