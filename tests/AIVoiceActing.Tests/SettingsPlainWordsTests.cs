namespace AIVoiceActing.Tests;

using System.Text.RegularExpressions;
using AIVoiceActing.UI.State;
using Xunit;

/// <summary>
/// The jargon list (execution provider, ONNX, model id, bias, exaggeration, pool, RTF, DirectML,
/// regex) may appear only inside Advanced. This scans the string literals of every Settings file
/// outside Advanced, and keeps the size of every Settings source under the 500-line limit.
/// </summary>
public sealed class SettingsPlainWordsTests
{
    private const int MaxLines = 500;

    private static readonly string[] PlainFiles =
    [
        "Pages/SettingsPage.cs",
        "Pages/QualityCard.cs",
        "Pages/ProcessorCard.cs",
        "Pages/ProcessorActions.cs",
        "Pages/DownloadsCard.cs",
        "Pages/KeybindsCard.cs",
        "Pages/SettingsParts.cs",
        "Pages/SettingsInputs.cs",
        "State/DeviceViewModel.cs",
        "State/EngineViewModel.cs",
        "State/DownloadsModel.cs",
        "State/QualityChoiceModel.cs",
        "State/SettingsModel.cs",
    ];

    private static readonly string[] AllSettingsFiles =
    [
        .. PlainFiles,
        "Pages/AdvancedSection.cs",
        "Pages/AdvancedEngines.cs",
        "Pages/CastingPresetsCard.cs",
        "Pages/DiagnosticsSource.cs",
        "State/EnginePrecisionModel.cs",
        "State/RemoteEndpointModel.cs",
        "State/DiagnosticsReport.cs",
        "State/CastingDraftGuard.cs",
    ];

    [Fact]
    public void OutsideAdvanced_NoStringUsesJargon()
    {
        var hits = new List<string>();
        foreach (var file in PlainFiles)
        {
            var lines = File.ReadAllLines(UiPath(file));
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match literal in Regex.Matches(lines[i], "\"(?:[^\"\\\\]|\\\\.)*\""))
                {
                    if (Labels.ContainsJargon(literal.Value))
                    {
                        hits.Add($"{file}:{i + 1}: {literal.Value}");
                    }
                }
            }
        }

        Assert.True(hits.Count == 0, "Jargon outside Advanced:\n" + string.Join("\n", hits));
    }

    [Fact]
    public void EverySettingsSource_StaysUnderTheLineLimit()
    {
        var tooLong = AllSettingsFiles
            .Select(f => (File: f, Lines: File.ReadAllLines(UiPath(f)).Length))
            .Where(x => x.Lines >= MaxLines)
            .Select(x => $"{x.File}: {x.Lines} lines")
            .ToList();

        Assert.True(tooLong.Count == 0, string.Join("\n", tooLong));
    }

    [Fact]
    public void TheOldMonolithsAreGone()
    {
        foreach (var gone in new[] { "Pages/EnginePanel.cs", "Pages/EngineKnobs.cs", "Pages/CastingPresetPanel.cs" })
        {
            Assert.False(File.Exists(UiPath(gone)), gone);
        }
    }

    private static string UiPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AIVoiceActing.sln")) &&
               !File.Exists(Path.Combine(dir.FullName, "AIVoiceActing.slnx")))
        {
            dir = dir.Parent;
        }

        var root = dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found above " + AppContext.BaseDirectory);
        return Path.Combine(root, "src", "AIVoiceActing", "UI", relative);
    }
}
