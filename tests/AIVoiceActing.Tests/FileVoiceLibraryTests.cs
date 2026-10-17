namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Storage;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

public sealed class FileVoiceLibraryTests
{
    private static FileVoiceLibrary Library(
        string[] roots, string[]? disabled = null, FakeLogSink? log = null) =>
        new(() => roots, () => disabled ?? [], log);

    [Fact]
    public void StockVoices_AreAlwaysThere_WithUnchangedIds()
    {
        IVoiceLibrary library = Library([]);

        var heart = library.Find("af_heart");
        Assert.NotNull(heart);
        Assert.Equal(VoiceQuality.Stock, heart.Quality);
        Assert.Equal("en-US", heart.Accent);
        Assert.Equal("female", heart.Sex);
        Assert.True(library.Has("bm_lewis"));
        Assert.False(library.Has("am_irishtest"));
        Assert.Null(library.Find(null));
        Assert.Equal("aiva-kokoro-stock", Assert.Single(library.Packs).Id);
        Assert.Equal("en-gb", library.Find("bf_emma")!.Realizations[0].Phonemizer);
    }

    [Fact]
    public void TwoPacks_AreBothLoaded_WithLicenseAndRealizationPaths()
    {
        var root = PackFixture.NewDir();
        PackFixture.WritePack(root, "pack-a", "a.one", "a.two");
        PackFixture.WritePack(root, "pack-b", "b.one");

        var library = Library([root]);

        Assert.Equal(["aiva-kokoro-stock", "pack-a", "pack-b"], library.Packs.Select(p => p.Id));
        var a = library.Packs.Single(p => p.Id == "pack-a");
        Assert.Equal(2, a.VoiceCount);
        Assert.Equal("CC-BY-SA-4.0", a.License);
        Assert.True(a.Enabled);
        Assert.Empty(a.Problems);
        Assert.True(a.Bytes > 0);

        var voice = library.Find("a.one")!;
        Assert.True(voice.Available);
        Assert.Equal("pack-a", voice.PackId);
        Assert.Equal("CC-BY-SA-4.0", voice.License);
        Assert.Equal("Test data", voice.Attribution);
        var tensor = Assert.Single(voice.Realizations);
        Assert.Equal(Path.Combine(root, "pack-a", "style", "a.one.npy"), tensor.Path);
        Assert.Equal("en-gb", tensor.Phonemizer);
        Assert.True(library.Has("b.one"));
    }

    [Fact]
    public void IdCollision_FirstPackWins_AndLoserGetsAProblem()
    {
        var root = PackFixture.NewDir();
        PackFixture.WritePack(root, "aaa-first", "shared.voice");
        PackFixture.WritePack(root, "zzz-second", "shared.voice", "only.second");

        var library = Library([root]);

        Assert.Equal("aaa-first", library.Find("shared.voice")!.PackId);
        Assert.Equal("zzz-second", library.Find("only.second")!.PackId);
        var loser = library.Packs.Single(p => p.Id == "zzz-second");
        Assert.Equal(1, loser.VoiceCount);
        Assert.Contains(loser.Problems, p => p.Contains("shared.voice") && p.Contains("aaa-first"));
        Assert.Empty(library.Packs.Single(p => p.Id == "aaa-first").Problems);
    }

    [Fact]
    public void Collision_AcrossRoots_FavoursTheEarlierRoot()
    {
        var bundled = PackFixture.NewDir();
        var user = PackFixture.NewDir();
        PackFixture.WritePack(bundled, "pack-a", "dup.voice");
        PackFixture.WritePack(user, "other", "dup.voice");

        var library = Library([bundled, user]);

        Assert.Equal("pack-a", library.Find("dup.voice")!.PackId);
    }

    [Fact]
    public void PackCannotShadowAStockVoice()
    {
        var root = PackFixture.NewDir();
        PackFixture.WritePack(root, "pack-a", "af_heart");

        var library = Library([root]);

        Assert.Equal("aiva-kokoro-stock", library.Find("af_heart")!.PackId);
        Assert.Contains(library.Packs.Single(p => p.Id == "pack-a").Problems, p => p.Contains("af_heart"));
    }

