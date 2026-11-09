namespace ZettaTrade.Soak.Tests;

using System.Text.Json;
using FluentAssertions;
using Xunit;

/// <summary>
/// The soak pipeline's JSON contracts (T-109): the driver writes the samples
/// and the budgets files, the tool answers with the summary — these facts
/// pin the exact shapes on both sides, so a key rename on one side fails the
/// other loudly instead of starving the report. Round-tripping the summary
/// also pins the serialization the docs page reads.
/// </summary>
public sealed class SoakContractTests
{
    private const string SamplesJson = """
        {
          "GeneratedAt": "2026-11-10T09:00:00.0000000Z",
          "RunSeconds": 60.0,
          "ColdStartMs": 2100.0,
          "Samples": [
            { "ElapsedSeconds": 0.0, "WorkingSetMb": 205.5, "PrivateMb": 310.25, "Gen0": 12, "Gen1": 3, "Gen2": 1, "CpuPercent": null },
            { "ElapsedSeconds": 30.0, "WorkingSetMb": 208.0, "PrivateMb": 311.5, "Gen0": 15, "Gen1": 4, "Gen2": 2, "CpuPercent": 2.5 }
          ]
        }
        """;

    private const string BudgetsJson = """{ "ColdStartMs": 3000.0, "MemoryMeanMb": 400.0, "MemoryPeakMb": 500.0 }""";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void TheDriverSamplesShape_ParsesIntoTheContract()
    {
        var run = JsonSerializer.Deserialize<SoakSamples>(SamplesJson, Options);

        run!.GeneratedAt.Should().Be("2026-11-10T09:00:00.0000000Z");
        run.RunSeconds.Should().Be(60.0);
        run.ColdStartMs!.Value.Should().Be(2100.0);
        run.Samples.Count.Should().Be(2);
        run.Samples[0].WorkingSetMb.Should().Be(205.5);
        run.Samples[0].Gen0.Should().Be(12);
        run.Samples[0].CpuPercent.Should().BeNull();
        run.Samples[1].PrivateMb.Should().Be(311.5);
        run.Samples[1].CpuPercent.Should().Be(2.5);
    }

    [Fact]
    public void TheBudgetsShape_ParsesIntoTheContract()
    {
        var budgets = JsonSerializer.Deserialize<SoakBudgets>(BudgetsJson, Options);

        budgets!.ColdStartMs.Should().Be(3000.0);
        budgets.MemoryMeanMb.Should().Be(400.0);
        budgets.MemoryPeakMb.Should().Be(500.0);
    }

    [Fact]
    public void TheSummary_RoundTripsWithTheExactKeysTheDriverReads()
    {
        var run = new SoakSamples
        {
            GeneratedAt = "2026-11-10T09:00:00.0000000Z",
            RunSeconds = 60.0,
            ColdStartMs = 2100.0,
            Samples = [
                new SoakSample(0.0, 205.5, 310.25, Gen0: 12, Gen1: 3, Gen2: 1, CpuPercent: null),
                new SoakSample(30.0, 208.0, 311.5, Gen0: 15, Gen1: 4, Gen2: 2, CpuPercent: 2.5),
            ],
        };
        var budgets = new SoakBudgets
        {
            ColdStartMs = 3000,
            MemoryMeanMb = 400,
            MemoryPeakMb = 500,
        };

        var summary = SoakMath.Summarize(run, budgets, generatedAt: "2026-11-10T13:00:00.0000000Z");

        var json = JsonSerializer.Serialize(summary, Options);

        json.Should().Contain("\"GeneratedAt\"");
        json.Should().Contain("\"WorkingSetSlopeMbPerHour\"");
        json.Should().Contain("\"PrivateSlopeMbPerHour\"");
        json.Should().Contain("\"Gc\"");
        json.Should().Contain("\"Verdicts\"");
        json.Should().Contain("\"OverallOk\"");
        json.Should().Contain("\"ToolStatus\"");

        var restored = JsonSerializer.Deserialize<SoakSummary>(json, Options);

        restored!.OverallOk.Should().Be(summary.OverallOk);
        restored.SampleCount.Should().Be(summary.SampleCount);
        restored.WorkingSetSlopeMbPerHour.Should().Be(summary.WorkingSetSlopeMbPerHour);
        restored.PrivateSlopeMbPerHour.Should().Be(summary.PrivateSlopeMbPerHour);
        restored.Gc!.Sampled.Should().BeTrue();
        restored.Gc.Gen0.Should().Be(3, "the last sample's Gen0 (15) minus the first's (12) is the run's tally.");
        restored.Verdicts.Count.Should().Be(3);
        restored.Verdicts.Count(v => v.Ok).Should().Be(summary.Verdicts.Count(v => v.Ok));
    }

    [Fact]
    public void TheBudgetsShape_NullColdStart_ParsesAsAdvisory()
    {
        var budgets = JsonSerializer.Deserialize<SoakBudgets>("""{ "ColdStartMs": null, "MemoryMeanMb": 400.0, "MemoryPeakMb": 500.0 }""", Options);

        budgets!.ColdStartMs.Should().BeNull("an explicit null is the advisory contract, not the default.");
        budgets.MemoryMeanMb.Should().Be(400.0);
        budgets.MemoryPeakMb.Should().Be(500.0);
    }
}
