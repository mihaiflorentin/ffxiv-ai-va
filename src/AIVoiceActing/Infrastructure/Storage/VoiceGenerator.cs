namespace AIVoiceActing.Infrastructure.Storage;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;

/// <summary>
/// Blends a character's own voice out of two or three existing English voices (same phonemizer,
/// same sex) and stores it. The choice of voices and weights is <see cref="VoiceBlend"/>'s
/// (deterministic from the speaker key); this class only reads the source tensors, mixes them and
/// hands the result to the <see cref="IGeneratedVoiceStore"/>. Stock tensors are read from the
/// plugin's voice bank folder, pack tensors from the path their library entry names.
/// </summary>
public sealed class VoiceGenerator : IVoiceGenerator
{
    private readonly IVoiceLibrary library;
    private readonly IGeneratedVoiceStore store;
    private readonly Func<string?> stockVoicesDir;
    private readonly ILogSink? log;
    private readonly Func<DateTimeOffset> clock;
    private readonly object gate = new();

    public VoiceGenerator(
        IVoiceLibrary library,
        IGeneratedVoiceStore store,
        Func<string?> stockVoicesDir,
        ILogSink? log = null,
        Func<DateTimeOffset>? clock = null)
    {
        this.library = library;
        this.store = store;
        this.stockVoicesDir = stockVoicesDir;
        this.log = log;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public GeneratedVoiceRecord? Ensure(
        string speakerKey, string displayName, IReadOnlyList<string> castVoiceIds, string? preferredSex)
    {
        lock (this.gate)
        {
            if (this.store.ForSpeaker(speakerKey) is { } existing && this.store.FindVoice(existing.Id) is not null)
            {
                return existing;
            }

            return this.Make(speakerKey, displayName, 0, castVoiceIds, preferredSex);
        }
    }

    public GeneratedVoiceRecord? Regenerate(string voiceId)
    {
        lock (this.gate)
        {
            if (this.store.Find(voiceId) is not { } old)
            {
                return null;
            }

            var made = this.Make(old.SpeakerKey, old.DisplayName, old.Salt + 1, old.Pool, old.Sex);
            if (made is not null)
            {
                this.store.Remove(old.Id);
            }

            return made;
        }
    }

    private GeneratedVoiceRecord? Make(
        string speakerKey, string displayName, int salt, IReadOnlyList<string> castVoiceIds, string? preferredSex)
    {
        var cast = castVoiceIds
            .Distinct(StringComparer.Ordinal)
            .Select(id => this.library.Find(id))
            .OfType<VoiceInfo>()
            .Select(VoiceBlend.CandidateFor)
            .OfType<BlendCandidate>()
            .ToList();
        var widen = this.library.Voices
            .Select(VoiceBlend.WidenCandidateFor)
            .OfType<BlendCandidate>()
            .ToList();

        var plan = VoiceBlend.Plan(speakerKey, salt, cast, widen, preferredSex);
        if (plan is null)
        {
            this.log?.Info($"No generated voice for \"{speakerKey}\": fewer than two matching English voices.");
            return null;
        }

        var tensors = new List<float[]>();
        foreach (var part in plan.Parts)
        {
            var tensor = this.ReadTensor(part.VoiceId);
            if (tensor is null)
            {
                this.log?.Warn($"No generated voice for \"{speakerKey}\": the tensor of \"{part.VoiceId}\" is unreadable.");
                return null;
            }

            tensors.Add(tensor);
        }

        var record = new GeneratedVoiceRecord(
            plan.VoiceId,
            speakerKey,
            displayName,
            plan.Phonemizer,
            plan.Sex,
            salt,
            plan.Parts,
            [.. castVoiceIds],
            this.clock());
        try
        {
            this.store.Save(record, VoiceBlend.Mix(tensors, [.. plan.Parts.Select(p => p.Weight)]));
        }
        catch (GeneratedVoiceStoreException e)
        {
            this.log?.Warn(e.Message);
            return null;
        }

        this.log?.Info(
            $"Generated voice \"{record.Id}\" for \"{speakerKey}\" from " +
            string.Join(", ", plan.Parts.Select(p => $"{p.VoiceId} {p.Weight:P0}")) + ".");
        return record;
    }

    private float[]? ReadTensor(string voiceId)
    {
        if (this.library.Find(voiceId) is not { } voice)
        {
            return null;
        }

        var realization = voice.Realizations.FirstOrDefault(r =>
            r.Kind == RealizationKind.StyleTensor
            && r.Available
            && string.Equals(r.Engine, VoicePackManifest.KokoroEngine, StringComparison.OrdinalIgnoreCase));
        if (realization is null)
        {
            return null;
        }

        var path = realization.Path.Length > 0
            ? realization.Path
            : this.stockVoicesDir() is { Length: > 0 } dir ? Path.Combine(dir, voiceId + ".npy") : null;
        if (path is null)
        {
            return null;
        }

        try
        {
            return NpyHeader.ReadStyleTensor(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
