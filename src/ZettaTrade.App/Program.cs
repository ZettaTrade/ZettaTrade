using Avalonia;
using ReactiveUI.Avalonia;
using System;

namespace ZettaTrade.App;

/// <summary>
/// Application entry point.
/// </summary>
class Program
{
    /// <summary>
    /// Main entry point.
    /// </summary>
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    /// <summary>
    /// Build Avalonia application.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();
}
