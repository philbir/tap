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
        .WithExample(["test", "tests/demo-smoke.test.md", "--env", "ci"])
        .WithExample(["test", "Checkout", "--var", "customer=cus_ci", "--var", "sku=ABC-1"])
        .WithExample(["test", "Demo API smoke", "--output", "junit", "--output-file", "results.xml"])
        .WithExample(["test", "--list"]);

    cfg.AddCommand<SendCommand>("send")
        .WithDescription("Send one request and evaluate its assertions.")
        .WithExample(["send", "GET /demo/methods"])
        .WithExample(["send", "collections/demo/methods/01-get.req.md", "--verbose"]);

    cfg.AddCommand<LintCommand>("lint")
        .WithDescription("Parse every file in the workspace and report what doesn't load.")
        .WithExample(["lint"])
        .WithExample(["lint", "--workspace", "./.tap"]);

    cfg.AddCommand<VarsCommand>("vars")
        .WithDescription("Print the resolved variable cascade. Secret values are masked.")
        .WithExample(["vars"])
        .WithExample(["vars", "--env", "prod", "--request", "collections/demo/methods/01-get.req.md"]);
});

return await app.RunAsync(args);
