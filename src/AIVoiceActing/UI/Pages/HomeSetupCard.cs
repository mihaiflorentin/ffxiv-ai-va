namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

/// <summary>
/// The inline "Set up recommended" flow on Home: choose, download, get ready, hear a sample.
/// <see cref="SetupFlowModel"/> decides what happens next; this class reads the world into
/// observations, runs the side effects the model asks for (start the download, warm the engine,
/// speak the sample) and draws one card per step. There is no separate window.
/// </summary>
internal sealed class HomeSetupCard
{
    public const string SampleKey = "home:setup-sample";

    private static readonly TimeSpan InputsLifetime = TimeSpan.FromSeconds(1);

    private readonly UiContext ctx;
    private readonly SetupFlowModel flow;

    private SetupInputs? inputs;
    private double inputsAt = double.NegativeInfinity;
    private EngineChoice? before;
    private Task? warmUp;

    public HomeSetupCard(UiContext ctx)
    {
        this.ctx = ctx;
        var config = ctx.Config;
        this.flow = new SetupFlowModel(
            config.OnboardingStep,
            config.OnboardingCompletedVersion >= Configuration.CurrentVersion,
            step =>
            {
                config.OnboardingStep = (int)step;
                ctx.Save();
            },
            () =>
            {
                config.OnboardingCompletedVersion = Configuration.CurrentVersion;
                ctx.Save();
            });
    }

    /// <summary>True while the card belongs on Home.</summary>
    public bool Visible
    {
        get
        {
            // Asked for from Settings: start the flow over before deciding whether the card shows.
            if (this.ctx.SetupRerun.Take())
            {
                this.before = null;
                this.warmUp = null;
                this.flow.Restart();
            }

            return this.flow.Visible(this.ctx.Synth.IsReady);
        }
    }

    /// <summary>Errors this card shows itself, so Home does not repeat them as banners.</summary>
    public bool Handles(string statusKey) =>
        this.Visible && (statusKey == DownloadQueueModel.FailureKey || statusKey == EngineSessionModel.FailureKey);

    public void Draw()
    {
        this.RefreshInputs();
        this.Advance();
        if (this.flow.AwaitingResume)
        {
            this.DrawInterrupted();
            return;
        }

        switch (this.flow.Step)
        {
            case SetupStep.Recommend:
                this.DrawChoice();
                break;
            case SetupStep.Download:
                this.DrawDownload();
                break;
            case SetupStep.WarmUp:
                this.DrawWarmUp();
                break;
            case SetupStep.PlaySample:
                this.DrawSample();
                break;
            default:
                this.DrawDone();
                break;
        }
    }

    // ---------------------------------------------------------------- world <-> model

    /// <summary>Recomputes the recommendation inputs about once a second (disk and partial files are real IO).</summary>
    private void RefreshInputs()
    {
        var now = ImGui.GetTime();
        if (this.inputs is not null && now - this.inputsAt < InputsLifetime.TotalSeconds)
        {
            return;
        }

        this.inputsAt = now;
        var device = this.ctx.DeviceInfo();
        var provisioner = this.ctx.Provisioner();
        var engines = new List<EngineDownload>();
        foreach (var id in new[] { ConfigMigrator.KokoroId, ConfigMigrator.TurboId })
        {
            if (this.ctx.EngineCatalog.Find(id) is not { } info)
            {
                continue;
            }

            var required = EngineParts.RequiredAssets(this.ctx, info);
            var missing = required.Where(a => !this.ctx.Probes.IsDownloaded(a)).ToList();
            engines.Add(new EngineDownload(
                info.Key,
                info.DisplayName,
                required.Sum(a => a.SizeBytes ?? 0),
                missing.Count == 0 ? 0 : provisioner.BytesNeeded(missing)));
        }

        // The Turbo q4f16 build (graphs and weights only; the base group above stays required): the GPU choice.
        var q4f16 = this.ctx.ModelAssets()
            .Where(a => a.Group == SetupAdvisor.TurboQ4F16Group)
            .Select(a => a.Asset)
            .ToList();
        var q4f16Missing = q4f16.Where(a => !this.ctx.Probes.IsDownloaded(a)).ToList();
        var precisionGroups = new List<GroupStatus>
        {
            new(
                SetupAdvisor.TurboQ4F16Group,
                q4f16.Sum(a => a.SizeBytes ?? 0),
                q4f16Missing.Count == 0 ? 0 : provisioner.BytesNeeded(q4f16Missing)),
        };

        this.inputs = new SetupInputs(
            device.HasUsableGpu,
            this.ctx.ModelStore().FreeBytes(this.ctx.ModelsDir()),
            engines,
            QualityTierModel.FromConfig(this.ctx.Config.Engine, this.ctx.EngineCatalog),
            precisionGroups);
    }

