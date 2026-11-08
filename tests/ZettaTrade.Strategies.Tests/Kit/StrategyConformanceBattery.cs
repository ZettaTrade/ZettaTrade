namespace ZettaTrade.Strategies.Tests.Kit;

using FluentAssertions;
using Xunit;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Strategies;
using ZettaTrade.Strategies.Tests.Harness;

/// <summary>
/// The strategy conformance battery: the standing recipe every
/// implementation must clear before it is a strategy in this repo (T-111,
/// the AdapterKit's battery shape applied to the seam's own contract).
/// Five legs, each a sentence of the <see cref="IStrategy"/> contract -
/// identity, the no-substitution guard, the honest warmup, the defined
/// stance and the replay agreement between fresh instances. A battery run
/// is <c>leg × implementation</c>: onboard a new strategy by writing its
/// battery subclass next to the suite; the legs are already written.
/// </summary>
public abstract class StrategyConformanceBattery
{
    #region Constructor

    /// <summary>The fresh-instance factory each leg runs against.</summary>
    private readonly Func<IStrategy> _factory;

    /// <summary>Create the battery over one implementation factory.</summary>
    /// <param name="factory">Builds a fresh instance per leg (the replay leg builds two).</param>
    protected StrategyConformanceBattery(Func<IStrategy> factory)
    {
        _factory = factory;
    }

    #endregion

    #region Tests

    /// <summary>The canonical id is fixed by the implementation and never blank.</summary>
    [Fact]
    public void TheIdentity_IsFixedAndNotBlank()
    {
        var strategy = _factory();

        strategy.Id.Should().NotBeNullOrWhiteSpace("a strategy registers under its canonical id, never a blank");
    }

    /// <summary>
    /// The no-substitution guard: a null context is invalid input and is
    /// rejected, never defaulted into a stance (the standing rule).
    /// </summary>
    [Fact]
    public void Evaluate_ANullContext_IsRejectedInsteadOfDefaulting()
    {
        FluentActions.Invoking(() => _factory().Evaluate(null!))
            .Should().ThrowExactly<ArgumentNullException>();
    }

    /// <summary>No closed candle, no opinion: an empty window is an honest neutral.</summary>
    [Fact]
    public void Evaluate_AnEmptyWindow_IsNeutral()
    {
        var context = new StrategyContext(TestBars.Symbol, TimeFrame.M1, Array.Empty<Bar>());

        _factory().Evaluate(context).Should().Be(StrategyStance.Neutral, "there is no closed candle to judge");
    }

    /// <summary>One closed candle may be too short to judge, but the answer stays a defined stance.</summary>
    [Fact]
    public void Evaluate_AOneBarWindow_ReturnsADefinedStance()
    {
        var context = new StrategyContext(TestBars.Symbol, TimeFrame.M1, TestBars.Candles(7));

        _factory().Evaluate(context).Should().BeOneOf(
            new[] { StrategyStance.Neutral, StrategyStance.Long, StrategyStance.Flat },
            "a defined stance, never a fabrication outside the vocabulary");
    }

    /// <summary>
    /// The replay agreement: two FRESH instances of the same
    /// implementation evaluate the same window into the same stance - the
    /// deterministic-per-input-order half of the contract.
    /// </summary>
    [Fact]
    public void Evaluate_TwoFreshInstances_AgreeOnTheSameContext()
    {
        var bars = TestBars.Candles(
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12,
            12, 10, 8, 6, 4, 2,
            10, 12, 14);
        var context = new StrategyContext(TestBars.Symbol, TimeFrame.M1, bars);

        var first = _factory().Evaluate(context);
        var second = _factory().Evaluate(context);

        first.Should().Be(second, "the same window evaluated by fresh instances of one implementation agrees");
    }

    #endregion
}
