namespace ZettaTrade.App.Services;

using System.Globalization;

/// <summary>
/// Composes the assistant's system prompt out of the workspace context
/// (current exchange, symbol, time frame, the latest price). Pure text
/// assembly: whatever the context knows is stated, whatever it doesn't is
/// left out — the prompt degrades, it never invents. English prompt text is
/// by design: it is model-facing instruction, not a user-facing string.
/// </summary>
public sealed class WorkspacePromptComposer
{
    #region Constants

    /// <summary>The prompt's stable identity line.</summary>
    private const string IdentityText =
        "You are the assistant inside ZettaTrade, a cross-platform desktop trading terminal.";

    /// <summary>The prompt's behavior line (honest, no invented data).</summary>
    private const string DisciplineText =
        "Answer questions about trading and the user's current market briefly and factually. " +
        "You cannot execute orders and you do not fabricate data you were not given: " +
        "if the context is missing what a question needs, say so.";

    #endregion

    #region Public Methods

    /// <summary>
    /// Compose the system prompt for one request: identity + workspace facts
    /// (exchange name, symbol, time frame, last price) + discipline.
    /// </summary>
    /// <param name="context">The workspace snapshot at send time.</param>
    /// <returns>The full system prompt text.</returns>
    public string Compose(ChatContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var builder = new System.Text.StringBuilder();
        builder.Append(IdentityText);

        if (context.HasSymbol)
        {
            builder.AppendLine();
            builder.Append(CultureInfo.InvariantCulture,
                $"Current workspace: exchange {context.ExchangeName}, symbol {context.Symbol}, time frame {context.TimeFrame}, ");

            if (context.LastPrice is { } price)
            {
                builder.Append(CultureInfo.InvariantCulture,
                    $"latest price {price.ToString("F8", CultureInfo.InvariantCulture)}");
            }
            else
            {
                builder.Append("no live price received yet");
            }
        }
        else
        {
            builder.AppendLine();
            builder.Append(CultureInfo.InvariantCulture,
                $"Current workspace: exchange {context.ExchangeName}, no symbol selected yet.");
        }

        builder.AppendLine();
        builder.Append(DisciplineText);

        return builder.ToString();
    }

    #endregion
}
