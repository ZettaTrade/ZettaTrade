namespace Livery.Localization;

using Avalonia;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

/// <summary>
/// Avalonia MarkupExtension for reactive localization in XAML.
/// Returns a binding that updates automatically when language changes.
/// </summary>
/// <example>
/// Usage in XAML:
/// <code>
/// <TextBlock Text="{loc:Localized Menu.File}" />
/// <Button Content="{loc:Localized Order.Buy}" />
/// </code>
/// </example>
public class LocalizedExtension : MarkupExtension
{
    /// <summary>
    /// Localization key.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Create a new LocalizedExtension instance.
    /// </summary>
    public LocalizedExtension(string key)
    {
        Key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return LocalizationManager.Instance
            .GetObservable(Key)
            .ToBinding();
    }
}
