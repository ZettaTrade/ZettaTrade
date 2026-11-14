namespace ZettaTrade.Infrastructure.Tests.Persistence;

using System.Threading.Tasks;
using FluentAssertions;
using Infrastructure.Persistence;
using Xunit;

/// <summary>
/// Tests for the startup model pre-warm (T-237): the once-per-process kick
/// settles every shipped context model warm (the throwaway in-memory
/// options never open a connection or touch a file), a second kick is a
/// no-op, and the outcome task settles exactly once and never faults —
/// the walk's own first touch builds the model exactly as before, so the
/// helper's degraded modes stay invisible to correctness.
/// </summary>
public sealed class PersistenceModelPrewarmTests
{
    [Fact]
    public async Task Start_KicksEveryShippedModelWarm_OncePerProcess()
    {
        // Two kicks: the second is the idempotency observable — the same
        // settled outcome answers, no second build wave, no throw.
        PersistenceModelPrewarm.Start();
        PersistenceModelPrewarm.Start();

        PersistenceModelPrewarm.Started.Should().BeTrue("the kick latches the process's gate");

        var first = await PersistenceModelPrewarm.Outcome;
        var second = await PersistenceModelPrewarm.Outcome;

        first.Warmed.Should().Be(
            6,
            "the shipped context set: the five walked stores plus the PnL ledger");
        first.Failed.Should().Be(
            0,
            "the throwaway in-memory builds never fail in a healthy process");
        first.WallMs.Should().BeGreaterThanOrEqualTo(0);
        second.Should().Be(
            first,
            "the outcome settles once and every later await reads the same record");
    }
}
