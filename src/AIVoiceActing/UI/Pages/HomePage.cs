namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// Home: the answers a player wants first. One status line (what is wrong, or that all is well),
/// the speech switch and volume, a notice when their settings were migrated, the inline setup
/// flow while the voices are not ready, and a "Try it" box. Engine internals (queue depth,
/// memory, per-engine downloads) live under Settings.
/// </summary>
public sealed class HomePage : IPage
{
    private const string ChangesModalId = "migration-changes";
    private const string ChangesModalTitle = "What changed";
    private const float ModalWidthEm = 32f;

    private readonly UiContext ctx;
    private readonly HomeSetupCard setup;
    private readonly HomeTryItCard tryIt;

    public HomePage(UiContext ctx)
    {
        this.ctx = ctx;
        this.setup = new HomeSetupCard(ctx);
        this.tryIt = new HomeTryItCard(ctx);
        ctx.Modals.Register(ChangesModalId, ChangesModalTitle, this.DrawChangesModal);
    }

    public NavTab Tab => NavTab.Home;

    public void Draw()
    {
        var errors = ctx.Status.Active().Where(m => m.Severity == Severity.Error).ToList();
        this.DrawStatusCard(errors.FirstOrDefault());

        if (MigrationNoticeModel.ShouldShow(ctx.Config))
        {
            this.DrawMigrationBanner();
        }

        foreach (var error in errors.Where(e => !this.setup.Handles(e.Key)))
        {
            Banner.Draw(ctx.Status, error);
        }

        if (this.setup.Visible)
        {
            SectionHeader.Draw("Set up");
            this.setup.Draw();
        }

        SectionHeader.Draw("Try it");
        this.tryIt.Draw();
    }

    private void DrawStatusCard(StatusMessage? error)
    {
        var status = HomeStatusModel.Describe(this.StatusInput(error));
        using var card = Card.Begin("home-status");
        if (!card.Visible)
        {
            return;
        }

        StatusPill.Draw(status.Severity, status.Text);
        Ui.Gap(Ui.Space2);

        var c = ctx.Config;
        Controls.Checkbox(
            "Speech on",
            () => c.Enabled,
            v =>
            {
                c.Enabled = v;
                ctx.Save();
            },
            "Turn off to silence every voice. Your settings are kept.");

        using (FormRow.Begin("Volume", "100 plays voices as they were made; higher lifts quiet ones."))
        {
            Controls.SliderScaled(
                "##home-volume",
                200f,
                () => c.GlobalVolume,
                v => c.GlobalVolume = v,
                ctx.Save,
                "How loud voices play.");
        }
    }

    private HomeStatusInput StatusInput(StatusMessage? error)
    {
        var engine = ctx.ActiveEngine;
        var downloads = ctx.Downloads;
        return new HomeStatusInput(
            ctx.Config.Enabled,
            ctx.Synth.IsReady,
            ctx.EngineSession.Warming,
            EngineParts.MissingRequired(ctx).Count > 0,
            engine.DisplayName,
            HomeStatusModel.OnGraphicsCard(ctx.Config.Device.Mode, engine),
            downloads.Active,
            downloads.FileDone + 1,
            downloads.FileTotal,
            ctx.Synth.IsReady ? string.Empty : ctx.Synth.NotReadyReason,
            error?.Title);
    }

    // ---------------------------------------------------------------- migration notice

    private void DrawMigrationBanner()
    {
        var clicked = Banner.Draw(
            "migration",
            Severity.Info,
            MigrationNoticeModel.Headline,
            MigrationNoticeModel.Summary(ctx.Config.Migration),
            ["See what changed", "Got it"]);
        if (clicked == 0)
        {
            ctx.Modals.Open(ChangesModalId);
        }
        else if (clicked == 1)
        {
            this.AcknowledgeMigration();
        }
    }

    private void AcknowledgeMigration()
    {
        MigrationNoticeModel.Acknowledge(ctx.Config);
        ctx.Save();
    }

    private void DrawChangesModal()
    {
        var report = ctx.Config.Migration;
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Ui.Em(ModalWidthEm));
        if (report.Changes.Count == 0)
        {
            ImGui.TextWrapped("Nothing to show. These changes were already marked as read.");
        }

        foreach (var group in MigrationNoticeModel.Groups(report))
        {
            SectionHeader.Draw(group.Title);
            foreach (var change in group.Changes)
            {
                ImGui.Bullet();
                ImGui.TextWrapped(change.Message);
            }
        }

        ImGui.PopTextWrapPos();
        Ui.Gap(Ui.Space3);

        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        if (ActionButton.Primary("Got it##migration-ack"))
        {
            this.AcknowledgeMigration();
            ctx.Modals.Close(ChangesModalId);
        }

        ImGui.SameLine();
        if (ActionButton.Secondary("Close##migration-close"))
        {
            ctx.Modals.Close(ChangesModalId);
        }
    }
}
