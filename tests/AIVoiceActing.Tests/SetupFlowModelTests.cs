namespace AIVoiceActing.Tests;

using AIVoiceActing.UI.State;
using Xunit;

/// <summary>
/// The inline setup flow: recommendation from the machine, step progression driven by
/// observations, cancel and resume, and failures that keep the step.
/// </summary>
public sealed class SetupFlowModelTests
{
    private const long Mb = 1024L * 1024;
    private const long Gb = 1024L * Mb;

    private static readonly EngineDownload KokoroMissing = new("kokoro", "Kokoro", 326 * Mb, 326 * Mb);
    private static readonly EngineDownload KokoroInstalled = new("kokoro", "Kokoro", 326 * Mb, 0);
    private static readonly EngineDownload TurboMissing = new("chatterbox-turbo", "Chatterbox Turbo", 3300 * Mb, 3300 * Mb);
    private static readonly EngineDownload TurboInstalled = new("chatterbox-turbo", "Chatterbox Turbo", 3300 * Mb, 0);

    private static SetupInputs Inputs(
        bool gpu = false,
        long free = 100 * Gb,
        EngineDownload? kokoro = null,
        EngineDownload? turbo = null,
        QualityTier? current = null) =>
        new(gpu, free, [kokoro ?? KokoroMissing, turbo ?? TurboMissing], current);

    private static SetupObservation Obs(
        bool downloading = false,
        bool installed = false,
        string? downloadError = null,
        bool warmRunning = false,
        bool warmDone = false,
        string? engineError = null,
        PreviewState sample = PreviewState.Idle,
        string? sampleError = null) =>
        new(downloading, installed, downloadError, warmRunning, warmDone, engineError, sample, sampleError);

    private static (SetupFlowModel Flow, List<SetupStep> Steps, List<string> Completed) NewFlow(int saved = 0, bool completed = false)
    {
        var steps = new List<SetupStep>();
        var done = new List<string>();
        return (new SetupFlowModel(saved, completed, steps.Add, () => done.Add("done")), steps, done);
    }

    // ---------------------------------------------------------------- recommendation

    [Fact]
    public void No_gpu_information_recommends_Fast()
    {
        var plan = SetupFlowModel.Recommend(Inputs(gpu: false));

        Assert.Equal(QualityTier.Fast, plan.Tier);
        Assert.Null(plan.Blocked);
        Assert.Equal("kokoro", Assert.Single(plan.ToDownload).EngineId);
        Assert.Equal(326 * Mb, plan.BytesToDownload);
    }

    [Fact]
    public void A_usable_gpu_with_room_recommends_Balanced_and_both_downloads()
    {
        var plan = SetupFlowModel.Recommend(Inputs(gpu: true));

        Assert.Equal(QualityTier.Balanced, plan.Tier);
        Assert.Equal(["kokoro", "chatterbox-turbo"], plan.ToDownload.Select(e => e.EngineId));
        Assert.Equal(3626 * Mb, plan.BytesToDownload);
    }

    [Fact]
    public void A_gpu_but_too_little_disk_for_turbo_falls_back_to_Fast()
    {
        var plan = SetupFlowModel.Recommend(Inputs(gpu: true, free: 2 * Gb));

        Assert.Equal(QualityTier.Fast, plan.Tier);
        Assert.Null(plan.Blocked);
    }

    [Fact]
    public void Downloaded_groups_are_not_counted_again()
    {
        var plan = SetupFlowModel.Recommend(Inputs(gpu: true, kokoro: KokoroInstalled));

        Assert.Equal(QualityTier.Balanced, plan.Tier);
        Assert.Equal("chatterbox-turbo", Assert.Single(plan.ToDownload).EngineId);
        Assert.Equal(3300 * Mb, plan.BytesToDownload);
    }

    [Fact]
    public void Already_downloaded_turbo_needs_no_extra_disk_space()
    {
        var plan = SetupFlowModel.Recommend(Inputs(gpu: true, free: 100 * Mb, kokoro: KokoroInstalled, turbo: TurboInstalled));

        Assert.Equal(QualityTier.Balanced, plan.Tier);
        Assert.Empty(plan.ToDownload);
        Assert.Null(plan.Blocked);
    }

    [Fact]
    public void A_tier_the_player_already_runs_and_has_downloaded_is_kept()
    {
        var inputs = Inputs(gpu: false, kokoro: KokoroInstalled, turbo: TurboInstalled, current: QualityTier.Balanced);

        Assert.Equal(QualityTier.Balanced, SetupFlowModel.Recommend(inputs).Tier);
    }

    [Fact]
    public void A_current_tier_that_is_not_downloaded_does_not_override_the_recommendation()
    {
        var inputs = Inputs(gpu: false, current: QualityTier.MostExpressive);

        Assert.Equal(QualityTier.Fast, SetupFlowModel.Recommend(inputs).Tier);
    }

