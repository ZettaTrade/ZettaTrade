namespace ZettaTrade.App.ViewModels;

using ReactiveUI;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// Base class for ViewModels that need localization.
/// Provides reactive subscription to language changes.
/// Use Loc.GetObservable("Key").ToProperty(this, x => x.Property) in derived classes.
/// </summary>
public abstract class LocalizedViewModelBase : ReactiveObject, IActivatableViewModel
{
    public ViewModelActivator Activator { get; } = new();
    
    protected readonly ILocalizationManager Loc;

    protected LocalizedViewModelBase(ILocalizationManager localizationManager)
    {
        Loc = localizationManager;
    }
}
