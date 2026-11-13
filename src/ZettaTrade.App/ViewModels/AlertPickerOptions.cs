namespace ZettaTrade.App.ViewModels;

using ZettaTrade.App.Services;

/// <summary>
/// The editor's alert-kind combo option (T-096): the canonical kind beside
/// its localized label. The combo binds the options; the ViewModel reads the
/// canonical kind back — the label is presentation only.
/// </summary>
/// <param name="Kind">The canonical alert kind the option carries.</param>
/// <param name="Label">The localized display label.</param>
public sealed record AlertKindOption(PriceAlertKind Kind, string Label);

/// <summary>
/// The editor's trigger-edge combo option (T-096): the canonical edge beside
/// its localized label, the same presentation-only pairing as the kind
/// options.
/// </summary>
/// <param name="Trigger">The canonical edge the option carries.</param>
/// <param name="Label">The localized display label.</param>
public sealed record AlertTriggerOption(AlertTrigger Trigger, string Label);

/// <summary>
/// The editor's cross-source combo option (T-203): the canonical comparison
/// source beside its localized label, the same presentation-only pairing as
/// the kind options.
/// </summary>
/// <param name="Source">The canonical cross source the option carries.</param>
/// <param name="Label">The localized display label.</param>
public sealed record AlertSourceOption(AlertCrossSource Source, string Label);

/// <summary>
/// The editor's re-arm mode combo option (T-204): the canonical mode beside
/// its localized label, the same presentation-only pairing as the kind
/// options.
/// </summary>
/// <param name="Mode">The canonical re-arm mode the option carries.</param>
/// <param name="Label">The localized display label.</param>
public sealed record AlertReArmOption(AlertReArmMode Mode, string Label);

/// <summary>
/// The editor's sound choice combo option (T-204): the canonical choice
/// beside its localized label, the same presentation-only pairing.
/// </summary>
/// <param name="Sound">The canonical sound choice the option carries.</param>
/// <param name="Label">The localized display label.</param>
public sealed record AlertSoundOption(AlertSoundChoice Sound, string Label);
