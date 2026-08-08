using Tap.Studio;

try
{
    var builder = WebApplication.CreateBuilder(args);
    var options = StudioOptions.FromConfiguration(builder.Configuration);
    var app = StudioHost.Build(args, options);
    app.Run();
}
catch (Exception ex)
{
    // A desktop shell only sees "the sidecar exited" and an exit code, so hand it the
    // message before the runtime dumps the stack to stderr. Rethrown either way: the
    // exit code and the stack trace are what a non-desktop host relies on.
    StartupSignal.Error("host", $"{ex.GetType().Name}: {ex.Message}");
    throw;
}
