namespace AIVoiceActing.Tests;

using System.Text.RegularExpressions;
using AIVoiceActing.Infrastructure.Onnx;
using Xunit;

public sealed class VerifiedGpuCombosTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "aiva-combos", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(this.dir))
        {
            Directory.Delete(this.dir, recursive: true);
        }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AIVoiceActing.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    [Fact]
    public void EveryPinnedFile_MatchesTheModelCatalogHashAndSize()
    {
        // Bumping a model in models-catalog.json must not silently widen the allowlist: this fails
        // until someone re-verifies the new file on the GPU and updates the literals deliberately.
        foreach (var combo in VerifiedGpuCombos.Combos)
        {
            foreach (var file in combo.Files)
            {
                var entry = Assert.Single(ModelCatalog.Assets, a => a.Asset.FileName == file.FileName);
                Assert.Equal(file.Sha256, entry.Sha256);
                Assert.Equal(file.SizeBytes, entry.Asset.SizeBytes);
            }
        }
    }

    [Fact]
    public void TheAllowlist_IsTheTurboFp16AndQ4F16LanguageModels_Only()
    {
        Assert.Equal(
            ["turbo-language-model-fp16.onnx", "turbo-language-model-q4f16.onnx"],
            VerifiedGpuCombos.Combos.Select(c => c.GraphFile).Order());
        Assert.All(VerifiedGpuCombos.Combos, c => Assert.Equal(VerifiedGpuCombos.WebGpuEpVersion, c.EpVersion));
    }

    [Fact]
    public void ThePinnedRuntimeVersion_MatchesTheProjectReferences()
    {
        var root = RepoRoot();
        foreach (var project in new[]
        {
            "src/AIVoiceActing/AIVoiceActing.csproj",
            "tests/AIVoiceActing.Tests/AIVoiceActing.Tests.csproj",
            "tools/SmokeSynth/SmokeSynth.csproj",
        })
        {
            var text = File.ReadAllText(Path.Combine(root, project));
            var match = Regex.Match(text, "Microsoft\\.ML\\.OnnxRuntime\\.EP\\.WebGpu\" Version=\"\\[([^\\]]+)\\]\"");
            Assert.True(match.Success, $"{project} must pin the WebGPU runtime with an exact [version].");
            Assert.Equal(VerifiedGpuCombos.WebGpuEpVersion, match.Groups[1].Value);
        }
    }

    private void Stage(string fileName, long size)
    {
        Directory.CreateDirectory(this.dir);
        using var file = File.Create(Path.Combine(this.dir, fileName));
        file.SetLength(size);
    }

    private void StageCombo(VerifiedCombo combo)
    {
        foreach (var file in combo.Files)
        {
            this.Stage(file.FileName, file.SizeBytes);
        }
    }

    private static string HashByName(string path)
    {
        var name = Path.GetFileName(path);
        return VerifiedGpuCombos.Combos.SelectMany(c => c.Files).Single(f => f.FileName == name).Sha256;
    }

    [Fact]
    public void AVerifiedCombo_PassesTheFullCheck()
    {
        var combo = VerifiedGpuCombos.Combos[1];
        this.StageCombo(combo);
        var combos = new VerifiedGpuCombos(HashByName);

        Assert.True(combos.IsVerified(VerifiedGpuCombos.WebGpuEpVersion, this.dir, combo.GraphFile));
    }

    [Fact]
    public void AWrongHash_FailsTheFullCheck_ButPassesTheCheapOne()
    {
        var combo = VerifiedGpuCombos.Combos[1];
        this.StageCombo(combo);
        var combos = new VerifiedGpuCombos(_ => new string('0', 64));

        Assert.False(combos.IsVerified(VerifiedGpuCombos.WebGpuEpVersion, this.dir, combo.GraphFile));
        Assert.True(combos.IsVerified(VerifiedGpuCombos.WebGpuEpVersion, this.dir, combo.GraphFile, cheap: true));
    }

    [Fact]
    public void AWrongSize_FailsBothChecks()
    {
        var combo = VerifiedGpuCombos.Combos[1];
        this.StageCombo(combo);
        this.Stage(combo.Files[1].FileName, combo.Files[1].SizeBytes + 1);
        var combos = new VerifiedGpuCombos(HashByName);

        Assert.False(combos.IsVerified(VerifiedGpuCombos.WebGpuEpVersion, this.dir, combo.GraphFile, cheap: true));
        Assert.False(combos.IsVerified(VerifiedGpuCombos.WebGpuEpVersion, this.dir, combo.GraphFile));
    }

    [Fact]
    public void AMissingWeightsFile_Fails()
    {
        var combo = VerifiedGpuCombos.Combos[0];
        this.Stage(combo.Files[0].FileName, combo.Files[0].SizeBytes);

        Assert.False(new VerifiedGpuCombos(HashByName).IsVerified(VerifiedGpuCombos.WebGpuEpVersion, this.dir, combo.GraphFile, cheap: true));
    }

    [Fact]
    public void TheFp32TurboModel_AndOtherRuntimeVersions_AreNotVerified()
    {
        this.Stage("turbo-language-model.onnx", 207266);
        var combo = VerifiedGpuCombos.Combos[1];
        this.StageCombo(combo);
        var combos = new VerifiedGpuCombos(HashByName);

        Assert.False(combos.IsVerified(VerifiedGpuCombos.WebGpuEpVersion, this.dir, "turbo-language-model.onnx", cheap: true));
        Assert.False(combos.IsVerified("0.4.0", this.dir, combo.GraphFile));
        Assert.False(combos.IsVerified("0.3.0", this.dir, combo.GraphFile, cheap: true));
    }

    [Fact]
    public void Hashes_AreComputedOncePerFileStamp()
    {
        var combo = VerifiedGpuCombos.Combos[1];
        this.StageCombo(combo);
        var calls = 0;
        var combos = new VerifiedGpuCombos(path =>
        {
            calls++;
            return HashByName(path);
        });

        combos.IsVerified(VerifiedGpuCombos.WebGpuEpVersion, this.dir, combo.GraphFile);
        combos.IsVerified(VerifiedGpuCombos.WebGpuEpVersion, this.dir, combo.GraphFile);

        Assert.Equal(combo.Files.Count, calls);
    }
}