    [Fact]
    public void Too_little_disk_for_anything_blocks_the_plan_with_a_reason()
    {
        var plan = SetupFlowModel.Recommend(Inputs(free: 100 * Mb));

        Assert.Equal(QualityTier.Fast, plan.Tier);
        Assert.NotNull(plan.Blocked);
        Assert.Contains("free", plan.Blocked);
    }

    [Fact]
    public void Most_expressive_is_blocked_without_a_gpu()
    {
        var plan = SetupFlowModel.PlanFor(QualityTier.MostExpressive, Inputs(gpu: false));

        Assert.NotNull(plan.Blocked);
        Assert.Contains("graphics card", plan.Blocked);
    }

    [Fact]
    public void Most_expressive_downloads_turbo_only()
    {
        var plan = SetupFlowModel.PlanFor(QualityTier.MostExpressive, Inputs(gpu: true));

        Assert.Null(plan.Blocked);
        Assert.Equal("chatterbox-turbo", Assert.Single(plan.ToDownload).EngineId);
    }

    [Fact]
    public void Choosing_an_unavailable_tier_is_refused_and_keeps_the_recommendation()
    {
        var (flow, _, _) = NewFlow();
        var inputs = Inputs(gpu: false);

        Assert.False(flow.Choose(QualityTier.MostExpressive, inputs));
        Assert.Null(flow.Chosen);
        Assert.True(flow.Choose(QualityTier.Balanced, inputs));
        Assert.Equal(QualityTier.Balanced, flow.EffectiveTier(SetupFlowModel.Recommend(inputs)));
    }

    [Fact]
    public void Closing_Customize_drops_the_pick()
    {
        var (flow, _, _) = NewFlow();
        flow.ToggleCustomize();
        flow.Choose(QualityTier.Balanced, Inputs());

        flow.ToggleCustomize();

        Assert.False(flow.Customizing);
        Assert.Null(flow.Chosen);
    }

    // ---------------------------------------------------------------- the happy path

    [Fact]
    public void Walks_through_every_step_and_persists_each_one()
    {
        var (flow, steps, completed) = NewFlow();
        Assert.True(flow.Begin(SetupFlowModel.Recommend(Inputs())));
        Assert.Equal(SetupStep.Download, flow.Step);
        Assert.True(flow.TakeStartDownload());
        Assert.False(flow.TakeStartDownload());

        flow.Observe(Obs(downloading: true));
        flow.Observe(Obs(installed: true));
        Assert.Equal(SetupStep.WarmUp, flow.Step);
        Assert.True(flow.TakeStartWarmUp());

        flow.Observe(Obs(installed: true, warmRunning: true));
        Assert.Equal(SetupStep.WarmUp, flow.Step);
        flow.Observe(Obs(installed: true, warmDone: true));
        Assert.Equal(SetupStep.PlaySample, flow.Step);

        flow.RequestSample();
        Assert.True(flow.TakeStartSample());
        flow.Observe(Obs(sample: PreviewState.Synthesizing));
        flow.Observe(Obs(sample: PreviewState.Playing));
        Assert.Equal(SetupStep.PlaySample, flow.Step);
        flow.Observe(Obs(sample: PreviewState.Idle));

        Assert.Equal(SetupStep.Done, flow.Step);
        Assert.Equal([SetupStep.Download, SetupStep.WarmUp, SetupStep.PlaySample, SetupStep.Done], steps);
        Assert.Single(completed);
    }

    [Fact]
    public void Nothing_to_download_goes_straight_to_warming_up()
    {
        var (flow, steps, _) = NewFlow();
        var plan = SetupFlowModel.Recommend(Inputs(kokoro: KokoroInstalled));

        Assert.True(flow.Begin(plan));

        Assert.Equal(SetupStep.WarmUp, flow.Step);
        Assert.True(flow.TakeStartWarmUp());
        Assert.Equal([SetupStep.WarmUp], steps);
    }

    [Fact]
    public void A_blocked_plan_does_not_begin()
    {
        var (flow, steps, _) = NewFlow();

        Assert.False(flow.Begin(SetupFlowModel.Recommend(Inputs(free: 10 * Mb))));

        Assert.Equal(SetupStep.Recommend, flow.Step);
        Assert.Empty(steps);
    }

    [Fact]
    public void The_sample_is_not_judged_until_the_page_has_started_it()
    {
        var (flow, _, _) = NewFlow();
        flow.Begin(SetupFlowModel.Recommend(Inputs(kokoro: KokoroInstalled)));
        flow.TakeStartWarmUp();
        flow.Observe(Obs(warmDone: true));

        flow.RequestSample();
        flow.Observe(Obs(sample: PreviewState.Idle));

        Assert.Equal(SetupStep.PlaySample, flow.Step);
    }

