namespace AIVoiceActing.Tests;

using AIVoiceActing.Container;
using AIVoiceActing.Domain;
using AIVoiceActing.Tests.Mock;
using Xunit;

/// <summary>
/// UseRaceVoicePresets drives the automatic voice assignment: on, a race/gender speaker
/// resolves from its race/gender slot set; off (TTT default-bucket semantics), every
/// unlisted speaker resolves from the ungendered set instead.
/// </summary>
public sealed class ContainerVoicePresetTests
{
    private static SpeakerIdentity Speaker() =>
        new("npc: sibold", "Sibold", Race: 1, Tribe: 1, Sex: 0, World: null);

    private static ServiceContainer Container(bool racePresets, string root) =>
        new(
            logSinkOverride: new FakeLogSink(),
            profileStorePathFactory: () => Path.Combine(root, "voice-assignments.json"),
            modelsDirFactory: () => Path.Combine(root, "models"),
            voicesManifestFactory: () => Path.Combine(AppContext.BaseDirectory, "voices.json"),
            speechQueueFactory: () => new FakeSpeechQueue(),
            useRaceVoicePresetsFactory: () => racePresets);

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "aiva-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void RacePresetsOn_ResolvesFromTheRaceGenderSet()
    {
        var root = NewRoot();
        try
        {
            using var container = Container(racePresets: true, root);
            var profile = container.ResolveProfile(Speaker());

            var maleIds = container.VoiceMap.SlotsFor(VoiceGroup.Male, 1).Select(s => s.Id).ToHashSet();
            var ungenderedIds = container.VoiceMap.SlotsFor(VoiceGroup.Ungendered, null).Select(s => s.Id).ToHashSet();

            Assert.Contains(profile.ReferenceVoiceId, maleIds);
            Assert.DoesNotContain(profile.ReferenceVoiceId, ungenderedIds);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RacePresetsOff_ResolvesFromTheUngenderedSet()
    {
        var root = NewRoot();
        try
        {
            using var container = Container(racePresets: false, root);
            var profile = container.ResolveProfile(Speaker());

            var ungenderedIds = container.VoiceMap.SlotsFor(VoiceGroup.Ungendered, null).Select(s => s.Id).ToHashSet();
            var maleIds = container.VoiceMap.SlotsFor(VoiceGroup.Male, 1).Select(s => s.Id).ToHashSet();

            Assert.Contains(profile.ReferenceVoiceId, ungenderedIds);
            Assert.DoesNotContain(profile.ReferenceVoiceId, maleIds);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
