namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.Domain.Pipeline;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// What's read: four cards (Story &amp; dialogue, Battle, Chat, Skip lines matching) in plain
/// words, then a collapsed "More options" section for the details most people never touch: speaker
/// name details, the rate limit, the chat preset's name and keys, the "only read matching lines"
/// list, and style tags. The styles command deep-links here through the
/// <see cref="ReadingAnchors.Styles"/> anchor, which opens More options and scrolls to style tags.
/// </summary>
public sealed class ReadingPage(UiContext ctx) : IPage
{
    private const int ScrollSettleFrames = 3;

    private static readonly TriggerListText SkipText = new(
        "Everything is read.",
        "Add a rule to skip matching lines.",
        "Add a rule",
        "Word or phrase to skip",
        "Remove this rule");

    private static readonly TriggerListText OnlyText = new(
        "Nothing is filtered this way.",
        "Add a word or phrase to read only the lines that contain one.",
        "Add a phrase",
        "Word or phrase to read",
        "Remove this phrase");

    private readonly ChatCard chat = new(ctx);
    private readonly StyleTagsPanel styles = new(ctx);
    private int scrollFrames;

    public NavTab Tab => NavTab.Reading;

    public void Draw()
    {
        var anchor = ctx.Nav.ConsumeAnchor(NavTab.Reading);
        var openMore = anchor == ReadingAnchors.Styles;
        if (openMore)
        {
            this.scrollFrames = ScrollSettleFrames;
        }

        using (var card = Card.Begin("reading-story"))
        {
            if (card.Visible)
            {
                this.DrawStory();
            }
        }

        using (var card = Card.Begin("reading-battle"))
        {
            if (card.Visible)
            {
                this.DrawBattle();
            }
        }

        using (var card = Card.Begin("reading-chat"))
        {
            if (card.Visible)
            {
                SectionHeader.Draw("Chat");
                this.chat.Draw();
            }
        }

        using (var card = Card.Begin("reading-skip"))
        {
            if (card.Visible)
            {
                SectionHeader.Draw("Skip lines matching…");
                Ui.MutedText("Lines that contain any of these words or phrases are not read, even in channels you have turned on.");
                TriggerList.Draw("skip", ctx.Config.Exclusions, ctx.Save, SkipText, ctx.Config.ShowAdvanced);
            }
        }

        if (openMore)
        {
            ImGui.SetNextItemOpen(true, ImGuiCond.Always);
        }

        if (ImGui.CollapsingHeader("More options##reading-more"))
        {
            this.DrawMoreOptions();
        }
    }

    private void DrawStory()
    {
        var c = ctx.Config;
        var save = ctx.Save;
        SectionHeader.Draw("Story & dialogue");
        CheckRow.Draw(
            "Read story dialogue",
            "Speaks the dialogue window you click through in quests and story scenes.",
            () => c.ReadFromQuestTalkAddon,
            v => { c.ReadFromQuestTalkAddon = v; save(); });
        CheckRow.Draw(
            "Stop the voice when I advance the text",
            "Cuts the current line the moment you click on, so speech never trails behind.",
            () => c.CancelSpeechOnTextAdvance,
            v => { c.CancelSpeechOnTextAdvance = v; save(); },
            indent: true);
        CheckRow.Draw(
            "Leave out lines the game already voices",
            "The original actors keep the lines they recorded.",
            () => c.SkipVoicedQuestText,
            v => { c.SkipVoicedQuestText = v; save(); },
            indent: true);
        CheckRow.Draw(
            "Read cutscene subtitles",
            "Speaks the captions of cutscenes that have no voice acting. Voiced lines are left alone.",
            () => c.ReadCutsceneSubtitles,
            v => { c.ReadCutsceneSubtitles = v; save(); });
        CheckRow.Draw(
            "Say the speaker's name before each line",
            "Helps you tell speakers apart. Details are under More options.",
            () => c.EnableNameWithSay,
            v => { c.EnableNameWithSay = v; save(); });
    }

