namespace ZettaTrade.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Livery.Extensions;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;

/// <summary>
/// The watchlist panel state (T-120): the rows' collection over the feed's
/// snapshots. The feed owns the row set and the cadence; the ViewModel only
/// diffs snapshots into stable row identities — a collection event fires for
/// the rows that join or retire, and each row's fields update in place at
/// the feed's pulse (twice a second at most, whatever the wire says).
/// </summary>
public sealed class WatchlistViewModel : ReactiveObject
{
    #region Private Fields

    /// <summary>The panel's rows, in the feed's snapshot order.</summary>
    private readonly ObservableCollection<WatchlistRowViewModel> _rows = new();

    /// <summary>The row lookup the diff keeps (UI-thread-only access).</summary>
    private readonly Dictionary<string, WatchlistRowViewModel> _rowByText =
        new(StringComparer.Ordinal);

    #endregion Private Fields

    #region Properties

    /// <summary>The panel's rows, in the feed's snapshot order.</summary>
    public ObservableCollection<WatchlistRowViewModel> Rows => _rows;

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create the panel over the feed: the quotes flow through the UI thread
    /// so the collection events stay on it (the panel VM lives as long as
    /// the application, so this subscription mirrors it).
    /// </summary>
    public WatchlistViewModel(WatchlistFeedService feed, ILogger<WatchlistViewModel> logger)
    {
        feed.Quotes
            .ObserveOnUiThread()
            .Subscribe(
                ApplyQuotes,
                ex => logger.LogError(ex, "The watchlist quote stream stopped unexpectedly"));
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Apply one snapshot: join the missing rows (the feed snapshots arrive
    /// in starter order, so a joined row appends at the end), update each
    /// row's fields in place, and retire the rows the feed dropped. Row
    /// identities stay stable as long as the symbol stays in the feed's set.
    /// </summary>
    public void ApplyQuotes(IReadOnlyList<WatchlistQuote> snapshot)
    {
        var alive = new HashSet<string>(StringComparer.Ordinal);

        foreach (var quote in snapshot)
        {
            alive.Add(quote.SymbolText);

            if (!_rowByText.TryGetValue(quote.SymbolText, out var row))
            {
                row = new WatchlistRowViewModel(quote.SymbolText);
                _rowByText.Add(quote.SymbolText, row);
                _rows.Add(row);
            }

            row.ApplyQuote(quote);
        }

        for (var index = _rows.Count - 1; index >= 0; index--)
        {
            var row = _rows[index];
            if (alive.Contains(row.SymbolText))
            {
                continue;
            }

            _rows.RemoveAt(index);
            _rowByText.Remove(row.SymbolText);
        }
    }

    #endregion Public Methods
}
