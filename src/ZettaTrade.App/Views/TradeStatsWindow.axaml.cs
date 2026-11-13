namespace ZettaTrade.App.Views;

using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Livery.Extensions;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using ReactiveUI.Avalonia;
using ReactiveUI.Primitives;
using Serilog;
using ZettaTrade.App.ViewModels;

/// <summary>
/// Trade statistics window. Owns the window plumbing: the view model starts
/// its paged scan once per window instance (a re-activation refocus must not
/// restart a running scan); the export save-dialogs and the trade-note
/// dialog handoff (T-211) are answered here; the Close button closes the
/// window directly. Disposing the execution subscription on window close
/// cancels an in-flight scan.
/// </summary>
public partial class TradeStatsWindow : ReactiveWindow<TradeStatsViewModel>
{
    /// <summary>Whether the one-shot activation load already ran for this window instance.</summary>
    private bool _initialLoadQueued;

    /// <summary>
    /// Create a new trade statistics window. The view model must be assigned
    /// before activation (the opener sets <c>DataContext</c>).
    /// </summary>
    public TradeStatsWindow()
    {
        InitializeComponent();

        // The registrations live in the collection-based activation scope
        // (the balances-panel pattern): the export handler registrations and
        // the scan subscription die with the window, so closing it cancels
        // an in-flight scan. The load runs once per window and errors land
        // in the Subscribe callback.
        this.WhenActivated(disposables =>
        {
            // The export dialogs (T-210): picking a file is view behavior —
            // the view model hands over the suggested name, the view asks
            // the storage provider, the confirmed path (or the cancel)
            // completes the interaction.
            ViewModel?.SavePositionsCsvRequested.RegisterHandler(async context =>
                {
                    var path = await SaveFilePicker.PickAsync(this, context.Input, "csv", "CSV");
                    context.SetOutput(path);
                })
                .DisposeWith(disposables);

            ViewModel?.SaveStatementRequested.RegisterHandler(async context =>
                {
                    var path = await SaveFilePicker.PickAsync(this, context.Input, "json", "JSON");
                    context.SetOutput(path);
                })
                .DisposeWith(disposables);

            // The trade note dialog (T-211): the annotation dialog opens over
            // this window and its applied result resolves the interaction -
            // the PaperResetDialogWindow pattern.
            ViewModel?.EditRealizedRowRequested.RegisterHandler(HandleEditRealizedRowRequestedAsync)
                .DisposeWith(disposables);

            if (_initialLoadQueued || ViewModel is null)
            {
                return;
            }

            _initialLoadQueued = true;

            ViewModel.ApplyCommand.Execute().Subscribe(
                _ => { },
                error => Log.Error(error, "Trade statistics scan failed"))
                .DisposeWith(disposables);
        });
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Show the note-and-tags dialog over this window and return the applied
    /// edit; null when the dialog closes without one (title bar X, or the
    /// Cancel button).
    /// </summary>
    private async Task HandleEditRealizedRowRequestedAsync(
        IInteractionContext<TradeRealizedRowViewModel, TradeNoteEditResult?> context)
    {
        try
        {
            var viewModel = App.Services.GetRequiredService<TradeNoteDialogViewModel>();
            viewModel.Initialize(context.Input.Note, context.Input.Tags);

            var dialog = new TradeNoteDialogWindow
            {
                DataContext = viewModel,
            };
            await dialog.ShowDialog(this);

            context.SetOutput(dialog.Applied
                ? new TradeNoteEditResult(dialog.ChosenNote, dialog.ChosenTags)
                : null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open the trade note dialog");
            context.SetOutput(null);
        }
    }
}
