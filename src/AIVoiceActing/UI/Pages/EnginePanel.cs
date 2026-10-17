namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// The old Engine tab body (below the status strip and first-run guide, which live on Home):
/// load/open buttons, the download progress, one card per offered engine with its asset table
/// and knobs, and the optional emotion and expressive-voice cards. Retired engines are not offered.
/// </summary>
internal sealed class EnginePanel(UiContext ctx)
{
    private const float AssetColumnWeight = 3f;

    public void Draw()
    {
        var anyDownload = ctx.Downloads.Active;
        var ready = ctx.Synth.IsReady;
        var warming = ctx.EngineSession.Warming;
        var missingRequired = EngineParts.MissingRequired(ctx).Count;

        if (Controls.Button(
                "Load engine now",
                !warming && !ready,
                warming ? "Already loading."
                    : ready ? "Engine is ready."
                    : missingRequired > 0 ? ctx.EngineReason()
                    : null))
        {
            this.StartWarmUp(ctx.PrimaryKey);
        }

        ImGui.SameLine();
        Controls.HelpMarker(
            "Builds the ONNX session ahead of the first line. The engine also loads itself " +
            "on login and on the first spoken line.");

        ImGui.SameLine();
        if (Controls.Button("Open Models Folder", !anyDownload, "A download is in progress."))
        {
            ctx.TryOpen(ctx.ModelsDir());
        }

        ImGui.SameLine();
        if (Controls.Button("Open Voices Folder", !anyDownload, "A download is in progress."))
        {
            ctx.TryOpen(ctx.VoicesDir());
        }

        EngineParts.DrawDownloadProgress(ctx);

        ImGui.Separator();
        ImGui.TextWrapped(
            "Every engine is downloadable and switchable here. Downloads go to the plugin's " +
            "models directory and run only when you press a button. Switching engines is safe " +
            "mid-line: the line in flight finishes on the old engine, the next line uses the new one.");

        var assets = ctx.ModelAssets();
        foreach (var engine in ctx.EngineCatalog.List().Where(e => !e.Retired && EngineParts.Groups(e).Count > 0))
        {
            this.DrawEngineCard(engine, assets, anyDownload);
        }

        this.DrawEmotionCard(assets, anyDownload);
        this.DrawExpressiveCard(assets, anyDownload);
    }

    private void StartWarmUp(string engineKey) => ctx.EngineSession.StartWarmUp(engineKey, ctx.Synth);

