namespace ZettaTrade.App.Services;

using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using Livery.Contracts;
using Livery.Themes;
using Microsoft.Extensions.Logging;

/// <summary>
/// Derives the scrim veil from the applied theme's Background: the palette
/// carries no translucent key (the source keys publish as opaque Colors,
/// the T-150 shape), and the two overlay scrims (the palette's and the
/// hotkeys sheet's workspace veils) need exactly that translucent wash —
/// the pre-T-182 sites carried a bare <c>Opacity</c> literal riding no key,
/// so a theme could not tune its veil and the literal dodged every pin.
/// The derivation is arithmetic, not a scored pick: the applied Background
/// color, its RGB kept, its alpha set to the veil constant, published as a
/// brush (the T-150 publish-as-brush canon) under the one resource key the
/// scrims ride. Every theme apply re-derives — the veil retints live with
/// the theme the way every published key does.
/// </summary>
/// <remarks>
/// Lifecycle: the subscription rides <see cref="IAsyncInitializable"/>
/// (the startup walk owns it) and mirrors the application-lifetime
/// ThemeManager the way the chip foreground does — no explicit disposal.
/// The behavioral theme stream replays the current name on subscribe, so a
/// subscription landing after the first apply still learns the active
/// palette; the derivation is idempotent per emission (the last apply's
/// palette wins). Applies may arrive from the hot-reload pipeline's
/// background thread, so the derivation marshals onto the UI thread the
/// resource world belongs to — and runs inline when the emission already
/// carries it.
/// </remarks>
public sealed class ScrimBrushPublisher : IAsyncInitializable
{
    /// <summary>The resource key the scrim consumers ride.</summary>
    public const string ResourceKey = "ScrimBrush";

    /// <summary>The veil's alpha: 75% of the source color, as the byte the
    /// pre-T-182 sites' 0.75 Opacity composited (191 / 255).</summary>
    public const byte VeilAlpha = 191;

    private readonly ThemeManager _themes;

    private readonly ILogger<ScrimBrushPublisher> _logger;

    public ScrimBrushPublisher(
        ThemeManager themes,
        ILogger<ScrimBrushPublisher> logger)
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
            ex => _logger.LogError(ex, "The scrim brush stream stopped unexpectedly"));

        return Task.CompletedTask;
    }

    /// <summary>The derivation: the applied Background from the resource
    /// world, the alpha veil, the publish. A world without a Background
    /// (nothing applied yet) or without an application stays untouched —
    /// the scrim carries no veil there, and the ambient background is the
    /// honest starter.</summary>
    private void DeriveAndPublish()
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        if (!app.Resources.TryGetValue("Background", out var value) ||
            value is not Color background)
        {
            return;
        }

        app.Resources[ResourceKey] = new SolidColorBrush(Derive(background));
    }

    /// <summary>The arithmetic: the source color's RGB kept exactly, the
    /// alpha set to the veil constant — the composited wash the bare
    /// Opacity literal produced, now a theme-owned key.</summary>
    internal static Color Derive(Color background) =>
        new(VeilAlpha, background.R, background.G, background.B);
}
