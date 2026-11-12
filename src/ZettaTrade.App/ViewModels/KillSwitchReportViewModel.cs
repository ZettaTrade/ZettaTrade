namespace ZettaTrade.App.ViewModels;

using ReactiveUI;

/// <summary>
/// ViewModel for the kill-switch report window (T-188). Holds the composed
/// multi-line summary only; the window plumbing stays in the View, so this
/// VM stays free of any View types.
/// </summary>
public sealed class KillSwitchReportViewModel
{
    /// <summary>
    /// Create a new KillSwitchReportViewModel instance.
    /// </summary>
    public KillSwitchReportViewModel(string summary)
    {
        Summary = summary;
    }

    /// <summary>Report text, already resolved in the user's language by the caller.</summary>
    public string Summary { get; }
}
