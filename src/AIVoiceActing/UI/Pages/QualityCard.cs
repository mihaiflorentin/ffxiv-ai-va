namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// Voice quality: Fast / Balanced / Most expressive, which writes the engine settings, plus the
/// handful of delivery switches most players ever touch. Picking a tier whose files are not on disk
/// does not switch the voices yet: the card offers the download and switches when it finishes, so the
/// current voices keep working meanwhile. "Most expressive" is greyed out, with the reason shown, when
/// there is no usable graphics card.
/// </summary>
internal sealed class QualityCard(UiContext ctx, SettingsInputs inputs)
{
    private readonly QualityChoiceModel choice = new();
    private bool wasDownloading;

    public void Draw()
    {
        this.ApplyWhenReady();

        SectionHeader.Draw("Voice quality");
        using var card = Card.Begin("settings-quality");
        if (!card.Visible)
        {
            return;
        }

        var setup = inputs.Setup;
        var current = QualityTierModel.FromConfig(ctx.Config.Engine, ctx.EngineCatalog);
        this.DrawEngineState();
        Ui.Gap(Ui.Space2);

        var labels = QualityTierModel.All.Select(QualityTierModel.Label).ToList();
        var reasons = QualityTierModel.All.Select(t => this.UnavailableReason(t, setup)).ToList();
        var selected = current is { } tier ? (int)tier : -1;
        var picked = SegmentedControl.Draw("settings-quality-tier", labels, selected, reasons);
        if (picked != selected && picked >= 0)
        {
            this.Choose(QualityTierModel.All[picked], setup);
        }

        Ui.Gap(Ui.Space1);
        if (current is { } active)
        {
            Ui.MutedText(QualityTierModel.Explanation(active));
        }
        else
        {
            Ui.MutedText(
                $"Custom setup: you are using {ctx.ActiveEngine.DisplayName}, chosen under Advanced. " +
                "Pick a quality above to go back to a standard setup.");
        }

        if (this.UnavailableReason(QualityTier.MostExpressive, setup) is { } unavailable)
        {
            Ui.MutedText("Most expressive: " + unavailable);
        }

        this.DrawPending(setup);
        this.DrawToggles();
    }

    private void DrawEngineState()
    {
        var info = ctx.ActiveEngine;
        var view = EngineViewModel.Describe(new EngineViewInput(
            info.DisplayName,
            SettingsParts.EngineInstalled(ctx, info),
            ctx.EngineSession.Warming,
            ctx.Synth.Status));
        StatusPill.Draw(view.Severity, $"Voices: {view.Word}");
        Ui.MutedText(view.Detail);
    }

    private string? UnavailableReason(QualityTier tier, SetupInputs setup)
    {
        if (QualityTierModel.IsAvailable(tier, setup.HasUsableGpu))
        {
            return null;
        }

        // A crash-guard trip hides the graphics card from the plan; say so instead of "none found".
        return ctx.Config.Device.GpuDisabledByCrashGuard
            ? "The graphics card was turned off after a crash. Turn it back on under Processor first."
            : QualityTierModel.UnavailableReason(tier, setup.HasUsableGpu);
    }

    private void Choose(QualityTier tier, SetupInputs setup)
    {
        var plan = SetupFlowModel.PlanFor(tier, setup);
        if (this.choice.Choose(tier, plan) == ChoiceOutcome.Applied)
        {
            this.Apply(tier, setup.HasUsableGpu);
        }
    }

    private void Apply(QualityTier tier, bool hasUsableGpu)
    {
        if (!QualityTierModel.Apply(ctx.Config.Engine, tier, hasUsableGpu))
        {
            return;
        }

        ctx.Save();
        ctx.InvalidateSynthesizer();
        ctx.Status.Resolve(EngineSessionModel.FailureKey);
        ctx.EngineSession.StartWarmUp(ctx.PrimaryKey, ctx.Synth);
        ctx.Report($"Voice quality is now {QualityTierModel.Label(tier)}.", isError: false);
    }

    /// <summary>Once per frame: switch to the waiting tier when its files have all arrived.</summary>
    private void ApplyWhenReady()
    {
        if (this.choice.Pending is not { } pending)
        {
            return;
        }

        // The cached probe lags up to a second behind a finished download, so the frame a download ends
        // is checked against the disk; every other frame reads the cache (no file probing per frame).
        var downloading = ctx.Downloads.Active;
        var justFinished = this.wasDownloading && !downloading;
        this.wasDownloading = downloading;
        var installed = !downloading && this.Missing(pending, fresh: justFinished).Count == 0;
        if (this.choice.TakeReady(downloading, installed) is { } ready)
        {
            this.Apply(ready, ctx.DeviceInfo().HasUsableGpu);
        }
    }

