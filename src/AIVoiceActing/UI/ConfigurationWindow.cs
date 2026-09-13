namespace AIVoiceActing.UI;

using System.Numerics;
using AIVoiceActing.Domain;
using AIVoiceActing.Domain.Handlers;
using AIVoiceActing.Domain.Pipeline;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

/// <summary>
/// The seven-tab configuration window (TextToTalk's tab structure plus our engine tabs).
/// Driving adapter only: it reads/mutates <see cref="Configuration"/> and calls the
/// container-resolved ports passed in from the plugin root — never Infrastructure
/// concretes. Test buttons route through the exact same speech path as the game
/// (SpeechRequestHandler, or its ISpeechSynthesizer + ISpeechQueue tail).
/// </summary>
public sealed class ConfigurationWindow : Window
{
    private static readonly string[] ExecutionProviders = ["auto", "cpu", "directml", "coreml"];
    private static readonly (int Code, string Name)[] Modifiers =
    [
        (VirtualKeys.Control, "Ctrl"),
        (VirtualKeys.Shift, "Shift"),
        (18, "Alt"),
    ];

    private static readonly (int Code, string Name)[] MajorKeys =
    [
        .. Enumerable.Range(48, 10).Select(i => (i, ((char)i).ToString())),
        .. Enumerable.Range(65, 26).Select(i => (i, ((char)i).ToString())),
    ];

    private readonly Configuration config;
    private readonly Action save;
    private readonly SpeechRequestHandler speech;
    private readonly Func<ISpeechSynthesizer> synthesizer;
    private readonly Action invalidateSynthesizer;

    /// <summary>Live synthesizer for the selected engine (rebuilt when engine/EP changes).</summary>
    private ISpeechSynthesizer Synth => this.synthesizer();
    private readonly ISpeechQueue queue;
    private readonly IProfileStore profiles;
    private readonly Func<IReadOnlyList<(ModelAsset Asset, string Group)>> modelAssets;
    private readonly Func<IModelProvisioner> provisioner;
    private readonly Func<IModelStore> modelStore;
    private readonly Func<RaceVoiceMap> voiceMap;
    private readonly Func<DialogueSessionFactory> sessions;
    private readonly Func<string> modelsDir;
    private readonly Func<string> voicesDir;
    private readonly Func<(string Name, ushort World)?> localPlayer;
    private readonly Func<string, bool> openDirectory;
    private readonly Action<string> reportError;

    private readonly TestBenchModel test = new();
    private readonly VoiceEntryForm playerForm = new(player: true);
    private readonly VoiceEntryForm npcForm = new(player: false);
    private readonly VoiceTable playerTable;
    private readonly VoiceTable npcTable;

    // Download state, written from the download task, read on the draw thread.
    private volatile ModelAsset? downloading;
    private long progressReceived;
    private long progressTotal;

    public ConfigurationWindow(
        Configuration config,
        Action save,
        SpeechRequestHandler speech,
        Func<ISpeechSynthesizer> synthesizer,
        Action invalidateSynthesizer,
        ISpeechQueue queue,
        IProfileStore profiles,
        Func<IReadOnlyList<(ModelAsset Asset, string Group)>> modelAssets,
        Func<IModelProvisioner> provisioner,
        Func<IModelStore> modelStore,
        Func<RaceVoiceMap> voiceMap,
        Func<DialogueSessionFactory> sessions,
        Func<string> modelsDir,
        Func<string> voicesDir,
        Func<(string Name, ushort World)?> localPlayer,
        Func<string, bool> openDirectory,
        Action<string> reportError)
        : base("AI Voice Acting Settings###AIVAConfig")
    {
        this.config = config;
        this.save = save;
        this.speech = speech;
        this.synthesizer = synthesizer;
        this.invalidateSynthesizer = invalidateSynthesizer;
        this.queue = queue;
        this.profiles = profiles;
        this.modelAssets = modelAssets;
        this.provisioner = provisioner;
        this.modelStore = modelStore;
        this.voiceMap = voiceMap;
        this.sessions = sessions;
        this.modelsDir = modelsDir;
        this.voicesDir = voicesDir;
        this.localPlayer = localPlayer;
        this.openDirectory = openDirectory;
        this.reportError = reportError;

        this.Size = new Vector2(620, 520);
        this.SizeCondition = ImGuiCond.FirstUseEver;

        this.playerTable = this.CreateTable(showWorld: true);
        this.npcTable = this.CreateTable(showWorld: false);
    }

    private VoiceTable CreateTable(bool showWorld) => new(showWorld, () => this.VoiceOptions, this.save)
    {
        SetOverride = this.profiles.SetOverride,
        Remove = key => this.profiles.Remove(key),
        SpeakTest = profile => this.SpeakVoiceTest(profile),
        EngineReady = () => this.Synth.IsReady,
        EngineNotReadyReason = () => this.EngineReason(),
    };

