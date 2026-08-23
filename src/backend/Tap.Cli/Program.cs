using Spectre.Console.Cli;
using Tap.Cli.Commands;

var app = new CommandApp<RunCommand>();
app.Configure(cfg =>
{
    cfg.SetApplicationName("tap");
    cfg.SetApplicationVersion("1.0.0");

    cfg.AddCommand<RunCommand>("run")
        .WithDescription("Start the inspector and optionally a Cloudflare tunnel in front of an upstream URL. With no args, lists saved profiles.")
        .WithExample(["run"])                                     // list profiles
        .WithExample(["run", "http://localhost:3000"])
        .WithExample(["run", "http://localhost:3000", "--quick"])
        .WithExample(["run", "--name", "my-api"])                 // run saved profile
        .WithExample(["run", "http://localhost:3000", "--token", "<cf-tunnel-token>"]);

    cfg.AddCommand<SaveCommand>("save")
        .WithDescription("Save a named tunnel profile to ~/.tap/tunnels.")
        .WithExample(["save", "my-api", "http://localhost:3000", "--quick"])
        .WithExample(["save", "prod", "http://localhost:3000", "--api-managed", "tap-prod", "--account", "<ID>", "--api-token", "<TOK>"]);

    cfg.AddCommand<RmCommand>("rm")
        .WithDescription("Delete a saved tunnel profile.")
        .WithExample(["rm", "my-api"]);

    cfg.AddCommand<InstallCloudflaredCommand>("install-cloudflared")
        .WithDescription("Install the cloudflared binary via the host's package manager.");

    cfg.AddCommand<UiCommand>("ui")
        .WithDescription("Open the inspector UI in your browser to manage Cloudflare tunnel profiles. Starts a local UI server (no upstream/tunnel).")
        .WithExample(["ui"])
        .WithExample(["ui", "--ui-port", "5050"])
        .WithExample(["ui", "--no-open"]);

    cfg.AddCommand<McpCommand>("mcp")
        .WithDescription("Serve a running inspector's captured traffic to a coding agent over MCP (stdio). Finds the inspector itself; requires Inspector:Agent:Enabled on it.")
        .WithExample(["mcp"])
        .WithExample(["mcp", "--ui-port", "5298"]);

    cfg.AddCommand<DocsCommand>("docs")
        .WithDescription($"Open the tap CLI documentation ({DocsCommand.Url}) in your browser.")
        .WithExample(["docs"])
        .WithExample(["docs", "--print"]);
});

return await app.RunAsync(args);