    private void DrawEngineCard(EngineInfo engine, IReadOnlyList<(ModelAsset Asset, string Group)> assets, bool anyDownload)
    {
        var key = engine.Key;
        var groups = EngineParts.Groups(engine);
        var sectionAssets = assets.Where(a => groups.Contains(a.Group)).ToList();
        var downloadedCount = sectionAssets.Count(a => ctx.Probes.IsDownloaded(a.Asset));
        var missingCount = sectionAssets.Count - downloadedCount;
        var active = ctx.PrimaryKey == key;

        var header = $"{engine.DisplayName} ({downloadedCount}/{sectionAssets.Count})"
            + (active ? " — ACTIVE" : string.Empty)
            + $"##engine-card-{key}";
        if (!ImGui.CollapsingHeader(header, active ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
        {
            Controls.Tooltip(engine.Blurb);
            return;
        }

        Controls.Tooltip(engine.Blurb);
        ImGui.TextWrapped(engine.Blurb);
        ImGui.Spacing();

        string readiness;
        if (active)
        {
            readiness = ctx.EngineSession.Warming && ctx.EngineSession.WarmingKey == key
                ? "loading…"
                : ctx.Synth.IsReady ? "ready" : ctx.EngineReason();
        }
        else
        {
            var missingRequired = sectionAssets.Count(a => !a.Asset.Optional && !ctx.Probes.IsDownloaded(a.Asset));
            readiness = missingRequired > 0
                ? $"not installed — {missingRequired} required asset(s) missing"
                : "installed — activates when set active";
        }

        ImGui.TextDisabled($"State: {readiness}");

        if (Controls.Button($"Set active##set-{key}", !active, active ? "Already the active engine." : null))
        {
            ctx.Config.Engine.Primary = key;
            ctx.Save();
            ctx.InvalidateSynthesizer();
            this.StartWarmUp(key);
        }

        ImGui.SameLine();
        if (missingCount > 0
            && Controls.Button(
                ModelsTabModel.DownloadAllLabel(missingCount) + $"##dl-{key}",
                ModelsTabModel.CanDownloadAll(anyDownload, missingCount),
                anyDownload ? "A download is already in progress." : null))
        {
            EngineParts.StartDownloads(
                ctx,
                [.. sectionAssets.Where(a => !ctx.Probes.IsDownloaded(a.Asset)).Select(a => a.Asset)]);
        }

        // Device/impact knobs render inside the ACTIVE card only.
        if (active)
        {
            EngineKnobs.Draw(ctx, engine);
        }

        this.DrawAssetTable(key, sectionAssets, active, anyDownload);
        ImGui.Spacing();
    }

    /// <summary>Per-card asset table: Download fetches EXACTLY that asset.</summary>
    private void DrawAssetTable(string key, IReadOnlyList<(ModelAsset Asset, string Group)> sectionAssets, bool active, bool anyDownload)
    {
        ImGui.Spacing();
        if (!ImGui.BeginTable($"##engine-assets-{key}", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            return;
        }

        ImGui.TableSetupColumn("Asset", ImGuiTableColumnFlags.WidthStretch, AssetColumnWeight);
        ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, Ui.Em(6));
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, Ui.Em(10));
        ImGui.TableSetupColumn("##action", ImGuiTableColumnFlags.WidthFixed, Ui.Em(7));
        ImGui.TableHeadersRow();

        foreach (var (asset, _) in sectionAssets)
        {
            var row = ModelsTabModel.Row(asset, ctx.Probes.IsDownloaded(asset));
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(asset.Optional ? $"{row.Name} (optional)" : row.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{row.SizeMb:0.0} MB");
            ImGui.TableNextColumn();
            var status = ctx.Downloads.Current?.FileName == asset.FileName
                ? "downloading…"
                : ModelsTabModel.StatusLabel(row, requiredForSelectedEngine: active);
            ImGui.TextUnformatted(status);
            ImGui.TableNextColumn();
            if (row.Downloaded)
            {
                if (Controls.Button("Remove##" + asset.FileName, !anyDownload, anyDownload ? "A download is in progress." : null))
                {
                    ctx.ModelStore().Remove(asset.FileName);
                }
            }
            else if (Controls.Button(
                "Download##" + asset.FileName,
                ModelsTabModel.CanDownload(anyDownload, row),
                anyDownload ? "A download is already in progress." : null))
            {
                EngineParts.StartDownloads(ctx, [asset]);
            }
        }

        ImGui.EndTable();
    }

    /// <summary>The optional expressive engine (Turbo) for strongly emotional lines.</summary>
    private void DrawExpressiveCard(IReadOnlyList<(ModelAsset Asset, string Group)> assets, bool anyDownload)
    {
        var expressive = ctx.EngineCatalog.Find(ctx.Config.Engine.Expressive.EngineId);
        if (expressive is null)
        {
            return;
        }

        var groups = EngineParts.Groups(expressive);
        var sectionAssets = assets.Where(a => groups.Contains(a.Group)).ToList();
        if (sectionAssets.Count == 0)
        {
            return;
        }

        var installed = sectionAssets.Where(a => !a.Asset.Optional).All(a => ctx.Probes.IsDownloaded(a.Asset));
        var defaultEngine = ctx.EngineCatalog.Resolve(null);
        var on = installed && ctx.Config.Engine.Expressive.Enabled && ctx.PrimaryKey == defaultEngine;
        if (!ImGui.CollapsingHeader("Expressive voices (experimental)" + (on ? " — ON" : string.Empty) + "##expressive-card"))
        {
            return;
        }

        ImGui.TextWrapped(
            "Optional second engine (Chatterbox Turbo, ~3.2 GB) for lines with a strong mood or a direction " +
            "such as a laugh or a sigh. It clones each speaker's Kokoro voice from a short clip made the first " +
            "time that voice needs it, so characters keep sounding like themselves. Every other line stays on " +
            "Kokoro. It runs on the CPU and is several times slower than Kokoro, so these lines arrive later; " +
            "if it fails, the line falls back to Kokoro.");
        Controls.Checkbox(
            "Use expressive voices for emotional lines when downloaded",
            () => ctx.Config.Engine.Expressive.Enabled,
            v => { ctx.Config.Engine.Expressive.Enabled = v; ctx.Save(); });
        ImGui.TextDisabled(
            !installed ? "State: not downloaded"
                : ctx.PrimaryKey != defaultEngine ? "State: downloaded (only used with Kokoro as the main engine)"
                : "State: downloaded");
        this.DrawMissingDownload(sectionAssets, anyDownload, "expressive");
        this.DrawAssetTable("expressive", sectionAssets, active: true, anyDownload);
        ImGui.Spacing();
    }

    /// <summary>The optional emotion classifier: reads each line's meaning to pick its delivery
    /// (and the expressive tags), on top of the punctuation rules.</summary>
    private void DrawEmotionCard(IReadOnlyList<(ModelAsset Asset, string Group)> assets, bool anyDownload)
    {
        var sectionAssets = assets.Where(a => a.Group == EngineParts.EmotionGroup).ToList();
        if (sectionAssets.Count == 0)
        {
            return;
        }

        var installed = sectionAssets.All(a => ctx.Probes.IsDownloaded(a.Asset));
        var header = "Emotion detection" + (installed && ctx.Config.DirectorEnabled ? " — ON" : string.Empty) + "##emotion-card";
        if (!ImGui.CollapsingHeader(header))
        {
            return;
        }

        ImGui.TextWrapped(
            "Optional text classifier (GoEmotions, ~480 MB) that reads what each line means: grief, fear, " +
            "anger, joy and more. It shapes pace, pauses, loudness and the expressive tags. Runs on the " +
            "speech thread in a few tens of milliseconds per line. Without it, mood comes from punctuation.");
        Controls.Checkbox(
            "Use the emotion classifier when downloaded",
            () => ctx.Config.DirectorEnabled,
            v => { ctx.Config.DirectorEnabled = v; ctx.Save(); });
        ImGui.TextDisabled(installed ? "State: downloaded" : "State: not downloaded");
        this.DrawMissingDownload(sectionAssets, anyDownload, "emotion");
        this.DrawAssetTable("emotion", sectionAssets, active: true, anyDownload);
        ImGui.Spacing();
    }

    private void DrawMissingDownload(IReadOnlyList<(ModelAsset Asset, string Group)> sectionAssets, bool anyDownload, string id)
    {
        var missing = sectionAssets.Where(a => !ctx.Probes.IsDownloaded(a.Asset)).Select(a => a.Asset).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        ImGui.SameLine();
        if (Controls.Button(
                ModelsTabModel.DownloadAllLabel(missing.Count) + $"##dl-{id}",
                ModelsTabModel.CanDownloadAll(anyDownload, missing.Count),
                anyDownload ? "A download is already in progress." : null))
        {
            EngineParts.StartDownloads(ctx, missing);
        }
    }
}
