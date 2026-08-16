using Spectre.Console.Cli;
using Tap.Studio.Cli.Commands;

var app = new CommandApp();
app.Configure(cfg =>
{
    cfg.SetApplicationName("tap-studio");
    cfg.SetApplicationVersion(Tap.Execution.ExecutionIdentity.Version);

    // Spectre renders an exception as a formatted panel by default. In a pipeline that panel
    // is noise wrapped around the one line that matters, and the exit code is what actually
    // gets read — so unexpected failures print plainly and exit 2.
    cfg.SetExceptionHandler((ex, _) =>
    {
        Console.Error.WriteLine(ex.Message);
        return Tap.Studio.Cli.ExitCode.UsageError;
    });

    cfg.AddCommand<TestCommand>("test")
        .WithDescription("Run a test set or a flow. Exits 1 when something failed, 0 when everything passed.")
        .WithExample(["test", "Demo API smoke"])
        .WithExample(["test", "tests/demo-smoke.test.tap", "--env", "ci"])
        .WithExample(["test", "Checkout", "--var", "customer=cus_ci", "--var", "sku=ABC-1"])
        .WithExample(["test", "Demo API smoke", "--output", "junit", "--output-file", "results.xml"])
        .WithExample(["test", "--list"]);

    cfg.AddCommand<SendCommand>("send")
        .WithDescription("Send one request and evaluate its assertions.")
        .WithExample(["send", "GET /demo/methods"])
        .WithExample(["send", "collections/demo/methods/01-get.req.tap", "--verbose"])
        .WithExample(["send", "GET /demo/methods", "--json"]);

    cfg.AddCommand<CallCommand>("call")
        .WithDescription("Send an ad-hoc request through a collection, inheriting its baseUrl, headers, and auth. Relative URLs only unless --allow-any-url.")
        .WithExample(["call", "GET", "/users/42", "--collection", "demo"])
        .WithExample(["call", "POST", "/things", "-c", "demo", "-H", "Content-Type: application/json", "-d", "@body.json", "--json"]);

    cfg.AddCommand<ListCommand>("list")
        .WithDescription("List what's in the workspace: collections, requests, envs, tests, auth profiles (name + type only).")
        .WithExample(["list"])
        .WithExample(["list", "requests", "--json"]);

    cfg.AddCommand<DescribeCommand>("describe")
        .WithDescription("Show one request's template surface: method, URL, headers, variables, auth (name only), assertions.")
        .WithExample(["describe", "GET /demo/methods"])
        .WithExample(["describe", "collections/demo/methods/01-get.req.tap", "--json"]);

    cfg.AddCommand<McpCommand>("mcp")
        .WithDescription("Serve the workspace to MCP clients over stdio: inventory, describe, send, call, and test as tools.")
        .WithExample(["mcp", "--workspace", "."])
        .WithExample(["mcp", "-w", "samples/sample-workspace", "--use-cached-tokens"]);

    cfg.AddBranch("agent", agent =>
    {
        agent.SetDescription("Set up AI-agent support for this workspace.");
        agent.AddCommand<AgentInitCommand>("init")
            .WithDescription("Install the agent skills and/or MCP registration for claude, codex, copilot, or opencode — per project or per user. With no --env, an interactive wizard detects and preselects the environments this machine runs.")
            .WithExample(["agent", "init"])
            .WithExample(["agent", "init", "--env", "claude"])
            .WithExample(["agent", "init", "--env", "claude", "--env", "copilot", "--scope", "project"])
            .WithExample(["agent", "init", "--env", "codex", "--scope", "user", "--mcp"]);
    });

    cfg.AddCommand<LintCommand>("lint")
        .WithDescription("Parse every file in the workspace and report what doesn't load.")
        .WithExample(["lint"])
        .WithExample(["lint", "--workspace", "./.tap"]);

    cfg.AddCommand<VarsCommand>("vars")
        .WithDescription("Print the resolved variable cascade. Secret values are masked.")
        .WithExample(["vars"])
        .WithExample(["vars", "--env", "prod", "--request", "collections/demo/methods/01-get.req.tap"]);

    cfg.AddCommand<MigrateCommand>("migrate")
        .WithDescription("Rename legacy .md workspace files to .tap and rewrite the refs that point at them.")
        .WithExample(["migrate", "--dry-run"])
        .WithExample(["migrate"]);
});

return await app.RunAsync(args);
