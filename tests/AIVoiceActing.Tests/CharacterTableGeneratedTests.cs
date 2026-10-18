namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using AIVoiceActing.UI.State;
using Xunit;

/// <summary>The character table's actions for generated voices: label, Regenerate and Keep as is.</summary>
public sealed class CharacterTableGeneratedTests
{
    private const string Generated = "bf_gen_aaaaaaaaaaaa";

    private sealed class FakeGenerator : IVoiceGenerator
    {
        public List<string> Regenerated { get; } = [];

        public GeneratedVoiceRecord? Ensure(string speakerKey, string displayName, IReadOnlyList<string> castVoiceIds, string? preferredSex) =>
            null;

        public GeneratedVoiceRecord? Regenerate(string voiceId)
        {
            this.Regenerated.Add(voiceId);
            return new GeneratedVoiceRecord(
                "bf_gen_bbbbbbbbbbbb", "npc:alisaie", "Alisaie", Phonemizers.EnglishGb, "female", 1, [], [], DateTimeOffset.UnixEpoch);
        }
    }

    private static (CharacterTableModel Model, FakeProfileStore Store, FakeGenerator Generator) Build(bool withGenerator = true)
    {
        var store = new FakeProfileStore();
        store.Add("npc:alisaie", Generated, pitch: 1.1f);
        store.SetVoice("npc:alisaie", Generated, custom: false);
        var generator = new FakeGenerator();
        var model = new CharacterTableModel(
            player: false,
            store,
            new StatusCenter(() => DateTimeOffset.UnixEpoch),
            _ => string.Empty,
            id => id == Generated ? "Generated for Alisaie" : id,
            generator: withGenerator ? () => generator : null);
        return (model, store, generator);
    }

    [Fact]
    public void GeneratedVoices_ShowAsGeneratedForTheCharacter_AndAreSearchable()
    {
        var (model, _, _) = Build();
        model.Filter = "generated";

        var row = Assert.Single(model.Rows);
        Assert.Equal("Generated for Alisaie", row.VoiceName);
        Assert.True(model.IsGenerated(row.Profile));
    }

    [Fact]
    public void Regenerate_SwapsTheVoice_KeepingTuningAndTheAutomaticFlag()
    {
        var (model, store, generator) = Build();

        Assert.True(model.Regenerate("npc:alisaie"));

        var profile = store.Get("npc:alisaie")!;
        Assert.Equal("bf_gen_bbbbbbbbbbbb", profile.ReferenceVoiceId);
        Assert.False(profile.Custom);
        Assert.Equal(1.1f, profile.Pitch);
        Assert.Equal([Generated], generator.Regenerated);
    }

    [Fact]
    public void KeepAsIs_PinsTheVoiceAsHandSet()
    {
        var (model, store, _) = Build();
        Assert.True(model.CanKeep(store.Get("npc:alisaie")!));

        Assert.True(model.Keep("npc:alisaie"));

        var profile = store.Get("npc:alisaie")!;
        Assert.Equal(Generated, profile.ReferenceVoiceId);
        Assert.True(profile.Custom);
        Assert.False(model.CanKeep(profile));
    }

    [Fact]
    public void PlainVoices_OfferNeitherAction()
    {
        var (model, store, _) = Build();
        store.Add("npc:thancred", "am_michael");
        var plain = store.Get("npc:thancred")!;

        Assert.False(model.IsGenerated(plain));
        Assert.False(model.CanRegenerate(plain));
        Assert.False(model.Regenerate("npc:thancred"));
        Assert.False(model.Keep("npc:thancred"));
        Assert.Equal("am_michael", store.Get("npc:thancred")!.ReferenceVoiceId);
    }

    [Fact]
    public void WithoutAGenerator_RegenerateIsUnavailable()
    {
        var (model, store, _) = Build(withGenerator: false);

        Assert.False(model.CanRegenerate(store.Get("npc:alisaie")!));
        Assert.False(model.Regenerate("npc:alisaie"));
    }
}
