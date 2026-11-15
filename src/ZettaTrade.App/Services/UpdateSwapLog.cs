namespace ZettaTrade.App.Services;

using System;
using System.IO;
using System.Threading;
using Serilog;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// The update swap's own file log. The swap runs before the composition
/// root builds the container (the next start replaces the install
/// directory BEFORE any application code runs), so every swap fact —
/// applied, failed, rolled back, settled — writes through this single
/// static sink into the app's log directory, never to stdout. The running
/// staging service does NOT log through it: its DI-installed
/// <c>ILogger</c> writes into the main file log.
/// </summary>
internal static class UpdateSwapLog
{
    /// <summary>How many swap log files the rolling shelf keeps.</summary>
    private const int RetainedFileCountLimit = 7;

    private static int _configured;

    private static ILogger _logger = Serilog.Log.Logger;

    public static void Information(string messageTemplate, params object?[] propertyValues) =>
        Write(Serilog.Events.LogEventLevel.Information, null, messageTemplate, propertyValues);

    public static void Warning(string messageTemplate, params object?[] propertyValues) =>
        Write(Serilog.Events.LogEventLevel.Warning, null, messageTemplate, propertyValues);

    /// <summary>Warn with the exception bound (Serilog prints it under the template — the template names the fact).</summary>
    public static void Warning(Exception exception, string messageTemplate, params object?[] propertyValues) =>
        Write(Serilog.Events.LogEventLevel.Warning, exception, messageTemplate, propertyValues);

    private static void Write(
        Serilog.Events.LogEventLevel level,
        Exception? exception,
        string messageTemplate,
        params object?[] propertyValues)
    {
        EnsureConfigured();
        _logger.Write(level, exception, messageTemplate, propertyValues);
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
                path: Path.Combine(ConfigPathResolver.LogDirectory, "zettatrade-update-swap-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedFileCountLimit,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] update-swap {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
