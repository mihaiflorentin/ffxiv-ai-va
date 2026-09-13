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
    private readonly ISpeechSynthesizer synthesizer;
    private readonly ISpeechQueue queue;
    private readonly IProfileStore profiles;
    private readonly Func<IReadOnlyList<ModelAsset>> modelAssets;
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
        ISpeechSynthesizer synthesizer,
        ISpeechQueue queue,
        IProfileStore profiles,
        Func<IReadOnlyList<ModelAsset>> modelAssets,
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
        EngineReady = () => this.synthesizer.IsReady,
        EngineNotReadyReason = ModelsTabModel.EngineNotReadyHint,
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
            Controls.Checkbox("Use keybind to toggle TTS", () => c.UseKeybind, v => { c.UseKeybind = v; save(); });
            if (c.UseKeybind)
            {
                ImGui.Indent();
                Controls.KeyCombo("Modifier", Modifiers, () => c.ModifierKey, v => { c.ModifierKey = v; save(); });
                Controls.KeyCombo("Key", MajorKeys, () => c.MajorKey, v => { c.MajorKey = v; save(); });
                ImGui.Unindent();
            }
        }

        if (ImGui.CollapsingHeader("General", ImGuiTreeNodeFlags.DefaultOpen))
        {
            Controls.Checkbox("Enabled", () => c.Enabled, v => { c.Enabled = v; save(); });
            Controls.SliderScaled("Global volume %", 200f, () => c.GlobalVolume, v => c.GlobalVolume = v, save);

            Controls.Section("Quest dialogue");
            Controls.Checkbox("Read quest Talk", () => c.ReadFromQuestTalkAddon, v => { c.ReadFromQuestTalkAddon = v; save(); });
            Controls.IndentedCheckbox(
                "Cancel speech on text advance", () => c.CancelSpeechOnTextAdvance, v => { c.CancelSpeechOnTextAdvance = v; save(); });
            Controls.IndentedCheckbox(
                "Skip quest text the game voices (courtesy)", () => c.SkipVoicedQuestText, v => { c.SkipVoicedQuestText = v; save(); });

            Controls.Section("Battle dialogue");
            Controls.Checkbox("Read BattleTalk", () => c.ReadFromBattleTalkAddon, v => { c.ReadFromBattleTalkAddon = v; save(); });
            Controls.IndentedCheckbox(
                "Skip BattleTalk the game voices (courtesy)", () => c.SkipVoicedBattleText, v => { c.SkipVoicedBattleText = v; save(); });

            Controls.Section("Chat");
            Controls.Checkbox("Skip messages from you", () => c.SkipMessagesFromYou, v => { c.SkipMessagesFromYou = v; save(); });
            Controls.Checkbox("Only messages from you", () => c.OnlyMessagesFromYou, v => { c.OnlyMessagesFromYou = v; save(); });

            Controls.Section("Name lines with \"say\"");
            Controls.Checkbox("Enable name with say", () => c.EnableNameWithSay, v => { c.EnableNameWithSay = v; save(); });
            Controls.IndentedCheckbox("Name NPCs with say", () => c.NameNpcWithSay, v => { c.NameNpcWithSay = v; save(); });
            Controls.IndentedCheckbox("Say player world name", () => c.SayPlayerWorldName, v => { c.SayPlayerWorldName = v; save(); });
            Controls.IndentedCheckbox("Disallow multiple say", () => c.DisallowMultipleSay, v => { c.DisallowMultipleSay = v; save(); });
            Controls.IndentedCheckbox("Say partial name", () => c.SayPartialName, v => { c.SayPartialName = v; save(); });
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
                Controls.DragFloat("Messages per second", 0.1f, 30f, () => c.MessagesPerSecond, v => c.MessagesPerSecond = v, save);
                ImGui.Unindent();
            }
        }

        if (ImGui.CollapsingHeader("Engine", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var status = this.synthesizer.IsReady
                ? "ready"
                : $"models missing ({this.RequiredAssetCount()})";
            ImGui.TextUnformatted($"Voice engine: {status}");
            Controls.Combo(
                "Execution provider",
                ExecutionProviders,
                () => c.SelectedEp,
                v => { c.SelectedEp = v; save(); });
            Controls.SliderFraction("Default exaggeration", () => c.DefaultExaggeration, v => c.DefaultExaggeration = v, save);
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

    private int RequiredAssetCount() => this.modelStore().Missing().Count;

    // ---- Tab 2: Models ----

    private void DrawModelsTab()
    {
        var provisioner = this.provisioner();
        var anyDownload = this.downloading is not null;

        ImGui.TextWrapped(
            "Local Chatterbox ONNX assets (~0.9 GB required). Downloads go to the plugin's " +
            "models directory and run only when you press a button.");

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

        if (ImGui.BeginTable("##models", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Asset", ImGuiTableColumnFlags.WidthStretch, 3f);
            ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 150f);
            ImGui.TableSetupColumn("##action", ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableHeadersRow();

            foreach (var asset in this.modelAssets())
            {
                var row = ModelsTabModel.Row(asset, provisioner.IsDownloaded(asset));
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Name);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{row.SizeMb:0.0} MB");
                ImGui.TableNextColumn();
                var status = this.downloading?.FileName == asset.FileName
                    ? "downloading…"
                    : ModelsTabModel.StatusLabel(row);
                ImGui.TextUnformatted(status);
                ImGui.TableNextColumn();
                if (Controls.Button(
                        "Download",
                        ModelsTabModel.CanDownload(anyDownload, row),
                        anyDownload ? "A download is already in progress." : null))
                {
                    this.StartDownload(asset);
                }
            }

            ImGui.EndTable();
        }
    }

    private void StartDownload(ModelAsset asset)
    {
        this.downloading = asset;
        this.progressReceived = 0;
        this.progressTotal = asset.SizeBytes ?? 0;
        var provisioner = this.provisioner();
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
            }
            catch (Exception ex)
            {
                this.reportError($"Download of \"{asset.Name}\" failed: {ex.Message}");
            }
            finally
            {
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
            });

            Controls.Checkbox("Use keybind for this preset", () => preset.UseKeybind, v =>
            {
                preset.UseKeybind = v;
                save();
            });
            if (preset.UseKeybind)
            {
                ImGui.Indent();
                Controls.KeyCombo("Modifier", Modifiers, () => preset.ModifierKey, v => { preset.ModifierKey = v; save(); });
                Controls.KeyCombo("Key", MajorKeys, () => preset.MajorKey, v => { preset.MajorKey = v; save(); });
                ImGui.Unindent();
            }

            if (!preset.EnableAllChatTypes)
            {
                preset.EnabledChatTypes ??= new List<int>();
                var enabled = preset.EnabledChatTypes;
                ImGui.TextWrapped("Channels this preset reads:");
                var channels = ChannelNames.All();
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
        var ready = this.synthesizer.IsReady;

        var text = this.test.Text;
        if (ImGui.InputTextMultiline("##test-text", ref text, 512, new Vector2(-1, 60)))
        {
            this.test.Text = text;
        }

        Controls.Checkbox("Use my character", () => this.test.UseMyCharacter, v => this.test.UseMyCharacter = v);
        if (!this.test.UseMyCharacter)
        {
            var race = Races.All[Math.Clamp(this.test.RaceIndex, 0, Races.All.Count - 1)];
            Controls.Combo("Race", [.. Races.All.Select(r => r.Name)], () => this.test.RaceIndex, v =>
            {
                this.test.RaceIndex = v;
                this.test.TribeIndex = 0;
            });
            Controls.Combo("Tribe", [.. race.Tribes.Select(t => t.Name)], () => this.test.TribeIndex, v => this.test.TribeIndex = v);
            Controls.Combo("Sex", ["Male", "Female"], () => (int)this.test.Sex, v => this.test.Sex = (byte)v);
        }

        Controls.Combo("Emotion", TestBenchModel.Emotions, () => this.test.EmotionIndex, v => this.test.EmotionIndex = v);
        Controls.HelpMarker(
            "\"auto\" uses the rules table (or director) exactly like the game path; a fixed " +
            "emotion auditions that delivery with the slider below.");
        if (this.test.EmotionForced)
        {
            Controls.SliderFraction("Exaggeration", () => this.test.Exaggeration, v => this.test.Exaggeration = v, () => { });
        }

        var plan = this.test.EmotionForced ? this.test.ForcedPlan() : this.test.RulesPlan();
        ImGui.TextDisabled(
            $"Plan: {plan.Emotion} @ {plan.Exaggeration:0.00}" +
            (plan.Tags.Count > 0 ? $" [{string.Join(", ", plan.Tags)}]" : string.Empty));

        var (myName, myWorld) = this.localPlayer() ?? default;
        var speaker = this.test.BuildSpeaker(this.test.UseMyCharacter ? myName : null, myWorld);

        if (Controls.Button("Speak", ready, ModelsTabModel.EngineNotReadyHint))
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
        if (Controls.Button("Speak with context", ready, ModelsTabModel.EngineNotReadyHint))
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
                this.test.EmotionForced ? ModelsTabModel.EngineNotReadyHint : "Pick a fixed emotion first."))
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
        var audio = await this.synthesizer.SynthesizeAsync(request, CancellationToken.None);
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
