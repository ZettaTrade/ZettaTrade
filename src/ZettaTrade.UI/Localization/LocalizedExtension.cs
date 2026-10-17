namespace ZettaTrade.UI.Localization;

using Avalonia;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

/// <summary>
/// Avalonia MarkupExtension for reactive localization in XAML.
/// Returns a binding that updates automatically when language changes.
/// Usage: {loc:Localized Menu.File}
/// </summary>
public class LocalizedExtension : MarkupExtension
{
    public string Key { get; }

    public LocalizedExtension(string key)
    {
        Key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        // Return binding to GetObservable which emits current value and updates on language change
        return LocalizationManager.Instance
            .GetObservable(Key)
            .ToBinding();
    }
}
