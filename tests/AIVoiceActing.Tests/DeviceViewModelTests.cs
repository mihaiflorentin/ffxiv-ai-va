namespace AIVoiceActing.Tests;

using AIVoiceActing.Ports;
using AIVoiceActing.UI.State;
using Xunit;

public sealed class DeviceViewModelTests
{
    private static readonly DeviceAdapter Nvidia = new("NVIDIA", "NVIDIA GeForce RTX 4070", "Vulkan");
    private static readonly DeviceAdapter Igpu = new("AMD", "Radeon Graphics", "Vulkan");

    private static DeviceReport Report(DevicePlatform platform = DevicePlatform.Windows, params DeviceAdapter[] adapters) =>
        new(platform, adapters, WebGpuAvailable: adapters.Length > 0, Reason: adapters.Length > 0 ? string.Empty : "No GPU adapter was found.");

    private static EngineStatus Status(EngineState state, string reason = "", bool retry = false, DeviceMode requested = DeviceMode.Auto) =>
        new(state, requested, state == EngineState.Ready ? new ActiveDevice(DeviceEp.WebGpu, "RTX") : ActiveDevice.Cpu, reason, retry);

    private static DeviceView Describe(
        DeviceReport? report,
        EngineStatus? status = null,
        DeviceMode mode = DeviceMode.Auto,
        bool crash = false,
        bool retrying = false) =>
        DeviceViewModel.Describe(new DeviceViewInput(mode, report, crash, status, retrying));

    // ---- the quiet cases: no banner

    [Theory]
    [InlineData(EngineState.NotInstalled)]
    [InlineData(EngineState.Loading)]
    [InlineData(EngineState.Ready)]
    public void NotInstalledLoadingAndReady_NeedNoBanner(EngineState state)
    {
        var view = Describe(Report(DevicePlatform.Windows, Nvidia), Status(state));

        Assert.Null(view.Banner);
    }

    [Fact]
    public void NoEngineUsingTheCard_NeedsNoBanner()
    {
        Assert.Null(Describe(Report(DevicePlatform.Windows, Nvidia), status: null).Banner);
    }

    // ---- fell back

    [Fact]
    public void FellBack_WithAUsableCard_WarnsAndOffersRetryAndUseCpu()
    {
        var reason = "The GPU could not be started, so Chatterbox Turbo runs on the CPU.";
        var banner = Describe(Report(DevicePlatform.Windows, Nvidia), Status(EngineState.FellBack, reason, retry: true)).Banner!;

        Assert.Equal(Severity.Warning, banner.Severity);
        Assert.Equal(DeviceViewModel.FellBackTitle, banner.Title);
        Assert.Equal(reason, banner.Detail);
        Assert.True(banner.CanRetryGpu);
        Assert.True(banner.CanUseCpu);
    }

    [Fact]
    public void FellBack_OnTheCpuByChoice_OffersNothingToSwitch()
    {
        var banner = Describe(
            Report(DevicePlatform.Windows, Nvidia),
            Status(EngineState.FellBack, "x", retry: false, requested: DeviceMode.Cpu),
            DeviceMode.Cpu).Banner!;

        Assert.False(banner.CanUseCpu);
        Assert.False(banner.CanRetryGpu);
    }

    // ---- failed

    [Fact]
    public void Failed_IsAnErrorWithTheReasonVerbatim()
    {
        var banner = Describe(Report(DevicePlatform.Windows, Nvidia), Status(EngineState.Failed, "Out of graphics memory.", retry: true)).Banner!;

        Assert.Equal(Severity.Error, banner.Severity);
        Assert.Equal(DeviceViewModel.FailedTitle, banner.Title);
        Assert.Equal("Out of graphics memory.", banner.Detail);
        Assert.True(banner.CanRetryGpu);
        Assert.True(banner.CanUseCpu);
    }

    [Fact]
    public void Failed_ForANonGraphicsReason_DoesNotOfferWhatCannotHelp()
    {
        var banner = Describe(Report(DevicePlatform.Windows, Nvidia), Status(EngineState.Failed, "A model file is corrupt.", retry: false)).Banner!;

        Assert.False(banner.CanRetryGpu);
        Assert.False(banner.CanUseCpu);
    }

    // ---- crash guard

    [Fact]
    public void CrashGuard_HasItsOwnTitleAndRetry()
    {
        var banner = Describe(
            Report(DevicePlatform.Windows, Nvidia),
            Status(EngineState.FellBack, "The game stopped while using the GPU last time.", retry: true),
            crash: true).Banner!;

        Assert.Equal(DeviceViewModel.CrashGuardTitle, banner.Title);
        Assert.Equal("The game stopped while using the GPU last time.", banner.Detail);
        Assert.True(banner.CanRetryGpu);
        Assert.Equal(Severity.Warning, banner.Severity);
    }

    [Fact]
    public void CrashGuard_ShowsEvenBeforeAnyEngineRuns()
    {
        var banner = Describe(Report(DevicePlatform.Windows, Nvidia), status: null, crash: true).Banner!;

        Assert.Equal(DeviceViewModel.CrashGuardTitle, banner.Title);
        Assert.False(string.IsNullOrWhiteSpace(banner.Detail));
        Assert.True(banner.CanRetryGpu);
    }

