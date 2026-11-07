namespace ZettaTrade.App.ViewModels;

using ZettaTrade.App.Services;

/// <summary>
/// One adapter health strip row (T-098): the bindable projection of an
/// <see cref="AdapterHealth"/> snapshot for the status bar's dot strip. A
/// value object — every snapshot refresh replaces the instances, so no
/// per-row change notification machinery exists; the four boolean class
/// flags are what the XAML styles pick the <c>DynamicResource</c> palette
/// fill from (exactly one is true).
/// </summary>
/// <param name="AdapterName">The adapter's display name (the dot's identity — not rendered).</param>
/// <param name="State">The mapped strip state.</param>
/// <param name="Tooltip">The composed localized tooltip text (dynamic data — never a static localized string).</param>
public sealed record AdapterHealthRow(
    string AdapterName,
    AdapterHealthState State,
    string Tooltip)
{
    /// <summary>The connected dot class flag.</summary>
    public bool IsConnected => State == AdapterHealthState.Connected;

    /// <summary>The reconnecting dot class flag.</summary>
    public bool IsReconnecting => State == AdapterHealthState.Reconnecting;

    /// <summary>The down dot class flag.</summary>
    public bool IsDown => State == AdapterHealthState.Down;

    /// <summary>The unsupervised dot class flag.</summary>
    public bool IsUnsupervised => State == AdapterHealthState.Unsupervised;
}
