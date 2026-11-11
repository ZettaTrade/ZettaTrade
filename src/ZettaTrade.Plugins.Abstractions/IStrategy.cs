namespace ZettaTrade.Strategies;

/// <summary>
/// The strategy implementation contract of the seam (T-111, contract
/// 2.1.0): a plugin assembly offers concrete <see cref="IStrategy"/> classes
/// the host's catalog discovers and the host keeps evaluating on every
/// closed candle. The evaluation is expected pure and cheap - the host
/// recomputes it per closed candle over the retained window - and
/// deterministic: the same candle sequence replayed through the same
/// implementation yields the same stance sequence.
/// </summary>
/// <remarks>
/// The load convention mirrors the indicator seam: a public parameterless
/// constructor is the documented shape (the loader probes it and reads the
/// id off the built instance), the <see cref="Id"/> is fixed by the
/// implementation and must be non-whitespace. One registration serves
/// every (symbol, interval) series - the context carries the series
/// identity, so an implementation must never carry series state in
/// instance fields. Returning a stance never places an order: the signals
/// channel is inert output and the human decides.
/// </remarks>
public interface IStrategy
{
    /// <summary>Canonical strategy id, fixed by the implementation (e.g. "sma-golden-cross").</summary>
    string Id { get; }

    /// <summary>
    /// Evaluate the strategy's stance at the LAST closed candle of the
    /// context (evaluate on candle close): one stance per call - the
    /// long/flat/neutral reading this implementation holds right now.
    /// A window too short to judge is answered with
    /// <see cref="StrategyStance.Neutral"/>, never a fabricated stance, and
    /// a null context is rejected with <see cref="ArgumentNullException"/>
    /// - the standing no-substitution rule, nothing defaults.
    /// </summary>
    StrategyStance Evaluate(StrategyContext context);
}
