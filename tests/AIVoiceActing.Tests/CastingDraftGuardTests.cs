namespace AIVoiceActing.Tests;

using AIVoiceActing.Ports;
using AIVoiceActing.UI.State;
using Xunit;

public sealed class CastingDraftGuardTests
{
    private static CastingGuardState State(bool dirty = false, string selected = "Raid night", string active = "Default") =>
        new(dirty, selected, active);

    [Theory]
    [InlineData(PresetAction.Select)]
    [InlineData(PresetAction.Use)]
    [InlineData(PresetAction.Duplicate)]
    [InlineData(PresetAction.New)]
    [InlineData(PresetAction.Import)]
    [InlineData(PresetAction.Delete)]
    public void UnsavedEdits_BlockEveryChangeOfCasting(PresetAction action)
    {
        var result = CastingDraftGuard.Check(action, State(dirty: true));

        Assert.Equal(GuardVerdict.Blocked, result.Verdict);
        Assert.Equal(CastingDraftGuard.UnsavedMessage, result.Message);
    }

    [Fact]
    public void UnsavedEdits_DoNotBlockExport()
    {
        Assert.True(CastingDraftGuard.Check(PresetAction.Export, State(dirty: true)).IsAllowed);
    }

    [Theory]
    [InlineData(PresetAction.Select)]
    [InlineData(PresetAction.Duplicate)]
    [InlineData(PresetAction.New)]
    [InlineData(PresetAction.Import)]
    [InlineData(PresetAction.Export)]
    [InlineData(PresetAction.Use)]
    public void CleanDraft_AllowsTheRest(PresetAction action)
    {
        Assert.True(CastingDraftGuard.Check(action, State()).IsAllowed);
    }

    [Fact]
    public void Use_OfTheCastingAlreadyInUse_ExplainsInsteadOfDoingNothing()
    {
        var result = CastingDraftGuard.Check(PresetAction.Use, State(selected: "Default", active: "Default"));

        Assert.Equal(GuardVerdict.Blocked, result.Verdict);
        Assert.Equal(CastingDraftGuard.AlreadyInUseMessage, result.Message);
    }

    [Fact]
    public void Delete_AlwaysAsksAndNamesTheCasting()
    {
        var result = CastingDraftGuard.Check(PresetAction.Delete, State());

        Assert.Equal(GuardVerdict.Confirm, result.Verdict);
        Assert.Contains("\"Raid night\"", result.Message);
        Assert.Equal("Delete", result.ConfirmLabel);
        Assert.DoesNotContain("in use", result.Message);
    }

    [Fact]
    public void Delete_OfTheCastingInUse_SaysWhatSpeakersFallBackTo()
    {
        var result = CastingDraftGuard.Check(PresetAction.Delete, State(active: "Raid night"));

        Assert.Equal(GuardVerdict.Confirm, result.Verdict);
        Assert.Contains("in use", result.Message);
        Assert.Contains("built-in casting", result.Message);
    }

    [Fact]
    public void Delete_OfTheBuiltInCasting_IsRefused()
    {
        var result = CastingDraftGuard.Check(PresetAction.Delete, State(selected: CastingPreset.DefaultPresetName));

        Assert.Equal(GuardVerdict.Blocked, result.Verdict);
        Assert.Equal(CastingDraftGuard.BuiltInDeleteMessage, result.Message);
    }

    // ---- names

    private static readonly string[] Existing = [CastingPreset.DefaultPresetName, "Raid night", "Story"];

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyName_IsRefused(string typed)
    {
        var result = CastingDraftGuard.CheckName(PresetAction.New, typed, Existing, State());

        Assert.Equal(GuardVerdict.Blocked, result.Verdict);
    }

    [Fact]
    public void TheReservedName_IsRefused()
    {
        var result = CastingDraftGuard.CheckName(PresetAction.Duplicate, " Default ", Existing, State());

        Assert.Equal(GuardVerdict.Blocked, result.Verdict);
    }

    [Theory]
    [InlineData(PresetAction.Duplicate)]
    [InlineData(PresetAction.New)]
    public void ATakenName_NeverOverwritesOnDuplicateOrNew(PresetAction action)
    {
        var result = CastingDraftGuard.CheckName(action, "raid night", Existing, State());

        Assert.Equal(GuardVerdict.Blocked, result.Verdict);
        Assert.Contains("already exists", result.Message);
    }

    [Fact]
    public void ATakenName_OnImport_AsksBeforeReplacing()
    {
        var result = CastingDraftGuard.CheckName(PresetAction.Import, "Story", Existing, State());

        Assert.Equal(GuardVerdict.Confirm, result.Verdict);
        Assert.Equal("Replace", result.ConfirmLabel);
        Assert.Contains("\"Story\"", result.Message);
    }

    [Theory]
    [InlineData(PresetAction.Duplicate)]
    [InlineData(PresetAction.New)]
    [InlineData(PresetAction.Import)]
    public void AFreshName_IsAllowed(PresetAction action)
    {
        Assert.True(CastingDraftGuard.CheckName(action, "Brand new", Existing, State()).IsAllowed);
    }

    [Fact]
    public void NameChecks_AlsoRespectUnsavedEdits()
    {
        var result = CastingDraftGuard.CheckName(PresetAction.Import, "Brand new", Existing, State(dirty: true));

        Assert.Equal(GuardVerdict.Blocked, result.Verdict);
    }

    // ---- the suggested names the dialogs start from

    [Fact]
    public void UniqueName_WithABase_SkipsTakenNames()
    {
        Assert.Equal("Story copy", CastingDraft.UniqueName(Existing, "Story copy"));
        Assert.Equal("Story copy 2", CastingDraft.UniqueName([.. Existing, "Story copy"], "Story copy"));
        Assert.Equal("My casting", CastingDraft.UniqueName(Existing));
    }
}