    private void DrawBattle()
    {
        var c = ctx.Config;
        var save = ctx.Save;
        SectionHeader.Draw("Battle");
        CheckRow.Draw(
            "Read battle talk",
            "Speaks the lines characters call out during fights and in scenes outside the dialogue window.",
            () => c.ReadFromBattleTalkAddon,
            v => { c.ReadFromBattleTalkAddon = v; save(); });
        CheckRow.Draw(
            "Leave out lines the game already voices",
            "Avoids doubling lines the game plays itself.",
            () => c.SkipVoicedBattleText,
            v => { c.SkipVoicedBattleText = v; save(); },
            indent: true);
    }

    private void DrawMoreOptions()
    {
        var c = ctx.Config;
        var save = ctx.Save;

        SectionHeader.Draw("Speaker names");
        ImGui.BeginDisabled(!c.EnableNameWithSay);
        CheckRow.Draw(
            "Include NPC names",
            "Off keeps the spoken name to players only.",
            () => c.NameNpcWithSay,
            v => { c.NameNpcWithSay = v; save(); });
        CheckRow.Draw(
            "Include the player's home world",
            "Useful in crowded cross-world areas.",
            () => c.SayPlayerWorldName,
            v => { c.SayPlayerWorldName = v; save(); });
        CheckRow.Draw(
            "Name only one speaker per line",
            "Never speak more than one name, even when several speakers share a line.",
            () => c.DisallowMultipleSay,
            v => { c.DisallowMultipleSay = v; save(); });
        CheckRow.Draw(
            "Shorten long names",
            "Speaks only part of a long name to keep the lead-in short.",
            () => c.SayPartialName,
            v => { c.SayPartialName = v; save(); });
        CheckRow.Draw(
            "Use the last name instead of the first",
            "Applies when long names are shortened.",
            () => c.OnlySayFirstOrLastName == FirstOrLastName.Last,
            v =>
            {
                c.OnlySayFirstOrLastName = v ? FirstOrLastName.Last : FirstOrLastName.First;
                save();
            },
            indent: true);
        ImGui.EndDisabled();
        if (!c.EnableNameWithSay)
        {
            Ui.MutedText("Turn on \"Say the speaker's name before each line\" to use these.");
        }

        SectionHeader.Draw("Pace");
        CheckRow.Draw(
            "Limit how fast chat lines start",
            "Stops a flood of messages from piling up.",
            () => c.UsePlayerRateLimiter,
            v => { c.UsePlayerRateLimiter = v; save(); });
        if (c.UsePlayerRateLimiter)
        {
            ImGui.Indent(CheckRow.Offset());
            Controls.DragFloat(
                "Lines per second", 0.1f, 30f, () => c.MessagesPerSecond, v => c.MessagesPerSecond = v, save,
                "The most lines that may start each second.");
            ImGui.Unindent(CheckRow.Offset());
        }

        SectionHeader.Draw("Chat preset");
        this.chat.DrawPresetDetails();

        SectionHeader.Draw("Only read lines matching…");
        Ui.MutedText("When this list has entries, a line is read only if it contains one of them. Skip rules above still win.");
        TriggerList.Draw("only", c.Triggers, save, OnlyText, c.ShowAdvanced);

        SectionHeader.Draw("Style tags");
        if (this.scrollFrames > 0)
        {
            // Settles over a few frames: the cards above and this section change height as they open.
            ImGui.SetScrollHereY(0f);
            this.scrollFrames--;
        }

        this.styles.Draw();

        SectionHeader.Draw("Technical details");
        CheckRow.Draw(
            "Show advanced details",
            "Adds pattern matching to the rule lists and shows the pattern behind style tags.",
            () => c.ShowAdvanced,
            v => { c.ShowAdvanced = v; save(); });
    }
}
