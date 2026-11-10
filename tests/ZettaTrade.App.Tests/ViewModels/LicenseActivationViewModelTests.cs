namespace ZettaTrade.App.Tests.ViewModels;

using System.Reactive.Linq;
using System.Reactive.Subjects;
using FluentAssertions;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ReactiveUI;
using ReactiveUI.Primitives;
using Xunit;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Infrastructure.Licensing;

/// <summary>
/// The license activation dialog's ViewModel matrix (T-132), inside the
/// AvaloniaDispatcherSerial collection: the verdict maps to a localized
/// dynamic string, a stored activation is the only closing shape, every
/// refusal keeps the dialog state with nothing written, a thrown activation
/// degrades to the explicit failed verdict, the EULA leg surfaces at the
/// first activation of a module carrying text and waits for the accept
/// tick, and the attach-canon renders the verdict again on a language
/// switch.
/// </summary>
[Collection("AvaloniaDispatcherSerial")]
public sealed class LicenseActivationViewModelTests
{
    static LicenseActivationViewModelTests() => AvaloniaDispatcherPump.EnsureInstalled();

    private readonly ILicenseActivationService _activation = Substitute.For<ILicenseActivationService>();

    private readonly ILicenseEulaService _eula = Substitute.For<ILicenseEulaService>();

    private readonly ILocalizationManager _localization = CreateLocalization();

    private readonly Subject<string> _languageChanged = new();

    /// <summary>
    /// Read a ReactiveCommand's gate: the CanExecute stream's current
    /// value, sampled fresh at the fact's call (the house shape).
    /// </summary>
    private static async Task<bool> Gate(ReactiveCommand<RxVoid, RxVoid> command) =>
        await Observable.FirstAsync(command.CanExecute);

    #region Command gating

    [Fact]
    public async Task Command_BlankInputs_Disabled()
    {
        var vm = CreateViewModel();

        (await Gate(vm.ActivateCommand)).Should().BeFalse();
    }

    [Fact]
    public async Task Command_ShownInputsAndNoEula_Enabled()
    {
        var vm = CreateViewModel();
        _eula.GetTextAsync(Arg.Any<string>()).Returns(null as string);
        vm.PluginId = "chart-lens";
        vm.LicenseKey = "zt1.a.b";
        await AvaloniaDispatcherPump.DrainAsync();

        (await Gate(vm.ActivateCommand)).Should().BeTrue();
    }

    #endregion

    #region Verdict mapping

    [Fact]
    public async Task ValidVerdict_PrintedAndStored()
    {
        var vm = CreateViewModel();
        _eula.GetTextAsync(Arg.Any<string>()).Returns(null as string);
        _activation.ActivateAsync("chart-lens", "zt1.good.good").Returns(LicenseValidationStatus.Valid);

        vm.PluginId = "chart-lens";
        vm.LicenseKey = "zt1.good.good";
        await vm.ActivateCommand.Execute(RxVoid.Default);

        vm.Stored.Should().BeTrue();
        vm.Verdict.Should().Be("[Settings.License.Verdict.Valid]");
    }

    [Fact]
    public async Task RefusedVerdict_VisibleAndNothingStored()
    {
        var vm = CreateViewModel();
        _eula.GetTextAsync(Arg.Any<string>()).Returns(null as string);
        _activation.ActivateAsync("chart-lens", "zt1.bad.bad").Returns(LicenseValidationStatus.Tampered);

        vm.PluginId = "chart-lens";
        vm.LicenseKey = "zt1.bad.bad";
        await vm.ActivateCommand.Execute(RxVoid.Default);

        vm.Stored.Should().BeFalse();
        vm.Verdict.Should().Be("[Settings.License.Verdict.Tampered]");
    }

    [Fact]
    public async Task ThrownActivation_DegradesToFailedVerdictWithoutTakingTheCommandDown()
    {
        var vm = CreateViewModel();
        _eula.GetTextAsync(Arg.Any<string>()).Returns(null as string);
        _activation.ActivateAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns<Task<LicenseValidationStatus>>(static _ => throw new InvalidOperationException("boom"));

        vm.PluginId = "chart-lens";
        vm.LicenseKey = "zt1.any.any";
        await vm.ActivateCommand.Execute(RxVoid.Default);

        vm.Stored.Should().BeFalse();
        vm.Verdict.Should().Be("[Settings.License.Verdict.Failed]");
    }

    [Fact]
    public async Task LanguageSwitch_RerendersTheStillShownVerdict()
    {
        var vm = CreateViewModel();
        using var attach = vm.AttachVerdictRendering();
        _eula.GetTextAsync(Arg.Any<string>()).Returns(null as string);
        _activation.ActivateAsync("chart-lens", "zt1.good.good").Returns(LicenseValidationStatus.Valid);

        vm.PluginId = "chart-lens";
        vm.LicenseKey = "zt1.good.good";
        await vm.ActivateCommand.Execute(RxVoid.Default);
        _languageChanged.OnNext("ru-Test");
        await AvaloniaDispatcherPump.DrainAsync();

        vm.Verdict.Should().Be("[Settings.License.Verdict.Valid]");
    }

