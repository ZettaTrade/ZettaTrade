namespace ZettaTrade.App.Views;

using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

/// <summary>
/// The text annotation's prompt window (T-200): one single-line entry plus
/// the place/cancel pair. The answer — the entered string, or null on a
/// cancel or an empty answer — becomes the annotation's text through the
/// chart ViewModel's interaction; a plain window, no view model: the
/// prompt carries no state beyond the entry itself.
/// </summary>
public partial class TextAnnotationWindow : Window
{
    /// <summary>
    /// Create the prompt window. Constructed through
    /// <see cref="PromptAsync"/> (the XAML runtime loader also wants the
    /// constructor public).
    /// </summary>
    public TextAnnotationWindow()
    {
        InitializeComponent();

        // Enter places the annotation (the same answer the Place button
        // gives); Escape rides the window's own close (a null answer).
        AnnotationInput.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                OnPlace(this, e);
            }
        };
    }

    /// <summary>
    /// Run the prompt: shows the window modal to its owner and completes
    /// with the entered string (trimmed), or null when the user cancels —
    /// either by the button, the window's close affordance, or an empty
    /// answer (an annotation never persists unlabelled).
    /// </summary>
    public static async Task<string?> PromptAsync(Window owner)
    {
        var window = new TextAnnotationWindow();
        await window.ShowDialog(owner);
        var answer = window._answer;
        return string.IsNullOrWhiteSpace(answer) ? null : answer.Trim();
    }

    private string? _answer;

    private void OnPlace(object? sender, RoutedEventArgs e)
    {
        _answer = AnnotationInput.Text;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _answer = null;
        Close();
    }
}
