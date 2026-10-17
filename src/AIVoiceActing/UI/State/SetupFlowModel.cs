namespace AIVoiceActing.UI.State;

using AIVoiceActing.Domain;

/// <summary>The inline "Set up recommended" flow. The numeric values are what is persisted as <c>OnboardingStep</c>.</summary>
public enum SetupStep
{
    Recommend = 0,
    Download = 1,
    WarmUp = 2,
    PlaySample = 3,
    Done = 4,
}

/// <summary>Required download of one engine: total size and what is still missing (partial files already subtracted).</summary>
public sealed record EngineDownload(string EngineId, string DisplayName, long TotalBytes, long MissingBytes)
{
    public bool Installed => this.MissingBytes <= 0;
}

/// <summary>What the recommendation is computed from.</summary>
/// <param name="CurrentTier">The tier the saved settings already describe; null for a custom setup.</param>
/// <param name="PrecisionGroups">
/// Status of the optional precision groups (the Turbo q4f16 build); null when the caller does not track them,
/// which counts them as already present and free.
/// </param>
public sealed record SetupInputs(
    bool HasUsableGpu,
    long FreeDiskBytes,
    IReadOnlyList<EngineDownload> Engines,
    QualityTier? CurrentTier = null,
    IReadOnlyList<GroupStatus>? PrecisionGroups = null)
{
    private static readonly IReadOnlyDictionary<string, string> GroupOfEngine = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [SetupAdvisor.KokoroEngine] = SetupAdvisor.KokoroGroup,
        [SetupAdvisor.TurboEngine] = SetupAdvisor.TurboGroup,
    };

    /// <summary>The same facts in the advisor's terms: one group per engine, plus the precision groups.</summary>
    public SetupAdvisorInput ToAdvisorInput()
    {
        var groups = new List<GroupStatus>();
        foreach (var engine in this.Engines)
        {
            if (GroupOfEngine.TryGetValue(engine.EngineId, out var group))
            {
                groups.Add(new GroupStatus(group, engine.TotalBytes, engine.MissingBytes));
            }
        }

        groups.AddRange(this.PrecisionGroups ?? [new GroupStatus(SetupAdvisor.TurboQ4F16Group, 0, 0)]);
        return new SetupAdvisorInput(this.HasUsableGpu, this.FreeDiskBytes, groups, this.CurrentTier);
    }
}

/// <summary>A tier with the downloads it needs. <see cref="Blocked"/> is the plain reason it cannot start; null when it can.</summary>
/// <param name="Recommendation">The advisor's full answer: engines, precision, download groups and bytes.</param>
public sealed record SetupPlan(
    QualityTier Tier,
    IReadOnlyList<EngineDownload> ToDownload,
    long BytesToDownload,
    string? Blocked,
    SetupRecommendation? Recommendation = null);

/// <summary>A step that failed. The flow stays on <see cref="Step"/> so Retry resumes from there.</summary>
public sealed record SetupFailure(SetupStep Step, string Message);

/// <summary>What the page saw this frame; the flow decides what it means.</summary>
public sealed record SetupObservation(
    bool DownloadRunning,
    bool AssetsInstalled,
    string? DownloadError,
    bool WarmUpRunning,
    bool WarmUpFinished,
    string? EngineError,
    PreviewState Sample,
    string? SampleError);

/// <summary>
/// Pure brain of the inline setup flow: Recommend, Download, Warm up, Play sample, Done. It picks
/// the quality tier and its downloads from the inputs, then advances only on what the page
/// observes. The page owns the side effects and asks the flow when to start them
/// (<see cref="TakeStartDownload"/>, <see cref="TakeStartWarmUp"/>, <see cref="TakeStartSample"/>).
/// A failure keeps the step; <see cref="Retry"/> repeats it. The step is persisted through
/// <c>onStep</c>, so a flow interrupted by a restart waits at <see cref="AwaitingResume"/> for a
/// click instead of downloading on its own.
/// </summary>
public sealed class SetupFlowModel
{
    /// <summary>Free space kept back on top of the download itself.</summary>
    public const long DiskHeadroomBytes = SetupAdvisor.DiskHeadroomBytes;

    /// <summary>The line the sample step speaks.</summary>
    public const string SampleLine = "Welcome to Eorzea. Your voices are ready.";

    /// <summary>The plain step count shown to the player: choose, download, get ready, listen.</summary>
    public const int StepCount = 4;

    private readonly Action<SetupStep> onStep;
    private readonly Action onCompleted;
    private bool completed;
    private bool wantsDownload;
    private bool wantsWarmUp;
    private bool wantsSample;
    private bool seenDownloadRunning;
    private bool sampleRequested;
    private bool sampleActive;

