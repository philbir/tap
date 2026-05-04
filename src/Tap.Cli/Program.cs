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
        .WithDescription("Save a named tunnel profile to %APPDATA%/tap/tunnels (or ~/.config/tap/tunnels).")
        .WithExample(["save", "my-api", "http://localhost:3000", "--quick"])
        .WithExample(["save", "prod", "http://localhost:3000", "--api-managed", "tap-prod", "--account", "<ID>", "--api-token", "<TOK>"]);

    cfg.AddCommand<RmCommand>("rm")
        .WithDescription("Delete a saved tunnel profile.")
        .WithExample(["rm", "my-api"]);

    cfg.AddCommand<InstallCloudflaredCommand>("install-cloudflared")
        .WithDescription("Install the cloudflared binary via the host's package manager.");
});

return await app.RunAsync(args);
