namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.UI.State;

/// <summary>
/// Voices: the voice library on top (search, filters, preview, Use for, voice packs) and "Who
/// sounds like who" below (auto-casting, race and gender rows, beast tribes, characters). Casting
/// presets live in Settings; this page links there.
/// </summary>
public sealed class VoicesPage : IPage
{
    private readonly VoiceLibrarySection library;
    private readonly WhoSoundsSection whoSounds;

    public VoicesPage(UiContext ctx)
    {
        this.library = new VoiceLibrarySection(ctx);
        this.whoSounds = new WhoSoundsSection(ctx);
    }

    public NavTab Tab => NavTab.Voices;

    public void Draw()
    {
        this.library.Draw();
        this.whoSounds.Draw();
    }
}