    public SetupFlowModel(int savedStep, bool completed, Action<SetupStep>? onStep = null, Action? onCompleted = null)
    {
        this.onStep = onStep ?? (_ => { });
        this.onCompleted = onCompleted ?? (() => { });
        this.completed = completed;
        var saved = Enum.IsDefined(typeof(SetupStep), savedStep) ? (SetupStep)savedStep : SetupStep.Recommend;
        if (!completed && saved is SetupStep.Download or SetupStep.WarmUp or SetupStep.PlaySample)
        {
            this.Step = saved;
            this.AwaitingResume = true;
        }
    }

    public SetupStep Step { get; private set; } = SetupStep.Recommend;

    /// <summary>True when the flow was restored mid-way; nothing runs until <see cref="Resume"/>.</summary>
    public bool AwaitingResume { get; private set; }

    public bool Customizing { get; private set; }

    /// <summary>The tier the player picked under Customize; null while the recommendation stands.</summary>
    public QualityTier? Chosen { get; private set; }

    public SetupFailure? Failure { get; private set; }

    /// <summary>A one-time note for the Recommend step, for example after a cancelled download.</summary>
    public string? Notice { get; private set; }

    public bool InProgress => this.Step is SetupStep.Download or SetupStep.WarmUp or SetupStep.PlaySample;

    /// <summary>1-based number of the current step out of <see cref="StepCount"/>.</summary>
    public int StepNumber => this.Step switch
    {
        SetupStep.Recommend => 1,
        SetupStep.Download => 2,
        SetupStep.WarmUp => 3,
        _ => 4,
    };

    /// <summary>
    /// True while the card belongs on Home: the engine cannot speak yet, setup was never finished,
    /// a run is under way, or the Done card is waiting for "Got it".
    /// </summary>
    public bool Visible(bool engineReady) => this.Step == SetupStep.Done
        || this.InProgress
        || !engineReady
        || !this.completed;

    // ---------------------------------------------------------------- recommendation

    /// <summary>The plan for <paramref name="tier"/>: which engines still need downloading and whether it can start. Sizes and the disk check come from <see cref="SetupAdvisor"/>.</summary>
    public static SetupPlan PlanFor(QualityTier tier, SetupInputs inputs)
    {
        var advice = SetupAdvisor.Plan(tier, inputs.ToAdvisorInput());
        var toDownload = new List<EngineDownload>();
        foreach (var id in QualityTierModel.EnginesFor(tier))
        {
            var engine = inputs.Engines.FirstOrDefault(e => string.Equals(e.EngineId, id, StringComparison.OrdinalIgnoreCase));
            if (engine is { Installed: false })
            {
                toDownload.Add(engine);
            }
        }

        return new SetupPlan(tier, toDownload, advice.BytesToDownload, advice.Blocked, advice);
    }

    /// <summary>
    /// The tier to suggest. A tier the player already runs and has fully downloaded stays; with a usable
    /// graphics card and room on disk it is Balanced; everything else is Fast.
    /// </summary>
    public static SetupPlan Recommend(SetupInputs inputs) =>
        PlanFor(SetupAdvisor.Recommend(inputs.ToAdvisorInput()).Tier, inputs);

    /// <summary>The tier the primary button will set up: the player's pick, else the recommendation.</summary>
    public QualityTier EffectiveTier(SetupPlan recommended) => this.Chosen ?? recommended.Tier;

    public void ToggleCustomize()
    {
        this.Customizing = !this.Customizing;
        if (!this.Customizing)
        {
            this.Chosen = null;
        }
    }

    /// <summary>Records a pick under Customize. False, changing nothing, when the tier is unavailable here.</summary>
    public bool Choose(QualityTier tier, SetupInputs inputs)
    {
        if (!QualityTierModel.IsAvailable(tier, inputs.HasUsableGpu))
        {
            return false;
        }

        this.Chosen = tier;
        return true;
    }

    // ---------------------------------------------------------------- transitions

    /// <summary>
    /// Starts the plan: downloads first, or straight to warming up when everything is on disk.
    /// False when the plan is blocked or the flow is not on the Recommend step.
    /// </summary>
    public bool Begin(SetupPlan plan)
    {
        if (this.Step != SetupStep.Recommend || plan.Blocked is not null)
        {
            return false;
        }

        this.Notice = null;
        this.Failure = null;
        this.Customizing = false;
        this.Chosen = null;
        if (plan.ToDownload.Count > 0)
        {
            this.Go(SetupStep.Download);
            this.wantsDownload = true;
        }
        else
        {
            this.Go(SetupStep.WarmUp);
            this.wantsWarmUp = true;
        }

        return true;
    }

    /// <summary>Continue an interrupted flow from the saved step.</summary>
    public void Resume()
    {
        if (!this.AwaitingResume)
        {
            return;
        }

        this.AwaitingResume = false;
        this.StartStep();
    }

    /// <summary>Drop the interrupted flow and go back to the choice.</summary>
    public void StartOver()
    {
        this.AwaitingResume = false;
        this.Failure = null;
        this.ResetWork();
        this.Go(SetupStep.Recommend);
    }

