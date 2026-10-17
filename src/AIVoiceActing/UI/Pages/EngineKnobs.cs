namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using Dalamud.Bindings.ImGui;

/// <summary>Engine knobs that apply to whichever engine is active; drawn once inside the active engine card.</summary>
internal static class EngineKnobs
{
    private static readonly string[] ExecutionProviders = ["auto", "cpu", "directml", "coreml"];

    public static void Draw(UiContext ctx, EngineInfo engine)
    {
        var c = ctx.Config;
        var save = ctx.Save;

        ImGui.Separator();
        // Per-engine device options: an engine only lists providers its adapter can
        // actually wire. The CPU-only default has none; GPU-capable engines can try
        // DirectML and fall back to CPU when the EP fails to initialize (e.g. under Wine).
        if (engine.SupportsGpu || engine.Retired)
        {
            var providers = engine.SupportsGpu
                ? ExecutionProviders
                : ["cpu", "directml"];
            Controls.Combo(
                "Execution provider",
                providers,
                () => c.Device.ToExecutionProvider(),
                v =>
                {
                    c.Device.ApplyExecutionProvider(v);
                    save();
                    ctx.InvalidateSynthesizer();
                },
                "Where synthesis runs: cpu always works; directml uses your GPU (Windows only, falls back to CPU when unavailable, e.g. under Wine).");
        }

        Controls.Combo(
            "CPU impact",
            ["low", "medium", "high"],
            () => c.Device.CpuImpact,
            v => { c.Device.CpuImpact = v; save(); },
            "How many CPU threads synthesis may use: low = 2 (gentlest on frame rate), medium = 4, high = 8. More threads mean faster lines but a bigger FPS hit.");
        ImGui.SameLine();
        Controls.HelpMarker(
            "ONNX synthesis threads: low = 2, medium = 4, high = 8. Lower protects framerate, higher shortens waits.");
        Controls.DragFloat(
            "Drop lines older than (seconds)",
            0f, 120f,
            () => c.StaleLineSeconds,
            v => c.StaleLineSeconds = (int)MathF.Round(v),
            save);
        ImGui.SameLine();
        Controls.HelpMarker(
            "If a line finishes synthesizing after the conversation moved on, skip it instead of playing it late. 0 keeps everything.");
        Controls.Checkbox(
            "Keep speaking pace on pitched voices",
            "Pitch shifting used to slow deep voices and speed up high ones. On keeps every voice at its own pace; off restores the old behaviour.",
            () => c.KeepTempoWhenPitched,
            v => { c.KeepTempoWhenPitched = v; save(); });
        Controls.Checkbox(
            "Even out voice loudness",
            "Levels every line to the same loudness, then applies the per-voice volume. Volumes above 100% are softly limited instead of clipping.",
            () => c.LevelVoiceLoudness,
            v => { c.LevelVoiceLoudness = v; save(); });
        Controls.Checkbox(
            "Start long lines sooner",
            "Long lines are rendered sentence by sentence, so the first sentence plays while the rest is still being made.",
            () => c.StreamSentences,
            v => { c.StreamSentences = v; save(); });
        Controls.Checkbox(
            "Show the now-speaking panel",
            "A small panel with who is talking, their voice and mood, and Replay, Skip and Stop buttons.",
            () => c.ShowNowSpeaking,
            v => { c.ShowNowSpeaking = v; save(); });
    }
}
