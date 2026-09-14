namespace AIVoiceActing.UI.Components;

using AIVoiceActing.Domain;
using Dalamud.Bindings.ImGui;

/// <summary>
/// Filtered voice picker shared by the General Voices and NPC/Player Voices tabs: one
/// instance per editor surface holds the transient filter state (language, sex, search)
/// and draws both the filter row and array+count voice combos over the filtered ids. A
/// stored id missing from the bank is APPENDED as a "(missing)" placeholder so the
/// stored value stays visible and any real pick commits a real voice. When the catalog
/// is empty (off-bank install) the owner passes the raw id list and the filters are
/// skipped entirely.
/// </summary>
public sealed class VoiceSetEditor
{
    private static readonly (VoiceLanguage Language, string Label)[] Languages =
    [
        (VoiceLanguage.AmericanEnglish, "American English"),
        (VoiceLanguage.BritishEnglish, "British English"),
        (VoiceLanguage.Spanish, "Spanish"),
        (VoiceLanguage.French, "French"),
        (VoiceLanguage.Italian, "Italian"),
        (VoiceLanguage.Portuguese, "Portuguese"),
        (VoiceLanguage.Hindi, "Hindi"),
        (VoiceLanguage.Japanese, "Japanese"),
        (VoiceLanguage.Chinese, "Chinese"),
    ];

    private static readonly (VoiceSex Sex, string Label)[] Sexes =
    [
        (VoiceSex.Female, "Female"),
        (VoiceSex.Male, "Male"),
    ];

    private int languageIndex;
    private int sexIndex;
    private string search = string.Empty;

    /// <summary>Draws the filter row; filters only render when the bank is available.</summary>
    public void DrawFilters(IReadOnlyList<VoiceCatalogEntry> catalog)
    {
        if (catalog.Count == 0)
        {
            return;
        }

        var languages = Languages.Where(l => catalog.Any(entry => entry.Language == l.Language)).ToArray();
        ImGui.SetNextItemWidth(150f);
        string[] languageItems = ["All languages", .. languages.Select(l => l.Label)];
        var langIndex = Math.Clamp(this.languageIndex, 0, languageItems.Length - 1);
        if (ImGui.Combo("##voice-language", ref langIndex, languageItems, languageItems.Length))
        {
            this.languageIndex = langIndex;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(100f);
        string[] sexItems = ["All", .. Sexes.Select(s => s.Label)];
        var sex = Math.Clamp(this.sexIndex, 0, sexItems.Length - 1);
        if (ImGui.Combo("##voice-sex", ref sex, sexItems, sexItems.Length))
        {
            this.sexIndex = sex;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(140f);
        if (ImGui.InputTextWithHint("##voice-search", "Search", ref this.search, 64))
        {
        }

        ImGui.SameLine();
        Controls.HelpMarker(
            "Filter the voice bank by accent, voice sex, or id substring. Filters are view-only: they never change the stored selection.");
    }

    /// <summary>The ids to offer: the catalog filtered by the current filters (or the raw
    /// fallback list when no bank is installed), as an array for the combo binding.</summary>
    public string[] OfferedIds(IReadOnlyList<VoiceCatalogEntry> catalog, IReadOnlyList<string> fallbackIds)
    {
        if (catalog.Count == 0)
        {
            return fallbackIds as string[] ?? [.. fallbackIds];
        }

        VoiceLanguage? language = this.languageIndex > 0 && this.languageIndex <= Languages.Length
            ? Languages[this.languageIndex - 1].Language
            : null;
        VoiceSex? sex = this.sexIndex > 0 && this.sexIndex <= Sexes.Length
            ? Sexes[this.sexIndex - 1].Sex
            : null;
        return [.. VoiceCatalog
            .Filter(catalog, language, sex, this.search)
            .Select(entry => entry.Id)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>Array+count combo over <paramref name="ids"/>; see class doc for the
    /// "(missing)" placeholder semantics.</summary>
    public bool DrawVoiceCombo(string label, string[] ids, string voiceId, out string picked)
    {
        var display = ids;
        var index = Array.IndexOf(display, voiceId);
        if (index < 0)
        {
            display = [.. display, $"{voiceId} (missing)"];
            index = display.Length - 1;
        }

        var changed = ImGui.Combo(label, ref index, display, display.Length);
        picked = display[index];
        return changed;
    }
}
