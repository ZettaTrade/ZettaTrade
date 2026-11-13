namespace ZettaTrade.App.Views;

using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

/// <summary>
/// The single-line prompt window (T-200; generalized T-202): one
/// single-line entry plus the place/cancel pair. The answer — the entered
/// string, or null on a cancel or an empty answer — rides the opener's
/// interaction; a plain window, no view model: the prompt carries no
/// state beyond the entry itself. The annotation prompt uses it verbatim
/// (the localized defaults stand); the chart templates' save and rename
/// prompts pass their own title, caption, prefill and place label.
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
    /// Run the annotation prompt (T-200's shape): the localized defaults
    /// stand, no prefill.
    /// </summary>
    public static Task<string?> PromptAsync(Window owner) =>
        PromptAsync(owner, prefill: null, title: null, caption: null, placeLabel: null);

    /// <summary>
    /// Run the prompt: shows the window modal to its owner and completes
    /// with the entered string (trimmed), or null when the user cancels —
    /// either by the button, the window's close affordance, or an empty
    /// answer (an annotation never persists unlabelled; a template name
    /// never saves blank). The optional overrides (T-202) replace the
    /// localized defaults per prompt; the window's shape and automation
    /// ids stay the annotation's own.
    /// </summary>
    public static async Task<string?> PromptAsync(
        Window owner,
        string? prefill,
        string? title,
        string? caption,
        string? placeLabel)
    {
        var window = new TextAnnotationWindow();
        if (title is not null)
        {
            window.Title = title;
        }

        if (caption is not null)
        {
            window.PromptCaption.Text = caption;
        }

        if (placeLabel is not null)
        {
            window.PlaceButton.Content = placeLabel;
        }

        if (prefill is not null)
        {
            window.AnnotationInput.Text = prefill;
        }

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
