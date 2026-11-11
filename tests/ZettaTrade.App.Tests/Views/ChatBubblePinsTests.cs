namespace ZettaTrade.App.Tests.Views;

using System;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

/// <summary>
/// The chat bubble pins (T-158): the assistant chat speaks bubbles — the
/// trader's turn right on the Highlight fill with the derived ChipForeground
/// caption (the T-152-scored pairing, the kit's chip treatment), the
/// assistant's answer left on the Surface card, an honest failure line bare
/// in the Error color, each row's header (role, moment, copy) carried INSIDE
/// the bubble, long answers wrapping in a bounded shape. The copy affordance
/// is view-side (the view-model stays clipboard-free) and the Stop button
/// rides the same input row. The repo has no headless render harness yet
/// (the T-144 lane is queued), so these facts walk the sources of truth
/// (the table grammar pins' precedent).
/// </summary>
public sealed class ChatBubblePinsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    /// <summary>The chat panel source, normalized to LF.</summary>
    private static string PanelXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "Panels", "AssistantPanelView.axaml"))
            .Replace("\r\n", "\n");

    /// <summary>The chat panel's code-behind.</summary>
    private static string PanelCodeBehind =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "Panels", "AssistantPanelView.axaml.cs"));

    /// <summary>The chat view-model source (the kind-class facts).</summary>
    private static string ChatViewModelSource =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "ViewModels", "ChatViewModel.cs"));

    private static string EnglishResx =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.UI", "Resources", "Strings.resx"));

    private static string RussianResx =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.UI", "Resources", "Strings.ru.resx"));

    [Fact]
    public void TheChatPanel_SpeaksBubblesOnTheSharedBorder()
    {
        var source = PanelXaml;

        // The ListBox is gone: the messages are never selectable data rows,
        // the scrolling stack of bubbles is the honest host.
        source.Should().NotContain("<ListBox", "the selection-free list-as-ListBox shape is the T-158 rewrite's target");
        source.Should().Contain("<ScrollViewer HorizontalScrollBarVisibility=\"Disabled\">", "the bubbles scroll without a horizontal run-out");
        source.Should().Contain("<ItemsControl ItemsSource=\"{Binding ChatMessages}\"", "the stack of bubbles rides the ItemsControl");

        // One shared Border carries the kind classes the view-model computes.
        source.Should().Contain("<Style Selector=\"Border.message\">", "the bubble scaffold lives once");
        foreach (var variant in new[] { "user", "assistant", "error" })
        {
            source.Should().Contain(
                $"<Style Selector=\"Border.message.{variant}\">",
                "the {0} variant is a class on the same bubble, never a second scaffold", variant);
        }

        foreach (var binding in new[]
                 {
                     "Classes.user=\"{Binding IsUserTurn}\"",
                     "Classes.assistant=\"{Binding IsAssistantTurn}\"",
                     "Classes.error=\"{Binding IsErrorLine}\"",
                 })
        {
            source.Should().Contain(binding, "the kind classes ride the view-model's computed facts");
        }
    }

    [Fact]
    public void TheBubbleVariants_RideTheScoredInkPairings()
    {
        // The user turn: the Highlight fill with the derived ChipForeground
        // caption — the exact pairing the T-152 contrast publisher scores
        // (and the same one the kit blesses for chips), positioned right.
        var userBlock = StyleBlock(PanelXaml, "Border.message.user");
        userBlock.Should().Contain("Property=\"Background\" Value=\"{DynamicResource Highlight}\"");
        userBlock.Should().Contain("Property=\"TextElement.Foreground\" Value=\"{DynamicResource ChipForeground}\"");
        userBlock.Should().Contain("<Setter Property=\"HorizontalAlignment\" Value=\"Right\" />");

        // The assistant's answer: the Surface card with the explicit
        // TextPrimary ink, left of the column.
        var assistantBlock = StyleBlock(PanelXaml, "Border.message.assistant");
        assistantBlock.Should().Contain("Property=\"Background\" Value=\"{DynamicResource Surface}\"");
        assistantBlock.Should().Contain("Property=\"TextElement.Foreground\" Value=\"{DynamicResource TextPrimary}\"");

        // The failure line: bare — no fill, no border, the Error ink.
        var errorBlock = StyleBlock(PanelXaml, "Border.message.error");
        errorBlock.Should().Contain("Property=\"Background\" Value=\"Transparent\"");
        errorBlock.Should().Contain("Property=\"TextElement.Foreground\" Value=\"{DynamicResource Error}\"");

        // The unscored pairing stays out: never Accent fill under a
        // chip-scored caption (the light palette fails it, T-152).
        PanelXaml.Should().NotContain(
            "DynamicResource Accent",
            "the Accent fill has no contrast score against ChipForeground — the bubbles ride the scored keys only");
    }

    [Fact]
    public void TheBubble_CarriesItsHeaderInsideAndBoundsItsWrap()
    {
        var source = PanelXaml;

        // The header (role, moment, copy) lives inside the bubble: the
        // template's message Border opens before the role label binds.
        var borderIndex = source.IndexOf(
            "<Border Classes=\"message\"",
            StringComparison.Ordinal);
        var roleIndex = source.IndexOf("Text=\"{Binding Role}\"", StringComparison.Ordinal);
        borderIndex.Should().BePositive("the bubble Border opens the message template");
        roleIndex.Should().BeGreaterThan(borderIndex, "the header row is carried inside the bubble");

        // The content wraps inside a bounded bubble (the detached window's
        // column is wide; the bubble caps the answer's run-out at 480).
        source.Should().Contain("Property=\"MaxWidth\" Value=\"480\"", "the bubble bounds the wrap");
        source.Should().Contain("TextWrapping=\"Wrap\"", "long answers wrap inside the bound");
    }

    [Fact]
    public void TheCopyPin_IsTheQuietViewSideAffordance()
    {
        var source = PanelXaml;

        // The pin: a quiet icon button whose click routes to the view-side
        // handler (the view-model stays clipboard-free), named for the
        // screen reader and tooltiped — the icon-only button's a11y lane.
        source.Should().Contain("Classes=\"copy-pin\"", "the copy affordance is the one quiet pin");
        source.Should().Contain("Click=\"OnCopyMessageClick\"", "the copy rides the view's handler, never a command");
        source.Should().Contain(
            "AutomationProperties.Name=\"{loc:Localized AI.Chat.Copy}\"",
            "the icon-only button carries its automation name");
        source.Should().Contain(
            "ToolTip.Tip=\"{loc:Localized AI.Chat.Copy}\"",
            "the icon-only button carries its tooltip");

        // The glyph's ink is picked per bubble class deterministically (a
        // Foreground inheritance chain would leak the platform button
        // chrome's foreground instead).
        StyleBlock(source, "Border.message.user Path.copy-glyph")
            .Should().Contain("Property=\"Stroke\" Value=\"{DynamicResource ChipForeground}\"");
        StyleBlock(source, "Border.message.assistant Path.copy-glyph")
            .Should().Contain("Property=\"Stroke\" Value=\"{DynamicResource TextPrimary}\"");
        StyleBlock(source, "Border.message.error Path.copy-glyph")
            .Should().Contain("Property=\"Stroke\" Value=\"{DynamicResource Error}\"");

        // The handler: reads the pinned row off the button's DataContext and
        // hands its content to the top-level clipboard best-effort.
        var code = PanelCodeBehind;
        code.Should().Contain("private async void OnCopyMessageClick", "the view-side handler exists");
        code.Should().Contain("DataContext: ChatMessageViewModel", "the handler reads the pinned row");
        code.Should().Contain("SetTextAsync", "the handler hands the content to the clipboard");
        code.Should().Contain("catch (Exception)", "the copy is best-effort: a clipboard fault is swallowed, not a chat crash");
    }

    [Fact]
    public void TheStop_AffordsItselfInTheInputRow_WithTheResxVocabulary()
    {
        var source = PanelXaml;

        // The Stop button rides the input row, wired to the command whose
        // availability IS the affordance (the VM pins live in
        // ChatViewModelTests: locked while idle, live while a reply runs).
        source.Should().Contain(
            "Content=\"{loc:Localized AI.Chat.Stop}\"",
            "the Stop button carries the localized label");
        source.Should().Contain(
            "Command=\"{Binding StopCommand}\"",
            "the Stop button rides the command — its availability is the affordance, never a dead button");

        // The vocabulary rides the resx in both cultures.
        foreach (var (name, resx) in new[] { ("english", EnglishResx), ("russian", RussianResx) })
        {
            resx.Should().Contain("\"AI.Chat.Stop\"", "the Stop label rides the {0} resx", name);
            resx.Should().Contain("\"AI.Chat.Copy\"", "the copy pin's name/tooltip rides the {0} resx", name);
        }
    }

    [Fact]
    public void TheKindClasses_AreComputedOnTheMessageRow()
    {
        var source = ChatViewModelSource;

        // The view-model computes the row's kind facts; the panel binds
        // exactly them (the enum itself never leaks into markup).
        foreach (var fact in new[] { "IsUserTurn", "IsAssistantTurn", "IsErrorLine" })
        {
            source.Should().Contain(
                $"public bool {fact} => Kind == ChatMessageKind.",
                "the {0} fact is computed on the message row, off the Kind", fact);
        }
    }

    /// <summary>Extract one style block (its selector to its close).</summary>
    private static string StyleBlock(string source, string selector)
    {
        var match = Regex.Match(
            source,
            $"<Style Selector=\"{Regex.Escape(selector)}\">(?s).*?</Style>");
        match.Success.Should().BeTrue($"the {selector} style must exist in the panel");
        return match.Value;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ZettaTrade.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test must run inside a ZettaTrade repository checkout");
        return directory!.FullName;
    }
}