    /// <summary>Stop during download or warm-up and return to the choice; downloaded files stay on disk.</summary>
    public void Cancel()
    {
        if (this.Step is not (SetupStep.Download or SetupStep.WarmUp))
        {
            return;
        }

        this.Failure = null;
        this.ResetWork();
        this.Notice = "Setup stopped. Files already downloaded are kept, so the next try picks up where this one left off.";
        this.Go(SetupStep.Recommend);
    }

    /// <summary>Repeat the failed step from where it stopped.</summary>
    public void Retry()
    {
        if (this.Failure is null)
        {
            return;
        }

        this.Failure = null;
        this.StartStep();
    }

    /// <summary>Marks the current step as failed; the page uses this when it could not even start the work.</summary>
    public void Fail(string message)
    {
        if (this.InProgress)
        {
            this.Failure = new SetupFailure(this.Step, message);
        }
    }

    /// <summary>The player pressed Play sample.</summary>
    public void RequestSample()
    {
        if (this.Step == SetupStep.PlaySample && this.Failure is null && !this.AwaitingResume)
        {
            this.sampleRequested = true;
            this.sampleActive = false;
            this.wantsSample = true;
        }
    }

    /// <summary>Finish without hearing the sample.</summary>
    public void SkipSample()
    {
        if (this.Step == SetupStep.PlaySample)
        {
            this.Finish();
        }
    }

    /// <summary>"Not now": the engine already works, so mark setup done without changing anything.</summary>
    public void NotNow()
    {
        if (this.Step != SetupStep.Recommend)
        {
            return;
        }

        this.completed = true;
        this.Notice = null;
        this.Customizing = false;
        this.Chosen = null;
        this.onCompleted();
    }

    /// <summary>"Got it" on the Done card: hide it and reset for next time.</summary>
    public void Acknowledge()
    {
        if (this.Step != SetupStep.Done)
        {
            return;
        }

        this.Go(SetupStep.Recommend);
    }

    public bool TakeStartDownload() => Take(ref this.wantsDownload);

    public bool TakeStartWarmUp() => Take(ref this.wantsWarmUp);

    public bool TakeStartSample() => Take(ref this.wantsSample);

    /// <summary>Advances the flow from what the page observed this frame. Call once per frame, before the Take methods.</summary>
    public void Observe(SetupObservation o)
    {
        if (this.Failure is not null || this.AwaitingResume)
        {
            return;
        }

        switch (this.Step)
        {
            case SetupStep.Download:
                this.ObserveDownload(o);
                break;
            case SetupStep.WarmUp:
                this.ObserveWarmUp(o);
                break;
            case SetupStep.PlaySample:
                this.ObserveSample(o);
                break;
        }
    }

    private void ObserveDownload(SetupObservation o)
    {
        if (o.DownloadRunning)
        {
            this.seenDownloadRunning = true;
            return;
        }

        if (this.wantsDownload)
        {
            return; // The page has not started it yet.
        }

        if (o.AssetsInstalled)
        {
            this.Go(SetupStep.WarmUp);
            this.wantsWarmUp = true;
        }
        else if (this.seenDownloadRunning || o.DownloadError is not null)
        {
            this.Failure = new SetupFailure(
                SetupStep.Download,
                o.DownloadError ?? "The download stopped before it finished.");
        }
    }

    private void ObserveWarmUp(SetupObservation o)
    {
        if (this.wantsWarmUp || o.WarmUpRunning || !o.WarmUpFinished)
        {
            return;
        }

        if (o.EngineError is not null)
        {
            this.Failure = new SetupFailure(SetupStep.WarmUp, o.EngineError);
        }
        else
        {
            this.Go(SetupStep.PlaySample);
        }
    }

    private void ObserveSample(SetupObservation o)
    {
        if (!this.sampleRequested || this.wantsSample)
        {
            return;
        }

        switch (o.Sample)
        {
            case PreviewState.Synthesizing:
            case PreviewState.Playing:
                this.sampleActive = true;
                break;
            case PreviewState.Error:
                this.Failure = new SetupFailure(SetupStep.PlaySample, o.SampleError ?? "The sample could not be played.");
                break;
            default:
                if (this.sampleActive)
                {
                    this.Finish();
                }

                break;
        }
    }

    private void Finish()
    {
        this.completed = true;
        this.Go(SetupStep.Done);
        this.onCompleted();
    }

    private void StartStep()
    {
        this.ResetWork();
        switch (this.Step)
        {
            case SetupStep.Download:
                this.wantsDownload = true;
                break;
            case SetupStep.WarmUp:
                this.wantsWarmUp = true;
                break;
        }
    }

    private void ResetWork()
    {
        this.wantsDownload = false;
        this.wantsWarmUp = false;
        this.wantsSample = false;
        this.seenDownloadRunning = false;
        this.sampleRequested = false;
        this.sampleActive = false;
    }

    private void Go(SetupStep step)
    {
        this.Step = step;
        this.onStep(step);
    }

    private static bool Take(ref bool flag)
    {
        var was = flag;
        flag = false;
        return was;
    }
}
