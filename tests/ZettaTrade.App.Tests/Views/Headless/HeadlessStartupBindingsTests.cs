namespace ZettaTrade.App.Tests.Views.Headless;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.LogicalTree;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.ViewModels;
using ZettaTrade.App.Views;
using ZettaTrade.App.Views.Panels;

/// <summary>
/// The startup binding sweep's pins (T-175): the app's construction — the
/// view-model-carrying <c>MainWindow(MainViewModel)</c> ctor — evaluates
/// every startup binding against a live graph and logs NOTHING at or above
/// the Warning level from the Binding source, while the menu still renders
/// the effective gesture hints and the workspace panels carry their own
/// view models (the T-103 LocalValues, now by construction). The designer
/// shape — the parameterless ctor with the late DataContext assignment — is
/// pinned as the counterfactual: it reproduces the null-DataContext binding
/// noise the sweep killed.
/// </summary>
[Collection("LocalizationManagerSingleton")]
public sealed class HeadlessStartupBindingsTests
{
    static HeadlessStartupBindingsTests() => AvaloniaDispatcherPump.EnsureInstalled();

    /// <summary>The {Name} placeholder matcher the sequential fill rides.</summary>
    private static readonly Regex PlaceholderPattern = new(@"\{[A-Za-z][A-Za-z0-9]*\}", RegexOptions.Compiled);

    [Fact]
    public async Task TheStartupConstruction_LogsNoBindingNoiseAndRendersTheGestureHints()
    {
        var world = await MainWorld.CreateAsync();
        var capture = new CaptureSink();
        var previous = Logger.Sink;

        Logger.Sink = capture;
        try
        {
            var window = await HeadlessWorld.OnUi(() => new MainWindow(world.Main));
            await HeadlessWorld.ShowAndSettleAsync(window);

            capture.Entries
                .Where(entry => entry.Level >= LogEventLevel.Warning && entry.Source == "Binding")
                .Should().BeEmpty("the startup construction resolves every binding against the live graph - no binding errors may reach the debug output");

            var gestures = await HeadlessWorld.OnUi(() => window.GetLogicalDescendants()
                .OfType<MenuItem>()
                .Select(item => item.InputGesture?.ToString())
                .Where(gesture => gesture is not null)
                .ToList());
            gestures.Should().BeEquivalentTo(
                ["Alt+F4", "Ctrl+OemComma", "Ctrl+N", "Ctrl+Shift+A"],
                "the menu renders the effective gesture hints - the static exit hint and the three typed hotkey hints");

            var panels = await HeadlessWorld.OnUi(() => (
                Chart: HeadlessWorld.Descendants<ChartPanelView>(window).Single().DataContext,
                Book: HeadlessWorld.Descendants<OrderBookPanelView>(window).Single().DataContext,
                Assistant: HeadlessWorld.Descendants<AssistantPanelView>(window).Single().DataContext));
            panels.Chart.Should().BeOfType<ChartViewModel>("the chart panel carries its own VM as a LocalValue from construction (the T-103 discipline)");
            panels.Book.Should().BeOfType<OrderBookViewModel>("the book panel carries its own VM as a LocalValue from construction (the T-103 discipline)");
            panels.Assistant.Should().BeOfType<ChatViewModel>("the assistant panel carries its own VM as a LocalValue from construction (the T-103 discipline)");

            await HeadlessWorld.CloseAndSettleAsync(window);
        }
        finally
        {
            Logger.Sink = previous;
        }
    }

    [Fact]
    public async Task TheDesignerShapeLateAssignment_ReproducesTheBindingNoise()
    {
        var world = await MainWorld.CreateAsync();
        var capture = new CaptureSink();
        var previous = Logger.Sink;

        Logger.Sink = capture;
        try
        {
            var window = await HeadlessWorld.OnUi(() => new MainWindow { DataContext = world.Main });
            await HeadlessWorld.ShowAndSettleAsync(window);

            capture.Entries
                .Where(entry => entry.Level >= LogEventLevel.Warning && entry.Source == "Binding")
                .Should().NotBeEmpty("the parameterless ctor with the late DataContext assignment evaluates the $parent[Window] lanes while the DataContext is still unset - the defect shape the startup sweep killed");

            await HeadlessWorld.CloseAndSettleAsync(window);
        }
        finally
        {
            Logger.Sink = previous;
        }
    }

    /// <summary>The binding-noise capture: every Avalonia logger record, with its level, source and formatted message.</summary>
    private sealed class CaptureSink : ILogSink
    {
        public List<(LogEventLevel Level, string Source, string Message)> Entries { get; } = [];

        public bool IsEnabled(LogEventLevel level, string source) => true;

        public void Log(LogEventLevel level, string source, object? sourceObject, string messageTemplate) =>
            Record(level, source, messageTemplate, null);

        public void Log(LogEventLevel level, string source, object? sourceObject, string messageTemplate, params object?[]? propertyValues) =>
            Record(level, source, messageTemplate, propertyValues);

        private void Record(LogEventLevel level, string source, string messageTemplate, object?[]? propertyValues)
        {
            string message;
            try
            {
                if (propertyValues is null || propertyValues.Length == 0)
                {
                    message = messageTemplate;
                }
                else
                {
                    // Avalonia's templates carry named placeholders ({Property},
                    // {Expression}...): fill them sequentially from the values.
                    var index = 0;
                    message = PlaceholderPattern.Replace(messageTemplate, _ =>
                        index < propertyValues.Length ? propertyValues[index++]?.ToString() ?? "null" : "?");
                }
            }
            catch (FormatException)
            {
                message = messageTemplate;
            }

            Entries.Add((level, source, message));
        }
    }
}
