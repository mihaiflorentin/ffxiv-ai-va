namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.State;
using Xunit;

/// <summary>Hardware, disk and what is already downloaded in; tier, engine, precision, groups and bytes out.</summary>
public sealed class SetupAdvisorTests
{
    private const long Mb = 1024L * 1024;
    private const long Gb = 1024L * Mb;

    private static readonly GroupStatus KokoroMissing = new("kokoro", 326 * Mb, 326 * Mb);
    private static readonly GroupStatus KokoroDone = new("kokoro", 326 * Mb, 0);
    private static readonly GroupStatus TurboMissing = new("turbo", 3300 * Mb, 3300 * Mb);
    private static readonly GroupStatus TurboDone = new("turbo", 3300 * Mb, 0);
    private static readonly GroupStatus Q4Missing = new("turbo-q4f16", 536 * Mb, 536 * Mb);
    private static readonly GroupStatus Q4Done = new("turbo-q4f16", 536 * Mb, 0);

    private static SetupAdvisorInput Input(
        bool gpu,
        long free = 100 * Gb,
        GroupStatus? kokoro = null,
        GroupStatus? turbo = null,
        GroupStatus? q4 = null,
        QualityTier? current = null,
        long? vram = null) =>
        new(gpu, free, [kokoro ?? KokoroMissing, turbo ?? TurboMissing, q4 ?? Q4Missing], current, vram);

    [Fact]
    public void GroupNames_MatchTheModelCatalog()
    {
        Assert.Equal(ModelCatalog.KokoroGroup, SetupAdvisor.KokoroGroup);
        Assert.Equal(ModelCatalog.TurboGroup, SetupAdvisor.TurboGroup);
        Assert.Equal(ModelCatalog.TurboQ4F16Group, SetupAdvisor.TurboQ4F16Group);
        Assert.Equal(DeviceModePolicy.TurboEngineId, SetupAdvisor.TurboEngine);
    }

