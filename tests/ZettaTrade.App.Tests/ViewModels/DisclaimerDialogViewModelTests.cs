namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ReactiveUI;
using ReactiveUI.Primitives;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The disclaimer dialog ViewModel: the acknowledge command persists the
/// shipped disclaimer version through the settings store, and a failed
/// write never escapes the command — it degrades to the next-launch
/// re-show instead of breaking the dialog close.
/// </summary>
public sealed class DisclaimerDialogViewModelTests
{
    [Fact]
    public async Task AcknowledgeCommand_Executed_PersistsTheShippedVersion()
    {
        // Arrange
        var settings = Substitute.For<ISettingsService>();
        var viewModel = new DisclaimerDialogViewModel(
            settings,
            "0.1.0-alpha.98",
            NullLogger<DisclaimerDialogViewModel>.Instance);

        // Act
        await viewModel.AcknowledgeCommand.Execute(RxVoid.Default);

        // Assert
        await settings.Received(1).SetAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            UiSettingsKeys.DisclaimerCurrentVersion);
    }

    [Fact]
    public async Task AcknowledgeCommand_FailedWrite_NeverThrowsThroughTheCommand()
    {
        // Arrange: the settings write fails (a locked file, say).
        var settings = Substitute.For<ISettingsService>();
        settings
            .When(sub => sub.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>()))
            .Do(_ => throw new InvalidOperationException("settings file locked"));
        var viewModel = new DisclaimerDialogViewModel(
            settings,
            "0.1.0-alpha.98",
            NullLogger<DisclaimerDialogViewModel>.Instance);

        // Act, assert: the swallow-into-log contract keeps the command
        // completed, so the window still closes and the flag re-shows.
        var act = async () => await viewModel.AcknowledgeCommand.Execute(RxVoid.Default);
        await act.Should().NotThrowAsync();
        await settings.Received(1).SetAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>());
    }

    [Fact]
    public void TheVersionLine_CarriesTheRunningVersion()
    {
        // T-153: the version left the status strip — the About dialog is its
        // one-action home, so the dialog's view model carries the version.
        var viewModel = new DisclaimerDialogViewModel(
            Substitute.For<ISettingsService>(),
            "0.1.0-alpha.98",
            NullLogger<DisclaimerDialogViewModel>.Instance);

        viewModel.VersionText.Should().Be("0.1.0-alpha.98");
    }
}
