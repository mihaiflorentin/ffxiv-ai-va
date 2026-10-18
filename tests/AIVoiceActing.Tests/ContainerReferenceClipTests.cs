namespace AIVoiceActing.Tests;

using System.Reflection;
using AIVoiceActing.Container;
using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Infrastructure.Storage;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

/// <summary>
/// Regression for the owner's in-game report (Most expressive: female characters spoke with male voices and
/// the accented voices had no accent). Turbo clones a reference clip, so which clip a voice id resolves to is
/// the whole story: a pack's real recording beats a Kokoro render, a rendered clip is found at its pitch, and
/// every fallback follows the voice's sex.
/// </summary>
public sealed class ContainerReferenceClipTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "aiva-refclip", Guid.NewGuid().ToString("N"));
    private readonly FakeLogSink log = new();

    public ContainerReferenceClipTests()
    {
        foreach (var dir in new[] { "models", "bank", "packs", "generated" })
        {
            Directory.CreateDirectory(Path.Combine(this.root, dir));
        }

        this.Wav("bank/uk_male_casual.wav", 0.1f);
        this.Wav("bank/uk_female_soft.wav", 0.2f);
        this.Wav("models/" + ModelCatalog.TurboDefaultVoiceFileName, 0.3f);
    }

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    private string Path_(string relative) => Path.Combine(this.root, relative.Replace('/', Path.DirectorySeparatorChar));

    private string Wav(string relative, float level, int seconds = 5)
    {
        var path = this.Path_(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WavCodec.WriteMono24k(path, Enumerable.Repeat(level, 24000 * seconds).ToArray());
        return path;
    }

    /// <summary>A pack with one voice per id; ids ending in a name listed in <paramref name="withClip"/> get a real clip.</summary>
    private void Pack(string packId, string sex, string[] ids, params string[] withClip)
    {
        var dir = Path.Combine(this.root, "packs", packId);
        var voices = new List<string>();
        foreach (var id in ids)
        {
            PackFixture.WriteTensor(dir, $"style/{id}.npy");
            var clip = withClip.Contains(id)
                ? $$""", { "kind": "ReferenceClip", "file": "clips/{{id}}.wav", "transcript": "clips/{{id}}.txt" }"""
                : string.Empty;
            if (clip.Length > 0)
            {
                this.Wav($"packs/{packId}/clips/{id}.wav", 0.5f);
                PackFixture.WriteFile(dir, $"clips/{id}.txt", "A short transcript.");
            }

            voices.Add($$"""
                { "id": "{{packId}}.{{id}}", "name": "{{id}}", "accent": "en-IE", "sex": "{{sex}}", "quality": "experimental",
                  "realizations": [ { "kind": "StyleTensor", "file": "style/{{id}}.npy", "phonemizer": "en-us" }{{clip}} ] }
                """);
        }

        File.WriteAllText(
            Path.Combine(dir, "voicepack.json"),
            $$"""
            { "schema": 1, "id": "{{packId}}", "version": "1.0.0", "name": "{{packId}}",
              "license": { "spdx": "CC-BY-SA-4.0", "attribution": "Test" },
              "voices": [ {{string.Join(",", voices)}} ] }
            """);
    }

    private ServiceContainer Container() =>
        new(
            logSinkOverride: this.log,
            modelsDirFactory: () => this.Path_("models"),
            voicePackRootsFactory: () => [this.Path_("packs")],
            referenceClipsDirFactory: () => this.Path_("bank"),
            generatedVoicesDirFactory: () => this.Path_("generated"));

    private static string? Resolve(ServiceContainer container, string voiceId)
    {
        var make = typeof(ServiceContainer).GetMethod("ClippingResolver", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var resolver = (Func<string, string?>)make.Invoke(container, [ModelCatalog.TurboDefaultVoiceFileName])!;
        return resolver(voiceId);
    }

    private static async Task<string?> Prepare(ServiceContainer container, string voiceId, float pitch = 1f)
    {
        var prepare = typeof(ServiceContainer).GetMethod("PrepareExpressiveVoiceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return await (Task<string?>)prepare.Invoke(
            container, [new SynthesisRequest(voiceId, "Hello.", 0.5f, [], Pitch: pitch), CancellationToken.None])!;
    }

    private static void NotReady(ServiceContainer container) =>
        typeof(ServiceContainer).GetField("kokoroReferenceRenderer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(container, new FakeSpeechSynthesizer { IsReady = false });

    private void SaveBlend(string id, string sex, params (string Source, float Weight)[] parts)
    {
        var store = new GeneratedVoiceStore(() => this.Path_("generated"), this.log);
        store.Save(
            new GeneratedVoiceRecord(
                id, "npc:test", "Test", "en-us", sex, 0,
                [.. parts.Select(p => new BlendPart(p.Source, p.Weight))], [], DateTimeOffset.UnixEpoch),
            new float[510 * 256]);
    }

    [Theory]
    [InlineData("isles.sth_f_01")]
    [InlineData("accented.es_f_dora")]
    public void AFemalePackId_NotInTheLibrary_GetsTheFemaleBankClip(string voiceId)
    {
        using var container = this.Container();

        Assert.Equal(this.Path_("bank/uk_female_soft.wav"), Resolve(container, voiceId));
    }

    [Fact]
    public void AMalePackId_StillGetsTheMaleBankClip()
    {
        using var container = this.Container();

        Assert.Equal(this.Path_("bank/uk_male_casual.wav"), Resolve(container, "isles.ie_m_01"));
    }

    [Fact]
    public void ALibraryVoiceWithoutSexInItsId_UsesTheLibrarysSex()
    {
        this.Pack("quirky", "female", ["lady"]);
        using var container = this.Container();

        Assert.Equal(this.Path_("bank/uk_female_soft.wav"), Resolve(container, "quirky.lady"));
    }

    [Fact]
    public void AnUnknownFemaleId_NeverFallsToTheMaleClip_EvenWithNothingRendered()
    {
        using var container = this.Container();
        NotReady(container);

        Assert.Null(Prepare(container, "nowhere.zz_f_99").Result);
        Assert.Equal(this.Path_("bank/uk_female_soft.wav"), Resolve(container, "nowhere.zz_f_99"));
    }

    [Fact]
    public async Task TheRenderedClipIsFoundAtTheRequestsPitch_AndByTheIdOnlyResolver()
    {
        var folder = this.Path_("models/" + ServiceContainer.ExpressiveClipsFolder);
        this.Wav($"models/{ServiceContainer.ExpressiveClipsFolder}/bf_zed_p112.wav", 0.4f);
        using var container = this.Container();

        Assert.Equal(Path.Combine(folder, "bf_zed_p112.wav"), await Prepare(container, "bf_zed", 1.12f));
        // No pitch on this path: the rendered clip of another pitch still beats the bank.
        Assert.Equal(Path.Combine(folder, "bf_zed_p112.wav"), Resolve(container, "bf_zed"));
    }

    [Fact]
    public void TheUnpitchedRenderedClip_WinsOverOtherPitches()
    {
        this.Wav($"models/{ServiceContainer.ExpressiveClipsFolder}/bf_zed_p086.wav", 0.4f);
        var plain = this.Wav($"models/{ServiceContainer.ExpressiveClipsFolder}/bf_zed_p100.wav", 0.4f);
        using var container = this.Container();

        Assert.Equal(plain, Resolve(container, "bf_zed"));
    }

    [Fact]
    public async Task APackVoiceWithARealClip_IsClonedFromThatClip_NotFromAKokoroRender()
    {
        this.Pack("isles", "female", ["sth_f_01"], "sth_f_01");
        var clip = this.Path_("packs/isles/clips/sth_f_01.wav");
        // A stale Kokoro render of an earlier version must not win.
        this.Wav($"models/{ServiceContainer.ExpressiveClipsFolder}/isles.sth_f_01_p100.wav", 0.4f);
        using var container = this.Container();

        Assert.Equal(clip, await Prepare(container, "isles.sth_f_01"));
        Assert.Equal(clip, Resolve(container, "isles.sth_f_01"));
    }

    [Fact]
    public async Task AUserClip_StillBeatsThePackClip()
    {
        this.Pack("isles", "female", ["sth_f_01"], "sth_f_01");
        var mine = this.Wav("models/voices/isles.sth_f_01.wav", 0.9f);
        using var container = this.Container();

        Assert.Equal(mine, await Prepare(container, "isles.sth_f_01"));
        Assert.Equal(mine, Resolve(container, "isles.sth_f_01"));
    }

    [Fact]
    public async Task APitchedPackVoice_GetsAResampledCopyOfTheRealClip()
    {
        this.Pack("isles", "female", ["sth_f_01"], "sth_f_01");
        var clip = this.Path_("packs/isles/clips/sth_f_01.wav");
        using var container = this.Container();

        var pitched = await Prepare(container, "isles.sth_f_01", 1.12f);

        Assert.NotNull(pitched);
        Assert.NotEqual(clip, pitched);
        Assert.Contains(ServiceContainer.ExpressiveClipsFolder, pitched);
        Assert.Contains("p112", pitched);
        var original = WavCodec.ReadMono24k(clip).Length;
        Assert.Equal((int)(original / 1.12f), WavCodec.ReadMono24k(pitched).Length);
        Assert.Equal(pitched, await Prepare(container, "isles.sth_f_01", 1.12f)); // cached
    }

    [Fact]
    public async Task AGeneratedFemaleBlend_UsesTheDominantSourcesPackClip()
    {
        this.Pack("isles", "female", ["mid_f_01", "wal_f_02"], "mid_f_01", "wal_f_02");
        this.SaveBlend("bf_gen_aaaaaaaaaaaa", "female", ("isles.wal_f_02", 0.3f), ("isles.mid_f_01", 0.7f));
        using var container = this.Container();

        var expected = this.Path_("packs/isles/clips/mid_f_01.wav");
        Assert.Equal(expected, await Prepare(container, "bf_gen_aaaaaaaaaaaa"));
        Assert.Equal(expected, Resolve(container, "bf_gen_aaaaaaaaaaaa"));
    }

    [Fact]
    public async Task AGeneratedBlend_FallsBackToAnySourceThatHasAClip()
    {
        this.Pack("isles", "female", ["mid_f_01", "wal_f_02"], "wal_f_02");
        this.SaveBlend("bf_gen_bbbbbbbbbbbb", "female", ("isles.mid_f_01", 0.7f), ("isles.wal_f_02", 0.3f));
        using var container = this.Container();

        Assert.Equal(this.Path_("packs/isles/clips/wal_f_02.wav"), await Prepare(container, "bf_gen_bbbbbbbbbbbb"));
    }

    [Fact]
    public async Task AGeneratedBlendWithNoRealClipAnywhere_KeepsTheKokoroRenderedClip_AndAFemaleFallback()
    {
        this.SaveBlend("bf_gen_cccccccccccc", "female", ("bf_emma", 0.6f), ("bf_lily", 0.4f));
        using var container = this.Container();
        NotReady(container);

        Assert.Null(await Prepare(container, "bf_gen_cccccccccccc"));
        Assert.Equal(this.Path_("bank/uk_female_soft.wav"), Resolve(container, "bf_gen_cccccccccccc"));

        this.Wav($"models/{ServiceContainer.ExpressiveClipsFolder}/bf_gen_cccccccccccc_p100.wav", 0.4f);
        Assert.Equal(
            this.Path_($"models/{ServiceContainer.ExpressiveClipsFolder}/bf_gen_cccccccccccc_p100.wav"),
            Resolve(container, "bf_gen_cccccccccccc"));
    }

    [Fact]
    public void APreparedClipPath_PassesThroughTheMainEnginesResolver()
    {
        var prepared = this.Wav($"models/{ServiceContainer.ExpressiveClipsFolder}/af_x_p100.wav", 0.4f);
        using var container = this.Container();

        Assert.Equal(prepared, Resolve(container, prepared));
    }

    [Fact]
    public void FirstUseOfAVoice_LogsTheClipSourceAndSex_Once()
    {
        this.Pack("isles", "female", ["sth_f_01"], "sth_f_01");
        using var container = this.Container();

        _ = Resolve(container, "isles.sth_f_01");
        _ = Resolve(container, "isles.sth_f_01");
        _ = Resolve(container, "nowhere.zz_f_99");

        var lines = this.log.Snapshot().Where(c => c.Level == "Info" && c.Message.StartsWith("Reference clip for")).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, c => c.Message.Contains("isles.sth_f_01") && c.Message.Contains("pack clip") && c.Message.Contains("sex female"));
        Assert.Contains(lines, c => c.Message.Contains("nowhere.zz_f_99") && c.Message.Contains("bank") && c.Message.Contains("sex female"));
    }

    [Theory]
    [InlineData("isles.sth_f_01", "female")]
    [InlineData("accented.es_f_dora", "female")]
    [InlineData("isles.ie_m_01", "male")]
    [InlineData("af_gen_abc", "female")]
    [InlineData("bm_gen_abc", "male")]
    [InlineData("bf_emma", "female")]
    [InlineData("quirky.lady", null)]
    [InlineData("", null)]
    public void SexFromId_ReadsStockPrefixesAndPackTokens(string id, string? expected) =>
        Assert.Equal(expected, ReferenceClipBank.SexFromId(id));

    [Fact]
    public void ClipIdFor_PrefersTheKnownSexOverTheDefault() =>
        Assert.Equal("uk_female_soft", ReferenceClipBank.ClipIdFor("quirky.lady", "female"));
}