    /// <summary>Feeds the model what happened this frame, then performs what it asks for.</summary>
    private void Advance()
    {
        var step = this.flow.Step;
        if (step is SetupStep.Recommend or SetupStep.Done || this.flow.AwaitingResume || this.flow.Failure is not null)
        {
            return; // Nothing runs on the choice, the finish, a restored flow or a failed step until the player acts.
        }

        this.flow.Observe(this.Observe(step));

        if (this.flow.TakeStartDownload())
        {
            this.StartDownload();
        }

        if (this.flow.TakeStartWarmUp())
        {
            this.ctx.Status.Resolve(EngineSessionModel.FailureKey);
            this.ctx.EngineSession.StartWarmUp(this.ctx.PrimaryKey, this.ctx.Synth);
            this.warmUp = this.ctx.EngineSession.Completion;
        }

        if (this.flow.TakeStartSample())
        {
            this.ctx.Status.Resolve(PreviewController.FailureKey);
            HomeSpeech.Speak(this.ctx, SampleKey, HomeSpeech.Speaker(this.ctx, useMyCharacter: false), SetupFlowModel.SampleLine);
        }
    }

    private SetupObservation Observe(SetupStep step)
    {
        var downloadRunning = this.ctx.Downloads.Active;
        var preview = this.ctx.Preview;
        var ours = preview.Key == SampleKey;
        return new SetupObservation(
            downloadRunning,
            // A fresh check, but only when it matters: probes lag by up to a second, which would
            // read a finished download as a failed one.
            step == SetupStep.Download && !downloadRunning && this.TierAssetsInstalled(),
            step == SetupStep.Download ? this.StatusText(DownloadQueueModel.FailureKey) : null,
            this.warmUp is { IsCompleted: false },
            this.warmUp is { IsCompleted: true },
            step == SetupStep.WarmUp ? this.StatusText(EngineSessionModel.FailureKey) : null,
            ours ? preview.State : PreviewState.Idle,
            ours ? preview.Error : null);
    }

    private QualityTier AppliedTier() =>
        QualityTierModel.FromConfig(this.ctx.Config.Engine, this.ctx.EngineCatalog) ?? QualityTier.Fast;

    /// <summary>
    /// Assets of the applied tier that are not on disk, checked against the disk now. The groups come from
    /// the advisor, so a machine with a verified GPU also gets the Turbo q4f16 build, not just the base set.
    /// </summary>
    private List<ModelAsset> MissingForAppliedTier()
    {
        var provisioner = this.ctx.Provisioner();
        var groups = SetupAdvisor.GroupsFor(this.AppliedTier(), this.ctx.DeviceInfo().HasUsableGpu);
        return
        [
            .. this.ctx.ModelAssets()
                .Where(a => groups.Contains(a.Group))
                .Select(a => a.Asset)
                .Where(a => !provisioner.IsDownloaded(a))
                .DistinctBy(a => a.FileName),
        ];
    }

    private bool TierAssetsInstalled() => this.MissingForAppliedTier().Count == 0;

    private void StartDownload()
    {
        if (this.ctx.Downloads.Active)
        {
            return;
        }

        var missing = this.MissingForAppliedTier();
        if (missing.Count > 0 && !this.ctx.Downloads.Start(missing))
        {
            this.flow.Fail("The download could not start. Try again in a moment.");
        }
    }

