namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using Xunit;
using ZettaTrade.App.Services;

/// <summary>
/// The crash guard's facts (T-101): the report path logs FIRST (the log entry
/// must exist before the dialog can point at it), composes the report and
/// presents the dialog exactly ONCE per process whatever fires; a presenting
/// fault never escapes the guard (the platform's own exit continues), and a
/// second report rides the log-only path. The composer keeps the summary
/// single-line and bounded, with the raw log path inside the report.
/// </summary>
public sealed class CrashGuardTests
{
    #region Tests: the composition

    /// <summary>One exception composes type + message into the summary, with the log path raw and inside the sentence.</summary>
    [Fact]
    public void AnException_ComposesTypeAndMessage_WithTheRawLogPath()
    {
        var composer = new CrashReportComposer(
            new TestLocalization(),
            @"C:\logs\zettatrade-20261108.log");
        var exception = new InvalidOperationException("The exchange closed the socket mid-read");

        var report = composer.Compose(exception);

        report.Title.Should().Be("[Log.Crash.Title]");
        report.Summary.Should().StartWith("[Log.Crash.Summary(");
        report.Summary.Should().Contain(nameof(InvalidOperationException));
        report.Summary.Should().Contain("The exchange closed the socket mid-read");
        report.LogPath.Should().Be(@"C:\logs\zettatrade-20261108.log");
        report.LogPathLine.Should().Contain(@"C:\logs\zettatrade-20261108.log");
    }

    /// <summary>The summary is single-line and bounded; a long message keeps its cap and gains the truncation marker.</summary>
    [Fact]
    public void ALongMessage_ComposesSingleLine_Bounded()
    {
        var composer = new CrashReportComposer(
            new TestLocalization(),
            @"C:\logs\log");
        var exception = new InvalidOperationException(new string('y', 600) + "\r\n" + "tail");

        var report = composer.Compose(exception);

        report.Summary.Should().NotContain("\r").And.NotContain("\n", "the dialog's single line never folds");
        report.Summary.Should().Contain(nameof(InvalidOperationException));
        report.Summary.Should().Contain(
            new string('y', 300) + "…",
            "exactly the cap lands in the summary and the truncation marks itself");
        report.Summary.Should().NotContain(
            new string('y', 301),
            "the message never runs past its cap (the y in 'Summary' sits outside the message run)");
    }

    /// <summary>An exception-less report keeps the dedicated summary sentence.</summary>
    [Fact]
    public void AnExceptionLessReport_KeepsTheDedicatedSummary()
    {
        var composer = new CrashReportComposer(new TestLocalization(), @"C:\logs\log");

        var report = composer.ComposeWithoutException();

        report.Summary.Should().StartWith("[Log.Crash.SummaryEmpty]");
        report.LogPath.Should().Be(@"C:\logs\log");
    }

    /// <summary>A whitespace-only message composes to an EMPTY frame — nothing is invented.</summary>
    [Fact]
    public void AWhitespaceMessage_ComposesEmpty_NeverDefaulted()
    {
        var composer = new CrashReportComposer(new TestLocalization(), @"C:\logs\log");

        var report = composer.Compose(new InvalidOperationException("   \r\n "));

        report.Summary.Should().Contain(nameof(InvalidOperationException), "the frame carries the type");
        report.Summary.Should().NotContain("…", "nothing truncated — the message simply holds no text");
    }

    #endregion

    #region Tests: the guard

    /// <summary>The report path logs the exception FIRST and presents the dialog with the composed report once.</summary>
    [Fact]
    public void AReport_LogsFirst_AndPresentsTheDialogOnce()
    {
        var logger = new CapturingLogger();
        var composer = new CrashReportComposer(new TestLocalization(), @"C:\logs\log");
        var presenter = new RecordingPresenter();
        var guard = new CrashGuard(logger, composer, presenter);
        var exception = new InvalidOperationException("socket closed");

        guard.ReportForTest(exception);

        var criticals = logger.EntriesFor(LogLevel.Critical).ToList();
        criticals.Should().ContainSingle().Which.Message.Should()
            .Contain("Unhandled exception caught by the crash guard", "the log entry lands FIRST — it is the dialog's whole point");
        criticals.Should().ContainSingle().Which.Exception.Should().Be(exception);
        presenter.Reports.Should().ContainSingle().Which.LogPath.Should().Be(@"C:\logs\log");
    }

