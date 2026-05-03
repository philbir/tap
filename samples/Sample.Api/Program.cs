var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/", () => "Hello from Sample.Api");
app.MapGet("/hello/{name}", (string name) => Results.Json(new { greeting = $"Hello, {name}!" }));
app.MapPost("/echo", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync();
    return Results.Json(new { you_sent = body });
});

app.Run();
