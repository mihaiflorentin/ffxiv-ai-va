namespace AIVoiceActing.UI.Components;

using System.Numerics;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// The plugin's one signature element: twelve bars whose heights come from a stable hash of
/// the voice id, so each voice has a recognisable shape. Pulses while the voice is playing
/// unless reduce-motion is on. Used in the voice list, the voice picker and Now Speaking only.
/// </summary>
public static class VoiceFingerprint
{
    public const float WidthEm = 3.2f;
    public const float HeightEm = 1.2f;

    /// <summary>Draws the fingerprint at the cursor and advances layout by its size.</summary>
    public static void Draw(string voiceId, bool playing = false, bool reduceMotion = false, bool dim = false)
    {
        var size = Ui.Em(WidthEm, HeightEm);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);

        var heights = VoiceFingerprintMath.Heights(voiceId);
        var draw = ImGui.GetWindowDrawList();
        var colour = dim ? Theme.Muted : Theme.Accent;
        var u32 = Theme.ToU32(colour);
        var slot = size.X / VoiceFingerprintMath.BarCount;
        var barWidth = slot * 0.62f;
        var time = ImGui.GetTime();

        for (var i = 0; i < heights.Length; i++)
        {
            var scale = VoiceFingerprintMath.PulseScale(i, time, playing, reduceMotion);
            var h = size.Y * heights[i] * scale;
            var x = origin.X + (slot * i) + ((slot - barWidth) * 0.5f);
            var bottom = origin.Y + size.Y;
            draw.AddRectFilled(new Vector2(x, bottom - h), new Vector2(x + barWidth, bottom), u32, barWidth * 0.5f);
        }
    }
}
