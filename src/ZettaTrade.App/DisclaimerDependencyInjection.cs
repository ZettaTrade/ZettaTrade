namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// DI wiring for the first-run disclaimer flow. The view model is transient:
/// every dialog open (the first run or the Help menu re-show) starts fresh,
/// and the stateless acknowledge write never carries a leftover from the
/// previous open. The version line reads the process' display version
/// (T-153: the About dialog is the version's one-action home).
/// </summary>
public static class DisclaimerDependencyInjection
{
    /// <summary>Register the disclaimer dialog view model.</summary>
    public static IServiceCollection AddDisclaimerServices(this IServiceCollection services) => services
        .AddTransient<DisclaimerDialogViewModel>(sp => new DisclaimerDialogViewModel(
            sp.GetRequiredService<ISettingsService>(),
            AppVersion.Current,
            sp.GetRequiredService<ILogger<DisclaimerDialogViewModel>>()));
}
