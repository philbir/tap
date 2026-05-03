var builder = DistributedApplication.CreateBuilder(args);

// Standalone inspector (no Cloudflare tunnel) — point any service through it via .WithInspector(inspector).
var inspector = builder.AddHttpInspector<Projects.Tap_Server>();

builder.AddProject<Projects.Sample_Api>("api")
    .WithInspector(inspector);

// Optional: if a Cloudflare tunnel token is configured, also wire up a tunnel with inspector attached.
//
//   dotnet user-secrets set Cloudflare:TunnelToken "<token>"
//
var tunnelToken = builder.Configuration["Cloudflare:TunnelToken"];
if (!string.IsNullOrWhiteSpace(tunnelToken))
{
    var tunnel = builder.AddCloudflaredTunnel()
        .WithToken(tunnelToken)
        .WithHttpInspector<Projects.Tap_Server>();

    builder.AddProject<Projects.Sample_Api>("api-tunneled")
        .WithCloudflareTunnel(tunnel, builder.Configuration["Cloudflare:Hostnames:Api"] ?? "api-local.example.com");
}

builder.Build().Run();
