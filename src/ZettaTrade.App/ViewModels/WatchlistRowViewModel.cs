namespace ZettaTrade.App.ViewModels;

using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using ReactiveUI;
using ZettaTrade.App.Services;

/// <summary>
/// One watchlist panel row (T-120): the stable identity over the symbol's
/// live quote — the feed never rebuilds rows, it only re-applies their
/// newest quote, so a row's bindings churn scale with actual data, not with
/// the pulse. Unpriced rows bind empty texts and an empty sparkline.
/// </summary>
public sealed class WatchlistRowViewModel : ReactiveObject
{
    #region Properties

    /// <summary>The normalized symbol text the feed keys the row by.</summary>
    public string SymbolText { get; }

    private string _lastPriceText = string.Empty;

    /// <summary>The formatted last price, or empty while the row is unpriced.</summary>
    public string LastPriceText
    {
        get => _lastPriceText;
        private set => this.RaiseAndSetIfChanged(ref _lastPriceText, value);
    }

    private string _change24hText = string.Empty;

    /// <summary>The formatted signed 24h change, or empty while the row is unpriced.</summary>
    public string Change24hText
    {
        get => _change24hText;
        private set => this.RaiseAndSetIfChanged(ref _change24hText, value);
    }

    private string _volume24hText = string.Empty;

    /// <summary>The formatted compact 24h volume, or empty while the row is unpriced.</summary>
    public string Volume24hText
    {
        get => _volume24hText;
        private set => this.RaiseAndSetIfChanged(ref _volume24hText, value);
    }

    private IList<Point> _sparklinePoints = new List<Point>();

    /// <summary>The sparkline's mapped points, or empty until the ring grows.</summary>
    public IList<Point> SparklinePoints
    {
        get => _sparklinePoints;
        private set => this.RaiseAndSetIfChanged(ref _sparklinePoints, value);
    }

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create the row over its fixed symbol identity.
    /// </summary>
    public WatchlistRowViewModel(string symbolText)
    {
        SymbolText = symbolText;
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Re-apply the row's newest quote: the texts format through the pure
    /// presentation helpers at the UI culture, and the sparkline maps the
    /// quote's price ring onto its canvas.
    /// </summary>
    public void ApplyQuote(WatchlistQuote quote)
    {
        LastPriceText = quote.LastPrice is { } price
            ? WatchlistRowPresentation.FormatPrice(price, CultureInfo.CurrentCulture)
            : string.Empty;
        Change24hText = quote.Change24hPercent is { } change
            ? WatchlistRowPresentation.FormatSignedPercent(change, CultureInfo.CurrentCulture)
            : string.Empty;
        Volume24hText = quote.Volume24h is { } volume
            ? WatchlistRowPresentation.FormatVolume(volume, CultureInfo.CurrentCulture)
            : string.Empty;
        SparklinePoints = WatchlistRowPresentation.BuildSparklinePoints(
            quote.Sparkline,
            WatchlistRowPresentation.SparklineWidth,
            WatchlistRowPresentation.SparklineHeight);
    }

    #endregion Public Methods
}