    private string? StatusText(string key)
    {
        var message = this.ctx.Status.Active().FirstOrDefault(m => m.Key == key);
        if (message is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(message.Detail) ? message.Title : $"{message.Title}: {message.Detail}";
    }

    private void Begin(SetupPlan plan)
    {
        if (!this.flow.Begin(plan))
        {
            return;
        }

        var config = this.ctx.Config;
        this.before = QualityTierModel.Capture(config.Engine);
        QualityTierModel.Apply(config.Engine, plan.Tier, this.ctx.DeviceInfo().HasUsableGpu);
        this.ctx.Save();
        this.ctx.InvalidateSynthesizer();
        this.warmUp = null;
        this.ctx.Status.Resolve(DownloadQueueModel.FailureKey);
        this.ctx.Status.Resolve(EngineSessionModel.FailureKey);
    }

    private void Cancel()
    {
        var wasDownloading = this.flow.Step == SetupStep.Download;
        this.flow.Cancel();
        if (wasDownloading)
        {
            this.ctx.Downloads.Cancel();
        }

        // Put the previous engine choice back, so cancelling never leaves a main engine that is not on disk.
        if (this.before is { } previous)
        {
            QualityTierModel.Restore(this.ctx.Config.Engine, previous);
            this.ctx.Save();
            this.ctx.InvalidateSynthesizer();
            this.before = null;
        }

        this.warmUp = null;
        this.ctx.Status.Resolve(DownloadQueueModel.FailureKey);
        this.ctx.Status.Resolve(EngineSessionModel.FailureKey);
    }

    // ---------------------------------------------------------------- drawing

    private static void Title(FontAwesomeIcon icon, string text)
    {
        Theme.IconText(icon, Theme.Accent);
        ImGui.SameLine(0f, Ui.Em(Ui.Space2));
        ImGui.TextUnformatted(text);
    }

    private static void StepLabel(SetupFlowModel flow) =>
        Ui.MutedText($"Step {flow.StepNumber} of {SetupFlowModel.StepCount}");

    private void DrawChoice()
    {
        var inputs = this.inputs!;
        var recommended = SetupFlowModel.Recommend(inputs);
        var tier = this.flow.EffectiveTier(recommended);
        var plan = SetupFlowModel.PlanFor(tier, inputs);
        var ready = this.ctx.Synth.IsReady;
        var downloading = this.ctx.Downloads.Active;

        using var card = Card.Begin("home-setup-choose-" + (this.flow.Customizing ? "open" : "closed"));
        if (!card.Visible)
        {
            return;
        }

        Title(
            ready ? FontAwesomeIcon.SlidersH : FontAwesomeIcon.Download,
            ready ? "Choose your voice quality" : "Get your voices ready");
        StepLabel(this.flow);
        Ui.MutedText(ready
            ? "Your voices already work. Switch quality here, or keep what you have."
            : "One button downloads the voices and plays a sample. Nothing downloads until you press it.");

        if (this.flow.Notice is { } notice)
        {
            Ui.Gap(Ui.Space1);
            StatusPill.Draw(Severity.Info, notice);
        }

        Ui.Gap(Ui.Space2);
        ImGui.TextUnformatted($"Recommended for this computer: {QualityTierModel.Label(recommended.Tier)}");
        Ui.MutedText(QualityTierModel.Explanation(recommended.Tier));
        if (inputs.CurrentTier is null && ready)
        {
            Ui.MutedText($"You are using {this.ctx.ActiveEngine.DisplayName}. Setting up switches you to the option below.");
        }

        if (this.flow.Customizing)
        {
            Ui.Gap(Ui.Space2);
            this.DrawTierChoice(inputs, tier);
        }

        Ui.Gap(Ui.Space2);
        this.DrawPlanSummary(plan, inputs);

        Ui.Gap(Ui.Space2);
        var recommendedPick = tier == recommended.Tier;
        var label = recommendedPick ? "Set up recommended" : $"Set up {QualityTierModel.Label(tier)}";
        var blocked = plan.Blocked ?? (downloading ? "Another download is running. Wait for it to finish." : null);
        if (ActionButton.Primary(label + "##home-setup-go", blocked is null, blocked))
        {
            this.Begin(plan);
        }

        ImGui.SameLine();
        if (ActionButton.Secondary((this.flow.Customizing ? "Hide options" : "Customize") + "##home-setup-customize"))
        {
            this.flow.ToggleCustomize();
        }

        if (ready)
        {
            ImGui.SameLine();
            if (ActionButton.Secondary("Not now##home-setup-skip"))
            {
                this.flow.NotNow();
            }
        }
    }

    /// <summary>The Customize area: a segmented choice with one plain line and one size per option.</summary>
    private void DrawTierChoice(SetupInputs inputs, QualityTier selected)
    {
        var labels = QualityTierModel.All.Select(QualityTierModel.Label).ToList();
        var picked = SegmentedControl.Draw("home-setup-tier", labels, (int)selected);
        if (picked != (int)selected)
        {
            this.flow.Choose(QualityTierModel.All[picked], inputs);
        }

        Ui.Gap(Ui.Space1);
        foreach (var option in QualityTierModel.All)
        {
            var plan = SetupFlowModel.PlanFor(option, inputs);
            var line = $"{QualityTierModel.Label(option)}: {QualityTierModel.Explanation(option)} {SizeNote(plan, inputs)}";
            if (option == selected)
            {
                ImGui.TextWrapped(line);
            }
            else
            {
                Ui.MutedText(line);
            }
        }

        if (QualityTierModel.UnavailableReason(QualityTier.MostExpressive, inputs.HasUsableGpu) is { } reason)
        {
            Ui.MutedText(reason);
        }
    }

    private static string SizeNote(SetupPlan plan, SetupInputs inputs)
    {
        if (!QualityTierModel.IsAvailable(plan.Tier, inputs.HasUsableGpu))
        {
            return "(Not available here.)";
        }

        return plan.BytesToDownload == 0
            ? "(Already downloaded.)"
            : $"(Downloads {Labels.FileSize(plan.BytesToDownload)}.)";
    }

    private void DrawPlanSummary(SetupPlan plan, SetupInputs inputs)
    {
        if (plan.Blocked is { } blocked)
        {
            StatusPill.Draw(Severity.Warning, blocked);
            return;
        }

        if (plan.Recommendation is { Precision: SetupAdvisor.Q4F16 })
        {
            Ui.MutedText("The expressive voice runs on your graphics card, using a compact build of the model.");
        }

        if (plan.BytesToDownload == 0)
        {
            Ui.MutedText("Everything for this option is already downloaded.");
            return;
        }

        var free = inputs.FreeDiskBytes == long.MaxValue ? string.Empty : $" {Labels.FileSize(inputs.FreeDiskBytes)} free on your drive.";
        Ui.MutedText($"Downloads {Labels.FileSize(plan.BytesToDownload)}, once.{free}");
    }

    private void DrawInterrupted()
    {
        using var card = Card.Begin("home-setup-interrupted");
        if (!card.Visible)
        {
            return;
        }

        Title(FontAwesomeIcon.Redo, "Setup was interrupted");
        StepLabel(this.flow);
        Ui.MutedText("Pick up where you left off. Files already downloaded are kept.");
        Ui.Gap(Ui.Space2);
        if (ActionButton.Primary("Continue setup##home-setup-resume"))
        {
            this.flow.Resume();
        }

        ImGui.SameLine();
        if (ActionButton.Secondary("Start over##home-setup-restart"))
        {
            this.flow.StartOver();
        }
    }

    private void DrawDownload()
    {
        using var card = Card.Begin("home-setup-download-" + (this.flow.Failure is null ? "run" : "fail"));
        if (!card.Visible)
        {
            return;
        }

        Title(FontAwesomeIcon.Download, "Downloading your voices");
        StepLabel(this.flow);
        Ui.Gap(Ui.Space2);
        if (this.flow.Failure is { } failure)
        {
            this.DrawFailure("The download stopped", failure.Message, "Retry download");
            return;
        }

        var downloads = this.ctx.Downloads;
        var caption = downloads.Active ? downloads.Caption : "Starting…";
        ProgressRow.Draw(
            caption,
            downloads.Active && downloads.Total > 0 ? (float)downloads.Fraction : null,
            reduceMotion: this.ctx.Config.ReduceMotion);
        Ui.Gap(Ui.Space2);
        this.DrawCancel();
    }

    private void DrawWarmUp()
    {
        using var card = Card.Begin("home-setup-warm-" + (this.flow.Failure is null ? "run" : "fail"));
        if (!card.Visible)
        {
            return;
        }

        Title(FontAwesomeIcon.Cog, "Getting your voices ready");
        StepLabel(this.flow);
        Ui.Gap(Ui.Space2);
        if (this.flow.Failure is { } failure)
        {
            this.DrawFailure("The voices could not load", failure.Message, "Try again");
            return;
        }

        ProgressRow.Draw(
            $"Loading {this.ctx.ActiveEngine.DisplayName}…",
            null,
            "The first load can take a few seconds.",
            this.ctx.Config.ReduceMotion);
        Ui.Gap(Ui.Space2);
        this.DrawCancel();
    }

    private void DrawSample()
    {
        var playing = this.ctx.Preview.Key == SampleKey && this.ctx.Preview.State is PreviewState.Synthesizing or PreviewState.Playing;
        using var card = Card.Begin("home-setup-sample-" + (this.flow.Failure is not null ? "fail" : playing ? "play" : "idle"));
        if (!card.Visible)
        {
            return;
        }

        Title(FontAwesomeIcon.VolumeUp, "Hear a sample");
        StepLabel(this.flow);
        Ui.Gap(Ui.Space2);
        if (this.flow.Failure is { } failure)
        {
            this.DrawFailure("The sample did not play", failure.Message, "Try again", skipLabel: "Skip the sample");
            return;
        }

        ImGui.TextWrapped($"\"{SetupFlowModel.SampleLine}\"");
        Ui.Gap(Ui.Space2);
        if (playing)
        {
            ProgressRow.Draw("Playing the sample…", null, reduceMotion: this.ctx.Config.ReduceMotion);
            return;
        }

        if (ActionButton.Primary("Play sample##home-setup-play"))
        {
            this.flow.RequestSample();
        }

        ImGui.SameLine();
        if (ActionButton.Secondary("Skip##home-setup-skip-sample"))
        {
            this.flow.SkipSample();
        }
    }

    private void DrawDone()
    {
        using var card = Card.Begin("home-setup-done");
        if (!card.Visible)
        {
            return;
        }

        Theme.IconText(FontAwesomeIcon.CheckCircle, Theme.Ok);
        ImGui.SameLine(0f, Ui.Em(Ui.Space2));
        ImGui.TextUnformatted("You are all set");
        Ui.MutedText(
            "Talk to any NPC and they get a voice that fits their race, gender and age, and keeps it. " +
            "Change any voice on the Voices tab.");
        Ui.Gap(Ui.Space2);
        if (ActionButton.Primary("Got it##home-setup-done"))
        {
            this.before = null;
            this.flow.Acknowledge();
        }

        ImGui.SameLine();
        if (ActionButton.Secondary("Browse voices##home-setup-voices"))
        {
            this.before = null;
            this.flow.Acknowledge();
            this.ctx.Nav.OpenAt(NavTab.Voices);
        }
    }

    private void DrawFailure(string title, string message, string retryLabel, string? skipLabel = null)
    {
        StatusPill.Draw(Severity.Error, title);
        Ui.MutedText(message);
        Ui.Gap(Ui.Space2);
        if (ActionButton.Primary(retryLabel + "##home-setup-retry"))
        {
            this.Retry();
        }

        ImGui.SameLine();
        if (skipLabel is not null)
        {
            if (ActionButton.Secondary(skipLabel + "##home-setup-skip-failed"))
            {
                this.flow.SkipSample();
            }
        }
        else if (ActionButton.Secondary("Cancel##home-setup-cancel-failed"))
        {
            this.Cancel();
        }
    }

    private void Retry()
    {
        var step = this.flow.Failure?.Step;
        this.ctx.Status.Resolve(DownloadQueueModel.FailureKey);
        this.ctx.Status.Resolve(EngineSessionModel.FailureKey);
        this.ctx.Status.Resolve(PreviewController.FailureKey);
        if (step == SetupStep.WarmUp)
        {
            this.warmUp = null;
        }

        this.flow.Retry();
    }

    private void DrawCancel()
    {
        if (ActionButton.Secondary("Cancel##home-setup-cancel"))
        {
            this.Cancel();
        }
    }
}
