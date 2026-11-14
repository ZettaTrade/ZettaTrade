namespace ZettaTrade.Infrastructure.Tests.Plugins;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ZettaTrade.Infrastructure.Licensing;
using ZettaTrade.Infrastructure.Plugins;

/// <summary>
/// The staging helpers for the <see cref="PluginSdkHostTests"/> matrix
/// (T-131): a per-fact plugins directory staged with the fixtures
/// assemblies located from the test run's own output — a plugin is loaded
/// from the plugins directory at runtime, never referenced by the product
/// graph — and a fault-recording logger the log-side behavior stays
/// assertable through.
/// </summary>
internal sealed class PluginSdkStage : IDisposable
{
    /// <summary>A per-fact plugins directory (the loader's scan root).</summary>
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "zettatrade-sdk-tests",
        Guid.NewGuid().ToString("N"));

    /// <summary>The fault recorder the host's logger rides.</summary>
    public RecordingLoggerFactory Loggers { get; } = new();

    public PluginSdkStage()
    {
        Directory.CreateDirectory(Path);
    }

    /// <summary>Stages the fixtures assembly (the valid scenario family).</summary>
    public void StageFixtures()
    {
        File.Copy(
            typeof(ZettaTrade.Plugins.Fixtures.SamplePanelPlugin).Assembly.Location,
            System.IO.Path.Combine(Path, "ZettaTrade.Plugins.Fixtures.dll"),
            overwrite: true);
    }

    /// <summary>Stages the hazards assembly (every failure shape).</summary>
    public void StageHazards()
    {
        File.Copy(
            typeof(ZettaTrade.Plugins.Fixtures.Hazards.DuplicateIdPlugin).Assembly.Location,
            System.IO.Path.Combine(Path, "ZettaTrade.Plugins.Fixtures.Hazards.dll"),
            overwrite: true);
    }

    /// <summary>Stages a file claiming to be a plugin dll but carrying no assembly.</summary>
    public void StageGarbage()
    {
        File.WriteAllText(System.IO.Path.Combine(Path, "garbage.dll"), "not an assembly at all");
    }

    /// <summary>
    /// Builds the host the house DI shape (AddPluginSdk) resolves and runs
    /// the one load pass; the returned registry and report are the
    /// post-load state. The disposables travel back for the test's own
    /// teardown. An optional entitlement gate (T-132) rides the host's
    /// constructor seam when staged.
    /// </summary>
    public async Task<(PluginSdkHost Host, ServiceProvider Provider, PluginExtensionRegistry Registry)> RunAsync(
        ILicenseGate? licenseGate = null)
    {
        var services = new ServiceCollection();
        services.AddPluginSdk(Path);
        if (licenseGate is not null)
        {
            services.AddSingleton(licenseGate);
        }

        services.AddSingleton<ILoggerFactory>(Loggers);
        services.AddSingleton<ILogger<PluginSdkHost>>(
            new RecordingTypeLogger<PluginSdkHost>(Loggers));

        var provider = services.BuildServiceProvider();
        var host = provider.GetRequiredService<PluginSdkHost>();
        await host.InitializeAsync(CancellationToken.None);
        return (host, provider, provider.GetRequiredService<PluginExtensionRegistry>());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // The load locks the staged dlls; the temp store owns the
            // leftovers, nothing lingers in the repo tree.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>A logger factory recording faults, so the log-side behavior stays assertable.</summary>
internal sealed class RecordingLoggerFactory : ILoggerFactory
{
    /// <summary>Every (level, fault) pair the host ever logged with a fault attached.</summary>
    public List<(LogLevel Level, Exception Fault)> Faults { get; } = [];

    public ILogger CreateLogger(string categoryName) => new RecordingSink(this);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    /// <summary>One recording sink: levels pass, faults land.</summary>
    private sealed class RecordingSink(RecordingLoggerFactory owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is { } fault)
            {
                owner.Faults.Add((logLevel, fault));
            }
        }
    }
}

/// <summary>The typed logger the host's constructor injection rides (records through the factory).</summary>
internal sealed class RecordingTypeLogger<T>(RecordingLoggerFactory factory) : ILogger<T>
{
    /// <summary>The sink the calls forward to (its category comes from the type the host logs under).</summary>
    private readonly ILogger _source = factory.CreateLogger(
        typeof(T).FullName ?? typeof(T).Name);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        _source.Log(logLevel, eventId, state, exception, formatter);
}
