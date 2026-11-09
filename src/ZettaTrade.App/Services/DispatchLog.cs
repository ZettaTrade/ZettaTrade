namespace ZettaTrade.App.Services;

using System;
using System.IO;
using System.Threading;
using Serilog;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// The dispatch lane's own file log. The fast path runs before the
/// composition root builds the container (a redirected start never builds
/// it at all), so the two pre-container facts — the second start's settled
/// redirect and the owned start's claim acquisition — write through this
/// single static sink into the app's log directory, never to stdout.
/// The app-lifetime host does NOT log through it: the DI-installed
/// <c>ILogger</c>s write the running instance's same facts into the main
/// file log.
/// </summary>
internal static class DispatchLog
{
    /// <summary>How many dispatch log files the rolling shelf keeps.</summary>
    private const int RetainedFileCountLimit = 7;

    private static int _configured;

    private static ILogger _logger = Serilog.Log.Logger;

    public static void Information(string messageTemplate, params object?[] propertyValues) =>
        Write(Serilog.Events.LogEventLevel.Information, messageTemplate, propertyValues);

    public static void Warning(string messageTemplate, params object?[] propertyValues) =>
        Write(Serilog.Events.LogEventLevel.Warning, messageTemplate, propertyValues);

    private static void Write(Serilog.Events.LogEventLevel level, string messageTemplate, params object?[] propertyValues)
    {
        EnsureConfigured();
        _logger.Write(level, messageTemplate, propertyValues);
    }

    private static void EnsureConfigured()
    {
        if (Interlocked.Exchange(ref _configured, 1) != 0)
        {
            return;
        }

        _logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                path: Path.Combine(ConfigPathResolver.LogDirectory, "zettatrade-handoff-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedFileCountLimit,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] dispatch {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
