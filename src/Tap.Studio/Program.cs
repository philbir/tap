using Tap.Studio;

var builder = WebApplication.CreateBuilder(args);
var options = StudioOptions.FromConfiguration(builder.Configuration);
var app = StudioHost.Build(args, options);
app.Run();
