namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Kokoro;
using AIVoiceActing.Infrastructure.Storage;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using KokoroSharp.Core;
using Xunit;

/// <summary>Generated voices end to end on temp dirs: store, generator, library lookup and the Kokoro adapter.</summary>
public sealed class GeneratedVoiceTests : IDisposable
{
    private static readonly string[] FemaleCast = ["af_heart", "af_bella", "af_nicole", "af_sarah"];

    private readonly string root = Path.Combine(Path.GetTempPath(), "aiva-gen", Guid.NewGuid().ToString("N"));

    public GeneratedVoiceTests()
    {
        Directory.CreateDirectory(this.StockDir);
        foreach (var (id, index) in new[] { "af_heart", "af_bella", "af_nicole", "af_sarah", "am_adam", "am_michael", "bf_emma", "bf_alice" }
            .Select((id, i) => (id, i)))
        {
            File.WriteAllBytes(Path.Combine(this.StockDir, id + ".npy"), NpyHeader.WriteStyleTensor(Tensor(index + 1)));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    private string StockDir => Path.Combine(this.root, "stock");

    private string GenDir => Path.Combine(this.root, "voices-generated");

    private static float[] Tensor(int seed)
    {
        var values = new float[NpyHeader.StyleRows * NpyHeader.StyleColumns];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = MathF.Sin((seed * 0.37f) + (i * 0.013f)) * seed;
        }

        return values;
    }

    private (GeneratedVoiceStore Store, FileVoiceLibrary Library, VoiceGenerator Generator) Build(string? genDir = null)
    {
        var store = new GeneratedVoiceStore(() => genDir ?? this.GenDir, new FakeLogSink());
        var library = new FileVoiceLibrary(() => [], generated: store);
        return (store, library, new VoiceGenerator(library, store, () => this.StockDir, new FakeLogSink()));
    }

    // ---- npy ----

    [Fact]
    public void Npy_WriteThenRead_RoundTripsAndValidates()
    {
        var values = Tensor(3);
        var bytes = NpyHeader.WriteStyleTensor(values);

        Assert.Null(NpyHeader.ValidateStyleTensor(bytes));
        Assert.Equal(0, NpyHeader.Parse(bytes)!.DataOffset % 64);
        Assert.Equal(values, NpyHeader.ReadStyleTensor(bytes));
        Assert.Null(NpyHeader.ReadStyleTensor(bytes.AsSpan(0, bytes.Length - 4)));
        Assert.Throws<ArgumentException>(() => NpyHeader.WriteStyleTensor(new float[10]));
    }

    // ---- generator ----

    [Fact]
    public void Ensure_SameKeyAndSet_GivesIdenticalTensorBytes_InSeparateStores()
    {
        var a = this.Build(Path.Combine(this.root, "a"));
        var b = this.Build(Path.Combine(this.root, "b"));

        var first = a.Generator.Ensure("pc:Ada Lovelace@42", "Ada Lovelace", FemaleCast, "female")!;
        var second = b.Generator.Ensure("pc:Ada Lovelace@42", "Ada Lovelace", FemaleCast, "female")!;

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(this.root, "a", first.Id[7..] + ".npy")),
            File.ReadAllBytes(Path.Combine(this.root, "b", second.Id[7..] + ".npy")));
    }

    [Fact]
    public void Ensure_WritesTheWeightedBlendOfItsSources()
    {
        var (store, _, generator) = this.Build();
        var record = generator.Ensure("npc:feo ul", "Feo Ul", FemaleCast, "female")!;

        var stored = NpyHeader.ReadStyleTensor(File.ReadAllBytes(Path.Combine(this.GenDir, record.Id[7..] + ".npy")))!;
        var sources = record.Sources
            .Select(p => NpyHeader.ReadStyleTensor(File.ReadAllBytes(Path.Combine(this.StockDir, p.VoiceId + ".npy")))!)
            .ToList();
        Assert.Equal(VoiceBlend.Mix(sources, [.. record.Sources.Select(p => p.Weight)]), stored);
        Assert.Equal(1.0, record.Sources.Sum(p => (double)p.Weight), 4);
        Assert.Equal(record.Id, store.Find(record.Id)!.Id);
    }

    [Fact]
    public void Ensure_ReusesTheStoredVoice_AndSurvivesAReload()
    {
        var (_, _, generator) = this.Build();
        var first = generator.Ensure("pc:Ada Lovelace@42", "Ada", FemaleCast, "female")!;
        var stamp = File.GetLastWriteTimeUtc(Path.Combine(this.GenDir, first.Id[7..] + ".npy"));

        // A different cast must not move an existing character, and a new session finds the files.
        var (reloaded, library, again) = this.Build();
        var second = again.Ensure("pc:Ada Lovelace@42", "Ada", ["am_adam", "am_michael"], "male")!;

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(this.GenDir, first.Id[7..] + ".npy")));
        Assert.Equal(first.Sources.Select(p => p.VoiceId), reloaded.Find(first.Id)!.Sources.Select(p => p.VoiceId));
        Assert.Equal(first.Pool, reloaded.Find(first.Id)!.Pool);
        Assert.True(library.Has(first.Id));
    }

    [Fact]
    public void LibraryFindsGeneratedVoices_ButDoesNotListThem()
    {
        var (_, library, generator) = this.Build();
        var record = generator.Ensure("npc:feo ul", "Feo Ul", FemaleCast, "female")!;

        var voice = library.Find(record.Id)!;

        Assert.Equal("Generated for Feo Ul", voice.DisplayName);
        Assert.Equal("female", voice.Sex);
        Assert.Equal(RealizationKind.StyleTensor, voice.Realizations.Single().Kind);
        Assert.Equal(Phonemizers.EnglishUs, voice.Realizations.Single().Phonemizer);
        Assert.DoesNotContain(library.Voices, v => v.Id == record.Id);
        Assert.Null(library.Find("af_gen_000000000000"));
    }

    [Fact]
    public void Ensure_OnlyBlendsEnglishVoicesOfTheSameSex()
    {
        var (_, _, generator) = this.Build();
        // French and Chinese stock voices are in the cast but must never be sources.
        string[] cast = ["ff_siwis", "zf_xiaobei", "af_heart", "af_bella", "am_adam"];

        var record = generator.Ensure("npc:x", "X", cast, "female")!;

        Assert.All(record.Sources, p => Assert.StartsWith("af_", p.VoiceId));
        Assert.StartsWith("af_gen_", record.Id);
    }

    [Fact]
    public void Ensure_ReturnsNullWhenNoEnglishBlendExists()
    {
        var (store, _, generator) = this.Build();

        Assert.Null(generator.Ensure("npc:x", "X", ["ff_siwis", "zf_xiaobei"], null));
        Assert.Empty(store.Records);
    }

    [Fact]
    public void Regenerate_MakesANewVoiceAndRemovesTheOldOne()
    {
        var (store, library, generator) = this.Build();
        var first = generator.Ensure("npc:feo ul", "Feo Ul", FemaleCast, "female")!;

        var next = generator.Regenerate(first.Id)!;

        Assert.NotEqual(first.Id, next.Id);
        Assert.Equal(1, next.Salt);
        Assert.Equal(first.SpeakerKey, next.SpeakerKey);
        Assert.Null(store.Find(first.Id));
        Assert.False(File.Exists(Path.Combine(this.GenDir, first.Id[7..] + ".npy")));
        Assert.True(library.Has(next.Id));
        Assert.Equal(next.Id, store.ForSpeaker("npc:feo ul")!.Id);
        Assert.Null(generator.Regenerate("af_gen_ffffffffffff"));
    }

    // ---- store ----

    [Fact]
    public void Save_IsAtomic_NoPartialFilesAndNoRecordWithoutTensor()
    {
        var (store, _, generator) = this.Build();
        generator.Ensure("npc:feo ul", "Feo Ul", FemaleCast, "female");

        Assert.Empty(Directory.GetFiles(this.GenDir, "*.part"));
        Assert.Equal(2, Directory.GetFiles(this.GenDir).Length);

        // A record whose tensor vanished is ignored on the next load.
        File.Delete(Directory.GetFiles(this.GenDir, "*.npy").Single());
        var reloaded = new GeneratedVoiceStore(() => this.GenDir);
        Assert.Empty(reloaded.Records);
        Assert.NotEmpty(store.Records);
    }

    [Fact]
    public void Store_IgnoresCorruptJson_AndRefusesPathTricks()
    {
        Directory.CreateDirectory(this.GenDir);
        File.WriteAllText(Path.Combine(this.GenDir, "0123456789ab.json"), "{ not json");
        var store = new GeneratedVoiceStore(() => this.GenDir, new FakeLogSink());

        Assert.Empty(store.Records);
        Assert.False(store.Remove("../../etc/passwd"));
    }

    [Fact]
    public void Prune_RemovesEverythingNotKept()
    {
        var (store, _, generator) = this.Build();
        var keep = generator.Ensure("npc:one", "One", FemaleCast, "female")!;
        var drop = generator.Ensure("npc:two", "Two", FemaleCast, "female")!;

        var removed = store.Prune(new HashSet<string> { keep.Id });

        Assert.Equal(1, removed);
        Assert.NotNull(store.Find(keep.Id));
        Assert.Null(store.Find(drop.Id));
        Assert.Equal(2, Directory.GetFiles(this.GenDir).Length);
    }

    // ---- Kokoro adapter ----

    [Fact]
    public void KokoroAdapter_LoadsAGeneratedTensorUnderItsOwnNameAndLanguage()
    {
        var (_, library, generator) = this.Build();
        var american = generator.Ensure("npc:one", "One", FemaleCast, "female")!;
        var british = generator.Ensure("npc:two", "Two", ["bf_emma", "bf_alice"], "female")!;
        var log = new FakeLogSink();
        using var synth = new KokoroSynthesizer(() => this.root, () => 4, log, voiceLibrary: library);

        var us = synth.ResolveVoice(american.Id);
        var gb = synth.ResolveVoice(british.Id);

        Assert.Equal(american.Id, us.Name);
        Assert.Equal(KokoroLanguage.AmericanEnglish, us.Language);
        Assert.Equal(british.Id, gb.Name);
        Assert.Equal(KokoroLanguage.BritishEnglish, gb.Language);
        var expected = NpyHeader.ReadStyleTensor(File.ReadAllBytes(Path.Combine(this.GenDir, american.Id[7..] + ".npy")))!;
        Assert.Equal(expected[0], us.Features[0, 0, 0]);
        Assert.Equal(expected[^1], us.Features[NpyHeader.StyleRows - 1, 0, NpyHeader.StyleColumns - 1]);
        Assert.Same(us, synth.ResolveVoice(american.Id));
    }
}
