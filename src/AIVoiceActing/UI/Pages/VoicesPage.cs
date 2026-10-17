namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// Voices: the old General Voices, NPC Voices and Player Voices tabs as three views behind a
/// segmented control (casting defaults, NPC overrides, player overrides). The Voices page ticket
/// replaces this with the voice library and the "who sounds like who" list.
/// </summary>
public sealed class VoicesPage : IPage
{
    private static readonly string[] Views = ["Casting defaults", "NPC voices", "Player voices"];

    private readonly CastingDefaultsView defaults;
    private readonly CharactersView npcs;
    private readonly CharactersView players;
    private int view;

    public VoicesPage(UiContext ctx)
    {
        this.defaults = new CastingDefaultsView(ctx);
        this.npcs = new CharactersView(ctx, "npc:", player: false);
        this.players = new CharactersView(ctx, "pc:", player: true);
    }

    public NavTab Tab => NavTab.Voices;

    public void Draw()
    {
        this.view = SegmentedControl.Draw("voices-view", Views, this.view);
        Ui.Gap();
        switch (this.view)
        {
            case 1:
                this.npcs.Draw();
                break;
            case 2:
                this.players.Draw();
                break;
            default:
                this.defaults.Draw();
                break;
        }
    }
}
