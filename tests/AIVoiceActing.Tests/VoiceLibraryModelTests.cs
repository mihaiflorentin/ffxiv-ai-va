namespace AIVoiceActing.Tests;

using AIVoiceActing.Infrastructure.Storage;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.State;
using Xunit;

public sealed class VoiceLibraryModelTests
{
    private static VoiceInfo Voice(
        string id,
        string name,
        string accent = "en-US",
        string sex = "female",
        string age = "adult",
        string description = "A voice.",
        VoiceQuality quality = VoiceQuality.Stock,
        string pack = "aiva-kokoro-stock") =>
        new(id, name, accent, sex, age, description, "Apache-2.0", null, [], quality, null, pack, []);

    private static IReadOnlyList<VoiceInfo> Library() =>
    [
        Voice("af_heart", "Heart", "en-US", "female", description: "Warm, steady narrator."),
        Voice("am_michael", "Michael", "en-US", "male", description: "Clear and brisk."),
        Voice("bf_emma", "Emma", "en-GB", "female", description: "British English female voice."),
        Voice("isles.ie_m_01", "Fionn", "en-IE", "male", "senior", "Gruff storyteller from the west coast.", VoiceQuality.Experimental, "isles"),
        Voice("isles.sct_f_01", "Isla", "en-GB-SCT", "female", "young", "Bright and quick.", VoiceQuality.Experimental, "isles"),
    ];

    [Fact]
    public void Search_FindsAnAccentWordThatIsNotInTheNameOrId()
    {
        var model = new VoiceLibraryModel { Search = "irish" };

        var result = model.Compute(Library());

        var row = Assert.Single(result.Rows);
        Assert.Equal("isles.ie_m_01", row.Id);
    }

    [Theory]
    [InlineData("HEART", "af_heart")]
    [InlineData("brisk", "am_michael")]
    [InlineData("sct_f", "isles.sct_f_01")]
    [InlineData("scottish", "isles.sct_f_01")]
    [InlineData("en-GB", "bf_emma,isles.sct_f_01")]
    public void Search_MatchesNameDescriptionIdAndAccentCode(string text, string expectedIds)
    {
        var model = new VoiceLibraryModel { Search = text };

        var ids = model.Compute(Library()).Rows.Select(r => r.Id).Order().ToArray();

        Assert.Equal(expectedIds.Split(',').Order().ToArray(), ids);
    }

    [Fact]
    public void Search_RequiresEveryWord()
    {
        var model = new VoiceLibraryModel { Search = "british female" };

        Assert.Equal(["bf_emma"], model.Compute(Library()).Rows.Select(r => r.Id));

        model.Search = "british male brisk";
        Assert.Empty(model.Compute(Library()).Rows);
    }

    [Fact]
    public void Filters_OrWithinAGroupAndAcrossGroups()
    {
        var model = new VoiceLibraryModel();
        model.Toggle(VoiceFilterGroup.Accent, "en-US");
        model.Toggle(VoiceFilterGroup.Accent, "en-GB");

        Assert.Equal(["bf_emma", "af_heart", "am_michael"], model.Compute(Library()).Rows.Select(r => r.Id));

        model.Toggle(VoiceFilterGroup.Sex, "male");
        Assert.Equal(["am_michael"], model.Compute(Library()).Rows.Select(r => r.Id));

        model.Toggle(VoiceFilterGroup.Age, "senior");
        Assert.Empty(model.Compute(Library()).Rows);
    }

    [Fact]
    public void Filters_CombineWithSearch()
    {
        var model = new VoiceLibraryModel { Search = "voice" };
        model.Toggle(VoiceFilterGroup.Sex, "female");

        Assert.Equal(["bf_emma"], model.Compute(Library()).Rows.Select(r => r.Id));
    }

    [Fact]
    public void Counts_ReportShownOfTotalAndOptionCounts()
    {
        var model = new VoiceLibraryModel();
        var all = model.Compute(Library());
        Assert.Equal("5 of 5 shown", all.CountText);
        Assert.False(all.Filtered);

        model.Toggle(VoiceFilterGroup.Sex, "male");
        var males = model.Compute(Library());
        Assert.Equal(2, males.Shown);
        Assert.Equal(5, males.Total);
        Assert.Equal("2 of 5 shown", males.CountText);
        Assert.True(males.Filtered);

        // Option counts describe the whole library, so a chip never shows a number that moves under it.
        var sexes = males.Sexes.ToDictionary(o => o.Value, o => o.Count);
        Assert.Equal(3, sexes["female"]);
        Assert.Equal(2, sexes["male"]);
        Assert.Equal(["Female", "Male"], males.Sexes.Select(o => o.Label));
    }

    [Fact]
    public void Options_AreOrderedAndLabelledForPlayers()
    {
        var result = new VoiceLibraryModel().Compute(Library());

        Assert.Equal(["American", "British", "Irish", "Scottish"], result.Accents.Select(o => o.Label));
        Assert.Equal(["Young", "Adult", "Senior"], result.Ages.Select(o => o.Label));
    }

    [Fact]
    public void Rows_AreSortedByName()
    {
        var names = new VoiceLibraryModel().Compute(Library()).Rows.Select(r => r.Voice!.DisplayName);

        Assert.Equal(["Emma", "Fionn", "Heart", "Isla", "Michael"], names);
    }

