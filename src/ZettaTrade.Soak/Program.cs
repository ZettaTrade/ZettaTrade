namespace ZettaTrade.Soak;

using System.Text.Json;

/// <summary>
/// The soak tool's entry point: reads the samples file and the budgets file
/// the driver prepared (both JSON), assembles the summary and writes it as
/// JSON. It prints NOTHING (stdout belongs to the MCP protocol surface) and
/// speaks only through its exit code: 0 = pass, 3 = budget breach, 2 = input
/// error. The driver renders the report from the summary text.
/// </summary>
internal static class Program
{
    /// <summary>Exit codes: how the tool's verdict reaches the PowerShell driver.</summary>
    private enum ExitCode
    {
        Pass = 0,
        Input = 2,
        Breach = 3,
    }

    /// <summary>The exact JSON options the driver's files and the summary share.</summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// args: samplesPath, budgetsPath, summaryPath.
    /// </summary>
    private static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            return (int)ExitCode.Input;
        }

        try
        {
            var run = Load<SoakSamples>(args[0]);
            var budgets = Load<SoakBudgets>(args[1]);

            var summary = SoakMath.Summarize(
                run,
                budgets,
                generatedAt: DateTime.UtcNow.ToString("o"),
                toolStatus: "ok");

            WriteSummary(args[2], summary);

            return summary.OverallOk ? (int)ExitCode.Pass : (int)ExitCode.Breach;
        }
        catch (Exception fault) when (IsInputFault(fault))
        {
            var run = TryLoad<SoakSamples>(args[0]);
            var budgets = TryLoad<SoakBudgets>(args[1]);

            WriteSummary(args[2], SoakMath.Summarize(
                run,
                budgets,
                generatedAt: DateTime.UtcNow.ToString("o"),
                toolStatus: $"input-error: {fault.Message}"));

            return (int)ExitCode.Input;
        }
    }

    /// <summary>Reads and parses one JSON input file; an unreadable file is an input fault.</summary>
    private static T Load<T>(string path)
    {
        var text = File.ReadAllText(path);

        return JsonSerializer.Deserialize<T>(text, Json)
            ?? throw new InvalidDataException($"'{path}' parsed to null.");
    }

    /// <summary>Best-effort input for the error summary; an unreadable file degrades to a fresh instance.</summary>
    private static T TryLoad<T>(string path)
        where T : new()
    {
        try
        {
            return Load<T>(path);
        }
        catch (Exception)
        {
            return new T();
        }
    }

    /// <summary>Writes the summary JSON next to the samples the driver left.</summary>
    private static void WriteSummary(string path, SoakSummary summary)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(summary, Json));
    }

    /// <summary>The faults that are the inputs' (unreadable/unparseable), not the tool's.</summary>
    private static bool IsInputFault(Exception fault) =>
        fault is IOException or JsonException or InvalidOperationException or InvalidDataException;
}
