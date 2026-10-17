namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Engines;
using AIVoiceActing.Infrastructure.Onnx;
using Xunit;

/// <summary>Which download groups make a Turbo install complete, and who owns each group.</summary>
public sealed class TurboInstallTests
{
    private static Func<string, bool> Have(params string[] groups) => groups.Contains;

    [Fact]
    public void TheGroupKeys_AreTheCatalogKeys()
    {
        Assert.Equal(ModelCatalog.TurboBaseGroup, TurboInstall.BaseGroup);
        Assert.Equal(ModelCatalog.TurboGroup, TurboInstall.Fp32Group);
        Assert.Equal(ModelCatalog.TurboFp16Group, TurboInstall.Fp16Group);
        Assert.Equal(ModelCatalog.TurboQ4F16Group, TurboInstall.Q4F16Group);
        Assert.All(TurboInstall.AllGroups, g => Assert.NotEmpty(ModelCatalog.GroupAssets(g)));
    }

    [Theory]
    [InlineData(new[] { "turbo-base", "turbo" }, true)]
    [InlineData(new[] { "turbo-base", "turbo-fp16" }, true)]
    [InlineData(new[] { "turbo-base", "turbo-q4f16" }, true)] // the small graphics card install: no fp32 files at all
    [InlineData(new[] { "turbo-base" }, false)]
    [InlineData(new[] { "turbo", "turbo-fp16", "turbo-q4f16" }, false)] // sets without the tokenizer and voice
    [InlineData(new string[0], false)]
    public void AnInstall_IsBaseFilesPlusAnyOneCompleteSet(string[] present, bool installed)
    {
        Assert.Equal(installed, TurboInstall.IsInstalled(Have(present)));
    }

    [Fact]
    public void ASetThatIsAlreadyOnDisk_CountsAsRequired_SoNothingIsAskedTwice()
    {
        // A graphics card machine that still has only the old full-size files is not told they are missing.
        Assert.Equal(["turbo-base", "turbo"], TurboInstall.RequiredGroups(gpu: true, Have("turbo-base", "turbo")));
        Assert.Equal(["turbo-base", "turbo-q4f16"], TurboInstall.RequiredGroups(gpu: true, Have("turbo-base", "turbo", "turbo-q4f16")));
        Assert.Equal(["turbo-base", "turbo-q4f16"], TurboInstall.RequiredGroups(gpu: false, Have("turbo-base", "turbo-q4f16")));
    }

    [Fact]
    public void WithNothingOnDisk_TheMachineIsAskedForItsOwnSet()
    {
        Assert.Equal(["turbo-base", "turbo-q4f16"], TurboInstall.RequiredGroups(gpu: true, Have()));
        Assert.Equal(["turbo-base", "turbo"], TurboInstall.RequiredGroups(gpu: false, Have()));
    }

    [Fact]
    public void ThePreferredSet_DependsOnWhetherAGraphicsCardIsUsable()
    {
        Assert.Equal("turbo-q4f16", TurboInstall.InstalledSet(true, Have("turbo", "turbo-fp16", "turbo-q4f16")));
        Assert.Equal("turbo", TurboInstall.InstalledSet(false, Have("turbo", "turbo-fp16", "turbo-q4f16")));
        Assert.Null(TurboInstall.InstalledSet(true, Have("turbo-base")));
    }

    [Fact]
    public void EveryCatalogGroup_HasExactlyOneOwner_AndNoGroupIsOrphaned()
    {
        var engines = EngineCatalog.Instance.List();
        var groups = ModelCatalog.Assets.Select(a => a.Group).Distinct().ToList();

        foreach (var group in groups)
        {
            Assert.Single(engines, e => e.AssetGroups.Contains(group));
            Assert.NotNull(EngineCatalog.Instance.OwnerOfGroup(group));
        }

        Assert.All(engines.SelectMany(e => e.AssetGroups), g => Assert.Contains(g, groups));
        Assert.Equal("chatterbox-turbo", EngineCatalog.Instance.OwnerOfGroup("turbo-base")!.Key);
    }

    [Fact]
    public void TheTurboEngineLists_BaseFirst_ThenTheSets()
    {
        Assert.Equal(
            ["turbo-base", "turbo", "turbo-fp16", "turbo-q4f16"],
            EngineCatalog.Instance.Get("chatterbox-turbo").AssetGroups);
    }

    [Fact]
    public void TheSmallGpuInstall_IsAboutAHalfGigabyte_WhereTheOldOneWasThreeAndAHalf()
    {
        long Size(string group) => ModelCatalog.Assets.Where(a => a.Group == group).Sum(a => a.Asset.SizeBytes ?? 0);

        var gpuInstall = Size("turbo-base") + Size("turbo-q4f16");
        var oldInstall = Size("turbo-base") + Size("turbo") + Size("turbo-q4f16");

        Assert.InRange(gpuInstall, 500_000_000, 600_000_000);
        Assert.True(oldInstall > 3_800_000_000);
        // Only the q4f16 LM runs on the GPU; the CPU graphs ride along in the same small group.
        Assert.Contains(ModelCatalog.GroupAssets("turbo-q4f16"), a => a.FileName == "turbo-language-model-q4f16.onnx");
        Assert.Contains(ModelCatalog.GroupAssets("turbo-q4f16"), a => a.FileName == "turbo-conditional-decoder-q4f16.onnx");
    }
}