    // tier, gpu, expected primary, expected expressive, expected precision, expected groups
    public static TheoryData<QualityTier, bool, string, string?, string?, string[]> Table => new()
    {
        { QualityTier.Fast, false, "kokoro", null, null, ["kokoro"] },
        { QualityTier.Fast, true, "kokoro", null, null, ["kokoro"] },
        { QualityTier.Balanced, false, "kokoro", "chatterbox-turbo", "fp32", ["kokoro", "turbo"] },
        { QualityTier.Balanced, true, "kokoro", "chatterbox-turbo", "q4f16", ["kokoro", "turbo", "turbo-q4f16"] },
        { QualityTier.MostExpressive, true, "chatterbox-turbo", null, "q4f16", ["turbo", "turbo-q4f16"] },
        { QualityTier.MostExpressive, false, "chatterbox-turbo", null, "fp32", ["turbo"] },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Plan_PicksEnginePrecisionAndGroups(
        QualityTier tier, bool gpu, string primary, string? expressive, string? precision, string[] groups)
    {
        var plan = SetupAdvisor.Plan(tier, Input(gpu));

        Assert.Equal(primary, plan.PrimaryEngine);
        Assert.Equal(expressive, plan.ExpressiveEngine);
        Assert.Equal(precision, plan.Precision);
        Assert.Equal(groups, plan.Groups);
    }

    [Fact]
    public void ABalancedGpuPlan_AsksForTheQ4f16BuildOnTopOfTheRequiredBaseGroup()
    {
        var plan = SetupAdvisor.Plan(QualityTier.Balanced, Input(gpu: true));

        Assert.Equal(["kokoro", "turbo", "turbo-q4f16"], plan.ToDownload.Select(g => g.Group));
        Assert.Equal((326 + 3300 + 536) * Mb, plan.BytesToDownload);
        Assert.Null(plan.Blocked);
    }

    [Fact]
    public void DownloadedGroups_AreNotCountedAgain()
    {
        var plan = SetupAdvisor.Plan(
            QualityTier.Balanced, Input(gpu: true, kokoro: KokoroDone, turbo: TurboDone));

        Assert.Equal("turbo-q4f16", Assert.Single(plan.ToDownload).Group);
        Assert.Equal(536 * Mb, plan.BytesToDownload);
    }

    [Fact]
    public void APartlyDownloadedGroup_CountsOnlyWhatIsMissing()
    {
        var partial = new GroupStatus("turbo-q4f16", 536 * Mb, 100 * Mb);

        var plan = SetupAdvisor.Plan(
            QualityTier.MostExpressive, Input(gpu: true, turbo: TurboDone, q4: partial));

        Assert.Equal(100 * Mb, plan.BytesToDownload);
    }

    [Fact]
    public void MostExpressive_NeedsAGpu()
    {
        var plan = SetupAdvisor.Plan(QualityTier.MostExpressive, Input(gpu: false));

        Assert.NotNull(plan.Blocked);
        Assert.Contains("graphics card", plan.Blocked);
    }

    [Fact]
    public void NotEnoughDisk_BlocksWithTheNumbers()
    {
        var plan = SetupAdvisor.Plan(QualityTier.Fast, Input(gpu: false, free: 100 * Mb));

        Assert.Equal(
            $"Needs {Labels.FileSize((326 * Mb) + SetupAdvisor.DiskHeadroomBytes)} free on the drive that holds your voices; {Labels.FileSize(100 * Mb)} is free.",
            plan.Blocked);
    }

    [Fact]
    public void AlreadyDownloaded_NeedsNoDisk()
    {
        var plan = SetupAdvisor.Plan(
            QualityTier.Balanced,
            Input(gpu: true, free: 10 * Mb, kokoro: KokoroDone, turbo: TurboDone, q4: Q4Done));

        Assert.Empty(plan.ToDownload);
        Assert.Null(plan.Blocked);
    }

    [Fact]
    public void AnUnlistedGroup_CountsAsNotDownloaded()
    {
        var plan = SetupAdvisor.Plan(QualityTier.Fast, new SetupAdvisorInput(false, 100 * Gb, []));

        Assert.Equal("kokoro", Assert.Single(plan.ToDownload).Group);
    }

    [Fact]
    public void KnownTinyVram_FallsBackToTheCpuBuild_UnknownVramDoesNot()
    {
        var tiny = SetupAdvisor.Plan(QualityTier.Balanced, Input(gpu: true, vram: 256 * Mb));
        var unknown = SetupAdvisor.Plan(QualityTier.Balanced, Input(gpu: true, vram: null));
        var roomy = SetupAdvisor.Plan(QualityTier.Balanced, Input(gpu: true, vram: 16 * Gb));

        Assert.Equal("fp32", tiny.Precision);
        Assert.DoesNotContain("turbo-q4f16", tiny.Groups);
        Assert.Equal("q4f16", unknown.Precision);
        Assert.Equal("q4f16", roomy.Precision);
    }

    [Theory]
    [InlineData(false, QualityTier.Fast)]
    [InlineData(true, QualityTier.Balanced)]
    public void Recommend_NoGpuIsFast_AGpuIsBalanced(bool gpu, QualityTier expected) =>
        Assert.Equal(expected, SetupAdvisor.Recommend(Input(gpu)).Tier);

    [Fact]
    public void Recommend_AGpuWithoutRoomForTurbo_FallsBackToFast()
    {
        var advice = SetupAdvisor.Recommend(Input(gpu: true, free: 2 * Gb));

        Assert.Equal(QualityTier.Fast, advice.Tier);
        Assert.Null(advice.Blocked);
    }

    [Fact]
    public void Recommend_KeepsADownloadedCurrentTier_EvenWithoutAGpu()
    {
        var advice = SetupAdvisor.Recommend(
            Input(gpu: false, kokoro: KokoroDone, turbo: TurboDone, current: QualityTier.Balanced));

        Assert.Equal(QualityTier.Balanced, advice.Tier);
    }

    [Fact]
    public void Recommend_IgnoresACurrentTierThatIsNotDownloaded()
    {
        var advice = SetupAdvisor.Recommend(Input(gpu: false, current: QualityTier.MostExpressive));

        Assert.Equal(QualityTier.Fast, advice.Tier);
    }

    [Fact]
    public void HasUsableGpu_NeedsAProbeResult_AndNoCrashGuardTrip()
    {
        var gpu = new DeviceReport(DevicePlatform.Wine, [new DeviceAdapter("AMD", "RX 9070 XT", "Vulkan")], true, string.Empty);

        Assert.True(SetupAdvisor.HasUsableGpu(gpu));
        Assert.False(SetupAdvisor.HasUsableGpu(gpu, gpuDisabledByCrashGuard: true));
        Assert.False(SetupAdvisor.HasUsableGpu(null));
        Assert.False(SetupAdvisor.HasUsableGpu(DeviceReport.Unknown));
    }

    // ---- the Home setup flow uses it ----------------------------------------------------------------

    [Fact]
    public void TheSetupFlow_PlansTheQ4f16BuildOnAGpu_AndCountsItsBytes()
    {
        var inputs = new SetupInputs(
            true,
            100 * Gb,
            [new EngineDownload("kokoro", "Kokoro", 326 * Mb, 326 * Mb), new EngineDownload("chatterbox-turbo", "Chatterbox Turbo", 3300 * Mb, 3300 * Mb)],
            PrecisionGroups: [Q4Missing]);

        var plan = SetupFlowModel.Recommend(inputs);

        Assert.Equal(QualityTier.Balanced, plan.Tier);
        Assert.Equal((326 + 3300 + 536) * Mb, plan.BytesToDownload);
        Assert.Equal("q4f16", plan.Recommendation!.Precision);
        Assert.Contains("turbo", plan.Recommendation.Groups);
        Assert.Contains("turbo-q4f16", plan.Recommendation.Groups);
    }

    [Fact]
    public void TheSetupFlow_MostExpressive_OnAGpu_NeedsTheBaseGroupAndTheQ4f16Build()
    {
        var inputs = new SetupInputs(
            true,
            100 * Gb,
            [new EngineDownload("kokoro", "Kokoro", 326 * Mb, 0), new EngineDownload("chatterbox-turbo", "Chatterbox Turbo", 3300 * Mb, 3300 * Mb)],
            PrecisionGroups: [Q4Missing]);

        var plan = SetupFlowModel.PlanFor(QualityTier.MostExpressive, inputs);

        Assert.Equal(["turbo", "turbo-q4f16"], plan.Recommendation!.Groups);
        Assert.Equal((3300 + 536) * Mb, plan.BytesToDownload);
        Assert.Null(plan.Blocked);
    }

    [Fact]
    public void TheSetupFlow_WithoutAGpu_NeverAsksForTheQ4f16Build()
    {
        var inputs = new SetupInputs(
            false,
            100 * Gb,
            [new EngineDownload("kokoro", "Kokoro", 326 * Mb, 0), new EngineDownload("chatterbox-turbo", "Chatterbox Turbo", 3300 * Mb, 3300 * Mb)],
            PrecisionGroups: [Q4Missing]);

        var plan = SetupFlowModel.PlanFor(QualityTier.Balanced, inputs);

        Assert.Equal(3300 * Mb, plan.BytesToDownload);
        Assert.DoesNotContain("turbo-q4f16", plan.Recommendation!.Groups);
    }
}