    /// <summary>Required files of the tier that are not on disk; <paramref name="fresh"/> asks the disk instead of the cached probe.</summary>
    private List<ModelAsset> Missing(QualityTier tier, bool fresh = true)
    {
        var provisioner = ctx.Provisioner();
        Func<ModelAsset, bool> isDownloaded = fresh ? provisioner.IsDownloaded : ctx.Probes.IsDownloaded;
        return
        [
            .. QualityTierModel.EnginesFor(tier)
                .Select(id => ctx.EngineCatalog.Find(id))
                .OfType<EngineInfo>()
                .SelectMany(info => EngineParts.RequiredAssets(ctx, info))
                .Where(a => !isDownloaded(a))
                .DistinctBy(a => a.FileName),
        ];
    }

    private void DrawPending(SetupInputs setup)
    {
        if (this.choice.Notice is { } notice && this.choice.Pending is null)
        {
            Ui.Gap(Ui.Space1);
            StatusPill.Draw(Severity.Warning, notice);
        }

        if (this.choice.Pending is not { } pending)
        {
            return;
        }

        var plan = SetupFlowModel.PlanFor(pending, setup);
        Ui.Gap(Ui.Space2);
        ImGui.TextWrapped(
            $"{QualityTierModel.Label(pending)} needs {Labels.FileSize(plan.BytesToDownload)} of voices first. " +
            "Your current voices keep working until it finishes.");
        if (this.choice.Notice is { } stopped)
        {
            StatusPill.Draw(Severity.Warning, stopped);
        }

        if (ctx.Downloads.Active)
        {
            ProgressRow.Draw("Downloading", (float)ctx.Downloads.Fraction, ctx.Downloads.Caption, ctx.Config.ReduceMotion);
            if (ActionButton.Secondary("Cancel download##settings-quality-cancel"))
            {
                ctx.Downloads.Cancel();
            }

            return;
        }

        var label = $"Download {Labels.FileSize(plan.BytesToDownload)} and switch";
        if (ActionButton.Primary(label + "##settings-quality-go", plan.Blocked is null, plan.Blocked)
            && !ctx.Downloads.Start(this.Missing(pending)))
        {
            ctx.Report("The download could not start. Try again in a moment.", isError: true);
        }

        ImGui.SameLine();
        if (ActionButton.Secondary("Not now##settings-quality-cancel"))
        {
            this.choice.Cancel();
        }

        if (plan.Blocked is { } blocked)
        {
            Ui.MutedText(blocked);
        }
    }

    private void DrawToggles()
    {
        var c = ctx.Config;
        Ui.Gap(Ui.Space2);
        ImGui.Separator();
        Ui.Gap(Ui.Space2);

        var emotion = inputs.Rows.FirstOrDefault(r => r.Group == DownloadsModel.EmotionGroup);
        var emotionReady = emotion is null || emotion.Installed;
        SettingsParts.Toggle(
            "Emotion detection",
            emotionReady
                ? "Reads what each line means (grief, fear, joy and more) so pace and tone fit the scene. Without it, mood comes from punctuation."
                : $"Reads what each line means so pace and tone fit the scene. Needs the emotion reader ({Labels.FileSize(emotion!.TotalBytes)}), which you can download below.",
            () => c.DirectorEnabled,
            v => { c.DirectorEnabled = v; ctx.Save(); },
            emotionReady);

        SettingsParts.Toggle(
            "Give every character a unique voice",
            "The first time someone speaks, they get a voice made just for them by mixing voices from their group, and keep it from then on. "
            + "Voices you pick by hand never change. Turning this off only affects characters who have not spoken yet.",
            () => c.Voices.UniquePerCharacter,
            v => { c.Voices.UniquePerCharacter = v; ctx.Save(); });

        SettingsParts.Toggle(
            "Even out loudness",
            "Brings every line to the same loudness before each voice's own volume is applied.",
            () => c.LevelVoiceLoudness,
            v => { c.LevelVoiceLoudness = v; ctx.Save(); });

        SettingsParts.Toggle(
            "Skip lines that arrive late",
            "If a line is still being made after the conversation moved on, skip it instead of playing it late.",
            () => c.StaleLineSeconds > 0,
            v =>
            {
                c.StaleLineSeconds = v ? DefaultStaleSeconds : 0;
                ctx.Save();
            });

        SettingsParts.Toggle(
            "Show the now-speaking panel",
            "A small panel with who is talking, their voice and mood, and Replay, Skip and Stop buttons.",
            () => c.ShowNowSpeaking,
            v => { c.ShowNowSpeaking = v; ctx.Save(); });
    }

    /// <summary>The wait before a late line is skipped when the switch is turned on (the seconds can be changed under Advanced).</summary>
    private static int DefaultStaleSeconds => new Configuration().StaleLineSeconds;
}
