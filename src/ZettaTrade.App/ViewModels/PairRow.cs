namespace ZettaTrade.App.ViewModels;

/// <summary>
/// One row of the settings all-pairs list: the pair name plus the star state
/// the row renders (the star button lives on the row itself now — the
/// favorites pin to the head of the list). The list is rebuilt wholesale on
/// every draft change, so the row is an immutable snapshot — no per-row
/// change notifications.
/// </summary>
/// <param name="Name">The row's normalized pair name.</param>
/// <param name="IsFavorite">Whether the row's star button is lit.</param>
public sealed record PairRow(string Name, bool IsFavorite);
