namespace ZettaTrade.App.Views.Panels;

using Avalonia.Controls;
using ZettaTrade.App.ViewModels;

/// <summary>
/// The order book panel (T-103): the extracted book column (the ladder and
/// the cumulative depth profile) the main window's workspace grid and the
/// detached book window share. ONE control instance exists — the detach
/// moves it cross-window, so the level-press route the window code-behind
/// armed once keeps working on either host.
/// </summary>
public partial class OrderBookPanelView : UserControl
{
    /// <summary>
    /// Create the order book panel around its view model (T-175): the
    /// DataContext is a LocalValue from the panel's own construction, so the
    /// subtree's compiled bindings evaluate against the book VM from their
    /// very first pass. The XAML-created alternative let the loader wire the
    /// inherited MainViewModel into the unattached panel ahead of every
    /// tree link, cast-erroring the whole subtree on startup (the noise the
    /// T-175 sweep kills); the parameterless route keeps the designer
    /// preview working and the T-175 counterfactual reproducible.
    /// </summary>
    public OrderBookPanelView(OrderBookViewModel? book)
    {
        DataContext = book;

        InitializeComponent();
    }

    /// <summary>
    /// The bare construction (the designer's route): the caller assigns the
    /// DataContext later; the startup cast-noise shape lives here.
    /// </summary>
    public OrderBookPanelView()
    {
        InitializeComponent();
    }
}
