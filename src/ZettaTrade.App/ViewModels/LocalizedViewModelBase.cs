using ReactiveUI;
using ZettaTrade.Core.Interfaces;

namespace ZettaTrade.App.ViewModels;

/// <summary>
/// Base class for ViewModels that need localization.
/// Provides reactive subscription to language changes.
/// Override OnLanguageChanged() to update localized properties.
/// </summary>
public abstract class LocalizedViewModelBase : ReactiveObject, IActivatableViewModel
{
    public ViewModelActivator Activator { get; } = new();
    
    protected readonly ILocalizationManager Loc;

    protected LocalizedViewModelBase(ILocalizationManager localizationManager)
    {
        Loc = localizationManager;

        this.WhenActivated(disposables =>
        {
            // Subscribe to language changes with automatic disposal
            Loc.LanguageChanged
                .Subscribe(_ => OnLanguageChanged())
                .DisposeWith(disposables);

            // Initial localization
            OnLanguageChanged();
        });
    }

    /// <summary>
    /// Override this method to update localized properties when language changes.
    /// Called automatically on activation and on every language change.
    /// </summary>
    protected abstract void OnLanguageChanged();
}
