namespace AIVoiceActing.Tests;

using System.Text;
using System.Text.Json;
using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Storage;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

public sealed class VoicePackValidatorTests
{
    private static string WavFile(double seconds)
    {
        const int rate = 24000;
        var data = new byte[(int)(seconds * rate) * 2];
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + data.Length);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(rate);
            writer.Write(rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(data.Length);
            writer.Write(data);
        }

        return Convert.ToBase64String(stream.ToArray());
    }

    private static string WriteFullPack(string root, double clipSeconds = 5, bool withNotes = true)
    {
        var dir = PackFixture.WritePack(root, "pack-x", "x.one");
        PackFixture.WriteTensor(dir, "style/x.one.npy");
        Directory.CreateDirectory(Path.Combine(dir, "clips"));
        Directory.CreateDirectory(Path.Combine(dir, "preview"));
        File.WriteAllBytes(Path.Combine(dir, "clips", "x.one.wav"), Convert.FromBase64String(WavFile(clipSeconds)));
        File.WriteAllBytes(Path.Combine(dir, "preview", "x.one.wav"), Convert.FromBase64String(WavFile(1)));
        File.WriteAllText(Path.Combine(dir, "clips", "x.one.txt"), "Hello there.");
        File.WriteAllText(
            Path.Combine(dir, "voicepack.json"),
            """
            { "schema": 1, "id": "pack-x", "version": "1.0.0", "name": "X",
              "license": { "spdx": "CC-BY-SA-4.0", "attribution": "t" },
              "voices": [ { "id": "x.one", "name": "One", "accent": "en-IE", "sex": "male",
                "previewFile": "preview/x.one.wav",
                "realizations": [
                  { "kind": "StyleTensor", "file": "style/x.one.npy", "phonemizer": "en-us" },
                  { "kind": "ReferenceClip", "file": "clips/x.one.wav", "transcript": "clips/x.one.txt" } ] } ] }
            """);
        if (withNotes)
        {
            File.WriteAllText(Path.Combine(dir, "LICENSE"), "l");
            File.WriteAllText(Path.Combine(dir, "NOTICE"), "n");
        }

        return dir;
    }

    [Fact]
    public void CompletePack_IsValid_WithoutWarnings()
    {
        var report = VoicePackValidator.Validate(WriteFullPack(PackFixture.NewDir()));

        Assert.True(report.IsValid, string.Join("; ", report.Errors));
        Assert.Empty(report.Warnings);
        Assert.Equal("pack-x", report.Manifest!.Id);
    }

    [Fact]
    public void MissingLicenseAndNoticeFiles_AreWarningsOnly()
    {
        var report = VoicePackValidator.Validate(WriteFullPack(PackFixture.NewDir(), withNotes: false));

        Assert.True(report.IsValid);
        Assert.Contains(report.Warnings, w => w.Contains("LICENSE"));
        Assert.Contains(report.Warnings, w => w.Contains("NOTICE"));
    }

    [Fact]
    public void MissingManifest_IsInvalid()
    {
        var report = VoicePackValidator.Validate(PackFixture.NewDir());

        Assert.False(report.IsValid);
        Assert.Null(report.Manifest);
        Assert.Contains(report.Errors, e => e.Contains("voicepack.json"));
    }

    [Fact]
    public void MalformedManifest_ReportsTheParserMessage()
    {
        var dir = PackFixture.NewDir();
        File.WriteAllText(Path.Combine(dir, "voicepack.json"), "{ \"schema\": 7 }");

        var report = VoicePackValidator.Validate(dir);

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e => e.Contains("schema 7"));
    }

    [Fact]
    public void MissingReferencedFile_IsAnError_ReportedOnce()
    {
        var dir = WriteFullPack(PackFixture.NewDir());
        File.Delete(Path.Combine(dir, "preview", "x.one.wav"));

        var report = VoicePackValidator.Validate(dir);

        Assert.False(report.IsValid);
        Assert.Single(report.Errors, e => e.Contains("preview/x.one.wav"));
    }

    [Fact]
    public void BrokenTensor_IsAnError()
    {
        var dir = WriteFullPack(PackFixture.NewDir());
        PackFixture.WriteTensor(dir, "style/x.one.npy", "<f4", 3, 3);

        var report = VoicePackValidator.Validate(dir);

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e => e.Contains("x.one"));
    }

    [Fact]
    public void ReferenceClipLongerThanTenSeconds_IsAnError()
    {
        var report = VoicePackValidator.Validate(WriteFullPack(PackFixture.NewDir(), clipSeconds: 12));

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e => e.Contains("clips/x.one.wav") && e.Contains("12.0"));
    }

    [Fact]
    public void WavSeconds_ReadsTheHeader()
    {
        var path = Path.Combine(PackFixture.NewDir(), "a.wav");
        File.WriteAllBytes(path, Convert.FromBase64String(WavFile(2.5)));

        Assert.Equal(2.5, VoicePackValidator.WavSeconds(path)!.Value, 3);
        File.WriteAllText(path, "not a wav");
        Assert.Null(VoicePackValidator.WavSeconds(path));
    }
}

/// <summary>
/// The pack that ships in the plugin zip (<c>src/AIVoiceActing/voicepacks/aiva-isles</c>): it must
/// validate, stay experimental, carry its license files, and every voice that
/// <c>voices.json</c> casts from it must exist in the manifest.
/// </summary>
public sealed class BundledIslesPackTests
{
    private static string PackDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "AIVoiceActing", "voicepacks", "aiva-isles");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("src/AIVoiceActing/voicepacks/aiva-isles not found above the test output.");
    }

    [Fact]
    public void ShippedPack_Validates_AndIsExperimentalCcBySa()
    {
        var report = VoicePackValidator.Validate(PackDir());

        Assert.True(report.IsValid, string.Join("; ", report.Errors));
        Assert.Empty(report.Warnings);
        var manifest = report.Manifest!;
        Assert.Equal("aiva-isles", manifest.Id);
        Assert.Equal("CC-BY-SA-4.0", manifest.License.Spdx);
        Assert.False(string.IsNullOrWhiteSpace(manifest.License.Attribution));
        Assert.All(manifest.Voices, v => Assert.Equal(VoiceQuality.Experimental, v.Quality));
        Assert.All(manifest.Voices, v => Assert.StartsWith("isles.", v.Id));
        Assert.All(manifest.Voices, v => Assert.Contains(v.Realizations, r => r.Kind == RealizationKind.StyleTensor));
    }

    [Fact]
    public void CastingSets_ReferenceOnlyVoicesThePackDeclares()
    {
        var declared = VoicePackValidator.Validate(PackDir()).Manifest!.Voices.Select(v => v.Id).ToHashSet();
        var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "voices.json")));
        var packIds = json.RootElement.GetProperty("sets").EnumerateObject()
            .SelectMany(set => set.Value.EnumerateArray())
            .Select(slot => slot.GetProperty("id").GetString()!)
            .Where(id => id.StartsWith("isles.", StringComparison.Ordinal))
            .Distinct()
            .ToList();

        Assert.NotEmpty(packIds);
        Assert.All(packIds, id => Assert.Contains(id, declared));
    }

    [Fact]
    public void ScottishSet_IsUnparked_AndDisabledBlockStays()
    {
        var map = RaceVoiceMap.FromJson(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "voices.json")));

        Assert.Contains("scottishMale", map.Sets.Keys);
        Assert.DoesNotContain("scottishMale", map.Disabled!.Sets.Keys);
        Assert.Contains("icelandicMale", map.Disabled.Sets.Keys);
    }
}
