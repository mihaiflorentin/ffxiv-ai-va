namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using Xunit;

public sealed class VoiceGroupResolverTests
{
    private static readonly IReadOnlySet<int> Overrides = new HashSet<int> { 2520, 1234 };

    public static TheoryData<byte?, VoiceGroup> MaleCases() => new()
    {
        { (byte)1, VoiceGroup.Male }, // Hyur
        { (byte)2, VoiceGroup.Male }, // Elezen
        { (byte)3, VoiceGroup.Male }, // Lalafell
        { (byte)4, VoiceGroup.Male }, // Miqo'te
        { (byte)5, VoiceGroup.Male }, // Roegadyn
        { (byte)6, VoiceGroup.Male }, // Au Ra
        { (byte)7, VoiceGroup.Male }, // Hrothgar (male is gendered)
        { (byte)8, VoiceGroup.Male }, // Viera
        { null, VoiceGroup.Male },    // race unknown, sex known → TTT-exact: sex drives
    };

    [Theory]
    [MemberData(nameof(MaleCases))]
    public void SexMale_IsMale(byte? race, VoiceGroup expected) =>
        Assert.Equal(expected, VoiceGroupResolver.Resolve(race, tribe: 1, sex: 0, modelCharaId: null, ungenderedModelIds: Overrides));

    public static TheoryData<byte?, VoiceGroup> FemaleCases() => new()
    {
        { (byte)1, VoiceGroup.Female },
        { (byte)2, VoiceGroup.Female },
        { (byte)3, VoiceGroup.Female },
        { (byte)4, VoiceGroup.Female },
        { (byte)5, VoiceGroup.Female },
        { (byte)6, VoiceGroup.Female },
        { (byte)7, VoiceGroup.Ungendered }, // Hrothgar female → Ungendered (TextToTalk behavior)
        { (byte)8, VoiceGroup.Female },
        { null, VoiceGroup.Female },
    };

    [Theory]
    [MemberData(nameof(FemaleCases))]
    public void SexFemale_IsFemaleExceptHrothgar(byte? race, VoiceGroup expected) =>
        Assert.Equal(expected, VoiceGroupResolver.Resolve(race, tribe: 2, sex: 1, modelCharaId: null, ungenderedModelIds: Overrides));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void UngenderedModelId_ForcesUngenderedRegardlesOfSex(byte sex) =>
        Assert.Equal(
            VoiceGroup.Ungendered,
            VoiceGroupResolver.Resolve(race: 1, tribe: 1, sex, modelCharaId: 2520, ungenderedModelIds: Overrides));

    [Fact]
    public void UngenderedModelIdWithoutSet_FallsBackToSexRules() =>
        Assert.Equal(
            VoiceGroup.Male,
            VoiceGroupResolver.Resolve(race: 1, tribe: 1, sex: 0, modelCharaId: 2520, ungenderedModelIds: null));

    [Fact]
    public void NonListedModelId_DoesNotForceUngendered() =>
        Assert.Equal(
            VoiceGroup.Female,
            VoiceGroupResolver.Resolve(race: 2, tribe: 1, sex: 1, modelCharaId: 999, ungenderedModelIds: Overrides));

    [Theory]
    [InlineData(null)]
    [InlineData((byte)2)]
    [InlineData((byte)255)]
    public void MissingOrUnknownSex_IsUnknown(byte? sex) =>
        Assert.Equal(
            VoiceGroup.Unknown,
            VoiceGroupResolver.Resolve(race: 1, tribe: 1, sex, modelCharaId: null, ungenderedModelIds: Overrides));
}
