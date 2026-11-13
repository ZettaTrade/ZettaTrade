namespace ZettaTrade.App.ViewModels;

/// <summary>
/// One applied trade-annotation edit (T-211): the normalized note and the
/// canonical comma-joined tag list the dialog produced. Both null is the
/// honest clearing — the edit removes the row's annotations.
/// </summary>
public sealed record TradeNoteEditResult(
    string? Note,
    string? Tags);
