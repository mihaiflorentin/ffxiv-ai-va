namespace AIVoiceActing.UI;

using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

/// <summary>
/// Small always-available panel: who is speaking (name, voice fingerprint, the voice's friendly
/// name and the mood), plus Replay (the last line again), Skip (the rest of this line), Stop
/// (everything queued) and a gear that opens the Voices tab. Its open state is the ShowNowSpeaking
/// setting, so closing it with the X (or Esc while it has focus) sticks.
/// </summary>
public sealed class NowSpeakingWindow : Window
{
    private const float MinWidthEm = 18f;
    private const float WrapEm = 22f;

    private readonly Configuration config;
    private readonly Action save;
    private readonly ISpeechQueue queue;
    private readonly IVoiceLibrary library;
    private readonly Action openVoices;
    private SpeechItem? last;

    public NowSpeakingWindow(
        Configuration config,
        Action save,
        ISpeechQueue queue,
        IVoiceLibrary library,
        Action openVoices)
        : base("Now speaking###AIVANowSpeaking", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
        this.config = config;
        this.save = save;
        this.queue = queue;
        this.library = library;
        this.openVoices = openVoices;
        this.IsOpen = config.ShowNowSpeaking;
        this.RespectCloseHotkey = true;
    }

    /// <summary>Runs every frame, open or not: the setting drives the window.</summary>
    public override void PreOpenCheck() => this.IsOpen = this.config.ShowNowSpeaking;

    public override void PreDraw()
    {
        this.SizeConstraints = new WindowSizeConstraints { MinimumSize = Ui.Em(MinWidthEm, 0f) };
    }

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
            Ui.MutedText("Nothing spoken yet.");
        }
        else
        {
            this.DrawSpeaker(shown, playing is not null);
        }

        Ui.Gap(Ui.Space1);
        this.DrawButtons(shown, playing);
    }

    private void DrawSpeaker(SpeechItem shown, bool isPlaying)
    {
        var voiceId = shown.Request.ReferenceVoiceId;
        VoiceFingerprint.Draw(voiceId, isPlaying, this.config.ReduceMotion);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(shown.Speaker.DisplayName);
        ImGui.SameLine();
        Ui.MutedTextInline(isPlaying ? "speaking" : "last line");

        var detail = $"{VoiceNames.Display(this.library, voiceId)}, {NowSpeakingModel.Mood(shown.Emotion)}";
        if (this.queue.Depth > 0)
        {
            detail += $", {this.queue.Depth} waiting";
        }

        Ui.MutedText(detail);
        ImGui.PushTextWrapPos(Ui.Em(WrapEm));
        ImGui.TextUnformatted(shown.Request.Text);
        ImGui.PopTextWrapPos();
    }

    private void DrawButtons(SpeechItem? shown, SpeechItem? playing)
    {
        if (IconButton.Draw("##ns-replay", FontAwesomeIcon.Redo, "Replay the last line", shown is not null, "Nothing to replay yet.") && shown is not null)
        {
            // A replay is user-initiated: never stale, and not part of the skipped line.
            this.queue.Enqueue(shown with { RequestedAtTicks = 0, LineId = 0 });
        }

        ImGui.SameLine();
        if (IconButton.Draw("##ns-skip", FontAwesomeIcon.StepForward, "Skip the rest of this line", playing is not null, "Nothing is playing."))
        {
            this.queue.CancelCurrent();
        }

        ImGui.SameLine();
        var busy = playing is not null || this.queue.Depth > 0;
        if (IconButton.Draw("##ns-stop", FontAwesomeIcon.Stop, "Stop everything that is queued", busy, "Nothing is playing or waiting."))
        {
            this.queue.Clear();
        }

        ImGui.SameLine();
        if (IconButton.Draw("##ns-voices", FontAwesomeIcon.Cog, "Change this voice in the Voices tab"))
        {
            this.openVoices();
        }
    }
}
