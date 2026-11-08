namespace ZettaTrade.App.ViewModels;

/// <summary>
/// The three steps of the first-run wizard, shown in order: the theme pick,
/// the adapter pick, the workspace symbol pick. The numeric order is the
/// display order; the wizard never skips a step except through the explicit
/// skip affordance.
/// </summary>
public enum FirstRunWizardStep
{
    /// <summary>Step one: the theme picker.</summary>
    Theme,

    /// <summary>Step two: the exchange adapter pick (demo is the default lane).</summary>
    Adapter,

    /// <summary>Step three: the first workspace symbol pick.</summary>
    Symbol,
}
