namespace AIVoiceActing.UI.Components;

using AIVoiceActing.Ports;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// The voice chooser used wherever a voice is picked: the casting editor (adding or changing a
/// slot) and the character tables. A modal with the same search, filters and rows as the
/// library, each row with its fingerprint and a preview button, and "Choose" as the row action.
/// One instance owns one popup, so draw it exactly once per frame at the spot where it was
/// opened from; a picker used inside another modal must be drawn from that modal's body.
/// </summary>
public sealed class VoicePicker
{
    private const float WidthEm = 44f;
    private const float HeightEm = 30f;
    private const float ListHeightEm = 18f;

    private readonly UiContext ctx;
    private readonly string id;
    private readonly VoiceLibraryModel model = new();
    private readonly VoiceRowsView rows;

    private string title = "Choose a voice";
    private string? current;
    private Action<string>? onPick;
    private bool openRequested;
    private bool shown;

    public VoicePicker(UiContext ctx, string id)
    {
        this.ctx = ctx;
        this.id = id;
        this.rows = new VoiceRowsView(ctx);
    }

    /// <summary>
    /// Asks the picker to open on the next <see cref="Draw"/>. <paramref name="currentId"/> is marked as the
    /// voice in use; <paramref name="pick"/> receives the chosen id, then the picker closes.
    /// </summary>
    public void Open(string heading, string? currentId, Action<string> pick)
    {
        this.title = heading;
        this.current = currentId;
        this.onPick = pick;
        this.openRequested = true;
    }

    /// <summary>True when the popup was showing this frame, even if Esc just closed it (so an outer modal can leave Esc to the picker).</summary>
    public bool IsOpen => this.shown;

    /// <summary>Draws the popup when it is open; call it every frame from the same place.</summary>
    public void Draw()
    {
        this.shown = false;
        var popup = this.title + "###aiva-picker-" + this.id;
        if (this.openRequested)
        {
            this.model.Clear();
            ImGui.OpenPopup(popup);
            this.openRequested = false;
        }

        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, Ui.CenterPivot);
        ImGui.SetNextWindowSize(Ui.Em(WidthEm, HeightEm), ImGuiCond.Appearing);
        var open = true;
        if (!ImGui.BeginPopupModal(popup, ref open))
        {
            return;
        }

        this.shown = true;
        this.DrawBody();
        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawBody()
    {
        var result = this.model.Compute(this.ctx.VoiceLibrary.Voices);
        VoiceFilterBar.Draw("picker-" + this.id, this.model, result);

        if (this.ctx.VoicesNotReadyReason is { } notReady)
        {
            Ui.Gap(Ui.Space1);
            Ui.MutedTextInline(notReady + " You can still choose a voice without hearing it.");
        }

        Ui.Gap(Ui.Space1);
        this.rows.Draw("picker-rows-" + this.id, this.model, result, ListHeightEm, "Choose", this.Choose, this.current);

        Ui.Gap(Ui.Space2);
        if (ImGui.Button("Cancel##picker-" + this.id))
        {
            ImGui.CloseCurrentPopup();
        }
    }

    private void Choose(VoiceInfo voice)
    {
        this.onPick?.Invoke(voice.Id);
        ImGui.CloseCurrentPopup();
    }
}
