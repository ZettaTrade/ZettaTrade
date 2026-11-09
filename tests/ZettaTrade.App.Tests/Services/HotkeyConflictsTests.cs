namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using Avalonia.Input;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;

/// <summary>
/// The hotkey grammar's goldens (T-102): the canonicalization rides
/// Avalonia's own parser and lands one spelling per meaning, blank or
/// garbage strings answer the null verdict, a modifier-less gesture is
/// named forbidden, and the conflict pair comes out in the input's own
/// order so a shared gesture always blames the same two actions.
/// </summary>
public sealed class HotkeyConflictsTests
{
    [Fact]
    public void Normalize_CanonicalizesModifierSpellings()
    {
        // "Control" and "Ctrl" spell the same gesture — one canonical
        // string comes out of both, and it re-parses to the same gesture
        // (the round trip the editor's every later diagnosis rides).
        var spelled = HotkeyConflicts.Normalize("Ctrl+N");
        var verbose = HotkeyConflicts.Normalize("Control+N");

        verbose.Should().NotBeNull("'Control+N' is a parseable gesture");
        verbose.Should().Be(spelled, "both spellings round into one canonical form");
        KeyGesture.Parse(verbose!).Key.Should().Be(Key.N);
        KeyGesture.Parse(verbose!).KeyModifiers.Should().Be(KeyModifiers.Control);
    }

    [Fact]
    public void Normalize_BlankOrUnparseableTextAnswersNull()
    {
        HotkeyConflicts.Normalize(null).Should().BeNull("a blank row edits nothing");
        HotkeyConflicts.Normalize(string.Empty).Should().BeNull();
        HotkeyConflicts.Normalize("   ").Should().BeNull("whitespace is a blank");

        HotkeyConflicts.Normalize("What!").Should().BeNull(
            "the parser refuses a non-gesture and the verdict stays a refusal, never a crash");
    }

    [Fact]
    public void IsPlain_NamesTheModifierLessVerdict()
    {
        HotkeyConflicts.IsPlain(HotkeyConflicts.Normalize("N")!).Should().BeTrue(
            "a bare letter would fire while the user types");
        HotkeyConflicts.IsPlain(HotkeyConflicts.Normalize("Ctrl+N")!).Should().BeFalse();
        HotkeyConflicts.IsPlain(HotkeyConflicts.Normalize("Ctrl+Shift+O")!).Should().BeFalse();
    }

    [Fact]
    public void FindDuplicateGesture_NamesThePairInTheScanOrder()
    {
        var duplicate = HotkeyConflicts.FindDuplicateGesture(
            [
                ("Settings.Open", "Ctrl+OemComma"),
                ("Order.New", "Ctrl+K"),
                ("AiChat.Toggle", "Ctrl+Shift+A"),
                ("Palette.Toggle", "Ctrl+K"),
            ]);

        duplicate.Should().NotBeNull("the two actions share a gesture");
        duplicate!.Value.FirstId.Should().Be("Order.New", "the pair carries the input's own order");
        duplicate.Value.SecondId.Should().Be("Palette.Toggle");
    }

    [Fact]
    public void FindDuplicateGesture_UniqueOrEmptyList_NeverNamesThePair()
    {
        HotkeyConflicts.FindDuplicateGesture(
            [
                ("Settings.Open", "Ctrl+OemComma"),
                ("Order.New", "Ctrl+N"),
            ]).Should().BeNull("every gesture is unique");
        HotkeyConflicts.FindDuplicateGesture([]).Should().BeNull("nothing to compare");
    }
}
