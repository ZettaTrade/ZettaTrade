namespace ZettaTrade.App.Tests.Views;

using System;
using System.IO;
using FluentAssertions;
using Xunit;

/// <summary>
/// The multi-chart grid's source pins (T-201): the grid's layout tree is
/// code-built — its rows-of-stacks grammar changes shape with the layout
/// mode and its bindings are subscribed in code (the T-175 discipline: a
/// markup binding on a tree the control rebuilds wholesale would either
/// die on the rebuild or resurrect a stale slot column) — so the axaml
/// carries only the styles the code-built handles need, and the window's
/// handover arms the slot routes before the grid can attach.
/// </summary>
public sealed class ChartSlotGridPinsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    [Fact]
    public void TheGridControl_CarriesOnlyStyles_TheTreeStaysCodeBuilt()
    {
        var axaml = File.ReadAllText(
            Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Controls", "ChartSlotGrid.axaml"));

        axaml.Should().NotContain(
            "{Binding",
            "the code-built tree subscribes its rides in code — no markup binding ever exists on a tree the rebuilds replace");
        axaml.Should().NotContain(
            "RowDefinition",
            "the grid's rows are built per mode in code, not declared in the template");
        axaml.Should().Contain(
            "RowResizeHandle",
            "the grid's styles cover the code-built horizontal seam grip");
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
