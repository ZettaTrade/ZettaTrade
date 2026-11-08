namespace ZettaTrade.App.Services;

using System;
using System.Reactive.Subjects;

/// <summary>
/// The composition-owned address of the chart's delivered series loads
/// (T-112): the chart publishes every full load here, the strategy host
/// service consumes the stream — both through this relay so neither
/// depends on the other's construction (the chart does not hold the
/// strategy service in its feed path, the service never resolves the
/// chart). A load publishes as a <see cref="ChartSeriesPage"/>: the bars
/// with their (symbol, time frame) identity.
/// </summary>
public sealed class SignalSeriesRelay
{
    private readonly Subject<ChartSeriesPage> _pages = new();

    /// <summary>
    /// The delivered full loads, raw (not UI-marshaled): the feed the
    /// strategy service ingests closed candles from.
    /// </summary>
    public IObservable<ChartSeriesPage> Pages => _pages;

    /// <summary>
    /// Publish one delivered full load. The chart's delivery handler is
    /// the writer; the strategy service the only meaningful consumer.
    /// </summary>
    /// <param name="page">The delivered load with its series identity.</param>
    public void Publish(ChartSeriesPage page) => _pages.OnNext(page);
}
