namespace ZettaTrade.UI.Tests.Localization;

using Xunit;

/// <summary>
/// Marker collection: classes whose facts share the static
/// <c>LocalizationManager.Instance</c> singleton run strictly sequentially.
/// Every <see cref="LocalizationManager"/> construction re-registers the
/// singleton, so two manager-creating classes running concurrently would
/// resolve each other's just-switched language (the
/// previously-observed ChartTickCulture interference).
/// </summary>
[CollectionDefinition("LocalizationManagerSingleton")]
public sealed class LocalizationManagerSingletonCollection;