    [Fact]
    public void Skipping_the_sample_finishes_the_flow()
    {
        var (flow, _, completed) = NewFlow(saved: 3);
        flow.Resume();

        flow.SkipSample();

        Assert.Equal(SetupStep.Done, flow.Step);
        Assert.Single(completed);
    }

    [Fact]
    public void Got_it_hides_the_done_card_and_resets_the_step()
    {
        var (flow, steps, _) = NewFlow(saved: 3);
        flow.Resume();
        flow.SkipSample();
        Assert.True(flow.Visible(engineReady: true));

        flow.Acknowledge();

        Assert.Equal(SetupStep.Recommend, flow.Step);
        Assert.False(flow.Visible(engineReady: true));
        Assert.Equal(SetupStep.Recommend, steps[^1]);
    }

    [Fact]
    public void After_completion_the_card_returns_only_if_the_engine_stops_being_ready()
    {
        var (flow, _, _) = NewFlow(completed: true);

        Assert.False(flow.Visible(engineReady: true));
        Assert.True(flow.Visible(engineReady: false));
    }

    [Fact]
    public void An_unfinished_setup_stays_visible_even_when_the_engine_is_ready()
    {
        var (flow, _, _) = NewFlow(completed: false);

        Assert.True(flow.Visible(engineReady: true));
    }

    [Fact]
    public void Not_now_completes_setup_without_changing_the_step()
    {
        var (flow, steps, completed) = NewFlow();

        flow.NotNow();

        Assert.Equal(SetupStep.Recommend, flow.Step);
        Assert.Empty(steps);
        Assert.Single(completed);
        Assert.False(flow.Visible(engineReady: true));
    }

    // ---------------------------------------------------------------- cancel

    [Fact]
    public void Cancel_during_download_returns_to_the_choice_with_a_note_and_persists_it()
    {
        var (flow, steps, completed) = NewFlow();
        flow.Begin(SetupFlowModel.Recommend(Inputs()));
        flow.TakeStartDownload();
        flow.Observe(Obs(downloading: true));

        flow.Cancel();

        Assert.Equal(SetupStep.Recommend, flow.Step);
        Assert.NotNull(flow.Notice);
        Assert.Equal(SetupStep.Recommend, steps[^1]);
        Assert.Empty(completed);

        // The cancelled queue winding down must not read as a failure on the choice step.
        flow.Observe(Obs(downloading: false));
        Assert.Null(flow.Failure);
    }

    [Fact]
    public void Cancel_does_nothing_on_the_sample_step()
    {
        var (flow, _, _) = NewFlow(saved: 3);
        flow.Resume();

        flow.Cancel();

        Assert.Equal(SetupStep.PlaySample, flow.Step);
    }

    [Fact]
    public void Beginning_again_after_a_cancel_clears_the_note_and_restarts_the_download()
    {
        var (flow, _, _) = NewFlow();
        var plan = SetupFlowModel.Recommend(Inputs());
        flow.Begin(plan);
        flow.TakeStartDownload();
        flow.Cancel();

        Assert.True(flow.Begin(plan));

        Assert.Null(flow.Notice);
        Assert.True(flow.TakeStartDownload());
    }

    // ---------------------------------------------------------------- resume

    [Theory]
    [InlineData(1, SetupStep.Download)]
    [InlineData(2, SetupStep.WarmUp)]
    [InlineData(3, SetupStep.PlaySample)]
    public void A_saved_mid_flow_step_restores_and_waits_for_a_click(int saved, SetupStep expected)
    {
        var (flow, _, _) = NewFlow(saved);

        Assert.Equal(expected, flow.Step);
        Assert.True(flow.AwaitingResume);
        Assert.False(flow.TakeStartDownload());
        Assert.False(flow.TakeStartWarmUp());

        // Even a stray observation does nothing until the player resumes.
        flow.Observe(Obs(installed: true));
        Assert.Equal(expected, flow.Step);
    }

    [Fact]
    public void Resuming_the_download_step_restarts_the_download()
    {
        var (flow, steps, _) = NewFlow(saved: 1);

        flow.Resume();

        Assert.False(flow.AwaitingResume);
        Assert.True(flow.TakeStartDownload());
        Assert.Empty(steps);
    }

    [Fact]
    public void Resuming_the_warm_up_step_restarts_the_warm_up()
    {
        var (flow, _, _) = NewFlow(saved: 2);

        flow.Resume();

        Assert.True(flow.TakeStartWarmUp());
    }

