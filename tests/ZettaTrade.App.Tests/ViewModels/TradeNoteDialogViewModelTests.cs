namespace ZettaTrade.App.Tests.ViewModels;

using System;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.ViewModels;

/// <summary>
/// Tests for the trade note-and-tags dialog view model (T-211): the tags
/// normalize into the canonical comma-joined token list (split, trim, drop
/// empties, dedupe case-insensitively with the first-seen case winning),
/// the note trims, an applied empty result clears honestly (both fields
/// null — there is no refusal path), and the prefill restores the row's
/// stored annotations exactly.
/// </summary>
public sealed class TradeNoteDialogViewModelTests
{
    [Fact]
    public void BuildResult_NormalizesTheTagsIntoTheCanonicalJoinedList()
    {
        var viewModel = new TradeNoteDialogViewModel
        {
            NoteInput = "  scaled out at the level  ",
            TagsInput = " Swing , swing-trade ,, , SCALP ,swing ",
        };
        viewModel.BuildResult();

        viewModel.ChosenNote.Should().Be("scaled out at the level");
        viewModel.ChosenTags.Should().Be("Swing,swing-trade,SCALP",
            "empties drop, duplicates fold case-insensitively, the first-seen case wins");
    }

    [Fact]
    public void BuildResult_AnAppliedEmptyResultClearsBothFields()
    {
        // The honest removal: an applied blank dialog clears the row's
        // annotations - there is no refusal path.
        var viewModel = new TradeNoteDialogViewModel
        {
            NoteInput = "   ",
            TagsInput = " , ,, ",
        };
        viewModel.BuildResult();

        viewModel.ChosenNote.Should().BeNull();
        viewModel.ChosenTags.Should().BeNull();
    }

    [Fact]
    public void Initialize_PrefillsTheInputsFromTheStoredAnnotations()
    {
        var viewModel = new TradeNoteDialogViewModel();
        viewModel.Initialize("the stored note", "swing,scalp");

        viewModel.NoteInput.Should().Be("the stored note");
        viewModel.TagsInput.Should().Be("swing,scalp");

        viewModel.BuildResult();
        viewModel.ChosenNote.Should().Be("the stored note", "the prefill round-trips unchanged");
        viewModel.ChosenTags.Should().Be("swing,scalp");
    }

    [Fact]
    public void Initialize_WithNulls_PrefillsTheHonestEmpty()
    {
        var viewModel = new TradeNoteDialogViewModel();
        viewModel.Initialize(null, null);

        viewModel.NoteInput.Should().BeEmpty();
        viewModel.TagsInput.Should().BeEmpty();
    }
}
