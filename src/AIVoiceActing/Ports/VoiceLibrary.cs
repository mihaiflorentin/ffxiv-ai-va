namespace AIVoiceActing.Ports;

/// <summary>Raised when a <c>voicepack.json</c> is structurally invalid (the whole pack is rejected).</summary>
public sealed class VoicePackInvalidException(string message, Exception? inner = null)
    : Exception(message, inner)
{
}

/// <summary>How much a listening pass vouches for a voice.</summary>
public enum VoiceQuality
{
    /// <summary>Ships with the engine (Kokoro bank).</summary>
    Stock,

    /// <summary>A pack voice that passed a human listening pass.</summary>
    Verified,

    /// <summary>A pack voice nobody has vetted yet.</summary>
    Experimental,
}

/// <summary>
/// One way an engine can speak a voice. <see cref="Path"/> is absolute; it is empty for
/// stock Kokoro voices, which are addressed by id inside the engine's own bank.
/// <see cref="Phonemizer"/> (<c>en-us</c>, <c>en-gb</c>, ...) names the G2P a style tensor
/// is paired with. <see cref="Transcript"/> is the absolute path of a clip's text file.
/// <see cref="Available"/> is false when the referenced file is missing or malformed.
/// </summary>
public sealed record VoiceRealization(
    RealizationKind Kind,
    string Engine,
    string Path,
    string? Phonemizer,
    string? Transcript,
    bool Available = true);

/// <summary>
/// A voice the user can cast. <see cref="Id"/> is the stable key stored in profiles and
/// presets; <see cref="Accent"/> is a code from the canonical accent list; <see cref="Sex"/>
/// is <c>female</c>, <c>male</c> or <c>neutral</c>; <see cref="AgeBand"/> is <c>child</c>,
/// <c>young</c>, <c>adult</c> or <c>senior</c>. A voice with <see cref="Available"/> false
/// keeps its place in the library (so profiles can show it as missing) but must not be cast.
/// </summary>
public sealed record VoiceInfo(
    string Id,
    string DisplayName,
    string Accent,
    string Sex,
    string AgeBand,
    string Description,
    string License,
    string? Attribution,
    IReadOnlyList<string> Tags,
    VoiceQuality Quality,
    string? PreviewFile,
    string PackId,
    IReadOnlyList<VoiceRealization> Realizations,
    bool Available = true,
    string? UnavailableReason = null);

/// <summary>A scanned voice pack. <see cref="Problems"/> are user-readable one-liners.</summary>
public sealed record VoicePackInfo(
    string Id,
    string Name,
    string Version,
    string License,
    long Bytes,
    int VoiceCount,
    bool Enabled,
    IReadOnlyList<string> Problems,
    string Directory = "");

/// <summary>Engine-agnostic catalog of voices: the stock Kokoro bank plus voice packs on disk.</summary>
public interface IVoiceLibrary
{
    /// <summary>Every voice of every enabled pack (unavailable ones included, flagged), stable order.</summary>
    IReadOnlyList<VoiceInfo> Voices { get; }

    /// <summary>Every pack found, enabled or not, stock first.</summary>
    IReadOnlyList<VoicePackInfo> Packs { get; }

    /// <summary>The voice for an id (case-sensitive), or null when no enabled pack declares it.</summary>
    VoiceInfo? Find(string? id);

    /// <summary>True when the id is declared by an enabled pack and can be cast right now.</summary>
    bool Has(string? id);

    /// <summary>Rescans every pack root; picks up dropped, removed and disabled packs.</summary>
    void Reload();
}