    #endregion

    #region EULA leg

    [Fact]
    public async Task EulaGate_TheAcceptTickIsTheOnlyPathThrough()
    {
        var vm = CreateViewModel();
        _eula.GetTextAsync("chart-lens").Returns("The module agreement.");
        _eula.IsAcceptedAsync(Arg.Any<string>()).Returns(false);
        _activation.ActivateAsync("chart-lens", "zt1.good.good").Returns(LicenseValidationStatus.Valid);
        vm.PluginId = "chart-lens";
        vm.LicenseKey = "zt1.good.good";

        // The first press surfaces the agreement (inputs non-blank, no EULA
        // shown yet — the gate is open) and waits; nothing is validated or
        // stored.
        await vm.ActivateCommand.Execute(RxVoid.Default);
        await AvaloniaDispatcherPump.DrainAsync();
        vm.HasEula.Should().BeTrue();
        vm.EulaText.Should().Be("The module agreement.");
        vm.Stored.Should().BeFalse();
        vm.Verdict.Should().Be("[Settings.License.Verdict.EulaRequired]");
        await _activation.DidNotReceiveWithAnyArgs().ActivateAsync(default!, default!);
        (await Gate(vm.ActivateCommand)).Should().BeFalse();

        // The tick is the only gate through: the second press marks the
        // acceptance and activates.
        vm.Accepted = true;
        await AvaloniaDispatcherPump.DrainAsync();
        (await Gate(vm.ActivateCommand)).Should().BeTrue();
        await vm.ActivateCommand.Execute(RxVoid.Default);
        await _eula.Received(1).MarkAcceptedAsync("chart-lens");
        await _activation.Received(1).ActivateAsync("chart-lens", "zt1.good.good");
        vm.Stored.Should().BeTrue();
        vm.Verdict.Should().Be("[Settings.License.Verdict.Valid]");
    }

    [Fact]
    public async Task NoEulaText_ActivatesWithoutAnyConsentLeg()
    {
        var vm = CreateViewModel();
        _eula.GetTextAsync(Arg.Any<string>()).Returns(null as string);
        _activation.ActivateAsync("chart-lens", "zt1.good.good").Returns(LicenseValidationStatus.Valid);

        vm.PluginId = "chart-lens";
        vm.LicenseKey = "zt1.good.good";
        await vm.ActivateCommand.Execute(RxVoid.Default);

        vm.HasEula.Should().BeFalse();
        await _eula.DidNotReceiveWithAnyArgs().MarkAcceptedAsync(default!);
        await _activation.Received(1).ActivateAsync("chart-lens", "zt1.good.good");
        vm.Verdict.Should().Be("[Settings.License.Verdict.Valid]");
    }

    [Fact]
    public async Task PersistedAcceptance_LaterActivation_GoesStraightToValidation()
    {
        var vm = CreateViewModel();
        _eula.GetTextAsync("chart-lens").Returns("The module agreement.");
        _eula.IsAcceptedAsync("chart-lens").Returns(true);
        _activation.ActivateAsync("chart-lens", "zt1.good.good").Returns(LicenseValidationStatus.Valid);

        vm.PluginId = "chart-lens";
        vm.LicenseKey = "zt1.good.good";
        await vm.ActivateCommand.Execute(RxVoid.Default);

        await _eula.DidNotReceiveWithAnyArgs().MarkAcceptedAsync(default!);
        vm.HasEula.Should().BeFalse();
        vm.Stored.Should().BeTrue();
    }

    [Fact]
    public async Task PluginIdChange_ResetsTheEulaSurface()
    {
        var vm = CreateViewModel();
        using var attach = vm.AttachVerdictRendering();
        _eula.GetTextAsync("chart-lens").Returns("The module agreement.");
        _eula.IsAcceptedAsync(Arg.Any<string>()).Returns(false);

        vm.PluginId = "chart-lens";
        vm.LicenseKey = "zt1.good.good";
        await vm.ActivateCommand.Execute(RxVoid.Default);
        vm.PluginId = "order-ladder";
        await AvaloniaDispatcherPump.DrainAsync();

        vm.HasEula.Should().BeFalse();
        vm.EulaText.Should().BeEmpty();
        vm.Accepted.Should().BeFalse();
    }

    #endregion

    #region Stubs

    private LicenseActivationViewModel CreateViewModel()
    {
        _localization.LanguageChanged.Returns(_languageChanged);
        return new LicenseActivationViewModel(
            _activation,
            _eula,
            _localization,
            Logger);
    }

    private static ILocalizationManager CreateLocalization()
    {
        var localization = Substitute.For<ILocalizationManager>();
        localization.GetString(Arg.Any<string>()).Returns(call => $"[{call.ArgAt<string>(0)}]");
        return localization;
    }

    private static readonly ILogger<LicenseActivationViewModel> Logger =
        Substitute.For<ILogger<LicenseActivationViewModel>>();

    #endregion
}
