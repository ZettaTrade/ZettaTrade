namespace ZettaTrade.Plugins.Abstractions.Extensions;

/// <summary>
/// The UI extension descriptors a plugin registers (T-131): the host
/// collects them through DI after the plugin's registration pass and
/// serves them to the shell. Nothing here builds on Avalonia — the
/// widget factories stay <c>object</c>-typed so UI-free plugins carry no
/// UI dependency and the contract assembly compiles alone.
/// </summary>
/// <param name="PanelId">A stable panel identity unique within the plugin.</param>
/// <param name="TitleResxKey">The localized title's resx key under the plugin's own module prefix.</param>
/// <param name="WidgetFactory">Builds the panel's visual (a control or a ViewModel) from the plugin's DI layer.</param>
public sealed record PluginPanelDescriptor(
    string PanelId,
    string TitleResxKey,
    Func<IServiceProvider, object> WidgetFactory);

/// <summary>
/// A toolbar slot a plugin fills (T-131): a stable id and the localized
/// label key; the host owns where and whether it renders.
/// </summary>
/// <param name="ItemId">A stable item identity unique within the plugin.</param>
/// <param name="LabelResxKey">The localized label's resx key under the plugin's own module prefix.</param>
public sealed record PluginToolbarItemDescriptor(
    string ItemId,
    string LabelResxKey);

/// <summary>
/// A settings section a plugin contributes (T-131): keyed, localized
/// future tab the host composes into the settings window.
/// </summary>
/// <param name="SectionId">A stable section identity unique within the plugin.</param>
/// <param name="TitleResxKey">The localized title's resx key under the plugin's own module prefix.</param>
public sealed record PluginSettingsSectionDescriptor(
    string SectionId,
    string TitleResxKey);
