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

    private bool _isChangeUp;

    /// <summary>
    /// Whether the row's 24h change is a gain (T-159): the sign tints the
    /// change text (the candle state colors). Neither flag for an unpriced
    /// or flat row — the plain ink is the no-signal state.
    /// </summary>
    public bool IsChangeUp
    {
        get => _isChangeUp;
        private set => this.RaiseAndSetIfChanged(ref _isChangeUp, value);
    }

    private bool _isChangeDown;

    /// <summary>Whether the row's 24h change is a loss (the sign tints the
    /// change text, T-159).</summary>
    public bool IsChangeDown
    {
        get => _isChangeDown;
        private set => this.RaiseAndSetIfChanged(ref _isChangeDown, value);
    }

    private bool _isSparklineUp;

    /// <summary>
    /// Whether the row's price ring RISES oldest to newest (T-159): the
    /// sparkline colors by the ring's direction — its own truth, deliberately
    /// independent of the 24h change sign (a ring may dip while the day's
    /// sum stays green; the two inks tell the two truths apart).
    /// </summary>
    public bool IsSparklineUp
    {
        get => _isSparklineUp;
        private set => this.RaiseAndSetIfChanged(ref _isSparklineUp, value);
    }

    private bool _isSparklineDown;

    /// <summary>Whether the row's price ring falls oldest to newest (the
    /// sparkline colors by the ring's direction, T-159).</summary>
    public bool IsSparklineDown
    {
        get => _isSparklineDown;
        private set => this.RaiseAndSetIfChanged(ref _isSparklineDown, value);
    }

    private string _volume24hText = string.Empty;

    /// <summary>The formatted compact 24h volume, or empty while the row is unpriced.</summary>
    public string Volume24hText
    {
        get => _volume24hText;
        private set => this.RaiseAndSetIfChanged(ref _volume24hText, value);
    }

    private string _fundingRateText = string.Empty;

    /// <summary>
    /// The formatted signed funding rate (T-197), or empty while the row
    /// carries none — a spot or stock row keeps it hidden forever.
    /// </summary>
    public string FundingRateText
    {
        get => _fundingRateText;
        private set => this.RaiseAndSetIfChanged(ref _fundingRateText, value);
    }

    private bool _hasFundingRate;

    /// <summary>
    /// Whether the row's quote carries a funding rate (T-197): the view
    /// gates the funding text on it, so a spot row leaves no empty gap.
    /// </summary>
    public bool HasFundingRate
    {
        get => _hasFundingRate;
        private set => this.RaiseAndSetIfChanged(ref _hasFundingRate, value);
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
        FundingRateText = quote.FundingRate is { } funding
            ? WatchlistRowPresentation.FormatFundingRate(funding, CultureInfo.CurrentCulture)
            : string.Empty;
        HasFundingRate = quote.FundingRate is not null;
        SparklinePoints = WatchlistRowPresentation.BuildSparklinePoints(
            quote.Sparkline,
            WatchlistRowPresentation.SparklineWidth,
            WatchlistRowPresentation.SparklineHeight);

        // The color semantics (T-159): the change text tints by the 24h
        // sign, the sparkline by the ring's oldest-to-newest direction;
        // an unpriced or flat row keeps neither flag (the no-signal inks).
        // RaiseAndSetIfChanged keeps the churn to the sign flips — a row
        // re-pulsed with the same sign raises nothing.
        if (quote.Change24hPercent is { } changePercent)
        {
            IsChangeUp = changePercent > 0m;
            IsChangeDown = changePercent < 0m;
        }
        else
        {
            IsChangeUp = false;
            IsChangeDown = false;
        }

        if (quote.Sparkline.Count >= 2)
        {
            var oldest = quote.Sparkline[0];
            var newest = quote.Sparkline[^1];
            IsSparklineUp = newest > oldest;
            IsSparklineDown = newest < oldest;
        }
        else
        {
            IsSparklineUp = false;
            IsSparklineDown = false;
        }
    }

    #endregion Public Methods
}
