using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Tap.Cli;

public static class SpectreConsoleLoggerExtensions
{
    public static ILoggingBuilder AddSpectreConsoleLogger(this ILoggingBuilder builder)
    {
        builder.AddProvider(new SpectreLoggerProvider());
        return builder;
    }
}

internal sealed class SpectreLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new SpectreLogger(categoryName);
    public void Dispose() { }
}

internal sealed class SpectreLogger(string category) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(level)) return;
        var msg = formatter(state, exception);
        var prefix = level switch
        {
            LogLevel.Error or LogLevel.Critical => "[red]✘[/]",
            LogLevel.Warning => "[yellow]![/]",
            LogLevel.Information => "[grey]→[/]",
            _ => "[grey] [/]",
        };
        AnsiConsole.MarkupLine($"{prefix} [grey]{Markup.Escape(category.Split('.').Last())}[/] {Markup.Escape(msg)}");
        if (exception is not null)
        {
            AnsiConsole.WriteException(exception, ExceptionFormats.ShortenEverything);
        }
    }
}