    [Fact]
    public void SamePackIdTwice_SecondIsIgnoredWithAProblem()
    {
        var bundled = PackFixture.NewDir();
        var user = PackFixture.NewDir();
        PackFixture.WritePack(bundled, "dir-one", "x.one");
        // Same manifest id in a different folder.
        var dir = PackFixture.WritePack(user, "dir-two", "y.two");
        File.WriteAllText(
            Path.Combine(dir, "voicepack.json"),
            PackFixture.Manifest("dir-one", [("y.two", "style/y.two.npy")]));

        var library = Library([bundled, user]);

        Assert.Equal(2, library.Packs.Count);
        Assert.False(library.Has("y.two"));
        Assert.Contains(library.Packs.Single(p => p.Id == "dir-one").Problems, p => p.Contains("same id"));
    }

    [Fact]
    public void DisabledPack_IsListedButItsVoicesAreNotCastable()
    {
        var root = PackFixture.NewDir();
        PackFixture.WritePack(root, "pack-a", "a.one");
        PackFixture.WritePack(root, "pack-b", "b.one");

        var library = Library([root], disabled: ["pack-a"]);

        var a = library.Packs.Single(p => p.Id == "pack-a");
        Assert.False(a.Enabled);
        Assert.Equal(1, a.VoiceCount);
        Assert.False(library.Has("a.one"));
        Assert.Null(library.Find("a.one"));
        Assert.True(library.Has("b.one"));
        Assert.DoesNotContain(library.Voices, v => v.PackId == "pack-a");
    }

    [Fact]
    public void DisabledPack_DoesNotBlockALaterPackFromUsingItsIds()
    {
        var root = PackFixture.NewDir();
        PackFixture.WritePack(root, "aaa", "dup.voice");
        PackFixture.WritePack(root, "bbb", "dup.voice");

        var library = Library([root], disabled: ["aaa"]);

        Assert.Equal("bbb", library.Find("dup.voice")!.PackId);
    }

    [Fact]
    public void MissingFile_DemotesTheVoice_NotThePack()
    {
        var root = PackFixture.NewDir();
        var dir = PackFixture.WritePack(root, "pack-a", "a.one", "a.two");
        File.Delete(Path.Combine(dir, "style", "a.one.npy"));

        var library = Library([root]);

        var demoted = library.Find("a.one")!;
        Assert.False(demoted.Available);
        Assert.Contains("missing", demoted.UnavailableReason);
        Assert.False(library.Has("a.one"));
        Assert.True(library.Has("a.two"));
        var pack = library.Packs.Single(p => p.Id == "pack-a");
        Assert.Contains(pack.Problems, p => p.Contains("a.one"));
        Assert.Equal(2, pack.VoiceCount);
    }

    [Theory]
    [InlineData("<f8", 510, 1, 256)]
    [InlineData("<f4", 510, 256, 0)]
    [InlineData("<f4", 100, 1, 256)]
    public void WrongTensorDtypeOrShape_DemotesTheVoice(string descr, int d0, int d1, int d2)
    {
        var root = PackFixture.NewDir();
        var dir = PackFixture.WritePack(root, "pack-a", "a.one");
        PackFixture.WriteTensor(dir, "style/a.one.npy", descr, d2 == 0 ? [d0, d1] : [d0, d1, d2]);

        var library = Library([root]);

        Assert.False(library.Has("a.one"));
        Assert.Contains("expected", library.Find("a.one")!.UnavailableReason);
    }

    [Fact]
    public void TruncatedOrGarbageTensor_DemotesTheVoice()
    {
        var root = PackFixture.NewDir();
        var dir = PackFixture.WritePack(root, "pack-a", "a.one", "a.two");
        var bytes = PackFixture.Npy();
        File.WriteAllBytes(Path.Combine(dir, "style", "a.one.npy"), bytes[..(bytes.Length / 2)]);
        File.WriteAllText(Path.Combine(dir, "style", "a.two.npy"), "not numpy");

        var library = Library([root]);

        Assert.False(library.Has("a.one"));
        Assert.False(library.Has("a.two"));
    }

    [Fact]
    public void ClipVoice_StaysAvailableWhenOnlyTheTranscriptIsMissing()
    {
        var root = PackFixture.NewDir();
        var dir = Path.Combine(root, "clips-pack");
        PackFixture.WriteFile(dir, "clips/a.wav");
        File.WriteAllText(Path.Combine(dir, "voicepack.json"), """
            { "schema": 1, "id": "clips-pack", "name": "C", "version": "1", "license": { "spdx": "CC0-1.0" },
              "voices": [ { "id": "c.one", "name": "C", "accent": "en-IE", "sex": "female",
                "realizations": [ { "kind": "ReferenceClip", "file": "clips/a.wav", "transcript": "clips/a.txt" } ] } ] }
            """);

        var library = Library([root]);

        var voice = library.Find("c.one")!;
        Assert.True(voice.Available);
        Assert.Null(voice.Realizations[0].Transcript);
        Assert.Contains(library.Packs.Single(p => p.Id == "clips-pack").Problems, p => p.Contains("transcript"));
    }