    [Fact]
    public void Start_over_discards_the_saved_step()
    {
        var (flow, steps, _) = NewFlow(saved: 2);

        flow.StartOver();

        Assert.Equal(SetupStep.Recommend, flow.Step);
        Assert.False(flow.AwaitingResume);
        Assert.Equal([SetupStep.Recommend], steps);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(99)]
    public void Saved_steps_that_are_not_mid_flow_start_at_the_choice(int saved)
    {
        var (flow, _, _) = NewFlow(saved);

        Assert.Equal(SetupStep.Recommend, flow.Step);
        Assert.False(flow.AwaitingResume);
    }

    [Fact]
    public void A_completed_setup_ignores_a_stale_saved_step()
    {
        var (flow, _, _) = NewFlow(saved: 2, completed: true);

        Assert.Equal(SetupStep.Recommend, flow.Step);
        Assert.False(flow.AwaitingResume);
    }

    // ---------------------------------------------------------------- failures keep position

    [Fact]
    public void A_download_error_keeps_the_download_step_and_Retry_restarts_it()
    {
        var (flow, steps, _) = NewFlow();
        flow.Begin(SetupFlowModel.Recommend(Inputs()));
        flow.TakeStartDownload();
        flow.Observe(Obs(downloading: true));

        flow.Observe(Obs(downloadError: "Download of \"Kokoro\" failed: disk full"));

        Assert.Equal(SetupStep.Download, flow.Step);
        var failure = Assert.IsType<SetupFailure>(flow.Failure);
        Assert.Equal(SetupStep.Download, failure.Step);
        Assert.Contains("disk full", failure.Message);
        Assert.Equal([SetupStep.Download], steps);

        flow.Retry();

        Assert.Null(flow.Failure);
        Assert.Equal(SetupStep.Download, flow.Step);
        Assert.True(flow.TakeStartDownload());
    }

    [Fact]
    public void A_queue_that_stops_without_the_files_is_a_failure_with_a_generic_message()
    {
        var (flow, _, _) = NewFlow();
        flow.Begin(SetupFlowModel.Recommend(Inputs()));
        flow.TakeStartDownload();
        flow.Observe(Obs(downloading: true));

        flow.Observe(Obs(downloading: false, installed: false));

        Assert.Equal(SetupStep.Download, flow.Failure!.Step);
        Assert.False(string.IsNullOrWhiteSpace(flow.Failure.Message));
    }

    [Fact]
    public void A_failed_flow_ignores_later_observations_until_Retry()
    {
        var (flow, _, _) = NewFlow();
        flow.Begin(SetupFlowModel.Recommend(Inputs()));
        flow.TakeStartDownload();
        flow.Observe(Obs(downloadError: "offline"));

        flow.Observe(Obs(installed: true));

        Assert.Equal(SetupStep.Download, flow.Step);
        Assert.NotNull(flow.Failure);
    }

    [Fact]
    public void An_engine_error_while_warming_up_keeps_the_warm_up_step()
    {
        var (flow, _, _) = NewFlow();
        flow.Begin(SetupFlowModel.Recommend(Inputs(kokoro: KokoroInstalled)));
        flow.TakeStartWarmUp();

        flow.Observe(Obs(warmDone: true, engineError: "The voice file is damaged."));

        Assert.Equal(SetupStep.WarmUp, flow.Step);
        Assert.Equal(SetupStep.WarmUp, flow.Failure!.Step);

        flow.Retry();
        Assert.True(flow.TakeStartWarmUp());
    }

    [Fact]
    public void A_sample_error_keeps_the_sample_step_and_Retry_allows_another_press()
    {
        var (flow, _, _) = NewFlow(saved: 3);
        flow.Resume();
        flow.RequestSample();
        flow.TakeStartSample();

        flow.Observe(Obs(sample: PreviewState.Error, sampleError: "No audio device."));

        Assert.Equal(SetupStep.PlaySample, flow.Step);
        Assert.Equal("No audio device.", flow.Failure!.Message);

        flow.Retry();
        flow.RequestSample();
        Assert.True(flow.TakeStartSample());
    }

    [Fact]
    public void The_page_can_fail_a_step_it_could_not_start()
    {
        var (flow, _, _) = NewFlow();
        flow.Begin(SetupFlowModel.Recommend(Inputs()));
        flow.TakeStartDownload();

        flow.Fail("Could not start the download.");

        Assert.Equal(SetupStep.Download, flow.Failure!.Step);
    }

    [Fact]
    public void Fail_is_ignored_on_the_choice_step()
    {
        var (flow, _, _) = NewFlow();

        flow.Fail("anything");

        Assert.Null(flow.Failure);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 4)]
    public void Step_numbers_count_the_four_stages(int saved, int expected)
    {
        var (flow, _, _) = NewFlow(saved);

        Assert.Equal(expected, flow.StepNumber);
    }
}
