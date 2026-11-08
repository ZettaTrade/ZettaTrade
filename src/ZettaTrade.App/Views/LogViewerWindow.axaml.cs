namespace ZettaTrade.App.Views;

using System.Globalization;
using Avalonia.Interactivity;
using Livery.Extensions;
using ReactiveUI;
using ReactiveUI.Avalonia;
using ReactiveUI.Primitives;
using Serilog;
using ZettaTrade.App.ViewModels;

/// <summary>
/// Log viewer window (T-101). Owns the window plumbing: the view model is
/// transient per open, so every activation attaches the live tail lane first
/// and seeds the view from the tail's history snapshot right behind it (the
/// value-identity guard inside the view model keeps the crossing slices from
/// double rendering). The lane stopper dies with the activation scope, the
/// export's save-file route resolves through the picker here (picking a file
/// is view behavior), and the Close button closes the window directly.
/// </summary>
public partial class LogViewerWindow : ReactiveWindow<LogViewerViewModel>
{
    /// <summary>
    /// Create the log viewer window. The view model must be assigned before
    /// activation (the opener sets <c>DataContext</c>).
    /// </summary>
    public LogViewerWindow()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            // The export route registers once for the window's stay and dies
            // with it (the same posture the transient windows keep).
            ViewModel?.ExportPathRequested.RegisterHandler(async context =>
                {
                    try
                    {
                        context.SetOutput(
                            await LogExcerptFilePicker.PickAsync(this, SuggestedExcerptName()));
                    }
                    catch (System.Exception ex)
                    {
                        Log.Error(ex, "The log excerpt's save-file dialog failed");
                        context.SetOutput(null);
                    }
                })
                .DisposeWith(disposables);

            // The live lane's subscription dies with the window; the seed run
            // follows the attach right here (the attach / seed discipline).
            ViewModel?.AttachLiveTail()
                .DisposeWith(disposables);
            ViewModel?.LoadCommand
                .Execute(RxVoid.Default)
                .Subscribe(
                    _ => { },
                    error => Log.Error(error, "Log viewer failed to load"))
                .DisposeWith(disposables);
        });
    }

    /// <summary>The save-file dialog's pre-filled name (timestamped, unambiguous).</summary>
    private static string SuggestedExcerptName() =>
        $"zettatrade-log-excerpt-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.txt";

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
