namespace AIVoiceActing.Infrastructure.Storage;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;

/// <summary>Outcome of <see cref="VoicePackValidator.Validate"/>: errors make the pack invalid, warnings do not.</summary>
public sealed record VoicePackReport(
    VoicePackManifest? Manifest,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool IsValid => this.Errors.Count == 0;
}

/// <summary>
/// Authoring-time check of a voice pack folder, using the same manifest parser and the same
/// <see cref="FileVoiceLibrary"/> scan the plugin runs at startup, so a pack that passes here
/// loads in game. On top of that it enforces what a shipped pack promises: every referenced
/// file exists, reference clips are at most <see cref="MaxClipSeconds"/> long, and the pack
/// carries LICENSE and NOTICE files (warnings, since the runtime does not need them).
/// </summary>
public static class VoicePackValidator
{
    public const double MaxClipSeconds = 10.0;

    public static VoicePackReport Validate(string packDirectory)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var dir = Path.GetFullPath(packDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var manifestPath = Path.Combine(dir, VoicePackManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            errors.Add($"{VoicePackManifest.FileName} not found in {dir}.");
            return new VoicePackReport(null, errors, warnings);
        }

        VoicePackManifest manifest;
        try
        {
            manifest = VoicePackManifest.Parse(File.ReadAllText(manifestPath));
        }
        catch (VoicePackInvalidException e)
        {
            errors.Add($"Manifest rejected: {e.Message}");
            return new VoicePackReport(null, errors, warnings);
        }

        foreach (var (voiceId, file) in manifest.FindMissingFiles(file => File.Exists(Path.Combine(dir, file))))
        {
            errors.Add($"Voice \"{voiceId}\": file \"{file}\" is missing.");
        }

        // The runtime scan adds tensor-shape, truncation and symlink-escape checks.
        var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar)) ?? dir;
        var library = new FileVoiceLibrary(() => [parent]);
        var pack = library.Packs.FirstOrDefault(p => p.Directory.Length > 0 &&
            string.Equals(Path.GetFullPath(p.Directory), dir, StringComparison.Ordinal));
        if (pack is null)
        {
            errors.Add($"The voice library did not load this pack (is its id \"{manifest.Id}\" already taken by another pack?).");
        }
        else
        {
            // Missing files were already listed above; keep what the scan adds (bad tensors, escapes).
            errors.AddRange(pack.Problems.Where(problem =>
                !problem.Contains("file is missing", StringComparison.Ordinal)
                && !problem.Contains("transcript ", StringComparison.Ordinal)
                && !problem.Contains("preview ", StringComparison.Ordinal)));
        }

        foreach (var voice in manifest.Voices)
        {
            foreach (var clip in voice.Realizations.Where(r => r.Kind == RealizationKind.ReferenceClip))
            {
                var path = Path.Combine(dir, clip.File);
                if (File.Exists(path) && WavSeconds(path) is { } seconds && seconds > MaxClipSeconds)
                {
                    errors.Add($"Voice \"{voice.Id}\": clip \"{clip.File}\" is {seconds:0.0} s; keep reference clips at {MaxClipSeconds:0} s or less.");
                }

                if (clip.Transcript is null)
                {
                    warnings.Add($"Voice \"{voice.Id}\": clip \"{clip.File}\" has no transcript.");
                }
            }

            if (!voice.Realizations.Any(r => r.Kind == RealizationKind.StyleTensor))
            {
                warnings.Add($"Voice \"{voice.Id}\" has no style tensor (Kokoro cannot speak it).");
            }

            if (voice.PreviewFile is null)
            {
                warnings.Add($"Voice \"{voice.Id}\" has no previewFile.");
            }
        }

        foreach (var name in new[] { "LICENSE", "NOTICE" })
        {
            if (!Directory.EnumerateFiles(dir, name + "*", SearchOption.TopDirectoryOnly).Any())
            {
                warnings.Add($"The pack has no {name} file.");
            }
        }

        return new VoicePackReport(manifest, errors, warnings);
    }

    /// <summary>Duration of a PCM WAV from its header, or null when the header cannot be read.</summary>
    public static double? WavSeconds(string path)
    {
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            if (reader.ReadInt32() != 0x46464952 /* RIFF */)
            {
                return null;
            }

            reader.ReadInt32();
            if (reader.ReadInt32() != 0x45564157 /* WAVE */)
            {
                return null;
            }

            int? byteRate = null;
            while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
            {
                var id = reader.ReadInt32();
                var size = reader.ReadInt32();
                if (id == 0x20746d66 /* fmt  */)
                {
                    reader.ReadInt32(); // format tag, channels
                    reader.ReadInt32(); // sample rate
                    byteRate = reader.ReadInt32();
                    reader.BaseStream.Seek(size - 12, SeekOrigin.Current);
                }
                else if (id == 0x61746164 /* data */)
                {
                    var bytes = Math.Min(size, reader.BaseStream.Length - reader.BaseStream.Position);
                    return byteRate is > 0 ? bytes / (double)byteRate : null;
                }
                else
                {
                    reader.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
                }
            }

            return null;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException)
        {
            return null;
        }
    }
}
