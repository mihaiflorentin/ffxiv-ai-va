namespace AIVoiceActing.UI.State;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;

/// <summary>One persisted voice assignment in the clipboard share format.</summary>
public sealed record AssignmentShare(
    string SpeakerKey,
    string ReferenceVoiceId,
    float ExaggerationBias,
    float Pitch,
    float Speed,
    float Volume);

/// <summary>
/// Pure Characters-tab brain: sorts persisted assignments into a stable table view
/// (players first, then NPCs by display name) and wraps the share codec for the tab's
/// clipboard export/import. Import upserts stay in the window — they write through the
/// profile store port.
/// </summary>
public static class CharacterVoicesModel
{
    public static IReadOnlyList<VoiceProfile> BuildView(IReadOnlyCollection<VoiceProfile> entries) =>
    [
        .. entries
            .Select(profile => (Profile: profile, View: SpeakerKeyView.Parse(profile.SpeakerKey)))
            .OrderBy(pair => pair.View.IsPlayer ? 0 : 1)
            .ThenBy(pair => pair.View.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.Profile.SpeakerKey, StringComparer.Ordinal)
            .Select(pair => pair.Profile),
    ];

    public static string ShareText(IEnumerable<VoiceProfile> entries) =>
        ShareCodec.Encode(entries.Select(ToShare).ToList());

    /// <summary>Parses a share string; throws <see cref="ShareCodecException"/> on any
    /// framing, checksum, or payload problem.</summary>
    public static IReadOnlyList<AssignmentShare> ParseShare(string text) =>
        ShareCodec.Decode<List<AssignmentShare>>(text);

    public static AssignmentShare ToShare(VoiceProfile profile) => new(
        profile.SpeakerKey,
        profile.ReferenceVoiceId,
        profile.ExaggerationBias,
        profile.Pitch,
        profile.Speed,
        profile.Volume);
}
