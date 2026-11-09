namespace ZettaTrade.App.ViewModels;

using ZettaTrade.Core.Entities;

/// <summary>
/// The chart export flows' view contracts (T-094): the ViewModel builds the
/// request and owns the outcome, while the window answers with the picked
/// destination — and, for the PNG snapshot, with the render itself, for the
/// visible-range CSV, with the chart's own slice. Picking a file and reading
/// the chart control are view behavior; these records are the only shapes
/// crossing that seam.
/// </summary>

/// <summary>
/// The PNG snapshot export request: the suggested file name pre-fills the
/// save dialog, the watermark (the symbol and time frame read) is what the
/// chart stamps over the rendered frame.
/// </summary>
/// <param name="SuggestedFileName">The pre-filled file name (no directory part).</param>
/// <param name="Watermark">The context line stamped over the frame's corner.</param>
public sealed record PngExportRequest(string SuggestedFileName, string Watermark);

/// <summary>
/// The visible-range CSV handoff: the picked destination plus exactly the
/// bars the chart's frame paints (the control's <c>VisibleBarsSlice</c>) —
/// the ViewModel streams that slice through the shared CSV writer, so the
/// file never carries a bar the chart did not show.
/// </summary>
/// <param name="Path">The absolute destination file path (overwritten if present).</param>
/// <param name="Bars">The visible window's bars, oldest first.</param>
public sealed record VisibleCsvHandoff(
    string Path,
    IReadOnlyList<Bar> Bars);
