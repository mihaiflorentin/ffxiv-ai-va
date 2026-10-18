namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

/// <summary>
/// The voice library: search and filters, the preview line, one scrolling list of every voice
/// with preview and "Use for...", and the voice packs underneath. Voices that casting or a
/// character still uses but no installed pack provides appear as "Missing" rows at the top.
/// </summary>
internal sealed class VoiceLibrarySection
{
    private const float ListHeightEm = 20f;
    private const float PreviewLineWidthEm = 30f;
    private const int PreviewLineMax = 200;
    private const double ReferencedRefreshSeconds = 1.0;

    private readonly UiContext ctx;
    private readonly VoiceLibraryModel model = new();
    private readonly VoiceRowsView rows;
    private readonly UseForChooser useFor;
    private readonly VoicePacksStrip packs;
    private string[] referenced = [];
    private double referencedAt = double.NegativeInfinity;
    private CastingPreset? referencedFrom;

    public VoiceLibrarySection(UiContext ctx)
    {
        this.ctx = ctx;
        this.rows = new VoiceRowsView(ctx);
        this.useFor = new UseForChooser(ctx);
        this.packs = new VoicePacksStrip(ctx);
        this.rows.PreviewLine = TestBenchModel.VoiceTestLine;
    }

    public void Draw()
    {
        SectionHeader.Draw("Voice library");
        Ui.MutedText("Every voice you can give a character. Hear one with the play button, or use it with Use for…");
        Ui.Gap(Ui.Space1);

        this.DrawNotReady();

        var result = this.model.Compute(this.ctx.VoiceLibrary.Voices, this.Referenced());
        VoiceFilterBar.Draw("library", this.model, result);
        Ui.Gap(Ui.Space1);
        this.rows.Draw("library-rows", this.model, result, ListHeightEm, "Use for…", this.useFor.Open);
        this.DrawPreviewLine();

        this.packs.Draw();
    }

    /// <summary>When previews cannot run, say why in words next to a way to fix it (not only in a tooltip).</summary>
    private void DrawNotReady()
    {
        var reason = this.ctx.VoicesNotReadyReason;
        if (reason is null)
        {
            return;
        }

        Theme.IconText(FontAwesomeIcon.ExclamationTriangle, Theme.Warn);
        ImGui.SameLine(0f, Ui.Em(Ui.Space2));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(reason);
        ImGui.SameLine();
        if (ImGui.Button("Go to Home##voices-home"))
        {
            this.ctx.Nav.OpenAt(NavTab.Home);
        }

        Ui.Gap(Ui.Space1);
    }

    private void DrawPreviewLine()
    {
        Ui.Gap(Ui.Space1);
        ImGui.AlignTextToFramePadding();
        Ui.MutedTextInline("Preview line");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Ui.Em(PreviewLineWidthEm));
        var line = this.rows.PreviewLine;
        if (ImGui.InputText("##preview-line", ref line, PreviewLineMax))
        {
            this.rows.PreviewLine = line;
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset##preview-line"))
        {
            this.rows.PreviewLine = TestBenchModel.VoiceTestLine;
        }
    }

    /// <summary>Voice ids casting and characters use: rebuilt when the casting edits change and about once a second otherwise (the store copy is not free).</summary>
    private string[] Referenced()
    {
        this.ctx.Casting.EnsureLoaded();
        var buffer = this.ctx.Casting.Model.EditBuffer;
        var now = ImGui.GetTime();
        if (now - this.referencedAt >= ReferencedRefreshSeconds || !ReferenceEquals(buffer, this.referencedFrom))
        {
            this.referenced = CastingSummary.ReferencedIds(buffer, this.ctx.Profiles.Entries);
            this.referencedFrom = buffer;
            this.referencedAt = now;
        }

        return this.referenced;
    }
}
