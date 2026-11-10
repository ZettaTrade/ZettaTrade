namespace ZettaTrade.Infrastructure.Tests.Licensing;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.Infrastructure.Licensing;
using ZettaTrade.Infrastructure.Tests.RateLimiting;

/// <summary>
/// The EULA-surface matrix (T-132): a module's <c>{pluginId}.EULA.txt</c>
/// next to its DLL is the text the activation dialog presents, no file
/// (or no plugins directory) means no acceptance step, a path-traversal
/// plugin id refuses the read, and the acceptance fact persists per
/// plugin id in settings.
/// </summary>
public sealed class LicenseEulaServiceTests : IDisposable
{
    private readonly string _pluginsDirectory = Path.Combine(
        Path.GetTempPath(),
        "zettatrade-eula-tests",
        Guid.NewGuid().ToString("N"));

    public LicenseEulaServiceTests() => Directory.CreateDirectory(_pluginsDirectory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_pluginsDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task MissingPluginsDirectory_PresentsNoText()
    {
        Directory.Delete(_pluginsDirectory);
        var service = EulaService();

        (await service.GetTextAsync("chart-lens")).Should().BeNull();
    }

    [Fact]
    public async Task ModuleWithoutEulaFile_PresentsNoText()
    {
        var service = EulaService();

        (await service.GetTextAsync("chart-lens")).Should().BeNull();
    }

    [Fact]
    public async Task EulaFileNextToTheModule_IsTheText()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_pluginsDirectory, "chart-lens.EULA.txt"),
            "The module agreement body.");

        var service = EulaService();

        (await service.GetTextAsync("chart-lens")).Should().Be("The module agreement body.");
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("sub/dir")]
    [InlineData("a\\b")]
    [InlineData("chart-lens:")]
    public async Task PathTraversalOrInvalidCharacters_RefuseTheRead(string pluginId)
    {
        var service = EulaService();

        (await service.GetTextAsync(pluginId)).Should().BeNull();
    }

    [Fact]
    public async Task Acceptance_DefaultsToNotAccepted()
    {
        var service = EulaService();

        (await service.IsAcceptedAsync("chart-lens")).Should().BeFalse();
    }

    [Fact]
    public async Task Acceptance_MarksAndPersistsPerPlugin()
    {
        var service = EulaService();

        await service.MarkAcceptedAsync("chart-lens");

        (await service.IsAcceptedAsync("chart-lens")).Should().BeTrue();
        (await service.IsAcceptedAsync("other-module")).Should().BeFalse();
    }

    [Fact]
    public async Task BlankPluginId_AcceptanceMark_Refuses()
    {
        var service = EulaService();

        var mark = async () => await service.MarkAcceptedAsync("  ");

        await mark.Should().ThrowAsync<ArgumentException>();
    }

    #region Stubs

    private LicenseEulaService EulaService() => new(
        new TestSettingsService(),
        _pluginsDirectory,
        NullLogger<LicenseEulaService>.Instance);

    #endregion
}
