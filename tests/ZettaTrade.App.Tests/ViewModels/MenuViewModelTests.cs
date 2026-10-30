namespace ZettaTrade.App.Tests.ViewModels;

using System.Reactive.Linq;
using FluentAssertions;
using ReactiveUI.Primitives;
using Xunit;
using ZettaTrade.App.ViewModels;

/// <summary>
/// Tests for the menu command routing: the settings command must raise the
/// <see cref="MenuViewModel.OpenSettingsRequested"/> interaction for the View
/// to host the window, keeping window plumbing out of the ViewModel.
/// </summary>
public sealed class MenuViewModelTests
{
    [Fact]
    public async Task OpenSettingsCommand_Executed_RoutesThroughOpenSettingsInteraction()
    {
        // Arrange
        var menu = new MenuViewModel();
        var handled = 0;
        menu.OpenSettingsRequested.RegisterHandler(context =>
        {
            Interlocked.Increment(ref handled);
            context.SetOutput(RxVoid.Default);
            return Task.CompletedTask;
        });

        // Act
        await menu.OpenSettingsCommand.Execute(RxVoid.Default);

        // Assert
        handled.Should().Be(1);
    }
}
