namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// Advanced > Engines: the raw engine behind each voice quality, straight from the engine catalog
/// (retired engines are not offered). Each card shows its state, license and a "Use as main engine"
/// button; engines with several file precisions get a precision picker with download sizes, and the
/// Remote server gets its address, the safety switch for servers on other computers, a timeout and
/// a model name. This is the only place engine names, precisions and the like appear.
/// </summary>
internal sealed class AdvancedEngines(UiContext ctx, SettingsInputs inputs)
{
    private const int TextMax = 256;
    private const int MinTimeout = RemoteEndpointModel.MinTimeoutSeconds;
    private const int MaxTimeout = RemoteEndpointModel.MaxTimeoutSeconds;

    private string urlText = string.Empty;
    private bool urlEditing;
    private string modelText = string.Empty;
    private bool modelEditing;

    public void Draw()
    {
        var c = ctx.Config.Engine;
        var expressive = ctx.EngineCatalog.Find(c.Expressive.EngineId);
        SettingsParts.Toggle(
            "Send emotional lines to the expressive engine",
            $"Lines with a laugh, a sigh or strong feeling go to {expressive?.DisplayName ?? "the expressive engine"} " +
            "while Kokoro stays the main engine. Same as the Balanced quality.",
            () => c.Expressive.Enabled,
            v =>
            {
                c.Expressive.Enabled = v;
                ctx.Save();
                ctx.InvalidateSynthesizer();
            });

        var gpu = SettingsParts.GpuEngine(ctx);
        foreach (var engine in ctx.EngineCatalog.List().Where(e => !e.Retired))
        {
            this.DrawEngine(engine, gpu);
        }
    }

    private void DrawEngine(EngineInfo engine, (ISpeechSynthesizer Synth, EngineInfo Info)? gpu)
    {
        var key = engine.Key;
        var active = ctx.PrimaryKey == key;
        using var card = Card.Begin("adv-engine-" + key);
        if (!card.Visible)
        {
            return;
        }

        ImGui.TextUnformatted(engine.DisplayName);
        if (active)
        {
            ImGui.SameLine();
            Ui.MutedTextInline("Main engine");
        }

        Ui.MutedText(engine.Blurb);
        Ui.MutedText(Meta(engine));
        if (engine.NonCommercial)
        {
            StatusPill.Draw(Severity.Warning, "Personal use only: the voice weights are not licensed for commercial use.");
        }

        EngineStatus? status = active ? ctx.Synth.Status : gpu is { } g && g.Info.Key == key ? g.Synth.Status : null;
        var installed = engine.AssetGroups.Count == 0 || SettingsParts.EngineInstalled(ctx, engine);
        var view = EngineViewModel.Describe(new EngineViewInput(
            engine.DisplayName,
            installed,
            ctx.EngineSession.Warming && ctx.EngineSession.WarmingKey == key,
            status));
        Ui.Gap(Ui.Space1);
        StatusPill.Draw(view.Severity, view.Word);
        Ui.MutedText(view.Detail);

        Ui.Gap(Ui.Space1);
        var reason = active ? "Already the main engine." : !installed ? "Download its files under Downloads first." : null;
        if (ActionButton.Secondary("Use as main engine##adv-use-" + key, reason is null, reason))
        {
            this.UseEngine(key);
        }

        this.DrawPrecision(engine);
        if (key == RemoteEndpointModel.EngineId)
        {
            this.DrawRemote();
        }
    }

    private static string Meta(EngineInfo engine)
    {
        var speed = engine.Speed switch
        {
            EngineSpeed.Fast => "Fast",
            EngineSpeed.Slower => "Slower",
            _ => "Slow",
        };
        var where = engine.Tier switch
        {
            EngineTier.Cpu => "runs on the processor",
            EngineTier.Gpu => "best on a graphics card",
            EngineTier.Remote => "runs on a server you set up",
            _ => "experimental",
        };
        return $"{speed}, {where}. License: {engine.License}.";
    }

    private void UseEngine(string key)
    {
        ctx.Config.Engine.Primary = key;
        ctx.Save();
        ctx.InvalidateSynthesizer();
        ctx.Status.Resolve(EngineSessionModel.FailureKey);
        ctx.EngineSession.StartWarmUp(ctx.PrimaryKey, ctx.Synth);
    }

    // ---------------------------------------------------------------- precision

