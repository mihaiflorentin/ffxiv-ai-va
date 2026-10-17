namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// A row's voices at a glance: up to four fingerprints (the active one pulses while it previews),
/// then the names in words and a "missing" tag when a voice is no longer installed. Fingerprints
/// alone are a signature, never the only way to tell voices apart.
/// </summary>
internal static class VoiceStrip
{
    public const int MaxFingerprints = 4;

    public static void Draw(UiContext ctx, IReadOnlyList<string> voiceIds)
    {
        if (voiceIds.Count == 0)
        {
            Ui.MutedTextInline("No voices set");
            return;
        }

        var missing = 0;
        foreach (var id in voiceIds.Take(MaxFingerprints))
        {
            var isMissing = VoiceNames.IsMissing(ctx.VoiceLibrary, id);
            missing += isMissing ? 1 : 0;
            VoiceFingerprint.Draw(id, PreviewButton.IsActive(ctx.Preview, id), ctx.Config.ReduceMotion, dim: isMissing);
            ImGui.SameLine();
        }

        missing += voiceIds.Skip(MaxFingerprints).Count(id => VoiceNames.IsMissing(ctx.VoiceLibrary, id));
        Ui.MutedTextInline(VoiceNames.Summary(ctx.VoiceLibrary, voiceIds, MaxFingerprints));
        if (missing > 0)
        {
            ImGui.SameLine();
            Chip.Tag(missing == 1 ? "1 voice missing" : $"{missing} voices missing", Theme.Error);
        }
    }
}
