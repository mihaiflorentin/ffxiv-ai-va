namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// What is downloaded and what is not, per group of files: size, installed state, progress with
/// Cancel while a download runs, Retry from the failure banner, free disk, and Remove for groups the
/// current voices do not need. Nothing downloads until a button is pressed; a stopped download resumes
/// where it left off.
/// </summary>
internal sealed class DownloadsCard(UiContext ctx, SettingsInputs inputs)
{
    public void Draw()
    {
        SectionHeader.Draw("Downloads");
        foreach (var failure in ctx.Status.Active().Where(m => m.Key == DownloadQueueModel.FailureKey))
        {
            Banner.Draw(ctx.Status, failure);
        }

        using var card = Card.Begin("settings-downloads");
        if (!card.Visible)
        {
            return;
        }

        var downloading = ctx.Downloads.Active;
        if (downloading)
        {
            this.DrawProgress();
            Ui.Gap(Ui.Space2);
        }

        this.DrawRows(downloading);
        Ui.Gap(Ui.Space2);
        this.DrawFooter(downloading);
    }

    private void DrawProgress()
    {
        var downloads = ctx.Downloads;
        var known = downloads.Total > 0;
        ProgressRow.Draw(
            $"Downloading file {downloads.FileDone + 1} of {Math.Max(downloads.FileTotal, downloads.FileDone + 1)}",
            known ? (float)downloads.Fraction : null,
            downloads.Caption,
            ctx.Config.ReduceMotion);
        if (ActionButton.Secondary("Cancel download##settings-downloads-cancel"))
        {
            downloads.Cancel();
        }

        Ui.MutedText("Cancelling keeps what was downloaded. Pressing Download again continues from there.");
    }

    private void DrawRows(bool downloading)
    {
        var rows = inputs.Rows;
        if (rows.Count == 0)
        {
            Ui.MutedText("Nothing to download.");
            return;
        }

        if (!ImGui.BeginTable("##settings-downloads-table", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.PadOuterX))
        {
            return;
        }

        ImGui.TableSetupColumn("Voices", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, Ui.Em(5f));
        ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, Ui.Em(10f));
        ImGui.TableSetupColumn("##action", ImGuiTableColumnFlags.WidthFixed, Ui.Em(10f));
        ImGui.TableHeadersRow();

        foreach (var row in rows)
        {
            this.DrawRow(row, downloading);
        }

        ImGui.EndTable();
    }

    private void DrawRow(DownloadRow row, bool downloading)
    {
        var current = ctx.Downloads.Current;
        var thisOne = current is not null && row.Assets.Any(a => a.FileName == current.FileName);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextWrapped(row.Name);
        if (row.Blurb.Length > 0)
        {
            Ui.MutedText(row.Blurb);
        }

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Labels.FileSize(row.TotalBytes));

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        StatusPill.Draw(DownloadsModel.StatusSeverity(row, thisOne), DownloadsModel.StatusWord(row, thisOne));

        ImGui.TableNextColumn();
        if (thisOne)
        {
            return;
        }

        if (!row.Installed)
        {
            var reason = DownloadsModel.CannotDownload(row, inputs.FreeBytes, downloading);
            if (ActionButton.Secondary(DownloadsModel.DownloadLabel(row) + "##dl-" + row.Group, reason is null, reason))
            {
                this.StartDownload(row);
            }

            // A full disk is worth saying out loud; "another download is running" is already visible above.
            if (reason is not null && !downloading)
            {
                Ui.MutedText(reason);
            }

            return;
        }

        var cannotRemove = DownloadsModel.CannotRemove(row, downloading);
        if (cannotRemove is null)
        {
            if (ActionButton.Secondary("Remove##rm-" + row.Group))
            {
                this.AskRemove(row);
            }
        }
        else if (row.InUse && !row.Optional)
        {
            Ui.MutedTextInline("In use");
            Ui.Tooltip(cannotRemove);
        }
        else
        {
            ActionButton.Secondary("Remove##rm-" + row.Group, false, cannotRemove);
        }
    }

    private void StartDownload(DownloadRow row)
    {
        var missing = row.Missing(ctx.Probes.IsDownloaded);
        if (missing.Count > 0 && !ctx.Downloads.Start(missing))
        {
            ctx.Report("The download could not start. Try again in a moment.", isError: true);
        }
    }

    private void AskRemove(DownloadRow row)
    {
        ctx.Confirm.Ask(
            $"Remove {row.Name}? This frees {Labels.FileSize(row.TotalBytes)}. You can download it again whenever you like.",
            "Remove",
            () =>
            {
                var store = ctx.ModelStore();
                foreach (var asset in row.Assets)
                {
                    store.Remove(asset.FileName);
                }

                // The engine may be holding the files open; build it again so it sees what is left.
                ctx.InvalidateSynthesizer();
                ctx.Report($"Removed {row.Name}.", isError: false);
            });
    }

    private void DrawFooter(bool downloading)
    {
        ImGui.AlignTextToFramePadding();
        Ui.MutedTextInline(
            $"Downloaded voices use {Labels.FileSize(ctx.Probes.ModelsFolderBytes)}. {DownloadsModel.FreeDiskText(inputs.FreeBytes)}.");

        if (ActionButton.Secondary("Open models folder##settings-open-models", !downloading, "A download is running."))
        {
            ctx.TryOpen(ctx.ModelsDir());
        }

        ImGui.SameLine();
        if (ActionButton.Secondary("Open voices folder##settings-open-voices", !downloading, "A download is running."))
        {
            ctx.TryOpen(ctx.VoicesDir());
        }
    }
}
