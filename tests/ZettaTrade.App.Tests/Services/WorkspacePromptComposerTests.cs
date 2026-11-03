namespace ZettaTrade.App.Tests.Services;

using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Enums;

/// <summary>
/// The workspace prompt composer is pure text assembly: the identity and the
/// no-fabrication discipline always carry, whatever the context knows is
/// stated (exchange, symbol, time frame, latest price), and whatever it
/// doesn't is left out — the prompt degrades, it never invents.
/// </summary>
public sealed class WorkspacePromptComposerTests
{
    /// <summary>The pure composer under test.</summary>
    private readonly WorkspacePromptComposer _composer = new();

    /// <summary>
    /// An idle workspace (no symbol yet): identity + the exchange fact +
    /// discipline; nothing about markets is invented.
    /// </summary>
    [Fact]
    public void Compose_AnIdleWorkspace_StatesTheExchangeAndNoSymbol()
    {
        // Act
        var prompt = _composer.Compose(new ChatContext { ExchangeName = "Demo Paper" });

        // Assert
        prompt.Should().Contain("ZettaTrade", "the identity line states the assistant's home");
        prompt.Should().Contain("Demo Paper", "the exchange the trader is on");
        prompt.Should().Contain("no symbol selected yet", "the honest no-market fact");
        prompt.Should().NotContain("latest price", "nothing is invented");
    }

    /// <summary>
    /// A priced symbol workspace: the market facts render together, the price
    /// with its full decimal precision (no rounding by the prompt).
    /// </summary>
    [Fact]
    public void Compose_APricedSymbolWorkspace_StatesTheSymbolTheFrameAndThePrice()
    {
        // Act
        var prompt = _composer.Compose(new ChatContext
        {
            ExchangeName = "Demo Paper",
            Symbol = "BTC/USDT",
            TimeFrame = TimeFrame.H1,
            LastPrice = 50_000.5m,
        });

        // Assert
        prompt.Should().Contain("BTC/USDT");
        prompt.Should().Contain("H1");
        prompt.Should().Contain("latest price 50000.50000000", "the price renders unrounded");
        prompt.Should().NotContain("no symbol", "the symbol is known");
    }

    /// <summary>
    /// A symbol without a price yet: the prompt is honest about the missing
    /// live price instead of inventing one.
    /// </summary>
    [Fact]
    public void Compose_ASymbolWithoutAPrice_IsHonestAboutTheMissingPrice()
    {
        // Act
        var prompt = _composer.Compose(new ChatContext
        {
            ExchangeName = "Demo Paper",
            Symbol = "BTC/USDT",
            TimeFrame = TimeFrame.H1,
        });

        // Assert
        prompt.Should().Contain("no live price received yet");
        prompt.Should().NotContain("latest price", "nothing is invented");
    }

    /// <summary>
    /// The discipline line always carries: the assistant states what it
    /// cannot do (no orders) and refuses to fabricate data it was not given.
    /// </summary>
    [Fact]
    public void Compose_CarriesTheNoFabricationDiscipline()
    {
        // Arrange: the minimal snapshot — the discipline is context-free
        var prompt = _composer.Compose(new ChatContext { ExchangeName = "Demo Paper" });

        // Assert
        prompt.Should().Contain("cannot execute orders");
        prompt.Should().Contain("do not fabricate data");
        prompt.Should().Contain("if the context is missing", "the honesty instruction");
    }

    /// <summary>A null context is a caller bug: the composer refuses loudly.</summary>
    [Fact]
    public void Compose_ANullContext_Throws()
    {
        // Act / Assert
        FluentActions.Invoking(() => _composer.Compose(null!))
            .Should().Throw<ArgumentNullException>();
    }
}
