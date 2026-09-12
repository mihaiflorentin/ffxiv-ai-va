namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Dalamud;
using AIVoiceActing.Ports;
using Xunit;

public sealed class GameObjectSpeakerDirectoryTests
{
    private static readonly GameObjectSpeakerDirectory Directory = new();

    [Fact]
    public void PlayerHint_FormsPcKey_WithCustomizeData()
    {
        var identity = Directory.Resolve(new SpeakerHint(
            Name: "Mihai Testa", World: 66, ObjectIndex: 5, ModelCharaId: null,
            Race: 1, Tribe: 1, Sex: 0));
        Assert.Equal(
            new SpeakerIdentity("pc:Mihai Testa@66", "Mihai Testa", 1, 1, (byte)0, 66),
            identity);
    }

    [Fact]
    public void NpcHint_FormsLowercaseNpcKey()
    {
        var identity = Directory.Resolve(new SpeakerHint(
            Name: "Feo Ul", World: null, ObjectIndex: 201, ModelCharaId: 2520,
            Race: null, Tribe: null, Sex: 0));
        Assert.Equal(
            new SpeakerIdentity("npc:feo ul", "Feo Ul", null, null, (byte)0, null),
            identity);
    }

    [Fact]
    public void HintsWithWorldSuffix_NormalizeToSameKey()
    {
        var a = Directory.Resolve(new SpeakerHint("Feo Ul", null, 1, null, null, null, null));
        var b = Directory.Resolve(new SpeakerHint("  Feo Ul  ", null, 2, null, null, null, null));
        Assert.Equal(a.Key, b.Key);
    }

    [Fact]
    public void EmptyName_WithObjectIndex_FallsBackToIndexKey() =>
        Assert.Equal(
            "npc:object-42",
            Directory.Resolve(new SpeakerHint("", null, 42, null, null, null, null)).Key);

    [Fact]
    public void EmptyName_WithoutObjectIndex_FallsBackToAnonymousKey() =>
        Assert.Equal(
            "npc:anonymous",
            Directory.Resolve(new SpeakerHint("   ", null, null, null, null, null, null)).Key);

    [Fact]
    public void EmptyName_CarriesCustomizeData_ForGroupResolution() =>
        Assert.Equal(
            (byte)1,
            Directory.Resolve(new SpeakerHint("", null, 42, null, 7, 73, 1)).Sex);

    [Fact]
    public void EveryHint_Resolves_NonNullIdentity()
    {
        foreach (var hint in new[]
        {
            new SpeakerHint("x", null, null, null, null, null, null),
            new SpeakerHint(null!, null, null, null, null, null, null),
            new SpeakerHint("", 66, 1, 2, 3, 4, 5),
        })
        {
            Assert.NotNull(Directory.Resolve(hint).Key);
        }
    }
}
