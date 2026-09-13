namespace AIVoiceActing.UI;

using System.Diagnostics;
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
/// Four-tab configuration window: Engine (models + status + engine selection), Voices,
/// Chat (capture, channels, triggers), Test. Driving adapter only: it reads/mutates
/// <see cref="Configuration"/> and calls the container-resolved ports passed in from the
/// plugin root — never Infrastructure concretes. Test buttons route through the exact
/// same speech path as the game (SpeechRequestHandler, or its ISpeechSynthesizer +
/// ISpeechQueue tail). Per-frame file/process probes are banned: the draw thread reads
/// cached snapshots refreshed at most once per interval.
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

    /// <summary>Full-fidelity log delegates: the window surfaces short messages on the
    /// status line, while the sink receives the detailed exception for debugging.</summary>
    private readonly Action<string> logInfo;
    private readonly Action<string, Exception?> logError;

    private readonly TestBenchModel test = new();
    private readonly VoiceEntryForm playerForm = new(player: true);
    private readonly VoiceEntryForm npcForm = new(player: false);
    private readonly VoiceTable playerTable;
    private readonly VoiceTable npcTable;

    // Download state, written from the download task, read on the draw thread.
    private volatile ModelAsset? downloading;
    private long progressReceived;
    private long progressTotal;

    /// <summary>Cancels the running download queue (C2); disposed when the queue settles.</summary>
    private CancellationTokenSource? downloadCts;

    /// <summary>Queue position for the progress caption: files total / files finished.</summary>
    private int downloadTotal;
    private int downloadDone;

    // In-flight test requests: speak buttons disable with a reason while > 0 (C6).
    private volatile int activeRequests;

    // Downloaded-state cache (C3): one wholesale refresh per second max; the draw
    // thread never touches File.Exists/FileInfo per asset per frame.
    private Dictionary<string, bool> downloadedCache = new(StringComparer.Ordinal);
    private DateTime downloadedCacheAt;

    // Models-dir size: recomputed on the thread pool every 10 s; draw thread reads the field.
    private long modelsDirBytesField;
    private DateTime modelsDirBytesAt;

    // Process memory: sampled at most once per second.
    private long processWorkingSet;
    private DateTime processSampleAt;

    // In-window status line (C5): errors (red, also routed to the external reporter)
    // and successes, auto-fading after ~10 s.
    private string? statusMessage;
    private bool statusIsError;
    private DateTime statusMessageAt;

    private volatile bool warmingEngine;
    private string? warmingEngineKey;

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
        Action<string> reportError,
        Action<string> logInfo,
        Action<string, Exception?> logError)
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
        this.logInfo = logInfo;
        this.logError = logError;

        this.Size = new Vector2(620, 520);
        this.SizeCondition = ImGuiCond.FirstUseEver;

        this.playerTable = this.CreateTable(showWorld: true);
        this.npcTable = this.CreateTable(showWorld: false);
    }

    private VoiceTable CreateTable(bool showWorld) => new(showWorld, this.save)
    {
        SetOverride = this.profiles.SetOverride,
        Remove = key => this.profiles.Remove(key),
        SpeakTest = profile => this.SpeakVoiceTest(profile),
        EngineReady = () => this.Synth.IsReady && this.activeRequests == 0,
        EngineNotReadyReason = () => this.Synth.IsReady ? "Synthesizing…" : this.EngineReason(),
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

        if (ImGui.BeginTabItem("Engine"))
        {
            this.DrawEngineTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Voices"))
        {
            this.DrawVoicesTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Chat"))
        {
            this.DrawChatTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Test"))
        {
            this.DrawTestTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
        this.DrawStatusLine();
    }

    /// <summary>In-window status line: one pinned line, red on error, fading after ~10 s.</summary>
    private void DrawStatusLine()
    {
        if (this.statusMessage is not { } message
            || (DateTime.UtcNow - this.statusMessageAt).TotalSeconds > 10)
        {
            return;
        }

        ImGui.Separator();
        ImGui.TextColored(
            this.statusIsError ? new Vector4(1f, 0.4f, 0.4f, 1f) : new Vector4(0.5f, 1f, 0.5f, 1f),
            message);
    }

    private void ReportStatus(string message, bool isError)
    {
        this.statusMessage = message;
        this.statusIsError = isError;
        this.statusMessageAt = DateTime.UtcNow;
        if (isError)
        {
            this.reportError(message);
        }
    }

    // ---- Tab 1: Engine ----

    // Section copy for the engine cards. Keys line up with the catalog group names so
    // the rows can be listed without the UI knowing engine internals.
    private static readonly (string Key, string Title, string Blurb)[] EngineSections =
    [
        ("kokoro", "Kokoro — fast narration (default)",
            "One 310 MB model; the 50+ voice banks ship inside the plugin. Robotic but instant — good for chat."),
        ("turbo", "Chatterbox Turbo — quality + speed",
            "Official ResembleAI export, all-fp32 (~3.2 GB). Real voice acting with [laugh]/[chuckle] tags, about 6x slower than real time on CPU."),
        ("f5", "F5-TTS — voice-acting quality (slow)",
            "Reference-clip cloning (~1.4 GB). Best delivery, but about 10x slower than real time on CPU — opt-in showcase engine."),
        ("chatterbox", "Legacy chatterbox (cloning)",
            "The original cloning engine (~1 GB). Kept for existing setups; Turbo supersedes it."),
    ];

    private static readonly string[] ChatterboxSectionGroups =
        [Infrastructure.Onnx.ModelCatalog.ChatterboxRequiredGroup, Infrastructure.Onnx.ModelCatalog.ChatterboxFp32LmGroup];

    private void DrawEngineTab()
    {
        var provisioner = this.provisioner();
        this.RefreshDownloadedCache(provisioner);
        this.RefreshProcessMemory();
        if ((DateTime.UtcNow - this.modelsDirBytesAt).TotalSeconds >= 10)
        {
            this.modelsDirBytesAt = DateTime.UtcNow;
            _ = Task.Run(this.SampleModelsDirBytes);
        }

        var anyDownload = this.downloading is not null;
        var ready = this.Synth.IsReady;

        // Status strip: cached fields only, never per-frame probes.
        ImGui.TextUnformatted(
            $"Engine: {this.config.SelectedEngine} — " +
            (this.warmingEngine ? "loading…"
                : ready ? "ready"
                : $"NOT ready — {this.EngineReason()}"));
        ImGui.TextDisabled(
            $"Queue: {this.queue.Depth}   Models folder: {this.modelsDirBytesField / (1024.0 * 1024.0):0} MB   " +
            $"Process memory: {this.processWorkingSet / (1024.0 * 1024.0):0} MB");

        var missingRequiredForSelected = this.RequiredForSelectedMissing();
        if (Controls.Button(
                "Load engine now",
                !this.warmingEngine && !ready,
                this.warmingEngine ? "Already loading."
                    : ready ? "Engine is ready."
                    : missingRequiredForSelected > 0 ? this.EngineReason()
                    : null))
        {
            this.StartWarmUp(this.config.SelectedEngine);
        }

        ImGui.SameLine();
        Controls.HelpMarker(
            "Builds the ONNX session ahead of the first line. The engine also loads itself " +
            "on login and on the first spoken line.");

        ImGui.SameLine();
        if (Controls.Button("Open Models Folder", !anyDownload, "A download is in progress."))
        {
            this.TryOpen(this.modelsDir());
        }

        ImGui.SameLine();
        if (Controls.Button("Open Voices Folder", !anyDownload, "A download is in progress."))
        {
            this.TryOpen(this.voicesDir());
        }

        if (anyDownload)
        {
            var total = this.progressTotal;
            var caption = ModelsTabModel.QueuePositionLabel(
                this.downloading!.Name,
                this.downloadDone + 1,
                Math.Max(this.downloadTotal, this.downloadDone + 1),
                this.progressReceived,
                total <= 0 ? null : total);
            ImGui.ProgressBar(
                (float)ModelsTabModel.OverallProgress(this.progressReceived, total <= 0 ? null : total),
                new Vector2(-130, 0),
                caption);
            ImGui.SameLine();
            if (Controls.Button("Cancel download", enabled: true, null))
            {
                try
                {
                    this.downloadCts?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Queue settled between draw and click.
                }
            }
        }

        ImGui.Separator();
        ImGui.TextWrapped(
            "Every engine is downloadable and switchable here. Downloads go to the plugin's " +
            "models directory and run only when you press a button. Switching engines is safe " +
            "mid-line: the line in flight finishes on the old engine, the next line uses the new one.");

        var assets = this.modelAssets();
        foreach (var (key, title, blurb) in EngineSections)
        {
            this.DrawEngineCard(key, title, blurb, assets, anyDownload);
        }
    }

    private void DrawEngineCard(string key, string title, string blurb, IReadOnlyList<(ModelAsset Asset, string Group)> assets, bool anyDownload)
    {
        var groupKeys = key == "chatterbox" ? ChatterboxSectionGroups : [key];
        var sectionAssets = assets.Where(a => groupKeys.Contains(a.Group)).ToList();
        var downloadedCount = sectionAssets.Count(a => this.IsDownloadedCached(a.Asset));
        var missingCount = sectionAssets.Count - downloadedCount;
        var active = this.config.SelectedEngine == key;

        var header = $"{title} ({downloadedCount}/{sectionAssets.Count})"
            + (active ? " — ACTIVE" : string.Empty)
            + $"##engine-card-{key}";
        if (!ImGui.CollapsingHeader(header, active ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
        {
            Controls.Tooltip(blurb);
            return;
        }

        Controls.Tooltip(blurb);
        ImGui.TextWrapped(blurb);
        ImGui.Spacing();

        string readiness;
        if (active)
        {
            readiness = this.warmingEngine && this.warmingEngineKey == key
                ? "loading…"
                : this.Synth.IsReady ? "ready" : this.EngineReason();
        }
        else
        {
            var missingRequired = sectionAssets.Count(a => !a.Asset.Optional && !this.IsDownloadedCached(a.Asset));
            readiness = missingRequired > 0
                ? $"not installed — {missingRequired} required asset(s) missing"
                : "installed — activates when set active";
        }

        ImGui.TextDisabled($"State: {readiness}");

        if (Controls.Button($"Set active##set-{key}", !active, active ? "Already the active engine." : null))
        {
            this.config.SelectedEngine = key;
            this.save();
            this.invalidateSynthesizer();
            this.StartWarmUp(key);
        }

        ImGui.SameLine();
        if (missingCount > 0
            && Controls.Button(
                ModelsTabModel.DownloadAllLabel(missingCount) + $"##dl-{key}",
                ModelsTabModel.CanDownloadAll(anyDownload, missingCount),
                anyDownload ? "A download is already in progress." : null))
        {
            this.StartDownloads(
                [.. sectionAssets.Where(a => !this.IsDownloadedCached(a.Asset)).Select(a => a.Asset)]);
        }

        // Device/impact knobs render inside the ACTIVE card only.
        if (active)
        {
            this.DrawActiveEngineKnobs();
        }

        // Per-engine asset table: Download fetches EXACTLY that asset.
        ImGui.Spacing();
        if (ImGui.BeginTable($"##engine-assets-{key}", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Asset", ImGuiTableColumnFlags.WidthStretch, 3f);
            ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 150f);
            ImGui.TableSetupColumn("##action", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableHeadersRow();

            foreach (var (asset, _) in sectionAssets)
            {
                var row = ModelsTabModel.Row(asset, this.IsDownloadedCached(asset));
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(asset.Optional ? $"{row.Name} (optional)" : row.Name);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{row.SizeMb:0.0} MB");
                ImGui.TableNextColumn();
                var status = this.downloading?.FileName == asset.FileName
                    ? "downloading…"
                    : ModelsTabModel.StatusLabel(row, requiredForSelectedEngine: active);
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
                    this.StartDownloads([asset]);
                }
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
    }

    /// <summary>Engine knobs that apply to whichever engine is active; drawn once per frame.</summary>
    private void DrawActiveEngineKnobs()
    {
        var c = this.config;
        var save = this.save;

        ImGui.Separator();
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

    /// <summary>Background warm-up for an engine: resolves the synthesizer once, reports
    /// superseded warm-ups as info, and drives the shared "loading…" state.</summary>
    private void StartWarmUp(string engineKey)
    {
        if (this.warmingEngine)
        {
            return;
        }

        // One instance for the whole warm-up: the property could otherwise re-resolve a
        // different engine mid-task.
        var synth = this.Synth;
        this.warmingEngine = true;
        this.warmingEngineKey = engineKey;
        this.logInfo($"Engine warm-up started: {engineKey} ({synth.GetType().Name}).");
        _ = Task.Run(async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await synth.WarmUpAsync(CancellationToken.None);
                this.logInfo($"Engine warm-up finished: {engineKey} in {stopwatch.Elapsed.TotalSeconds:0.0} s.");
            }
            catch (SpeechSynthesisEngineDisposedException)
            {
                this.logInfo("Engine warm-up superseded by an engine switch.");
                this.ReportStatus("Warm-up superseded by an engine switch.", isError: false);
            }
            catch (Exception ex)
            {
                this.logError($"Engine start failed for {engineKey}: {ex.Message}", ex);
                this.ReportStatus($"Engine start failed: {ex.Message}", isError: true);
            }
            finally
            {
                this.warmingEngine = false;
            }
        });
    }

    private bool IsDownloadedCached(ModelAsset asset) =>
        this.downloadedCache.GetValueOrDefault(asset.FileName);

    private void RefreshDownloadedCache(IModelProvisioner provisioner)
    {
        if ((DateTime.UtcNow - this.downloadedCacheAt).TotalSeconds < 1)
        {
            return;
        }

        this.downloadedCacheAt = DateTime.UtcNow;
        var cache = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (asset, _) in this.modelAssets())
        {
            cache[asset.FileName] = provisioner.IsDownloaded(asset);
        }

        this.downloadedCache = cache;
    }

    private void RefreshProcessMemory()
    {
        if ((DateTime.UtcNow - this.processSampleAt).TotalSeconds < 1)
        {
            return;
        }

        this.processSampleAt = DateTime.UtcNow;
        using var process = Process.GetCurrentProcess();
        this.processWorkingSet = process.WorkingSet64;
    }

    /// <summary>Thread-pool side of the models-dir size: walks the tree off the draw thread.</summary>
    private void SampleModelsDirBytes()
    {
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

        this.modelsDirBytesField = total;
    }

    /// <summary>Missing REQUIRED assets for the SELECTED engine (read from the cache).</summary>
    private int RequiredForSelectedMissing()
    {
        var groups = this.config.SelectedEngine == "chatterbox"
            ? ChatterboxSectionGroups
            : [this.config.SelectedEngine];
        return this.modelAssets()
            .Count(a => groups.Contains(a.Group)
                && !a.Asset.Optional
                && !this.IsDownloadedCached(a.Asset));
    }

    /// <summary>Live not-ready reason for tooltips; falls back to the generic hint.</summary>
    private string EngineReason() =>
        this.Synth.IsReady
            ? ModelsTabModel.EngineNotReadyHint
            : string.IsNullOrWhiteSpace(this.Synth.NotReadyReason)
                ? ModelsTabModel.EngineNotReadyHint
                : this.Synth.NotReadyReason;

    /// <summary>
    /// Starts a sequential background download queue: assets download one at a time in
    /// list order, and the in-flight marker never reads null between items, so the draw
    /// thread's idle gating stays exact. The queue stops at the first failure and is
    /// cancellable (C2).
    /// </summary>
    private void StartDownloads(IReadOnlyList<ModelAsset> assets)
    {
        if (assets.Count == 0 || this.downloading is not null)
        {
            return;
        }

        this.downloadCts?.Dispose();
        this.downloadCts = new CancellationTokenSource();
        this.downloadTotal = assets.Count;
        this.downloadDone = 0;
        this.logInfo($"Download queue started: {assets.Count} file(s): {string.Join(", ", assets.Select(a => a.Name))}.");
        this.DownloadNext(this.provisioner(), new Queue<ModelAsset>(assets), this.downloadCts);
    }

    private void DownloadNext(IModelProvisioner provisioner, Queue<ModelAsset> pending, CancellationTokenSource cts)
    {
        if (pending.Count == 0)
        {
            this.downloading = null;
            this.downloadTotal = 0;
            this.downloadCts = null;
            cts.Dispose();
            return;
        }

        var asset = pending.Dequeue();
        this.downloading = asset;
        this.progressReceived = 0;
        this.progressTotal = asset.SizeBytes ?? 0;
        this.logInfo($"Download started: {asset.Name} (file {this.downloadDone + 1} of {Math.Max(this.downloadTotal, this.downloadDone + 1)}).");
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
                    cts.Token);
                this.downloadDone++;
                this.logInfo($"Download finished: {asset.Name}.");
                this.ReportStatus($"Downloaded {asset.Name}.", isError: false);
                this.DownloadNext(provisioner, pending, cts);
            }
            catch (OperationCanceledException)
            {
                this.downloading = null;
                this.downloadCts = null;
                cts.Dispose();
                this.logInfo($"Download cancelled at \"{asset.Name}\".");
                this.ReportStatus("Download cancelled.", isError: false);
            }
            catch (Exception ex)
            {
                this.logError($"Download of \"{asset.Name}\" failed: {ex.Message}", ex);
                this.ReportStatus($"Download of \"{asset.Name}\" failed: {ex.Message}", isError: true);
                this.downloading = null;
                this.downloadCts = null;
                cts.Dispose();
            }
        });
    }

    private void TryOpen(string directory)
    {
        if (!this.openDirectory(directory))
        {
            this.ReportStatus($"Could not open \"{directory}\".", isError: true);
        }
    }

    // ---- Tab 2: Voices ----

    private void DrawVoicesTab()
    {
        var c = this.config;
        Controls.Checkbox(
            "Use race/gender voice presets for unlisted speakers",
            "Speakers without a manual override get a deterministic voice from their race/gender set.",
            () => c.UseRaceVoicePresets,
            v => { c.UseRaceVoicePresets = v; this.save(); });

        // Computed once per frame and handed to both tables — never per row.
        var voiceIds = this.VoiceOptions;

        if (ImGui.CollapsingHeader("Players", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextWrapped("Manual player overrides (name + world id). Overrides win over the automatic assignment and persist.");
            var entries = this.profiles.Entries
                .Where(e => e.Custom && e.SpeakerKey.StartsWith("pc:", StringComparison.Ordinal))
                .ToArray();
            this.playerTable.Draw("##players", entries, e => SpeakerKeyView.Parse(e.SpeakerKey), this.playerForm, voiceIds);
        }

        if (ImGui.CollapsingHeader("NPCs", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextWrapped("Manual NPC overrides by name. Overrides win over the automatic assignment and persist.");
            var entries = this.profiles.Entries
                .Where(e => e.Custom && e.SpeakerKey.StartsWith("npc:", StringComparison.Ordinal))
                .ToArray();
            this.npcTable.Draw("##npcs", entries, e => SpeakerKeyView.Parse(e.SpeakerKey), this.npcForm, voiceIds);
        }
    }

    // ---- Tab 3: Chat ----

    private void DrawChatTab()
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

        if (ImGui.CollapsingHeader("Experimental"))
        {
            Controls.Checkbox(
                "Remove stutter",
                "Strips repeated leading characters from stammering lines before synthesis.",
                () => c.RemoveStutter,
                v => { c.RemoveStutter = v; save(); });
        }

        if (ImGui.CollapsingHeader("Channel Settings", ImGuiTreeNodeFlags.DefaultOpen))
        {
            this.DrawChannelPresets();
        }

        if (ImGui.CollapsingHeader("Triggers/Exclusions"))
        {
            ImGui.TextWrapped(
                "Triggers restrict which lines are spoken (empty = everything); an exclusion wins " +
                "over any trigger. Entries may be plain substrings or regex.");
            TriggerList.Draw("Triggers", this.config.Triggers, this.save);
            ImGui.Spacing();
            TriggerList.Draw("Exclusions", this.config.Exclusions, this.save);
        }
    }

    private void DrawChannelPresets()
    {
        var c = this.config;
        var save = this.save;
        var presets = c.EnabledChatTypesPresets;
        var current = c.CurrentPreset ?? presets.FirstOrDefault();
        if (current is not null && current.Id != c.CurrentPresetId)
        {
            c.CurrentPresetId = current.Id;
            save(); // auto-heal: persist the corrected pointer immediately
        }

        var names = presets
            .Select(p => p.Name ?? $"#{p.Id}")
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

        ImGui.TextDisabled($"Current preset: {current?.Name ?? "—"}");

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

    // ---- Tab 4: Test ----

    private void DrawTestTab()
    {
        // Fresh window-open: the shared "ui-test" context window starts empty, so the
        // director never reads leftovers from a previous session with the tab open.
        if (ImGui.IsWindowAppearing())
        {
            this.sessions().EndSession(TestBenchModel.ContextSessionId);
        }

        var ready = this.Synth.IsReady;
        var reason = this.EngineReason();
        var busy = this.activeRequests > 0;

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

        var speakReason = busy ? "Synthesizing…" : reason;
        if (Controls.Button("Speak", ready && !busy, speakReason))
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
        if (Controls.Button("Speak with context", ready && !busy, speakReason))
        {
            // Director path: the shared window the cutscene pipeline feeds. The handler
            // appends the line AFTER planning, so the director never sees it twice.
            this.SpeakFireAndForget(
                this.speech.SpeakAsync(TestBenchModel.ContextSessionId, speaker, this.test.Text, CancellationToken.None));
        }

        ImGui.SameLine();
        if (Controls.Button(
                "Speak (forced emotion)",
                ready && !busy && this.test.EmotionForced,
                busy ? "Synthesizing…" : this.test.EmotionForced ? reason : "Pick a fixed emotion first."))
        {
            var voice = this.ResolveVoice(speaker);
            this.SpeakFireAndForget(this.SpeakDirect(speaker, this.BuildForcedRequest(voice)));
        }
    }

    /// <summary>The forced-emotion request through the game's text-processing chain
    /// (lexicon → stutter → style tags), so the audition matches what the game would say.</summary>
    private SynthesisRequest BuildForcedRequest(string voice)
    {
        var (processed, extractedTags) = this.speech.PrepareText(this.test.Text);
        var plan = this.test.ForcedPlan();
        var tags = plan.Tags.Concat(extractedTags).Distinct().ToList();
        return new SynthesisRequest(
            voice,
            processed,
            Math.Clamp(plan.Exaggeration, 0f, 1f),
            tags);
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

    /// <summary>Fire-and-forget wrapper: counts in-flight requests (disables the speak
    /// buttons), reports the elapsed time on success and faults on the status line.</summary>
    private async void SpeakFireAndForget(Task task, Action? onSettled = null)
    {
        this.activeRequests++;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await task;
            this.logInfo($"Test line finished in {stopwatch.Elapsed.TotalSeconds:0.0} s.");
            this.ReportStatus($"Spoke test line in {stopwatch.Elapsed.TotalSeconds:0.0} s.", isError: false);
        }
        catch (Exception ex)
        {
            this.logError($"Speech test failed: {ex.Message}", ex);
            this.ReportStatus($"Speech test failed: {ex.Message}", isError: true);
        }
        finally
        {
            this.activeRequests--;
            onSettled?.Invoke();
        }
    }
}