    /// <summary>Whatever fires, the dialog shows exactly once: the second report stays log-only.</summary>
    [Fact]
    public void ASecondReport_StaysLogOnly_OnceTheDialogShowed()
    {
        var logger = new CapturingLogger();
        var composer = new CrashReportComposer(new TestLocalization(), @"C:\logs\log");
        var presenter = new RecordingPresenter();
        var guard = new CrashGuard(logger, composer, presenter);

        guard.ReportForTest(new InvalidOperationException("first"));
        guard.ReportForTest(new InvalidOperationException("second"));

        presenter.Reports.Should().ContainSingle("one dialog per process, whatever fires");
        logger.EntriesFor(LogLevel.Information).Should().Contain(
            e => e.Message.Contains("already presented"),
            "the second leg's story lands in the log, not in a double dialog");
    }

    /// <summary>
    /// A presenter fault never escapes the guard: the failure logs loud and the
    /// platform's own exit continues (the last-inform must never become the
    /// crash that hangs the crash).
    /// </summary>
    [Fact]
    public void APresenterFault_LogsLoud_AndNeverThrowsOutward()
    {
        var logger = new CapturingLogger();
        var composer = new CrashReportComposer(new TestLocalization(), @"C:\logs\log");
        var presenter = new RecordingPresenter(throwOnShow: true);
        var guard = new CrashGuard(logger, composer, presenter);

        FluentActions
            .Invoking(() => guard.ReportForTest(new InvalidOperationException("first")))
            .Should().NotThrow();
        logger.EntriesFor(LogLevel.Error).Should().Contain(
            e => e.Message.Contains("failed to present the crash dialog"),
            "the fault surfaces through the log, never through an exception");
    }

    /// <summary>
    /// The no-state-writes acceptance runs on the structural shape: the guard
    /// holds NO settings, secret-store or confirmation dependency, so the
    /// compose/present path cannot fail a trading write even in principle.
    /// </summary>
    [Fact]
    public void TheGuard_DependsOnNoStateWriters()
    {
        var parameters = typeof(CrashGuard)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ToList();

        parameters.Should().Contain(typeof(ILogger<CrashGuard>));
        parameters.Should().Contain(typeof(CrashReportComposer));
        parameters.Should().Contain(typeof(ICrashDialogPresenter));
        parameters.Should().HaveCount(3, "nothing beyond the report path's own seam");
    }

    #endregion

    #region Harness

    /// <summary>A bracketed-key localization fake (the canon the VM facts ride too).</summary>
    private sealed class TestLocalization : ILocalizationManager
    {
        public string CurrentLanguage => "en";

        public IReadOnlyList<LanguageInfo> AvailableLanguages { get; } = [];

        public IObservable<string> LanguageChanged => throw new NotSupportedException();

        public Task SetLanguageAsync(
            string cultureCode,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public string GetString(string key) => GetString(key, []);

        public string GetString(string key, params object[] args) =>
            args.Length == 0 ? $"[{key}]" : $"[{key}({string.Join('|', args)})]";

        public IObservable<string> GetObservable(string key) => throw new NotSupportedException();
    }

    /// <summary>The recorder presenter: counts the composed reports.</summary>
    private sealed class RecordingPresenter : ICrashDialogPresenter
    {
        private readonly bool _throwOnShow;

        internal RecordingPresenter(bool throwOnShow = false) => _throwOnShow = throwOnShow;

        internal List<CrashReport> Reports { get; } = new();

        public void ShowCrashDialog(CrashReport report)
        {
            if (_throwOnShow)
            {
                throw new InvalidOperationException("the window system is already going down");
            }

            Reports.Add(report);
        }
    }

    /// <summary>A capture logger for the report path's log-first assertion.</summary>
    private sealed class CapturingLogger : ILogger<CrashGuard>
    {
        private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = new();

        internal IEnumerable<(LogLevel Level, string Message, Exception? Exception)> EntriesFor(LogLevel level) =>
            _entries.Where(e => e.Level == level);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Add((logLevel, formatter(state, exception), exception));
    }

    #endregion
}