    [Fact]
    public void InvalidManifest_ShowsUpAsAProblemPack_WithoutBreakingOthers()
    {
        var root = PackFixture.NewDir();
        PackFixture.WritePack(root, "good", "g.one");
        var bad = Path.Combine(root, "bad");
        Directory.CreateDirectory(bad);
        File.WriteAllText(
            Path.Combine(bad, "voicepack.json"),
            PackFixture.Manifest("bad", [("b.one", "style/b.npy")], license: string.Empty));

        var library = Library([root]);

        Assert.True(library.Has("g.one"));
        Assert.False(library.Has("b.one"));
        var rejected = library.Packs.Single(p => p.Id == "bad");
        Assert.Equal(0, rejected.VoiceCount);
        Assert.Contains(rejected.Problems, p => p.Contains("license"));
    }

    [Fact]
    public void FolderWithoutManifest_IsIgnored()
    {
        var root = PackFixture.NewDir();
        Directory.CreateDirectory(Path.Combine(root, "random-folder"));

        Assert.Single(Library([root]).Packs);
    }

    [Fact]
    public void MissingRoot_IsIgnored() =>
        Assert.Single(Library([Path.Combine(PackFixture.NewDir(), "nope"), string.Empty]).Packs);

    [Fact]
    public void Reload_PicksUpADroppedPack_AndForgetsARemovedOne()
    {
        var root = PackFixture.NewDir();
        var library = Library([root]);
        Assert.False(library.Has("late.voice"));

        var dir = PackFixture.WritePack(root, "late", "late.voice");
        Assert.False(library.Has("late.voice"));
        library.Reload();
        Assert.True(library.Has("late.voice"));
        Assert.Contains(library.Packs, p => p.Id == "late");

        Directory.Delete(dir, recursive: true);
        library.Reload();
        Assert.False(library.Has("late.voice"));
        Assert.DoesNotContain(library.Packs, p => p.Id == "late");
    }

    [Fact]
    public void Reload_NoticesAFileThatDisappeared()
    {
        var root = PackFixture.NewDir();
        var dir = PackFixture.WritePack(root, "pack-a", "a.one");
        var library = Library([root]);
        Assert.True(library.Has("a.one"));

        File.Delete(Path.Combine(dir, "style", "a.one.npy"));
        library.Reload();

        Assert.False(library.Has("a.one"));
        Assert.NotNull(library.Find("a.one"));
    }

    [Fact]
    public void DisabledSet_IsReadOnEveryReload()
    {
        var root = PackFixture.NewDir();
        PackFixture.WritePack(root, "pack-a", "a.one");
        var disabled = new List<string>();
        var library = new FileVoiceLibrary(() => [root], () => disabled);
        Assert.True(library.Has("a.one"));

        disabled.Add("pack-a");
        library.Reload();

        Assert.False(library.Has("a.one"));
    }

    [Fact]
    public void SymlinkEscapingThePack_DemotesTheVoice()
    {
        var root = PackFixture.NewDir();
        var outside = PackFixture.NewDir();
        File.WriteAllBytes(Path.Combine(outside, "secret.npy"), PackFixture.Npy());
        var dir = PackFixture.WritePack(root, "pack-a", "a.one");
        var link = Path.Combine(dir, "style", "a.one.npy");
        File.Delete(link);
        try
        {
            File.CreateSymbolicLink(link, Path.Combine(outside, "secret.npy"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // Host cannot create symlinks; the path-traversal tests still cover the manifest side.
        }

        var library = Library([root]);

        Assert.False(library.Has("a.one"));
        Assert.Contains("link", library.Find("a.one")!.UnavailableReason);
    }

    [Fact]
    public void ProblemsAreLogged()
    {
        var root = PackFixture.NewDir();
        var dir = PackFixture.WritePack(root, "pack-a", "a.one");
        File.Delete(Path.Combine(dir, "style", "a.one.npy"));
        var log = new FakeLogSink();

        _ = Library([root], log: log);

        Assert.Contains(log.Snapshot(), e => e.Level == "Warn" && e.Message.Contains("a.one"));
    }
}