    private void DrawPrecision(EngineInfo engine)
    {
        var choices = EnginePrecisionModel.Choices(engine);
        if (choices.Count == 0)
        {
            return;
        }

        var map = ctx.Config.Engine.PerEngine;
        var current = EnginePrecisionModel.Current(map, engine.Key);
        var index = Math.Max(0, choices.ToList().FindIndex(c => c.Value == current));
        var items = choices.Select(c => c.Label + this.Suffix(engine, c.Value)).ToList();

        Ui.Gap(Ui.Space1);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Precision");
        ImGui.SameLine(Ui.Em(FormRow.LabelColumnEm));
        ImGui.SetNextItemWidth(Ui.Em(20f));
        Controls.Combo(
            "##adv-precision-" + engine.Key,
            items,
            () => index,
            i =>
            {
                EnginePrecisionModel.Set(map, engine.Key, choices[i].Value);
                ctx.Save();
                ctx.InvalidateSynthesizer();
            });

        var group = EnginePrecisionModel.GroupFor(engine, current);
        var row = group is null ? null : inputs.Rows.FirstOrDefault(r => r.Group == group);
        SettingsParts.RowHelp(
            row is { Installed: false }
                ? "Its files are not downloaded yet. Download them under Downloads. Takes effect the next time the voices load."
                : "A smaller precision needs less graphics memory and may sound slightly less lifelike. Takes effect the next time the voices load.");
    }

    private string Suffix(EngineInfo engine, string precision)
    {
        var group = EnginePrecisionModel.GroupFor(engine, precision);
        var row = group is null ? null : inputs.Rows.FirstOrDefault(r => r.Group == group);
        return row is { Installed: false } ? ", not downloaded" : string.Empty;
    }

    // ---------------------------------------------------------------- remote server

    private EngineOptions RemoteOptions()
    {
        var map = ctx.Config.Engine.PerEngine;
        if (!map.TryGetValue(RemoteEndpointModel.EngineId, out var options))
        {
            options = map[RemoteEndpointModel.EngineId] = new EngineOptions();
        }

        return options;
    }

    private static string Extra(EngineOptions? options, string key) =>
        options is not null && options.Extra.TryGetValue(key, out var value) ? value : string.Empty;

    private void DrawRemote()
    {
        ctx.Config.Engine.PerEngine.TryGetValue(RemoteEndpointModel.EngineId, out var stored);
        Ui.Gap(Ui.Space2);
        Ui.MutedText("Speaks through an OpenAI-style server you run yourself. Only use a server you trust.");

        if (!this.urlEditing)
        {
            this.urlText = Extra(stored, RemoteEndpointModel.UrlKey);
        }

        if (!this.modelEditing)
        {
            this.modelText = Extra(stored, RemoteEndpointModel.ModelKey);
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Server address");
        ImGui.SameLine(Ui.Em(FormRow.LabelColumnEm));
        ImGui.SetNextItemWidth(Ui.Em(20f));
        ImGui.InputTextWithHint("##adv-remote-url", RemoteEndpointModel.DefaultUrl, ref this.urlText, TextMax);
        this.urlEditing = ImGui.IsItemActive();
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            this.SetExtra(RemoteEndpointModel.UrlKey, this.urlText.Trim());
        }

        var allow = RemoteEndpointModel.IsTrue(Extra(stored, RemoteEndpointModel.AllowNonLoopbackKey));
        var warning = RemoteEndpointModel.Warning(RemoteEndpointModel.Parse(this.urlText), allow);
        if (warning is not null)
        {
            StatusPill.Draw(RemoteEndpointModel.Parse(this.urlText).Valid ? Severity.Warning : Severity.Error, warning);
        }

        Controls.Checkbox(
            "Allow a server on another computer",
            () => allow,
            v => this.SetExtra(RemoteEndpointModel.AllowNonLoopbackKey, v ? "true" : string.Empty));

        var timeout = RemoteEndpointModel.TimeoutSeconds(Extra(stored, RemoteEndpointModel.TimeoutKey));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Wait up to (seconds)");
        ImGui.SameLine(Ui.Em(FormRow.LabelColumnEm));
        ImGui.SetNextItemWidth(Ui.Em(10f));
        if (ImGui.SliderInt("##adv-remote-timeout", ref timeout, MinTimeout, MaxTimeout))
        {
            this.SetExtra(RemoteEndpointModel.TimeoutKey, RemoteEndpointModel.FormatSeconds(timeout), save: false);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            ctx.Save();
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Model name");
        ImGui.SameLine(Ui.Em(FormRow.LabelColumnEm));
        ImGui.SetNextItemWidth(Ui.Em(12f));
        ImGui.InputTextWithHint("##adv-remote-model", "kokoro", ref this.modelText, TextMax);
        this.modelEditing = ImGui.IsItemActive();
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            this.SetExtra(RemoteEndpointModel.ModelKey, this.modelText.Trim());
        }
    }

    /// <summary>Stores one Remote server value; an empty value removes the key so the default applies. The server reads these fresh on every line.</summary>
    private void SetExtra(string key, string value, bool save = true)
    {
        if (value.Length == 0 && !ctx.Config.Engine.PerEngine.ContainsKey(RemoteEndpointModel.EngineId))
        {
            return;
        }

        var options = this.RemoteOptions();
        if (value.Length == 0)
        {
            options.Extra.Remove(key);
        }
        else
        {
            options.Extra[key] = value;
        }

        if (save)
        {
            ctx.Save();
        }
    }
}
