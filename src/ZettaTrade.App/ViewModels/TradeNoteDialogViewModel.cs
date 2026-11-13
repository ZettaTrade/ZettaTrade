namespace ZettaTrade.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Linq;
using ReactiveUI;

/// <summary>
/// Trade note and tags dialog view model (T-211). Collects the user's
/// annotations for one journal trade: the note's free text and the tags as a
/// comma-separated list the VM normalizes into the canonical storage form —
/// tokens split on commas, trimmed, empties dropped and deduped
/// case-insensitively with the first-seen case winning. Clearing both fields
/// is the honest removal (an applied empty result clears the row's
/// annotations), so there is no refusal path: whatever the dialog returns
/// truthfully describes the user's edit. The labels bind through the
/// localization markup in the view — the VM carries no text of its own. The
/// VM is transient per open.
/// </summary>
public sealed class TradeNoteDialogViewModel : ReactiveObject
{
    #region Private Constants

    private const string TagsSeparator = ",";

    #endregion Private Constants

    #region Properties

    private string _noteInput = string.Empty;
    private string _tagsInput = string.Empty;

    /// <summary>The note's text as the user typed it; empty means no note.</summary>
    public string NoteInput
    {
        get => _noteInput;
        set => this.RaiseAndSetIfChanged(ref _noteInput, value);
    }

    /// <summary>The tags as a comma-separated user list; empty means no tags.</summary>
    public string TagsInput
    {
        get => _tagsInput;
        set => this.RaiseAndSetIfChanged(ref _tagsInput, value);
    }

    /// <summary>The normalized note the apply produced, or null when closed without one.</summary>
    public string? ChosenNote { get; private set; }

    /// <summary>The normalized canonical tag list the apply produced, or null when closed without one.</summary>
    public string? ChosenTags { get; private set; }

    #endregion Properties

    #region Public Methods

    /// <summary>Prefill the dialog for one open from the row's stored
    /// annotations (the honest stored state, empty when none). Called before
    /// the window shows.</summary>
    public void Initialize(string? note, string? tags)
    {
        NoteInput = note ?? string.Empty;
        TagsInput = tags ?? string.Empty;
    }

    /// <summary>
    /// Build the applied result from the current input: the note trims (an
    /// empty note applies as null — the honest removal), the tags normalize
    /// into the canonical comma-joined token list (empty applies as null).
    /// There is no refusal path — any input honestly describes the edit.
    /// </summary>
    public void BuildResult()
    {
        var note = NoteInput.Trim();
        ChosenNote = note.Length == 0 ? null : note;

        var tokens = new List<string>();
        foreach (var raw in TagsInput.Split(TagsSeparator))
        {
            var token = raw.Trim();
            if (token.Length == 0
                || tokens.Any(existing => string.Equals(existing, token, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            tokens.Add(token);
        }

        ChosenTags = tokens.Count == 0 ? null : string.Join(TagsSeparator, tokens);
    }

    #endregion Public Methods
}
