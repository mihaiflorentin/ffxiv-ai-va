namespace AIVoiceActing.Tests;

using AIVoiceActing.Tests.Mock;
using AIVoiceActing.UI.State;
using Xunit;

public sealed class CharacterTableModelTests
{
    private sealed class Harness
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

        public FakeProfileStore Store { get; } = new();

        public StatusCenter Status { get; }

        public Harness()
        {
            this.Status = new StatusCenter(() => this.Now);
        }

        public CharacterTableModel Model(bool player = false) => new(
            player,
            this.Store,
            this.Status,
            world => world switch { 66 => "Zodiark", 1177 => "Alpha", null => "—", _ => world.ToString()! },
            voice => voice switch { "af_heart" => "Heart", "am_michael" => "Michael", "bf_emma" => "Emma", _ => voice },
            () => this.Now);
    }

    private static Harness WithNpcs()
    {
        var h = new Harness();
        h.Store.Add("npc:thancred", "am_michael");
        h.Store.Add("npc:alisaie", "bf_emma", pitch: 1.1f);
        h.Store.Add("npc:y'shtola", "af_heart");
        h.Store.Add("pc:warrior of light@66", "af_heart");
        return h;
    }

    [Fact]
    public void Rows_AreScopedToTheKindAndSortedByNameByDefault()
    {
        var h = WithNpcs();

        Assert.Equal(["alisaie", "thancred", "y'shtola"], h.Model().Rows.Select(r => r.Key.Name));
        Assert.Equal(["warrior of light"], h.Model(player: true).Rows.Select(r => r.Key.Name));
    }

    [Fact]
    public void SortBy_ReordersByEachColumnBothWays()
    {
        var h = WithNpcs();
        var model = h.Model();

        model.SortBy(CharacterColumn.Name, ascending: false);
        Assert.Equal(["y'shtola", "thancred", "alisaie"], model.Rows.Select(r => r.Key.Name));

        model.SortBy(CharacterColumn.Voice, ascending: true); // Emma, Heart, Michael
        Assert.Equal(["alisaie", "y'shtola", "thancred"], model.Rows.Select(r => r.Key.Name));

        model.SortBy(CharacterColumn.Voice, ascending: false);
        Assert.Equal(["thancred", "y'shtola", "alisaie"], model.Rows.Select(r => r.Key.Name));
    }

    [Fact]
    public void SortBy_WorldUsesTheWorldName()
    {
        var h = new Harness();
        h.Store.Add("pc:ada one@66", "af_heart");
        h.Store.Add("pc:bob two@1177", "af_heart");
        var model = h.Model(player: true);

        model.SortBy(CharacterColumn.World, ascending: true); // Alpha, Zodiark

        Assert.Equal(["bob two", "ada one"], model.Rows.Select(r => r.Key.Name));
        Assert.Equal(["Alpha", "Zodiark"], model.Rows.Select(r => r.WorldLabel));
    }

    [Fact]
    public void Filter_MatchesNameVoiceNameAndVoiceId()
    {
        var h = WithNpcs();
        var model = h.Model();

        model.Filter = "THAN";
        Assert.Equal(["thancred"], model.Rows.Select(r => r.Key.Name));

        model.Filter = "heart";
        Assert.Equal(["y'shtola"], model.Rows.Select(r => r.Key.Name));

        model.Filter = "bf_";
        Assert.Equal(["alisaie"], model.Rows.Select(r => r.Key.Name));

        Assert.Equal("1 of 3 shown", model.CountText);
    }

    [Fact]
    public void Filter_MatchesTheWorldOfAPlayer()
    {
        var h = new Harness();
        h.Store.Add("pc:ada one@66", "af_heart");
        h.Store.Add("pc:bob two@1177", "af_heart");
        var model = h.Model(player: true);

        model.Filter = "zodi";

        Assert.Equal(["ada one"], model.Rows.Select(r => r.Key.Name));
    }

    [Fact]
    public void CountText_And_EmptyDescribeTheList()
    {
        var h = new Harness();
        var model = h.Model();
        Assert.Equal(ListEmptyKind.Nothing, model.Empty!.Kind);
        Assert.Equal("No NPCs cast yet.", model.Empty.Title);

        h.Store.Add("npc:one", "af_heart");
        model.Invalidate();
        Assert.Equal("1 NPC", model.CountText);
        Assert.Null(model.Empty);

        model.Filter = "zzz";
        Assert.Equal(ListEmptyKind.NoMatches, model.Empty!.Kind);
        Assert.Equal("No one matches \"zzz\".", model.Empty.Title);
        Assert.Equal("Clear search", model.Empty.ActionLabel);
    }

    [Fact]
    public void Rows_AreCachedBetweenFrames_AndRefreshAfterTheInterval()
    {
        var h = WithNpcs();
        var model = h.Model();
        var first = model.Rows;

        Assert.Same(first, model.Rows);

        h.Store.Add("npc:minfilia", "af_heart"); // the game assigned someone while the window was open
        Assert.Same(first, model.Rows);

        h.Now += CharacterTableModel.RefreshInterval;
        Assert.Contains(model.Rows, r => r.Key.Name == "minfilia");
    }

    [Fact]
    public void PendingEdits_AreWrittenOnceOnCommit()
    {
        var h = WithNpcs();
        var model = h.Model();
        var profile = model.Rows.First(r => r.Key.Name == "alisaie").Profile;

        // A drag produces many values; only the last one reaches the store, and only on release.
        for (var i = 1; i <= 50; i++)
        {
            model.SetPending(profile.SpeakerKey, model.Effective(profile) with { Pitch = 1f + (i / 100f) });
        }

        Assert.Empty(h.Store.Writes);
        Assert.True(model.HasPending(profile.SpeakerKey));
        Assert.Equal(1.5f, model.Effective(profile).Pitch, 3);

        Assert.True(model.CommitPending(profile.SpeakerKey));
        Assert.False(model.CommitPending(profile.SpeakerKey));

        var write = Assert.Single(h.Store.Writes);
        Assert.Equal(profile.SpeakerKey, write.SpeakerKey);
        Assert.Equal(1.5f, write.Pitch, 3);
        Assert.Equal("bf_emma", write.ReferenceVoiceId);
        Assert.False(model.HasPending(profile.SpeakerKey));
    }

    [Fact]
    public void Commit_WritesAPickedVoiceAndDropsHeldSliderValues()
    {
        var h = WithNpcs();
        var model = h.Model();
        var profile = model.Rows.First(r => r.Key.Name == "thancred").Profile;
        model.SetPending(profile.SpeakerKey, model.Effective(profile) with { Speed = 1.2f });

        model.Commit(profile.SpeakerKey, CharacterEdit.From(profile) with { VoiceId = "af_heart" });

        var write = Assert.Single(h.Store.Writes);
        Assert.Equal("af_heart", write.ReferenceVoiceId);
        Assert.Equal(profile.Speed, write.Speed);
        Assert.Equal(0, model.PendingCount);
        Assert.Equal("Heart", model.Rows.First(r => r.Key.Name == "thancred").VoiceName);
    }

    [Fact]
    public void Commit_ClampsVolumeAndBias()
    {
        var h = WithNpcs();
        var model = h.Model();

        model.Commit("npc:thancred", new CharacterEdit("am_michael", 3f, 1f, 1f, 9f));

        var write = Assert.Single(h.Store.Writes);
        Assert.Equal(1f, write.ExaggerationBias);
        Assert.Equal(2f, write.Volume);
    }

    [Fact]
    public void Delete_RemovesTheCharacterAndOffersUndoThatRestoresIt()
    {
        var h = WithNpcs();
        var model = h.Model();
        var profile = model.Rows.First(r => r.Key.Name == "alisaie").Profile;

        Assert.True(model.Delete(profile.SpeakerKey));

        Assert.Null(h.Store.Get(profile.SpeakerKey));
        Assert.DoesNotContain(model.Rows, r => r.Key.Name == "alisaie");
        var toast = Assert.Single(h.Status.Active());
        Assert.Equal("Removed alisaie", toast.Title);
        var undo = Assert.Single(toast.Actions);
        Assert.Equal("Undo", undo.Label);

        Assert.True(h.Status.Invoke(toast.Id, 0));

        var restored = h.Store.Get(profile.SpeakerKey)!;
        Assert.Equal("bf_emma", restored.ReferenceVoiceId);
        Assert.Equal(1.1f, restored.Pitch, 3);
        Assert.Contains(model.Rows, r => r.Key.Name == "alisaie");
        Assert.Empty(h.Status.Active());
    }

    [Fact]
    public void Undo_IsOfferedForTheToastLifetimeOnly()
    {
        var h = WithNpcs();
        var model = h.Model();
        model.Delete("npc:thancred");
        var toast = h.Status.Active().Single();

        h.Now += StatusCenter.UndoLifetime;

        Assert.False(h.Status.Invoke(toast.Id, 0));
        Assert.Null(h.Store.Get("npc:thancred"));
    }

    [Fact]
    public void Delete_DropsHeldEditsAndIgnoresUnknownSpeakers()
    {
        var h = WithNpcs();
        var model = h.Model();
        var profile = model.Rows.First().Profile;
        model.SetPending(profile.SpeakerKey, CharacterEdit.From(profile) with { Pitch = 1.4f });

        Assert.True(model.Delete(profile.SpeakerKey));
        Assert.Equal(0, model.PendingCount);

        Assert.False(model.Delete("npc:nobody"));
        Assert.DoesNotContain(h.Status.Active(), m => m.Title == "Removed nobody");
    }

    [Fact]
    public void RemoveAll_ClearsOnlyThisKind()
    {
        var h = WithNpcs();
        var model = h.Model();

        Assert.Equal(3, model.RemoveAll());

        Assert.Empty(model.Rows);
        Assert.NotNull(h.Store.Get("pc:warrior of light@66"));
    }
}
