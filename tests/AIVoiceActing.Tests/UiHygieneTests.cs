namespace AIVoiceActing.Tests;

using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// Source-text scan of every file under src/AIVoiceActing/UI. UI code is sized in em (Ui.Em) and
/// uses icon glyphs from the icon font, so pixel literals and emoji are banned everywhere, with no
/// allowlist. Scale maths goes through Ui.Em / Ui.Scaled / the ThemeTokens helpers.
/// </summary>
public sealed class UiHygieneTests
{
    private static readonly (string Rule, Regex Pattern)[] Rules =
    {
        ("SetNextItemWidth with a numeric literal", new Regex(@"SetNextItemWidth\(\s*-?\d", RegexOptions.Compiled)),
        ("new Vector2 with a numeric literal", new Regex(@"new\s+Vector2\(\s*-?\d", RegexOptions.Compiled)),
        ("TableSetupColumn with a numeric fixed width", new Regex(@"TableSetupColumn\([^;]*,\s*-?\d[\d.]*f?\s*[,)]", RegexOptions.Compiled)),
    };

    [Fact]
    public void UiSources_HaveNoPixelLiteralsOrEmoji()
    {
        var uiDir = Path.Combine(FindRepoRoot(), "src", "AIVoiceActing", "UI");
        Assert.True(Directory.Exists(uiDir), uiDir);

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(uiDir, "*.cs", SearchOption.AllDirectories))
        {
            Scan(file, File.ReadAllLines(file), violations);
        }

        Assert.True(violations.Count == 0, "UI hygiene violations:\n" + string.Join("\n", violations));
    }

    [Theory]
    [InlineData("ImGui.SetNextItemWidth(120f);", 1)]
    [InlineData("ImGui.SetNextItemWidth(Ui.Em(8));", 0)]
    [InlineData("var v = new Vector2(10, 4);", 1)]
    [InlineData("var v = new Vector2(Ui.Em(1), Ui.Em(2));", 0)]
    [InlineData("ImGui.TableSetupColumn(\"Name\", ImGuiTableColumnFlags.WidthFixed, 140f);", 1)]
    [InlineData("ImGui.TableSetupColumn(\"Name\", ImGuiTableColumnFlags.WidthFixed, Ui.Em(10));", 0)]
    [InlineData("ImGui.TableSetupColumn(\"Name\", ImGuiTableColumnFlags.WidthStretch);", 0)]
    [InlineData("var s = \"ready \U0001F600\";", 1)]
    [InlineData("var s = \"ready ✅\";", 1)]
    [InlineData("var s = \"plain text - fine\";", 0)]
    public void Scanner_FlagsExpectedPatterns(string line, int expected)
    {
        var found = new List<string>();
        Scan("x.cs", new[] { line }, found);
        Assert.Equal(expected, found.Count);
    }

    private static void Scan(string file, string[] lines, List<string> violations)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            foreach (var (rule, pattern) in Rules)
            {
                if (pattern.IsMatch(line))
                {
                    violations.Add($"{file}:{i + 1}: {rule}");
                }
            }

            if (HasEmojiInStringLiteral(line))
            {
                violations.Add($"{file}:{i + 1}: emoji in string literal");
            }
        }
    }

    private static bool HasEmojiInStringLiteral(string line)
    {
        foreach (Match m in Regex.Matches(line, "\"(?:[^\"\\\\]|\\\\.)*\""))
        {
            var text = m.Value;
            for (var i = 0; i < text.Length; i++)
            {
                var cp = char.ConvertToUtf32(text, i);
                if (char.IsHighSurrogate(text[i]))
                {
                    i++;
                }

                if (IsEmoji(cp))
                {
                    return true;
                }
            }

            // Escaped forms such as "\U0001F600" or "✅" in source.
            foreach (Match esc in Regex.Matches(text, @"\\U([0-9A-Fa-f]{8})|\\u([0-9A-Fa-f]{4})"))
            {
                var hex = esc.Groups[1].Success ? esc.Groups[1].Value : esc.Groups[2].Value;
                if (IsEmoji(Convert.ToInt32(hex, 16)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsEmoji(int cp) =>
        (cp >= 0x1F000 && cp <= 0x1FAFF) ||    // pictographs, emoticons, transport, symbols extended
        (cp >= 0x2600 && cp <= 0x27BF) ||      // misc symbols and dingbats (check, cross, warning...)
        (cp >= 0x2B00 && cp <= 0x2BFF) ||      // arrows and stars (e.g. star, large circles)
        (cp >= 0x2300 && cp <= 0x23FF) ||      // misc technical (hourglass, play controls)
        cp == 0x200D || cp == 0xFE0F;          // joiner and emoji variation selector

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AIVoiceActing.sln")) &&
               !File.Exists(Path.Combine(dir.FullName, "AIVoiceActing.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root (AIVoiceActing.sln) not found above " + AppContext.BaseDirectory);
    }
}
