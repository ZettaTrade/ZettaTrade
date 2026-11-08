namespace ZettaTrade.App.ViewModels;

using ReactiveUI;

/// <summary>
/// One manager-table row (T-096): the persisted alert's rendered facts. Every
/// text is computed at render time from the definition and the localized
/// manager — a language switch re-renders the whole row set, so no value is
/// ever a static string bound once.
/// </summary>
public sealed class AlertsRowViewModel : ReactiveObject
{
    #region Private Fields

    private string _symbolText = string.Empty;
    private string _conditionText = string.Empty;
    private string _statusText = string.Empty;
    private string _lastFiredText = string.Empty;
    private string _noteText = string.Empty;

    #endregion Private Fields

    /// <summary>The alert's identity (the engine's operations key on it).</summary>
    public Guid Id { get; init; }

    /// <summary>The alert's normalized symbol.</summary>
    public string SymbolText
    {
        get => _symbolText;
        set => this.RaiseAndSetIfChanged(ref _symbolText, value);
    }

    /// <summary>The localized condition line (above/below/at-level or the percent-move window).</summary>
    public string ConditionText
    {
        get => _conditionText;
        set => this.RaiseAndSetIfChanged(ref _conditionText, value);
    }

    /// <summary>The localized lifecycle line (Armed / Snoozed until X / Fired).</summary>
    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    /// <summary>The localized last-fired stamp (never fired is an explicit absence).</summary>
    public string LastFiredText
    {
        get => _lastFiredText;
        set => this.RaiseAndSetIfChanged(ref _lastFiredText, value);
    }

    /// <summary>The user's own free-text note.</summary>
    public string NoteText
    {
        get => _noteText;
        set => this.RaiseAndSetIfChanged(ref _noteText, value);
    }
}
