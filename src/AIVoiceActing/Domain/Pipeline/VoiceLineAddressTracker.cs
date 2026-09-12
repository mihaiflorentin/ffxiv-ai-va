namespace AIVoiceActing.Domain.Pipeline;

/// <summary>
/// Address bookkeeping for the voiced-line courtesy (pure core of TextToTalk's
/// SoundHandler): tracks which resource-data addresses hold the game's voice lines.
/// Classification runs under the ignored-tree gate, but the Add/Remove bookkeeping runs
/// OUTSIDE it — an ignored-tree sound (music, vfx, Vo_Emote, …) reusing an address must
/// still clear it, or a later playback at that address would be mistaken for a voice line.
/// A voice line is assumed to play only once after it loads, so playback consumes the entry.
/// </summary>
public sealed class VoiceLineAddressTracker
{
    private readonly HashSet<nint> knownVoiceLinePtrs = [];

    /// <summary>
    /// Records a loaded .scd container: registers voice lines, and clears an address a
    /// non-voice-line sound has reused. Returns a log note when an address was cleared,
    /// null otherwise.
    /// </summary>
    public string? OnSoundLoaded(string fileName, nint resourceDataPtr)
    {
        var isVoiceLine = false;

        if (!VoiceLinePathMatcher.IsIgnoredSound(fileName))
        {
            if (VoiceLinePathMatcher.IsVoiceLine(fileName))
            {
                isVoiceLine = true;
            }
        }

        if (isVoiceLine)
        {
            this.knownVoiceLinePtrs.Add(resourceDataPtr);
            return null;
        }

        // Addresses can be reused, so a non-voice-line sound may load to an address
        // previously occupied by a voice line.
        return this.knownVoiceLinePtrs.Remove(resourceDataPtr)
            ? $"Cleared voice line from address {resourceDataPtr:x} (address reused by: {fileName})"
            : null;
    }

    /// <summary>True when the played sound was a known voice line (consumes the entry).</summary>
    public bool OnSoundPlayed(nint soundDataPtr) => this.knownVoiceLinePtrs.Remove(soundDataPtr);
}
