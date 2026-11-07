namespace ZettaTrade.App.Tests.Services;

using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Tests for the workspace symbol snapshot: the latest confirmed selection
/// wins, a symbol-less workspace reads as null (the bulk flows reject their
/// action with an explicit message instead of guessing a scope), and late
/// selections replace the standing one.
/// </summary>
public sealed class WorkspaceSymbolServiceTests
{
    [Fact]
    public void Constructor_WithoutAnySelection_CurrentIsNull()
    {
        // Arrange/act: a stream that never confirms a symbol.
        var service = new WorkspaceSymbolService(Observable.Never<Symbol>());

        // Assert
        service.Current.Should().BeNull();
    }

    [Fact]
    public void LatestSelection_Wins()
    {
        // Arrange
        var selections = new Subject<Symbol>();
        var service = new WorkspaceSymbolService(selections);

        // Act
        selections.OnNext(Symbol.Parse("BTC/USDT"));

        // Assert
        service.Current!.Normalized.Should().Be("BTC/USDT");

        // Act: a later confirmed selection replaces the standing one.
        selections.OnNext(Symbol.Parse("ETH/USDT"));

        // Assert
        service.Current!.Normalized.Should().Be("ETH/USDT");
    }
}
