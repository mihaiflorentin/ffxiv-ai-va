namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// The collapsed "Advanced" section: engines and precision, delivery details, casting presets, the
/// test line, diagnostics, and the switches that change the window itself (show advanced details,
/// reduce motion) with Run setup again and Reset to defaults. Technical words are allowed here and
/// nowhere else on the Settings page. Deep links (<see cref="SettingsAnchors"/>) open the section
/// and the part they name.
/// </summary>
internal sealed class AdvancedSection
{
    private static readonly string[] Backends = ["auto", "webgpu", "directml"];
    private static readonly string[] Apis = ["auto", "vulkan", "d3d12"];

    private readonly UiContext ctx;
    private readonly SettingsInputs inputs;
    private readonly AdvancedEngines engines;
    private readonly CastingPresetsCard presets;
    private readonly TestPanel test;

    public AdvancedSection(UiContext ctx, SettingsInputs inputs)
    {
        this.ctx = ctx;
        this.inputs = inputs;
        this.engines = new AdvancedEngines(ctx, inputs);
        this.presets = new CastingPresetsCard(ctx);
        this.test = new TestPanel(ctx);
    }

    public void Draw(string? anchor)
    {
        SectionHeader.Draw("Advanced");
        Ui.MutedText("Engines, precision, casting presets and diagnostics. You should not need these for everyday use.");
        if (SettingsAnchors.InsideAdvanced(anchor))
        {
            ImGui.SetNextItemOpen(true);
        }

        if (!ImGui.CollapsingHeader("Show advanced settings##settings-advanced"))
        {
            return;
        }

        if (Part("Engines", SettingsAnchors.Engines, anchor))
        {
            this.engines.Draw();
            ImGui.TreePop();
        }

        if (Part("Delivery details", SettingsAnchors.Delivery, anchor))
        {
            this.DrawDelivery();
            ImGui.TreePop();
        }

        if (Part("Casting presets", SettingsAnchors.Presets, anchor))
        {
            this.presets.Draw();
            ImGui.TreePop();
        }

        if (Part("Test a line", SettingsAnchors.Test, anchor))
        {
            this.test.Draw();
            ImGui.TreePop();
        }

        if (Part("Diagnostics", SettingsAnchors.Diagnostics, anchor))
        {
            this.DrawDiagnostics();
            ImGui.TreePop();
        }

        if (Part("This window and setup", null, anchor))
        {
            this.DrawWindowAndSetup();
            ImGui.TreePop();
        }
    }

    /// <summary>A collapsible part of Advanced; opens by itself when a deep link names it.</summary>
    private static bool Part(string title, string? key, string? anchor)
    {
        if (key is not null && anchor == key)
        {
            ImGui.SetNextItemOpen(true);
        }

        return ImGui.TreeNodeEx(title + "##adv-part-" + (key ?? "window"), ImGuiTreeNodeFlags.Framed);
    }

    // ---------------------------------------------------------------- delivery

    private void DrawDelivery()
    {
        var c = this.ctx.Config;
        var save = this.ctx.Save;

        using (FormRow.Begin(
                   "Late line limit (seconds)",
                   "0 keeps every line, however late. This is the number behind \"Skip lines that arrive late\"."))
        {
            Controls.DragFloat(
                "##adv-stale",
                0f,
                120f,
                () => c.StaleLineSeconds,
                v => c.StaleLineSeconds = (int)MathF.Round(v),
                save);
        }

        SettingsParts.Toggle(
            "Keep speaking pace on pitched voices",
            "Pitch shifting used to slow deep voices and speed up high ones. On keeps every voice at its own pace.",
            () => c.KeepTempoWhenPitched,
            v => { c.KeepTempoWhenPitched = v; save(); });
        SettingsParts.Toggle(
            "Start long lines sooner",
            "Long lines are made sentence by sentence, so the first sentence plays while the rest is still being made.",
            () => c.StreamSentences,
            v => { c.StreamSentences = v; save(); });
        SettingsParts.Toggle(
            "Remove stutter",
            "Strips repeated leading characters from stammering lines before they are spoken.",
            () => c.RemoveStutter,
            v => { c.RemoveStutter = v; save(); });

        Ui.Gap(Ui.Space1);
        ImGui.TextUnformatted("Graphics runtime");
        Ui.MutedText("Only change these if the graphics card fails to start. Automatic is right otherwise.");
        Controls.Combo(
            "Backend##adv-backend",
            Backends,
            () => c.Device.Backend,
            v => this.SetRuntime(() => c.Device.Backend = v),
            "Which graphics runtime to use: automatic, WebGPU, or DirectML (native Windows only).");
        Controls.Combo(
            "Graphics API##adv-api",
            Apis,
            () => c.Device.Api,
            v => this.SetRuntime(() => c.Device.Api = v),
            "For WebGPU only. Automatic uses Vulkan under Wine or Proton and Direct3D 12 on Windows.");
    }