    private string[] VoiceOptions => this.voiceMap().DistinctVoiceIds();

    public override void PreDraw() =>
        this.WindowName =
            $"AI Voice Acting Settings (TTS {(this.config.Enabled ? "Enabled" : "Disabled")})###AIVAConfig";

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##aiva-tabs"))
        {
            return;
        }

        if (ImGui.BeginTabItem("Speech Settings"))
        {
            this.DrawSpeechTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Models"))
        {
            this.DrawModelsTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Status"))
        {
            this.DrawStatusTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Player Voices"))
        {
            this.DrawVoicesTab(playerTab: true);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("NPC Voices"))
        {
            this.DrawVoicesTab(playerTab: false);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Channel Settings"))
        {
            this.DrawChannelsTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Triggers/Exclusions"))
        {
            this.DrawTriggersTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Test"))
        {
            this.DrawTestTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    // ---- Tab 1: Speech Settings ----

    private void DrawSpeechTab()
    {
        var c = this.config;
        var save = this.save;

        if (ImGui.CollapsingHeader("Keybinds"))
        {
            Controls.Checkbox("Use keybind to toggle TTS", () => c.UseKeybind, v => { c.UseKeybind = v; save(); },
                "Hold the keys below to mute or unmute all voice output at once.");
            if (c.UseKeybind)
            {
                ImGui.Indent();
                Controls.KeyCombo("Modifier", Modifiers, () => c.ModifierKey, v => { c.ModifierKey = v; save(); }, "First key of the toggle combination.");
                Controls.KeyCombo("Key", MajorKeys, () => c.MajorKey, v => { c.MajorKey = v; save(); }, "Second key of the toggle combination.");
                ImGui.Unindent();
            }
        }

        if (ImGui.CollapsingHeader("General", ImGuiTreeNodeFlags.DefaultOpen))
        {
            Controls.Checkbox("Enabled", () => c.Enabled, v => { c.Enabled = v; save(); },
                "Master switch: unchecked stops all synthesis and playback.");
            Controls.SliderScaled("Global volume %", 200f, () => c.GlobalVolume, v => c.GlobalVolume = v, save,
                "Playback volume; above 100% boosts quiet voices. 100% plays clips as synthesized.");

            Controls.Section("Quest dialogue");
            Controls.Checkbox("Read quest Talk", () => c.ReadFromQuestTalkAddon, v => { c.ReadFromQuestTalkAddon = v; save(); },
                "Speak quest dialogue from the Talk addon (the dialogue window during quests).");
            Controls.IndentedCheckbox(
                "Cancel speech on text advance", () => c.CancelSpeechOnTextAdvance, v => { c.CancelSpeechOnTextAdvance = v; save(); },
                "Stop the current line the moment you click through dialogue, so speech never trails behind.");
            Controls.IndentedCheckbox(
                "Skip quest text the game voices (courtesy)", () => c.SkipVoicedQuestText, v => { c.SkipVoicedQuestText = v; save(); },
                "Leave lines the game ships voice acting for to the original actors.");

            Controls.Section("Battle dialogue");
            Controls.Checkbox("Read BattleTalk", () => c.ReadFromBattleTalkAddon, v => { c.ReadFromBattleTalkAddon = v; save(); },
                "Speak BattleTalk lines (in-scene dialogue outside the quest Talk window).");
            Controls.IndentedCheckbox(
                "Skip BattleTalk the game voices (courtesy)", () => c.SkipVoicedBattleText, v => { c.SkipVoicedBattleText = v; save(); },
                "Avoid doubling lines the game already voices itself.");

            Controls.Section("Cutscenes");
            Controls.Checkbox(
                "Read cutscene subtitles",
                "Speaks the caption lines of unvoiced cutscenes; the game's own voiced lines stay untouched.",
                () => c.ReadCutsceneSubtitles,
                v => { c.ReadCutsceneSubtitles = v; save(); });

            Controls.Section("Chat");
            Controls.Checkbox("Skip messages from you", () => c.SkipMessagesFromYou, v => { c.SkipMessagesFromYou = v; save(); },
                "Never read your own chat messages (your emotes, party chat, and so on).");
            Controls.Checkbox("Only messages from you", () => c.OnlyMessagesFromYou, v => { c.OnlyMessagesFromYou = v; save(); },
                "Read nothing except your own messages — hear your own emote lines back.");

            Controls.Section("Name lines with \"say\"");
            Controls.Checkbox("Enable name with say", () => c.EnableNameWithSay, v => { c.EnableNameWithSay = v; save(); },
                "Prefix \"say\" lines with the speaker's name so speakers are identifiable.");
            Controls.IndentedCheckbox("Name NPCs with say", () => c.NameNpcWithSay, v => { c.NameNpcWithSay = v; save(); },
                "Include NPC names in the prefix; off keeps prefixes to players only.");
            Controls.IndentedCheckbox("Say player world name", () => c.SayPlayerWorldName, v => { c.SayPlayerWorldName = v; save(); },
                "Add the player's home world to the prefix (useful on crowded cross-world servers).");
            Controls.IndentedCheckbox("Disallow multiple say", () => c.DisallowMultipleSay, v => { c.DisallowMultipleSay = v; save(); },
                "Never speak more than one name even when several speakers share the line.");
            Controls.IndentedCheckbox("Say partial name", () => c.SayPartialName, v => { c.SayPartialName = v; save(); },
                "Speak only part of long names to keep prefixes short.");
            Controls.IndentedCheckbox("Only say last name (instead of first)", () => c.OnlySayFirstOrLastName == FirstOrLastName.Last, v =>
            {
                c.OnlySayFirstOrLastName = v ? FirstOrLastName.Last : FirstOrLastName.First;
                save();
            });

            Controls.Section("Rate limiting");
            Controls.Checkbox("Use player rate limiter", () => c.UsePlayerRateLimiter, v => { c.UsePlayerRateLimiter = v; save(); });
            if (c.UsePlayerRateLimiter)
            {
                ImGui.Indent();
                Controls.DragFloat("Messages per second", 0.1f, 30f, () => c.MessagesPerSecond, v => c.MessagesPerSecond = v, save,
                    "Maximum lines started per second while the limiter is on.");
                ImGui.Unindent();
            }
        }

        if (ImGui.CollapsingHeader("Engine", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var status = this.Synth.IsReady
                ? "ready"
                : string.IsNullOrWhiteSpace(this.Synth.NotReadyReason)
                    ? $"models missing ({this.RequiredAssetCount()})"
                    : this.Synth.NotReadyReason;
            ImGui.TextUnformatted($"Voice engine: {status}");
            Controls.Combo(
                "Engine",
                ["kokoro", "f5", "turbo", "chatterbox"],
                () => c.SelectedEngine,
                v =>
                {
                    c.SelectedEngine = v;
                    save();
                    this.invalidateSynthesizer();
                });
            ImGui.SameLine();
            Controls.HelpMarker(
                "f5: voice-acting quality via reference-clip cloning (download on the Models tab, ~1.4 GB). " +
                "kokoro: fast CPU narration bank. chatterbox: legacy cloning engine.");
            // Per-engine device options: an engine only lists providers its adapter can
            // actually wire. kokoro is CPU-only; f5 and chatterbox can try DirectML and
            // fall back to CPU when the EP fails to initialize (e.g. under Wine).
            if (c.SelectedEngine is "f5" or "chatterbox" or "turbo")
            {
                var providers = c.SelectedEngine == "f5"
                    ? ["cpu", "directml"]
                    : ExecutionProviders;
                Controls.Combo(
                    "Execution provider",
                    providers,
                    () => c.SelectedEp,
                    v =>
                    {
                        c.SelectedEp = v;
                        save();
                        this.invalidateSynthesizer();
                    },
                    "Where synthesis runs: cpu always works; directml uses your GPU (Windows only, falls back to CPU when unavailable, e.g. under Wine).");
            }

            Controls.Combo(
                "CPU impact",
                ["low", "medium", "high"],
                () => c.CpuImpact,
                v => { c.CpuImpact = v; save(); },
                "How many CPU threads synthesis may use: low = 2 (gentlest on frame rate), medium = 4, high = 8. More threads mean faster lines but a bigger FPS hit.");
            ImGui.SameLine();
            Controls.HelpMarker(
                "ONNX synthesis threads: low = 2, medium = 4, high = 8. Lower protects framerate, higher shortens waits.");
            Controls.DragFloat(
                "Drop lines older than (seconds)",
                0f, 120f,
                () => c.StaleLineSeconds,
                v => c.StaleLineSeconds = (int)MathF.Round(v),
                save);
            ImGui.SameLine();
            Controls.HelpMarker(
                "If a line finishes synthesizing after the conversation moved on, skip it instead of playing it late. 0 keeps everything.");
            Controls.Checkbox(
                "Use LLM emotion director (context-aware delivery)",
                "Needs the optional director model; the free rules table runs otherwise.",
                () => c.DirectorEnabled,
                v => { c.DirectorEnabled = v; save(); });
        }

        if (ImGui.CollapsingHeader("Experimental"))
        {
            Controls.Checkbox(
                "Remove stutter",
                "Strips repeated leading characters from stammering lines before synthesis.",
                () => c.RemoveStutter,
                v => { c.RemoveStutter = v; save(); });
        }
    }


    /// <summary>Live not-ready reason for tooltips; falls back to the generic hint.</summary>
    private string EngineReason() =>
        this.Synth.IsReady
            ? ModelsTabModel.EngineNotReadyHint
            : string.IsNullOrWhiteSpace(this.Synth.NotReadyReason)
                ? ModelsTabModel.EngineNotReadyHint
                : this.Synth.NotReadyReason;

    // ---- Tab 2: Models ----

    // Section copy for the Models tab. Keys line up with the catalog group names so
    // the rows can be listed without the UI knowing engine internals.
    private static readonly (string Key, string Title, string Blurb)[] EngineSections =
    [
        ("kokoro", "Kokoro — fast narration (default)",
            "One 310 MB model; the 50+ voice banks ship inside the plugin. Robotic but instant — good for chat."),
        ("turbo", "Chatterbox Turbo — quality + speed",
            "Official ResembleAI export (~1.9 GB). Real voice acting with [laugh]/[chuckle] tags, about 6x slower than real time on CPU."),
        ("f5", "F5-TTS — voice-acting quality (slow)",
            "Reference-clip cloning (~1.4 GB). Best delivery, but about 10x slower than real time on CPU — opt-in showcase engine."),
        ("chatterbox", "Legacy chatterbox (cloning)",
            "The original cloning engine (~1 GB). Kept for existing setups; Turbo supersedes it."),
    ];

    private static readonly string[] ChatterboxSectionGroups =
        [Infrastructure.Onnx.ModelCatalog.ChatterboxRequiredGroup, Infrastructure.Onnx.ModelCatalog.ChatterboxFp32LmGroup];

    private void DrawModelsTab()
    {
        var provisioner = this.provisioner();
        var anyDownload = this.downloading is not null;

        ImGui.TextWrapped(
            "Every engine is downloadable here regardless of which one is active — switch " +
            "engines on the Speech Settings tab. Downloads go to the plugin's models " +
            "directory and run only when you press a button.");

        if (anyDownload)
        {
            var total = this.progressTotal;
            ImGui.ProgressBar(
                (float)ModelsTabModel.OverallProgress(this.progressReceived, total <= 0 ? null : total),
                new Vector2(-1, 0),
                $"{this.downloading!.Name}: {ModelsTabModel.ProgressLabel(this.progressReceived, total <= 0 ? null : total)}");
        }

        if (Controls.Button("Open Models Folder", !anyDownload, "A download is in progress."))
        {
            this.TryOpen(this.modelsDir());
        }

        ImGui.SameLine();
        if (Controls.Button("Open Voices Folder", !anyDownload, "A download is in progress."))
        {
            this.TryOpen(this.voicesDir());
        }

        ImGui.Separator();

        // One row set per selected engine: kokoro shows its model, f5 shows the four
        // F5 assets, chatterbox shows every non-Kokoro catalog row. Downloads target
        // the active engine's full required set, so "Download" provisions what the
        // engine actually needs.
        var assets = this.modelAssets();
        foreach (var (key, title, blurb) in EngineSections)
        {
            var groupKeys = key == "chatterbox" ? ChatterboxSectionGroups : [key];
            var sectionAssets = assets.Where(a => groupKeys.Contains(a.Group)).ToList();
            var downloadedCount = sectionAssets.Count(a => provisioner.IsDownloaded(a.Asset));
            var active = this.config.SelectedEngine == key ? " — ACTIVE" : string.Empty;
            if (!ImGui.CollapsingHeader($"{title} ({downloadedCount}/{sectionAssets.Count}){active}##models-{key}"))
            {
                Controls.Tooltip(blurb);
                continue;
            }

            Controls.Tooltip(blurb);
            ImGui.TextWrapped(blurb);
            ImGui.Spacing();

            if (ImGui.BeginTable("##models-" + key, 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Asset", ImGuiTableColumnFlags.WidthStretch, 3f);
                ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 90f);
                ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 150f);
                ImGui.TableSetupColumn("##action", ImGuiTableColumnFlags.WidthFixed, 100f);
                ImGui.TableHeadersRow();

                foreach (var (asset, _) in sectionAssets)
                {
                    var row = ModelsTabModel.Row(asset, provisioner.IsDownloaded(asset));
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(asset.Optional ? $"{row.Name} (optional)" : row.Name);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{row.SizeMb:0.0} MB");
                    ImGui.TableNextColumn();
                    var status = this.downloading?.FileName == asset.FileName
                        ? "downloading…"
                        : ModelsTabModel.StatusLabel(row);
                    ImGui.TextUnformatted(status);
                    ImGui.TableNextColumn();
                    if (row.Downloaded)
                    {
                        if (Controls.Button("Remove##" + asset.FileName, !anyDownload, anyDownload ? "A download is in progress." : null))
                        {
                            this.modelStore().Remove(asset.FileName);
                        }
                    }
                    else if (Controls.Button(
                        "Download##" + asset.FileName,
                        ModelsTabModel.CanDownload(anyDownload, row),
                        anyDownload ? "A download is already in progress." : null))
                    {
                        this.StartDownloads([.. sectionAssets.Select(a => a.Asset)]);
                    }
                }

                ImGui.EndTable();
            }

            ImGui.Spacing();
        }
    }

    private const string KokoroModelFileName = "kokoro-v1.0.onnx";

    /// <summary>Missing required assets for the SELECTED engine (Status tab hint).</summary>
    private int RequiredAssetCount()
    {
        var groups = this.config.SelectedEngine == "chatterbox"
            ? ChatterboxSectionGroups
            : [this.config.SelectedEngine];
        return this.modelAssets()
            .Count(a => groups.Contains(a.Group)
                && !a.Asset.Optional
                && !this.modelStore().IsDownloaded(a.Asset.FileName));
    }

    // ---- Tab 3: Status ----

    private long statusModelsDirBytes;
    private DateTime statusModelsDirBytesAt;
    private volatile bool warmingEngine;

    private void DrawStatusTab()
    {
        ImGui.TextUnformatted("Engine");
        ImGui.Separator();
        var ready = this.Synth.IsReady;
        ImGui.BulletText($"Selected engine: {this.config.SelectedEngine}");
        ImGui.BulletText(
            this.warmingEngine ? "State: loading the model…"
            : ready ? "State: ready"
            : $"State: NOT ready — {this.EngineReason()}");
        if (Controls.Button(
                "Load engine now",
                !this.warmingEngine && !ready,
                this.warmingEngine ? "Already loading." : ready ? "Engine is ready." : null))
        {
            // Session creation takes a few seconds; keep it off the draw thread.
            this.warmingEngine = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    await this.Synth.WarmUpAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    this.reportError($"Engine start failed: {ex.Message}");
                }
                finally
                {
                    this.warmingEngine = false;
                }
            });
        }

        ImGui.SameLine();
        Controls.HelpMarker(
            "Builds the ONNX session ahead of the first line. The engine also loads itself " +
            "on login and on the first spoken line.");
        ImGui.BulletText(
            this.config.SelectedEngine is "f5" or "chatterbox" or "turbo"
                ? $"Execution provider: {this.config.SelectedEp}"
                : "Execution provider: cpu (Kokoro is CPU-only)");
        ImGui.BulletText(
            $"CPU impact: {this.config.CpuImpact} ({this.config.CpuImpact switch { "low" => 2, "high" => 8, _ => 4 }} synthesis threads)");

        ImGui.Spacing();
        ImGui.TextUnformatted("Playback");
        ImGui.Separator();
        ImGui.BulletText($"Queue depth: {this.queue.Depth}");
        ImGui.BulletText(
            this.config.StaleLineSeconds > 0
                ? $"Stale lines dropped after {this.config.StaleLineSeconds}s"
                : "Stale-line dropping disabled");

        ImGui.Spacing();
        ImGui.TextUnformatted("Resources");
        ImGui.Separator();
        using (var process = System.Diagnostics.Process.GetCurrentProcess())
        {
            ImGui.BulletText($"Plugin process memory: {process.WorkingSet64 / (1024.0 * 1024.0):0} MB");
        }

        var groups = this.config.SelectedEngine == "chatterbox"
            ? ChatterboxSectionGroups
            : [this.config.SelectedEngine];
        var selected = this.modelAssets().Where(a => groups.Contains(a.Group)).ToList();
        var downloaded = selected.Count(a => this.modelStore().IsDownloaded(a.Asset.FileName));
        ImGui.BulletText($"Model assets ({this.config.SelectedEngine}): {downloaded}/{selected.Count} downloaded");
        ImGui.BulletText($"Models directory: {this.ModelsDirBytes() / (1024.0 * 1024.0):0} MB");
        ImGui.BulletText(
            $"Voice bank: {this.voiceMap().DistinctVoiceIds().Length} distinct voices (race/gender mapped)");
    }

    /// <summary>Total size of the models directory, recomputed at most every 2s.</summary>
    private long ModelsDirBytes()
    {
        if ((DateTime.UtcNow - this.statusModelsDirBytesAt).TotalSeconds < 2)
        {
            return this.statusModelsDirBytes;
        }

        this.statusModelsDirBytesAt = DateTime.UtcNow;
        long total = 0;
        try
        {
            total = Directory.EnumerateFiles(this.modelsDir(), "*", SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length);
        }
        catch (Exception)
        {
            // Directory missing or unreadable: report zero rather than drawing errors.
        }

        this.statusModelsDirBytes = total;
        return total;
    }

    /// <summary>
    /// Starts a sequential background download queue: assets download one at a time in
    /// list order, and the in-flight marker never reads null between items, so the draw
    /// thread's idle gating stays exact. The queue stops at the first failure.
    /// </summary>
    private void StartDownloads(IReadOnlyList<ModelAsset> assets)
    {
        if (assets.Count == 0 || this.downloading is not null)
        {
            return;
        }

        this.DownloadNext(this.provisioner(), new Queue<ModelAsset>(assets));
    }

    private void DownloadNext(IModelProvisioner provisioner, Queue<ModelAsset> pending)
    {
        if (pending.Count == 0)
        {
            this.downloading = null;
            return;
        }

        var asset = pending.Dequeue();
        this.downloading = asset;
        this.progressReceived = 0;
        this.progressTotal = asset.SizeBytes ?? 0;
        _ = Task.Run(async () =>
        {
            try
            {
                await provisioner.DownloadAsync(
                    asset,
                    new Progress<DownloadProgress>(p =>
                    {
                        this.progressReceived = p.BytesReceived;
                        if (p.TotalBytes is { } total)
                        {
                            this.progressTotal = total;
                        }
                    }),
                    CancellationToken.None);
                this.DownloadNext(provisioner, pending);
            }
            catch (Exception ex)
            {
                this.reportError($"Download of \"{asset.Name}\" failed: {ex.Message}");
                this.downloading = null;
            }
        });
    }

    private void TryOpen(string directory)
    {
        if (!this.openDirectory(directory))
        {
            this.reportError($"Could not open \"{directory}\".");
        }
    }

    // ---- Tabs 3 & 4: Player / NPC Voices ----

    private void DrawVoicesTab(bool playerTab)
    {
        var c = this.config;
        Controls.Checkbox(
            "Use race/gender voice presets for unlisted speakers",
            "Speakers without a manual override get a deterministic voice from their race/gender set.",
            () => c.UseRaceVoicePresets,
            v => { c.UseRaceVoicePresets = v; this.save(); });

        ImGui.TextWrapped(playerTab
            ? "Manual player overrides (name + world id). Overrides win over the automatic assignment and persist."
            : "Manual NPC overrides by name. Overrides win over the automatic assignment and persist.");

        var entries = this.profiles.Entries
            .Where(e => e.Custom && e.SpeakerKey.StartsWith(playerTab ? "pc:" : "npc:", StringComparison.Ordinal))
            .ToArray();

        var table = playerTab ? this.playerTable : this.npcTable;
        var form = playerTab ? this.playerForm : this.npcForm;
        table.Draw(
            playerTab ? "##players" : "##npcs",
            entries,
            e => SpeakerKeyView.Parse(e.SpeakerKey),
            form);
    }

    // ---- Tab 5: Channel Settings ----

    private void DrawChannelsTab()
    {
        var c = this.config;
        var save = this.save;
        var presets = c.EnabledChatTypesPresets;
        var current = c.CurrentPreset ?? presets.FirstOrDefault();
        if (current is not null && current.Id != c.CurrentPresetId)
        {
            c.CurrentPresetId = current.Id;
        }

        var names = presets
            .Select(p => $"{p.Name ?? $"#{p.Id}"}{(p.Id == c.CurrentPresetId ? " *" : string.Empty)}")
            .ToArray();
        var index = Math.Max(0, presets.IndexOf(current!));
        if (names.Length > 0 && ImGui.Combo("Preset", ref index, names, names.Length))
        {
            c.CurrentPresetId = presets[index].Id;
            save();
            current = presets[index];
        }

        ImGui.SameLine();
        if (ImGui.Button("New Preset"))
        {
            var created = new EnabledChatTypesPreset
            {
                Id = presets.Count == 0 ? 0 : presets.Max(p => p.Id) + 1,
                Name = $"Preset {presets.Count + 1}",
                EnabledChatTypes = [ChatChannels.NpcDialogue],
            };
            presets.Add(created);
            c.CurrentPresetId = created.Id;
            save();
            current = created;
        }

        ImGui.SameLine();
        if (Controls.Button("Delete", presets.Count > 1, "At least one preset must exist.") && current is not null)
        {
            presets.Remove(current);
            c.CurrentPresetId = presets[0].Id;
            save();
            current = presets[0];
        }

        if (current is { } preset)
        {
            var name = preset.Name ?? string.Empty;
            if (ImGui.InputText("##preset-name", ref name, 64))
            {
                preset.Name = name;
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    save();
                }
            }

            ImGui.SameLine();
            Controls.Checkbox("Enable all chat types", () => preset.EnableAllChatTypes, v =>
            {
                preset.EnableAllChatTypes = v;
                save();
            }, "Read every chat channel, ignoring the list below — quick setup without ticking dozens of boxes.");

            Controls.Checkbox("Use keybind for this preset", () => preset.UseKeybind, v =>
            {
                preset.UseKeybind = v;
                save();
            }, "Read channels only while holding the keys below — a hold-to-talk gate for this preset.");
            if (preset.UseKeybind)
            {
                ImGui.Indent();
                Controls.KeyCombo("Modifier", Modifiers, () => preset.ModifierKey, v => { preset.ModifierKey = v; save(); }, "First key of this preset's hold-to-talk combination.");
                Controls.KeyCombo("Key", MajorKeys, () => preset.MajorKey, v => { preset.MajorKey = v; save(); }, "Second key of this preset's hold-to-talk combination.");
                ImGui.Unindent();
            }

            if (!preset.EnableAllChatTypes)
            {
                preset.EnabledChatTypes ??= new List<int>();
                var enabled = preset.EnabledChatTypes;
                ImGui.TextWrapped("Channels this preset reads, grouped by category:");
                ImGui.Spacing();
                foreach (var (category, channels) in ChannelNames.ByCategory())
                {
                    var enabledCount = channels.Count(ch => enabled.Contains(ch.Id));
                    // The ##-suffix pins the ImGui ID: the displayed count changes with
                    // every tick, and an ID derived from it would collapse the section.
                    var header = $"{category} ({enabledCount}/{channels.Count})##cat-{category}";
                    if (!ImGui.CollapsingHeader(header))
                    {
                        Controls.Tooltip(ChannelNames.CategoryDescription(category));
                        continue;
                    }

                    Controls.Tooltip(ChannelNames.CategoryDescription(category));

                    var all = enabledCount == channels.Count;
                    if (Controls.Button(all ? "none" : "all", true, all ? "Uncheck every channel in this category." : "Check every channel in this category."))
                    {
                        if (all)
                        {
                            preset.EnabledChatTypes = enabled
                                .Where(id => channels.All(ch => ch.Id != id))
                                .ToList();
                        }
                        else
                        {
                            foreach (var ch in channels.Where(ch => !enabled.Contains(ch.Id)))
                            {
                                enabled.Add(ch.Id);
                            }
                        }

                        save();
                    }

                    ImGui.SameLine();
                    for (var i = 0; i < channels.Count; i++)
                    {
                        var channel = channels[i];
                        if (i % 3 != 0)
                        {
                            ImGui.SameLine();
                        }

                        var on = enabled.Contains(channel.Id);
                        if (ImGui.Checkbox(channel.Name, ref on))
                        {
                            if (on)
                            {
                                enabled.Add(channel.Id);
                            }
                            else
                            {
                                enabled.Remove(channel.Id);
                            }

                            save();
                        }
                    }
                }
            }
        }
    }

    // ---- Tab 6: Triggers / Exclusions ----

    private void DrawTriggersTab()
    {
        ImGui.TextWrapped(
            "Triggers restrict which lines are spoken (empty = everything); an exclusion wins " +
            "over any trigger. Entries may be plain substrings or regex.");

        TriggerList.Draw("Triggers", this.config.Triggers, this.save);
        ImGui.Spacing();
        TriggerList.Draw("Exclusions", this.config.Exclusions, this.save);
    }

    // ---- Tab 7: Test ----

    private void DrawTestTab()
    {
        var ready = this.Synth.IsReady;
        var reason = this.EngineReason();

        var text = this.test.Text;
        if (ImGui.InputTextMultiline("##test-text", ref text, 512, new Vector2(-1, 60)))
        {
            this.test.Text = text;
        }

        Controls.Checkbox("Use my character", () => this.test.UseMyCharacter, v => this.test.UseMyCharacter = v,
            "Speak the test line with your own character's race/gender voice assignment.");
        if (!this.test.UseMyCharacter)
        {
            var race = Races.All[Math.Clamp(this.test.RaceIndex, 0, Races.All.Count - 1)];
            Controls.Combo("Race", [.. Races.All.Select(r => r.Name)], () => this.test.RaceIndex, v =>
            {
                this.test.RaceIndex = v;
                this.test.TribeIndex = 0;
            }, "Race used for the test line's automatic voice assignment.");
            Controls.Combo("Tribe", [.. race.Tribes.Select(t => t.Name)], () => this.test.TribeIndex, v => this.test.TribeIndex = v, "Tribe refines the race assignment where voice sets differ.");
            Controls.Combo("Sex", ["Male", "Female"], () => (int)this.test.Sex, v => this.test.Sex = (byte)v, "Male or female voice set for the test.");
        }

        Controls.Combo("Emotion", TestBenchModel.Emotions, () => this.test.EmotionIndex, v => this.test.EmotionIndex = v);
        Controls.HelpMarker(
            "\"auto\" uses the rules table (or director) exactly like the game path; a fixed " +
            "emotion auditions that delivery with the slider below.");
        if (this.test.EmotionForced)
        {
            Controls.SliderFraction("Exaggeration", () => this.test.Exaggeration, v => this.test.Exaggeration = v, () => { },
                "How much expressive variation the synthesizer applies: 0 is flat narration, higher is theatrical.");
        }

        var plan = this.test.EmotionForced ? this.test.ForcedPlan() : this.test.RulesPlan();
        ImGui.TextDisabled(
            $"Plan: {plan.Emotion} @ {plan.Exaggeration:0.00}" +
            (plan.Tags.Count > 0 ? $" [{string.Join(", ", plan.Tags)}]" : string.Empty));

        var (myName, myWorld) = this.localPlayer() ?? default;
        var speaker = this.test.BuildSpeaker(this.test.UseMyCharacter ? myName : null, myWorld);

        if (Controls.Button("Speak", ready, reason))
        {
            // Rules path: a throwaway session id reads no history — exactly what the
            // rules table would produce in-game. The session is torn down when the line
            // settles so presses do not leak window entries.
            var sessionId = $"ui-rules-{Guid.NewGuid():N}";
            this.SpeakFireAndForget(
                this.speech.SpeakAsync(sessionId, speaker, this.test.Text, CancellationToken.None),
                onSettled: () => this.sessions().EndSession(sessionId));
        }

        ImGui.SameLine();
        if (Controls.Button("Speak with context", ready, reason))
        {
            // Director path: the shared window the cutscene pipeline feeds. The handler
            // appends the line AFTER planning, so the director never sees it twice.
            this.SpeakFireAndForget(
                this.speech.SpeakAsync(TestBenchModel.ContextSessionId, speaker, this.test.Text, CancellationToken.None));
        }

        ImGui.SameLine();
        if (Controls.Button(
                "Speak forced",
                ready && this.test.EmotionForced,
                this.test.EmotionForced ? reason : "Pick a fixed emotion first."))
        {
            var voice = this.ResolveVoice(speaker);
            this.SpeakFireAndForget(this.SpeakDirect(speaker, this.test.BuildDirectRequest(voice, bias: 0f)));
        }
    }

    /// <summary>Per-profile ▶ Test target (the voice tables): the courtesy line on the
    /// row's voice with the configured default exaggeration plus the row's bias. Faults
    /// surface through the same reporting wrapper as the Test-tab buttons.</summary>
    internal void SpeakVoiceTest(VoiceProfile profile) =>
        this.SpeakFireAndForget(this.SpeakDirect(
            new SpeakerIdentity(
                profile.SpeakerKey,
                SpeakerKeyView.Parse(profile.SpeakerKey).Name,
                null, null, null, null),
            TestBenchModel.BuildVoiceTestRequest(
                profile.ReferenceVoiceId,
                Math.Clamp(this.config.DefaultExaggeration + profile.ExaggerationBias, 0f, 1f))));

    /// <summary>The direct ISpeechSynthesizer + ISpeechQueue path — the exact tail of the
    /// game pipeline (SpeechRequestHandler), reused for forced-emotion auditions.</summary>
    private async Task SpeakDirect(SpeakerIdentity speaker, SynthesisRequest request)
    {
        var audio = await this.Synth.SynthesizeAsync(request, CancellationToken.None);
        this.queue.Enqueue(new SpeechItem(speaker, request, audio));
    }

    /// <summary>
    /// Resolves the deterministic voice for a test speaker WITHOUT touching assignment
    /// state: an existing profile wins; otherwise the pure xxHash slot pick runs over the
    /// race-map slots with no store entry — auditioning never writes
    /// voice-assignments.json.
    /// </summary>
    private string ResolveVoice(SpeakerIdentity speaker)
    {
        var existing = this.profiles.Entries.FirstOrDefault(e => e.SpeakerKey == speaker.Key);
        if (existing is not null)
        {
            return existing.ReferenceVoiceId;
        }

        var map = this.voiceMap();
        var group = VoiceGroupResolver.Resolve(speaker.Race, speaker.Tribe, speaker.Sex, null, null);
        return VoiceAssigner.AssignSlot(
            speaker.Key,
            map.SlotsFor(group, speaker.Race),
            new Dictionary<string, VoiceProfile>()).ReferenceVoiceId;
    }

    private async void SpeakFireAndForget(Task task, Action? onSettled = null)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            this.reportError($"Speech test failed: {ex.Message}");
        }
        finally
        {
            onSettled?.Invoke();
        }
    }
}