    [Fact]
    public void Clear_DropsSearchAndEveryChip()
    {
        var model = new VoiceLibraryModel { Search = "irish" };
        model.Toggle(VoiceFilterGroup.Sex, "male");
        Assert.True(model.HasFilters);

        model.Clear();

        Assert.False(model.HasFilters);
        Assert.Equal(5, model.Compute(Library()).Rows.Count);
    }

    [Fact]
    public void Empty_NoMatchesNamesTheSearchAndOffersClearFilters()
    {
        var model = new VoiceLibraryModel { Search = "scot x" };

        var empty = model.Compute(Library()).Empty;

        Assert.NotNull(empty);
        Assert.Equal(ListEmptyKind.NoMatches, empty.Kind);
        Assert.Equal("No voices match \"scot x\".", empty.Title);
        Assert.Equal("Clear filters", empty.ActionLabel);
    }

    [Fact]
    public void Empty_FiltersOnlyHasAGenericTitle()
    {
        var model = new VoiceLibraryModel();
        model.Toggle(VoiceFilterGroup.Age, "child");

        var empty = model.Compute(Library()).Empty;

        Assert.Equal("No voices match these filters.", empty!.Title);
    }

    [Fact]
    public void Empty_EmptyLibraryPointsAtThePacksFolder()
    {
        var empty = new VoiceLibraryModel().Compute([]).Empty;

        Assert.NotNull(empty);
        Assert.Equal(ListEmptyKind.Nothing, empty.Kind);
        Assert.Equal("Open packs folder", empty.ActionLabel);
    }

    [Fact]
    public void Empty_IsNullWhenThereAreRows()
    {
        Assert.Null(new VoiceLibraryModel().Compute(Library()).Empty);
    }

    [Fact]
    public void MissingVoices_ShowAsPlaceholderRowsFirst_AndStayOutOfTheCounts()
    {
        var model = new VoiceLibraryModel();

        var result = model.Compute(Library(), ["af_heart", "gone.pack_01", "zz_lost"]);

        Assert.Equal(["gone.pack_01", "zz_lost"], result.Rows.Take(2).Select(r => r.Id));
        Assert.All(result.Rows.Take(2), row => Assert.True(row.Missing));
        Assert.Equal(2, result.MissingCount);
        Assert.Equal(5, result.Shown);
        Assert.Equal("5 of 5 shown", result.CountText);
        Assert.Null(result.Empty);
    }

    [Fact]
    public void MissingVoices_FollowTheSearchAndHideUnderChips()
    {
        var model = new VoiceLibraryModel { Search = "lost" };
        var result = model.Compute(Library(), ["gone.pack_01", "zz_lost"]);
        Assert.Equal(["zz_lost"], result.Rows.Select(r => r.Id));
        Assert.Equal(0, result.Shown);

        model.Search = string.Empty;
        model.Toggle(VoiceFilterGroup.Sex, "male");
        Assert.DoesNotContain(model.Compute(Library(), ["gone.pack_01"]).Rows, r => r.Missing);
    }

    [Fact]
    public void MissingVoices_AreNotListedWhenThePackStillProvidesThem()
    {
        var result = new VoiceLibraryModel().Compute(Library(), ["af_heart", "isles.ie_m_01"]);

        Assert.Equal(0, result.MissingCount);
    }

    [Fact]
    public void Compute_IsCachedByItsInputs()
    {
        var model = new VoiceLibraryModel { Search = "heart" };
        var library = Library();

        var first = model.Compute(library, ["af_heart"]);
        for (var frame = 0; frame < 100; frame++)
        {
            Assert.Same(first, model.Compute(library, ["af_heart"]));
        }

        Assert.Equal(1, model.ComputeCount);
    }

    [Fact]
    public void Compute_RebuildsWhenTheLibraryFiltersOrReferencesChange()
    {
        var model = new VoiceLibraryModel();
        var library = Library();
        model.Compute(library, []);

        model.Compute(Library(), []); // a reload hands out a new snapshot
        Assert.Equal(2, model.ComputeCount);

        model.Search = "heart";
        model.Compute(library, []);
        Assert.Equal(3, model.ComputeCount);

        model.Search = "heart"; // same text again is not a change
        model.Compute(library, []);
        Assert.Equal(3, model.ComputeCount);

        model.Toggle(VoiceFilterGroup.Sex, "male");
        model.Compute(library, []);
        Assert.Equal(4, model.ComputeCount);

        model.Compute(library, ["gone"]);
        Assert.Equal(5, model.ComputeCount);
    }

    [Fact]
    public void StockLibrary_FindsBritishVoicesByAccentWord()
    {
        var library = new FileVoiceLibrary(() => [], () => []);
        var model = new VoiceLibraryModel { Search = "british" };

        var result = model.Compute(library.Voices);

        Assert.NotEmpty(result.Rows);
        Assert.All(result.Rows, row => Assert.Equal("en-GB", row.Voice!.Accent));
        Assert.Equal(library.Voices.Count, result.Total);
    }

    [Theory]
    [InlineData("female", "Female")]
    [InlineData("MALE", "Male")]
    [InlineData("neutral", "Neutral")]
    [InlineData("", "Unknown")]
    public void Labels_Sex(string raw, string expected) => Assert.Equal(expected, Labels.Sex(raw));

    [Theory]
    [InlineData("child", "Child")]
    [InlineData("young", "Young")]
    [InlineData("senior", "Senior")]
    [InlineData(null, "Unknown")]
    public void Labels_Age(string? raw, string expected) => Assert.Equal(expected, Labels.Age(raw));
}
