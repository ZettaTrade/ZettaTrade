namespace ZettaTrade.App.Services;

/// <summary>
/// The crash dialog's presenter seam (T-101): the guard composes the
/// report, this presents it. Split so the unhandled-handler path tests
/// without a window system — a test presenter records the composed report.
/// </summary>
public interface ICrashDialogPresenter
{
    /// <summary>
    /// Present the composed crash report (best effort, never throws
    /// outward: a failed dialog logs inside and the process dies quietly
    /// per the platform contract).
    /// </summary>
    /// <param name="report">The composed read-only content.</param>
    void ShowCrashDialog(CrashReport report);
}
