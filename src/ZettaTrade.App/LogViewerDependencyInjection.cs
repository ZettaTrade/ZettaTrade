namespace ZettaTrade.App;

using Livery.Localization;
using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.App.Views;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// DI wiring for the log viewer and the global crash guard (T-101). The
/// tail service is a process-wide singleton (one bounded redacted ring
/// across every viewer open, warmed by the startup walk's
/// IAsyncInitializable pass); the crash guard is a singleton attached
/// exactly once right after the container builds. The viewer's view model
/// is transient: every open seeds fresh from the tail's history snapshot.
/// </summary>
public static class LogViewerDependencyInjection
{
    /// <summary>Register the log tail, the crash guard and the viewer's view model.</summary>
    public static IServiceCollection AddLogViewerServices(this IServiceCollection services) => services
        .AddSingleton<LogTailService>()
        .AddSingleton<CrashReportComposer>(sp => new CrashReportComposer(
            sp.GetRequiredService<ILocalizationManager>(),
            ConfigPathResolver.LogDirectory))
        .AddSingleton<CrashGuard>()
        .AddSingleton<ICrashDialogPresenter, CrashDialogPresenter>()
        .AddTransient<LogViewerViewModel>();
}
