namespace AIVoiceActing.Domain;

/// <summary>
/// The small bank of reference clips shipped with the plugin (<c>voices-clips/&lt;id&gt;.wav</c>) that a
/// cloning engine falls back on when a speaker's voice id has no clip of its own. Voice slots carry
/// Kokoro voice ids; this maps them onto the closest-sounding bundled clip. Unknown ids get the default.
/// </summary>
public static class ReferenceClipBank
{
    public const string DefaultClipId = "uk_male_casual";

    /// <summary>Folder next to the plugin assembly that holds the clips.</summary>
    public const string FolderName = "voices-clips";

    public static string ClipIdFor(string? voiceId) =>
        voiceId is not null && Aliases.TryGetValue(voiceId, out var clip) ? clip : DefaultClipId;

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["bm_lewis"] = "uk_male_casual",
        ["bm_fable"] = "uk_male_casual",
        ["am_eric"] = "uk_male_casual",
        ["am_echo"] = "uk_male_casual",
        ["im_nicola"] = "uk_male_casual",
        ["em_alex"] = "uk_male_casual",
        ["default"] = "uk_male_casual",
        ["bm_daniel"] = "uk_male_posh",
        ["bm_george"] = "uk_male_posh",
        ["am_michael"] = "uk_male_posh",
        ["hm_omega"] = "uk_male_posh",
        ["bf_lily"] = "uk_female_soft",
        ["bf_alice"] = "uk_female_soft",
        ["af_sarah"] = "uk_female_soft",
        ["af_nicole"] = "uk_female_soft",
        ["af_heart"] = "uk_female_soft",
        ["if_sara"] = "uk_female_soft",
        ["ef_dora"] = "uk_female_soft",
        ["pf_dora"] = "uk_female_soft",
        ["hf_alpha"] = "uk_female_soft",
        ["ff_siwis"] = "uk_female_posh",
        ["bf_emma"] = "uk_female_posh",
        ["bf_isabella"] = "uk_female_posh",
        ["am_onyx"] = "deep_male",
        ["am_adam"] = "deep_male",
        ["am_fenrir"] = "deep_male",
        ["af_alloy"] = "deep_female",
        ["af_river"] = "deep_female",
        ["af_aoede"] = "deep_female",
    };
}
