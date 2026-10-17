namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// Where the voices run: Automatic, Graphics card or CPU, with the card the system reports, the
/// Performance / Low power preference when there is more than one, and the background load. A problem
/// (fell back to the processor, failed to start, graphics card turned off after a crash) appears as a
/// banner with its actions: Retry on GPU, Use CPU, Copy details. All reasons are the engine's own words.
/// </summary>
internal sealed class ProcessorCard(UiContext ctx, ProcessorActions actions)
{
    private Task? recheck;

    public void Draw()
    {
        var device = ctx.Config.Device;
        var gpu = SettingsParts.GpuEngine(ctx);
        var report = ctx.DeviceProbe?.LastReport;
        var view = DeviceViewModel.Describe(new DeviceViewInput(
            ModeOf(device.Mode),
            report,
            device.GpuDisabledByCrashGuard,
            gpu?.Synth.Status,
            actions.Retrying));

        SectionHeader.Draw("Processor");
        if (view.Banner is { } banner)
        {
            this.DrawBanner(banner, gpu?.Synth);
        }

        using var card = Card.Begin("settings-processor");
        if (!card.Visible)
        {
            return;
        }

        DrawState(ctx, gpu);
        Ui.Gap(Ui.Space2);

        var reasons = new string?[] { null, view.GraphicsCardUnavailable, null };
        var picked = SettingsParts.SegmentedRow(
            Labels.ProcessorLabel,
            "settings-processor-mode",
            DeviceViewModel.ModeLabels,
            DeviceViewModel.IndexOf(DeviceViewModel.ModeValues, device.Mode),
            reasons);
        if (picked != DeviceViewModel.IndexOf(DeviceViewModel.ModeValues, device.Mode))
        {
            actions.SetMode(DeviceViewModel.ModeValues[picked]);
        }

        SettingsParts.RowHelp(view.GraphicsCardUnavailable is { } why && device.Mode != DeviceSettings.ModeCpu
            ? why
            : DeviceViewModel.ModeExplanation(device.Mode));

        if (view.ShowPreference)
        {
            var current = DeviceViewModel.IndexOf(DeviceViewModel.PreferenceValues, device.GpuPreference);
            var preference = SettingsParts.SegmentedRow(
                "Graphics card to prefer", "settings-processor-pref", DeviceViewModel.PreferenceLabels, current);
            if (preference != current)
            {
                actions.SetPreference(DeviceViewModel.PreferenceValues[preference]);
            }

            SettingsParts.RowHelp(DeviceViewModel.PreferenceExplanation(device.GpuPreference));
        }

        var load = DeviceViewModel.IndexOf(DeviceViewModel.LoadValues, device.CpuImpact);
        var loadPicked = SettingsParts.SegmentedRow(
            Labels.BackgroundLoadLabel, "settings-processor-load", DeviceViewModel.LoadLabels, load);
        if (loadPicked != load)
        {
            device.CpuImpact = DeviceViewModel.LoadValues[loadPicked];
            ctx.Save();
        }

        SettingsParts.RowHelp(
            $"{Labels.BackgroundLoad(device.CpuThreads())}. {DeviceViewModel.LoadExplanation(device.CpuImpact)}");

        this.DrawAdapters(view);
    }

    private static DeviceMode ModeOf(string mode) => mode switch
    {
        DeviceSettings.ModeGpu => DeviceMode.Gpu,
        DeviceSettings.ModeCpu => DeviceMode.Cpu,
        _ => DeviceMode.Auto,
    };

    /// <summary>The state of the engine the graphics card matters to, or of the main engine when none does.</summary>
    private static void DrawState(UiContext ctx, (ISpeechSynthesizer Synth, EngineInfo Info)? gpu)
    {
        var info = gpu?.Info ?? ctx.ActiveEngine;
        var synth = gpu?.Synth ?? ctx.Synth;
        var view = EngineViewModel.Describe(new EngineViewInput(
            info.DisplayName,
            SettingsParts.EngineInstalled(ctx, info),
            ctx.EngineSession.Warming && ctx.EngineSession.WarmingKey == info.Key,
            synth.Status));

        StatusPill.Draw(view.Severity, $"{info.DisplayName}: {view.Word}");
        Ui.MutedText(view.Detail);
        if (gpu is null)
        {
            Ui.MutedText("Your voice quality uses only the processor. The graphics card helps with Balanced and Most expressive.");
        }
    }

    private void DrawAdapters(DeviceView view)
    {
        Ui.Gap(Ui.Space2);
        Ui.MutedText(view.Detected);
        foreach (var line in view.Adapters)
        {
            ImGui.Bullet();
            ImGui.TextUnformatted(line);
        }

        if (view.Note is { } note)
        {
            Ui.MutedText(note);
        }

        var checking = this.recheck is { IsCompleted: false };
        if (ActionButton.Secondary("Check again##settings-processor-check", !checking && ctx.DeviceProbe is not null, checking ? "Checking…" : null))
        {
            var probe = ctx.DeviceProbe!;
            this.recheck = Task.Run(() => probe.Detect());
        }
    }

    private void DrawBanner(DeviceBanner banner, ISpeechSynthesizer? gpuSynth)
    {
        var labels = new List<string>();
        var runs = new List<Action>();
        if (banner.CanRetryGpu && !banner.Retrying)
        {
            labels.Add("Retry on GPU");
            runs.Add(() => actions.RetryOnGpu(gpuSynth));
        }

        if (banner.CanUseCpu)
        {
            labels.Add("Use CPU");
            runs.Add(actions.UseCpu);
        }

        labels.Add("Copy details");
        runs.Add(() => DiagnosticsSource.CopyToClipboard(ctx));

        var clicked = Banner.Draw("settings-processor", banner.Severity, banner.Title, banner.Detail, labels);
        if (clicked >= 0)
        {
            runs[clicked]();
        }

        if (banner.Retrying)
        {
            StatusPill.Draw(Severity.Info, "Trying the graphics card again…");
            Ui.Gap(Ui.Space2);
        }
    }
}
