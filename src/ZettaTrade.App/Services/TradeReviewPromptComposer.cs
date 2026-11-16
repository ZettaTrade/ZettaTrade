namespace ZettaTrade.App.Services;

using System;
using System.Text;
using ZettaTrade.App.ViewModels;

/// <summary>
/// The trade-review prompt's composer (T-255): one realized trade's
/// journal facts composed into a structured English review request over
/// the chat's existing provider surface (the T-114 market context rides
/// the system prompt already, so the review carries only the trade).
/// Static English (the AI-prompt canon — the model's language, never
/// the UI culture). Read-only by construction: the composer takes the
/// row, reads it, returns text — no service, no store, no trading
/// surface behind it. Absent facts stay absent (a null note omits its
/// line — never a fabricated "none"); the free-text fields ride a
/// deterministic cap so one long journal note cannot unbound the
/// prompt.
/// </summary>
public static class TradeReviewPromptComposer
{
    #region Constants

    /// <summary>The free-text fields' cap in characters (a journal note can run long; the prompt stays bounded).</summary>
    internal const int MaxFreeTextCharacters = 200;

    /// <summary>The truncation marker a shaved field ends with.</summary>
    private const string Ellipsis = "...";

    #endregion Constants

    #region Composition

    /// <summary>
    /// Compose the review prompt for one realized trade: the journal
    /// facts line by line (the honest absences omitted), closed by the
    /// analysis ask. The side reads off the structural flags (the
    /// display side is a localized surface — the prompt is not).
    /// </summary>
    /// <param name="row">The realized trade's journal row.</param>
    /// <returns>The English review prompt.</returns>
    public static string Compose(TradeRealizedRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var verdict = row.IsWinning ? "a win" : row.IsLosing ? "a loss" : "scratch";
        var prompt = new StringBuilder(512);

        prompt.Append("Review the following closed trade from my trading journal.");
        AppendFact(prompt, "Closed at", row.ClosedAt);
        AppendFact(prompt, "Exchange", row.Exchange);
        AppendFact(prompt, "Symbol", row.Symbol);
        AppendFact(prompt, "Side", row.IsBuySide ? "BUY" : row.IsSellSide ? "SELL" : row.Side);
        AppendFact(prompt, "Quantity", row.Quantity);
        AppendFact(prompt, "Entry price", row.EntryPrice);
        AppendFact(prompt, "Exit price", row.ExitPrice);
        AppendFact(prompt, "Realized PnL", $"{row.Pnl} ({verdict})");
        AppendFact(prompt, "Adverse excursion (MAE)", row.Mae);
        AppendFact(prompt, "Favorable excursion (MFE)", row.Mfe);
        AppendFreeText(prompt, "Journal note", row.Note);
        AppendFreeText(prompt, "Tags", row.Tags);
        prompt.Append(
            "\n\nAnalyze this trade: what the journal facts support, what they leave" +
            " open, and what a follow-up would settle. Ground every observation in" +
            " the facts above; never invent what the journal does not carry. If the" +
            " workspace context is available to you, use it; otherwise stay with" +
            " the journal facts alone.");

        return prompt.ToString();
    }

    #endregion Composition

    #region Rendering

    private static void AppendFact(StringBuilder prompt, string label, string value)
    {
        prompt.Append('\n').Append(label).Append(": ").Append(value);
    }

    /// <summary>A free-text fact: its line rides only when the text exists — the honest absence is a missing line, never a fabricated "none".</summary>
    private static void AppendFreeText(StringBuilder prompt, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        AppendFact(prompt, label, Shave(value));
    }

    private static string Shave(string text) =>
        text.Length <= MaxFreeTextCharacters
            ? text
            : text[..MaxFreeTextCharacters] + Ellipsis;

    #endregion Rendering
}
