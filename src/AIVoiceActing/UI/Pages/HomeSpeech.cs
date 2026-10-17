namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.Domain;
using AIVoiceActing.UI.State;

/// <summary>
/// Speaks a line from Home through the real pipeline (the handler, so voice assignment, text
/// processing and emotion all apply) as the one in-flight preview. Used by the Try it box and
/// the setup sample.
/// </summary>
internal static class HomeSpeech
{
    /// <summary>The speaker for an automatic voice (the same stock NPC the old test tab started on) or the player's own character.</summary>
    public static SpeakerIdentity Speaker(UiContext ctx, bool useMyCharacter)
    {
        var (name, world) = ctx.LocalPlayer() ?? default;
        var bench = new TestBenchModel { UseMyCharacter = useMyCharacter };
        return bench.BuildSpeaker(useMyCharacter ? name : null, useMyCharacter ? world : null);
    }

    /// <summary>Starts <paramref name="text"/> as the current preview under <paramref name="previewKey"/>.</summary>
    public static void Speak(UiContext ctx, string previewKey, SpeakerIdentity speaker, string text)
    {
        // A throwaway session reads no conversation history, exactly what a first line would get
        // in game. It is torn down when the line settles so presses do not leak window entries.
        var sessionId = $"ui-home-{Guid.NewGuid():N}";
        _ = ctx.Preview.Start(
            previewKey,
            ct => ctx.Speech.SpeakAsync(sessionId, speaker, text, ct),
            onSettled: () => ctx.Sessions().EndSession(sessionId));
    }
}
