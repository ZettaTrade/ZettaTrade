namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using FluentAssertions;
using NSubstitute;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The harness adapter allowlist (T-141): the composition's
/// <see cref="ActiveExchangeDependencyInjection.ApplyAdapterAllowlist"/> rule
/// — null, empty and whitespace-only values keep the shipped full catalog; a
/// real list picks the named adapters case-insensitively (entries collapse
/// onto one adapter each), preserves the CATALOG's order (the demo lane stays
/// the facade's first adapter, so the shipped default never moves) and
/// refuses unknown ids by throwing — never a silent substitution (trading
/// safety). The env leg itself is the soak acceptance's own proof: the driver
/// sets the variable and the run's isolation census fails loudly when the
/// real-exchange calls leak past it.
/// </summary>
public sealed class AdapterAllowlistTests
{
    private static IMarketDataAdapter Adapter(string id)
    {
        var adapter = Substitute.For<IMarketDataAdapter>();
        adapter.Id.Returns(id);
        return adapter;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TheAllowlist_ABrokenValue_KeepsTheFullCatalog(string? value)
    {
        IReadOnlyList<IMarketDataAdapter> catalog =
        [
            Adapter("demo"),
            Adapter("binance"),
            Adapter("bybit"),
        ];

        ActiveExchangeDependencyInjection.ApplyAdapterAllowlist(value, catalog)
            .Should().BeSameAs(catalog, "an absent allowlist is the shipped full catalog");
    }

    [Fact]
    public void TheAllowlist_ARealList_PicksTheNamedAdapter_CaseInsensitivelyAndTrimmed()
    {
        var demo = Adapter("demo");
        var binance = Adapter("binance");
        var bybit = Adapter("bybit");
        IReadOnlyList<IMarketDataAdapter> catalog = [demo, binance, bybit];

        var picked = ActiveExchangeDependencyInjection.ApplyAdapterAllowlist(" Demo ", catalog);

        picked.Should().ContainSingle("the named adapter is the only survivor")
            .Which.Should().BeSameAs(demo);
    }

    [Fact]
    public void TheAllowlist_MultipleNames_KeepTheCatalogOrder()
    {
        var demo = Adapter("demo");
        var binance = Adapter("binance");
        var bybit = Adapter("bybit");
        IReadOnlyList<IMarketDataAdapter> catalog = [demo, binance, bybit];

        var picked = ActiveExchangeDependencyInjection.ApplyAdapterAllowlist("bybit,demo", catalog);

        picked.Should().HaveCount(2, "both named adapters survive");
        picked[0].Should().BeSameAs(demo, "the catalog's order stands: the demo lane stays first");
        picked[1].Should().BeSameAs(bybit);
    }

    [Fact]
    public void TheAllowlist_AnUnknownId_IsACompositionRefusal()
    {
        IReadOnlyList<IMarketDataAdapter> catalog = [Adapter("demo"), Adapter("binance")];

        var refuse = () => ActiveExchangeDependencyInjection.ApplyAdapterAllowlist(
            "demo, ghost", catalog);

        refuse.Should().Throw<InvalidOperationException>()
            .WithMessage("*ghost*", "the refusal names the offender, never a silent substitution");
    }
}
