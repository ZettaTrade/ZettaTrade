namespace ZettaTrade.Infrastructure.Tests.Exchange;

using System;
using System.Collections.Generic;
using FluentAssertions;
using Infrastructure.Exchange;
using Xunit;

/// <summary>
/// Tests for the caller-side book-depth resolution
/// (<see cref="ExchangeAdapterBase.ResolveBookDepth"/>): the served depth set
/// stays the adapter's contract (BUG-032 rejects out-of-set requests there),
/// while a caller pre-resolves its generic display default against the
/// declaration — the smallest served depth at least as deep as the request,
/// else the deepest served depth; an undeclared constraint passes through.
/// </summary>
public sealed class ExchangeAdapterBaseResolveBookDepthTests
{
    /// <summary>Bybit spot serves <c>orderbook.1</c> and <c>orderbook.50</c> only.</summary>
    private static IReadOnlySet<int> BybitServedSet() => Served(1, 50);

    /// <summary>Binance serves the stepped <c>@depth5..@depth100</c> set.</summary>
    private static IReadOnlySet<int> BinanceServedSet() => Served(5, 10, 20, 25, 100);

    private static IReadOnlySet<int> Served(params int[] depths) => new HashSet<int>(depths);

    [Fact]
    public void Resolve_ExactServedDepth_StaysOnIt() =>
        ExchangeAdapterBase.ResolveBookDepth(BybitServedSet(), 50).Should().Be(50);

    [Fact]
    public void Resolve_RequestUnderEveryServedDepth_ResolvesToTheSmallestDeepEnoughDepth()
    {
        // Bybit only serves 1 and 50: the display default of 20 lands on 50,
        // while Binance's stepped set keeps small requests shallow.
        ExchangeAdapterBase.ResolveBookDepth(BybitServedSet(), 20).Should().Be(50);
        ExchangeAdapterBase.ResolveBookDepth(BinanceServedSet(), 3).Should().Be(5);
    }

    [Fact]
    public void Resolve_RequestAboveEveryServedDepth_FallsBackToTheDeepestServedDepth() =>
        ExchangeAdapterBase.ResolveBookDepth(BinanceServedSet(), 200).Should().Be(100);

    [Fact]
    public void Resolve_RequestDeepEnoughBetweenServedDepths_ResolvesToTheNearestAbove()
    {
        // 12 sits between 10 and 20: the nearest deep-enough serve is 20,
        // not shallower truncation via 10.
        ExchangeAdapterBase.ResolveBookDepth(BinanceServedSet(), 12).Should().Be(20);
    }

    [Fact]
    public void Resolve_WithoutADeclaredConstraint_PassesTheRequestThrough()
    {
        // The demo adapter declares nothing and serves any depth: the
        // request is the adapter's own business.
        ExchangeAdapterBase.ResolveBookDepth(null, 20).Should().Be(20);
        ExchangeAdapterBase.ResolveBookDepth(new HashSet<int>(), 20).Should().Be(20);
    }
}
