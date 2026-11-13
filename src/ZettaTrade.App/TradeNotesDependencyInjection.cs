namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.App.ViewModels;

/// <summary>
/// DI wiring for the trade-annotation flow (T-211). The composition root
/// calls the single aggregate extension, so module-level registration
/// details stay local. The transient dialog view model lives exactly as
/// long as one annotation interaction (the PaperResetDialogViewModel
/// pattern); the journal write rides the store Infrastructure already
/// registers.
/// </summary>
public static class TradeNotesDependencyInjection
{
    /// <summary>Register the transient trade note-and-tags dialog view model.</summary>
    public static IServiceCollection AddTradeNotesServices(this IServiceCollection services) => services
        .AddTransient<TradeNoteDialogViewModel>();
}
