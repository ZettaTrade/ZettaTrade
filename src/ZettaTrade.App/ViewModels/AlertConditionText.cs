namespace ZettaTrade.App.ViewModels;

using Livery.Localization;
using ZettaTrade.App.Services;

/// <summary>
/// The alert condition line's one composer (T-204): the manager table's
/// condition column and the notification row's alert-fire summary render
/// the SAME vocabulary from the SAME definition, so the composition lives
/// in exactly one place — text composes at display time from the
/// localization catalog, never carried inside an event.
/// </summary>
public static class AlertConditionText
{
    #region Resx Keys

    private const string ConditionAboveKey = "Alert.Condition.Price.Above";
    private const string ConditionBelowKey = "Alert.Condition.Price.Below";
    private const string ConditionAtKey = "Alert.Condition.Price.At";
    private const string ConditionPercentFormatKey = "Alert.Condition.PercentMove.Format";
    private const string ConditionIndicatorValueAboveFormatKey = "Alert.Condition.Indicator.ValueAbove.Format";
    private const string ConditionIndicatorValueBelowFormatKey = "Alert.Condition.Indicator.ValueBelow.Format";
    private const string ConditionPriceAboveFormatKey = "Alert.Condition.Indicator.PriceAbove.Format";
    private const string ConditionPriceBelowFormatKey = "Alert.Condition.Indicator.PriceBelow.Format";

    #endregion Resx Keys

    /// <summary>
    /// Compose the definition's condition line: the kind's own localized
    /// frame; a kind or edge the vocabulary does not name composes the
    /// honest empty string (the manager column renders it as nothing).
    /// </summary>
    /// <param name="definition">The definition whose kind and facts compose.</param>
    /// <param name="localization">The localization catalog the line resolves from.</param>
    /// <returns>The condition line.</returns>
    public static string Compose(AlertDefinition definition, ILocalizationManager localization) =>
        definition.Kind switch
        {
            PriceAlertKind.Price => definition.Trigger switch
            {
                AlertTrigger.Above => localization.GetString(ConditionAboveKey, definition.TargetPrice),
                AlertTrigger.Below => localization.GetString(ConditionBelowKey, definition.TargetPrice),
                AlertTrigger.At => localization.GetString(ConditionAtKey, definition.TargetPrice),
                _ => string.Empty,
            },
            PriceAlertKind.PercentMove => localization.GetString(
                ConditionPercentFormatKey,
                definition.ThresholdPercent,
                definition.WindowMinutes),
            PriceAlertKind.IndicatorCross => definition.Indicator is not { } spec
                ? string.Empty
                : (definition.Trigger, spec.Source) switch
                {
                    (AlertTrigger.Above, AlertCrossSource.IndicatorValue) =>
                        localization.GetString(ConditionIndicatorValueAboveFormatKey, spec.Name, spec.Period, spec.Level),
                    (AlertTrigger.Below, AlertCrossSource.IndicatorValue) =>
                        localization.GetString(ConditionIndicatorValueBelowFormatKey, spec.Name, spec.Period, spec.Level),
                    (AlertTrigger.Above, AlertCrossSource.Price) =>
                        localization.GetString(ConditionPriceAboveFormatKey, spec.Name, spec.Period),
                    (AlertTrigger.Below, AlertCrossSource.Price) =>
                        localization.GetString(ConditionPriceBelowFormatKey, spec.Name, spec.Period),
                    _ => string.Empty,
                },
            _ => string.Empty,
        };
}
