namespace ZettaTrade.App.Tests.Services;

using System;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using ZettaTrade.App.Services;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// One settings-port kit (T-100) over a real store in a fresh per-test temp
/// directory: the concrete settings service, the port over it and the
/// directory both die together on dispose (the watcher dies with them).
/// </summary>
internal sealed class SettingsPortKit : IDisposable
{
    /// <summary>Create a fresh kit: a temp directory, the store's file inside it, the port over the store.</summary>
    public SettingsPortKit()
    {
        Directory = Path.Combine(Path.GetTempPath(), "t100-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Settings = new SettingsService(
            Path.Combine(Directory, "settings.json"),
            NullLogger<SettingsService>.Instance);
        Port = new SettingsPortService(Settings, NullLogger<SettingsPortService>.Instance);
    }

    /// <summary>The directory the store's file lives in (removed on dispose).</summary>
    public string Directory { get; }

    /// <summary>The concrete store the port rides (the batch/snapshot primitives are its members).</summary>
    public SettingsService Settings { get; }

    /// <summary>The port under test.</summary>
    public SettingsPortService Port { get; }

    /// <summary>Stop the store's watcher and remove the temp directory with every file in it.</summary>
    public void Dispose()
    {
        Settings.Dispose();
        System.IO.Directory.Delete(Directory, recursive: true);
    }
}
