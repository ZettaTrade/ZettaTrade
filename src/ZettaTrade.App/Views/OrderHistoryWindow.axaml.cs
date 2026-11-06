namespace ZettaTrade.App.Views;

using Avalonia.Controls;
using Avalonia.Interactivity;
using ReactiveUI;
using ReactiveUI.Avalonia;
using ReactiveUI.Primitives;
using Serilog;
using ZettaTrade.App.ViewModels;

/// <summary>
/// Order history window. Owns the window plumbing: the view model loads the
/// persisted rows once per activation through the
/// <see cref="OrderHistoryViewModel.LoadCommand"/>; the Close button closes
/// the window directly; the export commands pick their destination through
/// the CSV save-file dialog here (picking a file is view behavior).
/// </summary>
public partial class OrderHistoryWindow : ReactiveWindow<OrderHistoryViewModel>
{
    /// <summary>
    /// Create the order history window. The view model must be assigned
    /// before activation (the opener sets <c>DataContext</c>).
    /// </summary>
    public OrderHistoryWindow()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            // The load pipeline runs once per activation and self-terminates,
            // with errors caught in the Subscribe call. The per-activation
            // save-dialog handler registrations die with the same disposal
            // scope (the transient view model and its only window share
            // their lifetime).
            ViewModel?.LoadCommand
                .Execute()
                .Subscribe(
                    _ => { },
                    error => Log.Error(error, "Order history window failed to load"));

            ViewModel?.SaveOrdersCsvRequested.RegisterHandler(async context =>
                {
                    var path = await CsvSaveFilePicker.PickAsync(this, context.Input);
                    context.SetOutput(path);
                })
                .DisposeWith(disposables);

            ViewModel?.SaveFillsCsvRequested.RegisterHandler(async context =>
                {
                    var path = await CsvSaveFilePicker.PickAsync(this, context.Input);
                    context.SetOutput(path);
                })
                .DisposeWith(disposables);
        });
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