    [Fact]
    public void CrashGuard_IsSilentWhenThePlayerChoseTheProcessor()
    {
        Assert.Null(Describe(Report(DevicePlatform.Windows, Nvidia), null, DeviceMode.Cpu, crash: true).Banner);
    }

    [Fact]
    public void Retrying_IsReportedSoTheButtonCanStepAside()
    {
        var banner = Describe(Report(DevicePlatform.Windows, Nvidia), null, crash: true, retrying: true).Banner!;

        Assert.True(banner.Retrying);
    }

    // ---- no adapter

    [Fact]
    public void NoAdapter_DisablesTheGraphicsCardChoiceWithThePortsReason()
    {
        var view = Describe(Report(DevicePlatform.Windows));

        Assert.False(view.GraphicsCardAvailable);
        Assert.Equal("No GPU adapter was found.", view.GraphicsCardUnavailable);
        Assert.Equal(DeviceViewModel.NoAdapterText, view.Detected);
        Assert.Empty(view.Adapters);
    }

    [Fact]
    public void NoAdapter_FellBackIsExpectedSoItIsQuietAndOffersNoRetry()
    {
        var banner = Describe(
            Report(DevicePlatform.Windows),
            Status(EngineState.FellBack, "No compatible graphics card was found. Running on the CPU.")).Banner!;

        Assert.Equal(Severity.Info, banner.Severity);
        Assert.False(banner.CanRetryGpu);
        Assert.False(banner.CanUseCpu);
    }

    [Fact]
    public void BeforeTheFirstCheck_SaysItIsChecking()
    {
        var view = Describe(null);

        Assert.Equal(DeviceViewModel.CheckingText, view.Detected);
        Assert.False(view.GraphicsCardAvailable);
    }

    // ---- Wine and adapters

    [Fact]
    public void Wine_AddsANoteAndKeepsTheListLabelledAsReported()
    {
        var view = Describe(Report(DevicePlatform.Wine, new DeviceAdapter("AMD", "AMD Radeon HD 5600", "Vulkan")));

        Assert.NotNull(view.Note);
        Assert.Contains("Wine", view.Note);
        Assert.Equal(DeviceViewModel.AdaptersHeading, view.Detected);
        Assert.Contains("reported by the system", DeviceViewModel.AdaptersHeading);
        Assert.Single(view.Adapters);
    }

    [Fact]
    public void NativeWindows_HasNoWineNote()
    {
        Assert.Null(Describe(Report(DevicePlatform.Windows, Nvidia)).Note);
    }

    [Fact]
    public void Preference_ShowsOnlyWithMoreThanOneCardAndNotOnTheProcessor()
    {
        var two = Report(DevicePlatform.Windows, Nvidia, Igpu);
        var one = Report(DevicePlatform.Windows, Nvidia);

        Assert.True(Describe(two).ShowPreference);
        Assert.False(Describe(one).ShowPreference);
        Assert.False(Describe(two, mode: DeviceMode.Cpu).ShowPreference);
    }

    [Theory]
    [InlineData("NVIDIA", "NVIDIA GeForce RTX 4070", "Vulkan", "NVIDIA GeForce RTX 4070 (Vulkan)")]
    [InlineData("AMD", "Radeon Graphics", "D3D12", "AMD Radeon Graphics (D3D12)")]
    [InlineData("", "Some card", "", "Some card")]
    [InlineData("Intel", "", "Vulkan", "Intel (Vulkan)")]
    public void AdapterLine_JoinsVendorNameAndApi(string vendor, string name, string api, string expected)
    {
        Assert.Equal(expected, DeviceViewModel.AdapterLine(new DeviceAdapter(vendor, name, api)));
    }

    // ---- choices

    [Fact]
    public void Choices_LineUpWithTheirStoredValues()
    {
        Assert.Equal(DeviceViewModel.ModeLabels.Count, DeviceViewModel.ModeValues.Count);
        Assert.Equal(DeviceViewModel.PreferenceLabels.Count, DeviceViewModel.PreferenceValues.Count);
        Assert.Equal(DeviceViewModel.LoadLabels.Count, DeviceViewModel.LoadValues.Count);
        Assert.Equal(1, DeviceViewModel.IndexOf(DeviceViewModel.ModeValues, "gpu"));
        Assert.Equal(0, DeviceViewModel.IndexOf(DeviceViewModel.ModeValues, "something-else"));
        Assert.Equal(2, DeviceViewModel.IndexOf(DeviceViewModel.LoadValues, "HIGH"));
    }

    [Fact]
    public void LoadValues_AreTheOnesTheConfigurationUnderstands()
    {
        foreach (var value in DeviceViewModel.LoadValues)
        {
            var settings = new DeviceSettings { CpuImpact = value };
            Assert.Contains(settings.CpuThreads(), new[] { 2, 4, 8 });
        }
    }

    [Fact]
    public void ModeValues_RoundTripThroughThePolicy()
    {
        var settings = new DeviceSettings { Mode = DeviceSettings.ModeGpu };
        Assert.Equal(DeviceMode.Gpu, settings.ToPreferences().Mode);
        foreach (var value in DeviceViewModel.ModeValues)
        {
            Assert.False(string.IsNullOrWhiteSpace(DeviceViewModel.ModeExplanation(value)));
        }
    }
}