    private void SetRuntime(Action write)
    {
        write();
        this.ctx.Save();
        this.ctx.InvalidateSynthesizer();
    }

    // ---------------------------------------------------------------- diagnostics

    private void DrawDiagnostics()
    {
        var probes = this.ctx.Probes;
        Ui.MutedText(
            "Copies a plain-text report of your setup: voice quality, processor, engine states, graphics cards and sizes. " +
            "It contains no folder paths, character names or line text.");
        if (ActionButton.Primary("Copy report##adv-report"))
        {
            DiagnosticsSource.CopyToClipboard(this.ctx);
        }

        Ui.Gap(Ui.Space1);
        Ui.MutedText(
            $"Lines waiting: {this.ctx.Queue.Depth}. Downloaded voices: {Labels.FileSize(probes.ModelsFolderBytes)}. " +
            $"Game memory: {Labels.FileSize(probes.ProcessWorkingSetBytes)}. {DownloadsModel.FreeDiskText(this.inputs.FreeBytes)}.");
    }

    // ---------------------------------------------------------------- window and setup

    private void DrawWindowAndSetup()
    {
        var c = this.ctx.Config;
        SettingsParts.Toggle(
            "Show advanced details",
            "Shows technical options elsewhere, such as the match pattern on What's read.",
            () => c.ShowAdvanced,
            v => { c.ShowAdvanced = v; this.ctx.Save(); });
        SettingsParts.Toggle(
            "Reduce motion",
            "Stops moving progress bars and the pulsing voice fingerprint. The state is still written out in words.",
            () => c.ReduceMotion,
            v => { c.ReduceMotion = v; this.ctx.Save(); });

        Ui.Gap(Ui.Space1);
        if (ActionButton.Secondary("Run setup again##adv-rerun"))
        {
            this.RunSetupAgain();
        }

        Ui.MutedText("Goes to Home and shows the first-time setup. Your voices and downloads are kept.");
        Ui.Gap(Ui.Space1);
        if (ActionButton.Secondary("Reset settings to defaults##adv-reset"))
        {
            this.ctx.Confirm.Ask(SettingsReset.ConfirmText, "Reset settings", this.ResetSettings);
        }
    }

    private void RunSetupAgain()
    {
        var c = this.ctx.Config;
        c.OnboardingCompletedVersion = 0;
        c.OnboardingStep = 0;
        this.ctx.Save();
        this.ctx.SetupRerun.Request();
        this.ctx.Nav.OpenAt(NavTab.Home);
    }

    private void ResetSettings()
    {
        SettingsReset.Apply(this.ctx.Config);
        this.ctx.Save();
        this.ctx.InvalidateSynthesizer();
        this.ctx.Status.Resolve(EngineSessionModel.FailureKey);
        if (EngineParts.MissingRequired(this.ctx).Count == 0)
        {
            this.ctx.EngineSession.StartWarmUp(this.ctx.PrimaryKey, this.ctx.Synth);
        }

        this.ctx.Report("Settings reset to their defaults.", isError: false);
    }
}
