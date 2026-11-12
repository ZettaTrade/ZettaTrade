namespace ZettaTrade.Infrastructure.Tests.Exchange;

using System;
using System.Collections.Generic;
using System.Linq;
using Core.Entities;
using Core.Enums;
using Core.ValueObjects;
using FluentAssertions;
using Infrastructure.Exchange;
using Xunit;

/// <summary>
/// Tests for the <see cref="SequencedOrderBookAssembler"/> state machine: a
/// snapshot rebuilds, a delta applies only when the sequence continues
/// (quantity "0" deletes a level), a stale sequence is skipped without a
/// state change, and a gap discards the state (the adapter re-subscribes and
/// the next snapshot rebuilds). Depth is capped best-priced-first.
/// </summary>
public sealed class SequencedOrderBookAssemblerTests
{
    private static readonly Symbol BtcUsdt = new("btc", "usdt", AssetClass.Crypto);

    private static readonly DateTimeOffset Timestamp =
        DateTimeOffset.FromUnixTimeMilliseconds(1700000000100);

    private static SequencedOrderBookAssembler CreateAssembler(int maxDepth = 50) =>
        new(BtcUsdt, maxDepth);

    private static List<PriceLevel> Levels(params (decimal Price, decimal Quantity)[] pairs) =>
        [.. pairs.Select(pair => new PriceLevel(pair.Price, pair.Quantity))];

    [Fact]
    public void ApplySnapshot_PublishesTheFullRebuiltBookSortedBestFirst()
    {
        var assembler = CreateAssembler();

        var apply = assembler.ApplySnapshot(
            100,
            Levels((100m, 1m), (101m, 2m)),   // the wire may deliver any order
            Levels((102m, 1m), (101.5m, 3m)),
            Timestamp);

        apply.Outcome.Should().Be(SequencedBookOutcome.Applied);
        apply.Book!.Symbol.Should().Be(BtcUsdt);
        apply.Book.Timestamp.Should().Be(Timestamp);
        apply.Book.Bids.Should().Equal(Levels((101m, 2m), (100m, 1m)));
        apply.Book.Asks.Should().Equal(Levels((101.5m, 3m), (102m, 1m)));
    }

    [Fact]
    public void ApplySnapshot_ExceedingTheCap_KeepsTheBestPricedLevels()
    {
        var assembler = CreateAssembler(maxDepth: 2);

        var apply = assembler.ApplySnapshot(
            100,
            Levels((100m, 1m), (101m, 2m), (102m, 3m)),
            Levels((103m, 1m), (104m, 2m), (105m, 3m)),
            Timestamp);

        apply.Book!.Bids.Should().Equal(Levels((102m, 3m), (101m, 2m)), "highest bids win");
        apply.Book.Asks.Should().Equal(Levels((103m, 1m), (104m, 2m)), "lowest asks win");
    }

    [Fact]
    public void ApplyDelta_ContinuingSequence_AppliesAndPublishesTheFullState()
    {
        var assembler = CreateAssembler();
        assembler.ApplySnapshot(
            100,
            Levels((100m, 1m), (99m, 2m)),
            Levels((101m, 3m)),
            Timestamp);

        var apply = assembler.ApplyDelta(
            101,
            Levels((99m, 4m)),
            Levels((101.5m, 5m)),
            Timestamp);

        apply.Outcome.Should().Be(SequencedBookOutcome.Applied);
        apply.Book!.Bids.Should().Equal(Levels((100m, 1m), (99m, 4m)));
        apply.Book.Asks.Should().Equal(Levels((101m, 3m), (101.5m, 5m)));
    }

    [Fact]
    public void ApplyDelta_ZeroQuantityDeletesTheLevel()
    {
        var assembler = CreateAssembler();
        assembler.ApplySnapshot(
            100,
            Levels((100m, 1m), (99m, 2m)),
            Levels((101m, 3m)),
            Timestamp);

        var apply = assembler.ApplyDelta(
            101,
            Levels((100m, 0m)),
            Levels((101m, 0m)),
            Timestamp);

        apply.Outcome.Should().Be(SequencedBookOutcome.Applied);
        apply.Book!.Bids.Should().Equal(Levels((99m, 2m)));
        apply.Book.Asks.Should().BeEmpty();
    }

    [Fact]
    public void ApplyDelta_StaleSequence_IsSkippedAndLeavesTheStateUntouched()
    {
        var assembler = CreateAssembler();
        assembler.ApplySnapshot(
            100,
            Levels((100m, 1m)),
            Levels((101m, 3m)),
            Timestamp);

        var stale = assembler.ApplyDelta(
            100,
            Levels((90m, 9m)),
            Levels((110m, 9m)),
            Timestamp);
        stale.Outcome.Should().Be(SequencedBookOutcome.StaleSkipped);

        // A duplicate of an already-applied sequence is stale too.
        var older = assembler.ApplyDelta(
            99,
            Levels((95m, 9m)),
            Levels((115m, 9m)),
            Timestamp);
        older.Outcome.Should().Be(SequencedBookOutcome.StaleSkipped);

        // The next continuing delta publishes the untouched state.
        var alive = assembler.ApplyDelta(
            101,
            Levels((99m, 4m)),
            Levels((101.5m, 5m)),
            Timestamp);
        alive.Outcome.Should().Be(SequencedBookOutcome.Applied);
        alive.Book!.Bids.Should().Equal(Levels((100m, 1m), (99m, 4m)));
        alive.Book.Asks.Should().Equal(Levels((101m, 3m), (101.5m, 5m)));
    }

    [Fact]
    public void ApplyDelta_BeforeAnySnapshot_IsAGapAndWaitsForTheRestart()
    {
        var assembler = CreateAssembler();

        var apply = assembler.ApplyDelta(
            1,
            Levels((100m, 1m)),
            Levels((101m, 3m)),
            Timestamp);

        apply.Outcome.Should().Be(SequencedBookOutcome.GapDetected);

        // After the gap the state is gone: only a snapshot restarts it.
        var restart = assembler.ApplySnapshot(
            7,
            Levels((100m, 1m)),
            Levels((101m, 3m)),
            Timestamp);
        restart.Outcome.Should().Be(SequencedBookOutcome.Applied);

        var continued = assembler.ApplyDelta(
            8,
            Levels((100m, 2m)),
            [],
            Timestamp);
        continued.Outcome.Should().Be(SequencedBookOutcome.Applied);
        continued.Book!.Bids.Should().Equal(Levels((100m, 2m)));
    }

    [Fact]
    public void ApplyDelta_SequenceGap_DiscardsTheState()
    {
        var assembler = CreateAssembler();
        assembler.ApplySnapshot(
            100,
            Levels((100m, 1m), (99m, 2m)),
            Levels((101m, 3m)),
            Timestamp);

        var gap = assembler.ApplyDelta(
            102, // skips 101
            Levels((90m, 9m)),
            Levels((110m, 9m)),
            Timestamp);

        gap.Outcome.Should().Be(SequencedBookOutcome.GapDetected);
        gap.Book.Should().BeNull();

        // The state must not silently revive from the gap frame: the next
        // delta (after the re-subscribe) is still a gap until a snapshot lands.
        var afterGap = assembler.ApplyDelta(
            103,
            Levels((100m, 1m)),
            Levels((101m, 3m)),
            Timestamp);
        afterGap.Outcome.Should().Be(SequencedBookOutcome.GapDetected);
    }
}
