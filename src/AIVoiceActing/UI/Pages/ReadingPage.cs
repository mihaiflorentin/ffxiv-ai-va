namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.Domain.Pipeline;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// What's read: the old Chat tab minus the master switch (Home) and the keybind and
/// experimental sections (Settings). Capture sources, chat filters, name-with-say, rate
/// limiting, channel presets and triggers/exclusions; the Reading page ticket consolidates them.
/// </summary>
public sealed class ReadingPage(UiContext ctx) : IPage
{
    private readonly ChannelPresetsPanel channels = new(ctx);

    public NavTab Tab => NavTab.Reading;

    public void Draw()
    {
        var c = ctx.Config;
        var save = ctx.Save;

        if (ImGui.CollapsingHeader("General", ImGuiTreeNodeFlags.DefaultOpen))
        {
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

        if (ImGui.CollapsingHeader("Channel Settings", ImGuiTreeNodeFlags.DefaultOpen))
        {
            this.channels.Draw();
        }

        if (ImGui.CollapsingHeader("Triggers/Exclusions"))
        {
            ImGui.TextWrapped(
                "Triggers restrict which lines are spoken (empty = everything); an exclusion wins " +
                "over any trigger. Entries may be plain substrings or regex.");
            TriggerList.Draw("Triggers", c.Triggers, save);
            ImGui.Spacing();
            TriggerList.Draw("Exclusions", c.Exclusions, save);
        }
    }
}
