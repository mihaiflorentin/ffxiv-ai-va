namespace AIVoiceActing.UI;

using System.Numerics;
using AIVoiceActing.Ports;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

/// <summary>
/// Small always-available panel: who is speaking, with which voice and mood, plus Replay
/// (the last line again), Skip (the rest of this line) and Stop (everything queued).
/// Its open state is the ShowNowSpeaking setting, so closing it with the X sticks.
/// </summary>
public sealed class NowSpeakingWindow : Window
{
    private readonly Configuration config;
    private readonly Action save;
    private readonly ISpeechQueue queue;
    private SpeechItem? last;

    public NowSpeakingWindow(Configuration config, Action save, ISpeechQueue queue)
        : base("Now speaking###AIVANowSpeaking", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
        this.config = config;
        this.save = save;
        this.queue = queue;
        this.IsOpen = config.ShowNowSpeaking;
        this.SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(260, 0) };
    }

    /// <summary>Runs every frame, open or not: the setting drives the window.</summary>
    public override void PreOpenCheck() => this.IsOpen = this.config.ShowNowSpeaking;

    public override void OnClose()
    {
        if (this.config.ShowNowSpeaking)
        {
            this.config.ShowNowSpeaking = false;
            this.save();
        }
    }

    public override void Draw()
    {
        var playing = this.queue.NowPlaying;
        if (playing is not null)
        {
            this.last = playing;
        }

        var shown = playing ?? this.last;
        if (shown is null)
        {
            ImGui.TextDisabled("Nothing spoken yet.");
        }
        else
        {
            ImGui.TextUnformatted(shown.Speaker.DisplayName);
            ImGui.SameLine();
            ImGui.TextDisabled(playing is null ? "(last line)" : "(speaking)");
            ImGui.TextDisabled(
                $"{shown.Request.ReferenceVoiceId} · {shown.Emotion ?? "neutral"}" +
                (this.queue.Depth > 0 ? $" · {this.queue.Depth} waiting" : string.Empty));
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 22);
            ImGui.TextUnformatted(shown.Request.Text);
            ImGui.PopTextWrapPos();
        }

        ImGui.Spacing();
        ImGui.BeginDisabled(shown is null);
        if (ImGui.Button("Replay") && shown is not null)
        {
            // A replay is user-initiated: never stale, and not part of the skipped line.
            this.queue.Enqueue(shown with { RequestedAtTicks = 0, LineId = 0 });
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(playing is null);
        if (ImGui.Button("Skip"))
        {
            this.queue.CancelCurrent();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(playing is null && this.queue.Depth == 0);
        if (ImGui.Button("Stop all"))
        {
            this.queue.Clear();
        }

        ImGui.EndDisabled();
    }
}
