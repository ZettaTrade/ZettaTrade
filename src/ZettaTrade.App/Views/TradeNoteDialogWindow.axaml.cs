namespace ZettaTrade.App.Views;

using Avalonia.Interactivity;
using ReactiveUI.Avalonia;
using ZettaTrade.App.ViewModels;

/// <summary>
/// Trade note and tags dialog (T-211). Pure View: shows the note's free text
/// and the tags input over the row's stored annotations, and hands the
/// normalized result back to the opener through <see cref="ChosenNote"/> and
/// <see cref="ChosenTags"/> (both null when closed without an applied edit —
/// title bar X or the Cancel button). Button clicks stay in code-behind on
/// purpose: closing a window is View behavior.
/// </summary>
public partial class TradeNoteDialogWindow : ReactiveWindow<TradeNoteDialogViewModel>
{
    /// <summary>
    /// Create a new TradeNoteDialogWindow instance. The view model must be
    /// assigned (and initialized) before activation: the opener sets
    /// <c>DataContext</c>.
    /// </summary>
    public TradeNoteDialogWindow()
    {
        InitializeComponent();
    }

    /// <summary>The normalized note the applied edit produced, or null when closed without one.</summary>
    public string? ChosenNote { get; private set; }

    /// <summary>The normalized canonical tag list the applied edit produced, or null when closed without one.</summary>
    public string? ChosenTags { get; private set; }

    /// <summary>
    /// Whether the dialog closed through Apply (true) or by cancelling
    /// (false: title bar X or the Cancel button). Both-null with Applied
    /// true is the honest clearing — an applied empty result.
    /// </summary>
    public bool Applied { get; private set; }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.BuildResult();
            ChosenNote = viewModel.ChosenNote;
            ChosenTags = viewModel.ChosenTags;
            Applied = true;
            Close();
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
