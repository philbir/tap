using Tap.Server;

var builder = WebApplication.CreateBuilder(args);
var options = TapInspectorHost.OptionsFromConfiguration(builder.Configuration);
var app = TapInspectorHost.Build(args, options);
app.Run();
