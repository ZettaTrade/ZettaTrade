namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using Livery.Contracts;
using Livery.Themes;
using Microsoft.Extensions.Logging;

/// <summary>
/// The readable-chip foreground (T-152): every theme apply re-derives the
/// text color the state-colored chips ride — the status-bar lane chip fills
/// with CandleUp/Highlight/Error, the ticket's testnet badge and the panels'
/// badges with Highlight, the notification count badge with Highlight — and
/// the inherited theme foreground fails the pill-sized WCAG bar on those
/// fills (white over #22c55e measures ~2.3:1; the light theme's black over
/// its own darker fills ~3.8:1). No fixed foreground serves both worlds:
/// the dark themes ship bright fills (the readable text is near-black),
/// the light theme ships darker fills (the readable text is near-white).
/// The publisher therefore derives per apply: it reads the applied fills
/// from the application's resource world, scores the two achromatic
/// candidates through Livery's public <see cref="Contrast.Ratio"/> (the
/// same math the theme battery pins), picks the candidate whose WORST pair
/// reads best — the extremes maximize the contrast against any fill, and
/// against any single fill at least one achromatic candidate clears 4.5:1
/// — and publishes it as the "ChipForeground" brush (the T-150 canon:
/// brush keys publish as brushes). The chips ride
/// {DynamicResource ChipForeground} and stay legible under every palette
/// without knowing which one runs. A user palette pairing a bright and a
/// dark fill on the same surface can deny both candidates; the derivation
/// keeps the better of the two (best-effort), and the shipped themes'
/// own battery guards their margins.
/// </summary>
/// <remarks>
/// Lifecycle: the subscription rides <see cref="IAsyncInitializable"/>
/// (the startup walk owns it) and mirrors the application-lifetime
/// ThemeManager the way the status bar does — no explicit disposal. The
/// behavioral theme stream replays the current name on subscribe, so a
/// subscription landing after the first apply still learns the active
/// palette; the derivation is idempotent per emission (the last apply's
/// palette wins). Applies may arrive from the hot-reload pipeline's
/// background thread, so the derivation marshals onto the UI thread the
/// resource world belongs to — and runs inline when the emission already
/// carries it.
/// </remarks>
public sealed class ChipForegroundPublisher : IAsyncInitializable
{
    /// <summary>The resource key the chip consumers ride.</summary>
    public const string ResourceKey = "ChipForeground";

    /// <summary>The achromatic candidates, in argmax tie order.</summary>
    private static readonly string[] Candidates = ["#000000", "#ffffff"];

    /// <summary>The palette keys the chip consumers paint as fills.</summary>
    private static readonly string[] ChipFillKeys = ["CandleUp", "Highlight", "Error"];

    private readonly ThemeManager _themes;

    private readonly ILogger<ChipForegroundPublisher> _logger;

    public ChipForegroundPublisher(
        ThemeManager themes,
        ILogger<ChipForegroundPublisher> logger)
    {
        _themes = themes;
        _logger = logger;
    }

    /// <inheritdoc cref="IAsyncInitializable.InitializeAsync"/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        _themes.ThemeChanged.Subscribe(
            _ =>
            {
                if (Dispatcher.UIThread.CheckAccess())
                {
                    DeriveAndPublish();
                }
                else
                {
                    Dispatcher.UIThread.Post(DeriveAndPublish);
                }
            },
            ex => _logger.LogError(ex, "The chip foreground stream stopped unexpectedly"));

        return Task.CompletedTask;
    }

    /// <summary>The derivation: the applied fills from the resource world,
    /// the scoring, the publish. A world without fills (nothing applied
    /// yet) or without an application stays untouched — the chip carries
    /// no fill there either, and the ambient foreground is the honest
    /// starter.</summary>
    private void DeriveAndPublish()
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var fills = new List<string>();
        foreach (var fillKey in ChipFillKeys)
        {
            if (!app.Resources.TryGetValue(fillKey, out var value) ||
                value is not Color fill)
            {
                return;
            }

            fills.Add($"#{fill.R:X2}{fill.G:X2}{fill.B:X2}");
        }

        app.Resources[ResourceKey] =
            new SolidColorBrush(Color.Parse(ChooseForeground(fills)));
    }

    /// <summary>The candidate scoring: every candidate against every fill
    /// (Livery's public contrast math — the battery's own), the best
    /// worst-case wins, and the candidate order keeps a tie deterministic.
    /// </summary>
    internal static string ChooseForeground(IReadOnlyCollection<string> fills)
    {
        string best = Candidates[0];
        double bestWorst = WorstCase(best, fills);

        foreach (var candidate in Candidates.Skip(1))
        {
            var worst = WorstCase(candidate, fills);
            if (worst > bestWorst)
            {
                best = candidate;
                bestWorst = worst;
            }
        }

        return best;
    }

    private static double WorstCase(string foreground, IReadOnlyCollection<string> fills) =>
        fills.Min(fill => Contrast.Ratio(foreground, fill));
}
