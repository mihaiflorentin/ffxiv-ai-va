namespace AIVoiceActing.Infrastructure.Storage;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;

/// <summary>
/// <see cref="IVoiceLibrary"/> over the embedded stock Kokoro manifest plus voice packs on disk.
/// Each root in <c>packRoots</c> (the bundled <c>voicepacks/</c> beside the plugin, then
/// <c>&lt;ConfigDir&gt;/voicepacks/</c>, then the user's extra directories, in that order) is
/// scanned for sub-folders holding a <c>voicepack.json</c>. Order is deterministic: roots in
/// the order given, folders ordinal-sorted, so on an id collision the first one wins and the
/// loser gets a problem line instead of silently overriding. A missing or malformed file
/// demotes just that voice (it stays listed, flagged unavailable); a structurally invalid
/// manifest or a pack without a license shows up as a problem-only pack. Stock voices are
/// always first, so a pack can never shadow one. <see cref="Reload"/> rescans and swaps the
/// whole snapshot atomically.
/// </summary>
public sealed class FileVoiceLibrary : IVoiceLibrary
{
    private const int TensorHeaderProbeBytes = 2048;

    private readonly Func<IReadOnlyList<string>> packRoots;
    private readonly Func<IReadOnlyCollection<string>> disabledPacks;
    private readonly ILogSink? log;
    private readonly (VoicePackInfo Pack, IReadOnlyList<VoiceInfo> Voices) core;
    private readonly object reloadGate = new();
    private volatile Snapshot snapshot = Snapshot.Empty;

    public FileVoiceLibrary(
        Func<IReadOnlyList<string>> packRoots,
        Func<IReadOnlyCollection<string>>? disabledPacks = null,
        ILogSink? log = null,
        (VoicePackInfo Pack, IReadOnlyList<VoiceInfo> Voices)? coreManifest = null)
    {
        this.packRoots = packRoots ?? throw new ArgumentNullException(nameof(packRoots));
        this.disabledPacks = disabledPacks ?? (() => []);
        this.log = log;
        this.core = coreManifest ?? CoreVoiceManifest.Load();
        this.Reload();
    }

    public IReadOnlyList<VoiceInfo> Voices => this.snapshot.Voices;

    public IReadOnlyList<VoicePackInfo> Packs => this.snapshot.Packs;

    public VoiceInfo? Find(string? id) =>
        id is not null && this.snapshot.ById.TryGetValue(id, out var voice) ? voice : null;

    public bool Has(string? id) => this.Find(id)?.Available == true;

    public void Reload()
    {
        lock (this.reloadGate)
        {
            this.snapshot = this.Scan();
        }
    }

    private Snapshot Scan()
    {
        var voices = new List<VoiceInfo>(this.core.Voices);
        var byId = voices.ToDictionary(voice => voice.Id, StringComparer.Ordinal);
        var packs = new List<PackBuilder> { new(this.core.Pack) };
        var packIds = new Dictionary<string, PackBuilder>(StringComparer.OrdinalIgnoreCase)
        {
            [this.core.Pack.Id] = packs[0],
        };
        var disabled = new HashSet<string>(this.disabledPacks(), StringComparer.OrdinalIgnoreCase);

        var seenRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in this.packRoots())
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string rootFull;
            try
            {
                rootFull = Path.GetFullPath(root);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!seenRoots.Add(rootFull) || !Directory.Exists(rootFull))
            {
                continue;
            }

            foreach (var dir in SafeDirectories(rootFull))
            {
                var manifestPath = Path.Combine(dir, VoicePackManifest.FileName);
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                this.ScanPack(dir, manifestPath, disabled, voices, byId, packs, packIds);
            }
        }

        foreach (var problem in packs.SelectMany(pack => pack.Problems.Select(p => $"{pack.Info.Id}: {p}")))
        {
            this.log?.Warn($"Voice library: {problem}");
        }

        var infos = packs.Select(pack => pack.Build()).ToList();
        this.log?.Info(
            $"Voice library: {voices.Count(v => v.Available)} usable voice(s) in {infos.Count} pack(s) " +
            $"({infos.Count(p => !p.Enabled)} disabled).");
        return new Snapshot(voices, infos, byId);
    }

    private void ScanPack(
        string dir,
        string manifestPath,
        HashSet<string> disabled,
        List<VoiceInfo> voices,
        Dictionary<string, VoiceInfo> byId,
        List<PackBuilder> packs,
        Dictionary<string, PackBuilder> packIds)
    {
        var folderName = Path.GetFileName(dir);
        VoicePackManifest manifest;
        try
        {
            manifest = VoicePackManifest.Parse(File.ReadAllText(manifestPath));
        }
        catch (Exception e) when (e is VoicePackInvalidException or IOException or UnauthorizedAccessException)
        {
            var broken = new PackBuilder(new VoicePackInfo(
                folderName, folderName, string.Empty, string.Empty, DirectoryBytes(dir), 0,
                !disabled.Contains(folderName), [], dir));
            broken.Problems.Add($"Pack rejected: {e.Message}");
            packs.Add(broken);
            return;
        }

        if (packIds.TryGetValue(manifest.Id, out var winner))
        {
            winner.Problems.Add($"Ignored a second pack with the same id at \"{dir}\".");
            return;
        }

        var builder = new PackBuilder(new VoicePackInfo(
            manifest.Id, manifest.Name, manifest.Version, manifest.License.Spdx, DirectoryBytes(dir),
            manifest.Voices.Count, !disabled.Contains(manifest.Id), [], dir));
        packs.Add(builder);
        packIds[manifest.Id] = builder;
        if (!builder.Info.Enabled)
        {
            return;
        }

        var packReal = RealPath(dir);
        var added = 0;
        foreach (var voice in manifest.Voices)
        {
            if (byId.TryGetValue(voice.Id, out var existing))
            {
                builder.Problems.Add(
                    $"Voice \"{voice.Id}\" skipped: already provided by pack \"{existing.PackId}\".");
                continue;
            }

            var info = BuildVoice(manifest, voice, dir, packReal, builder.Problems);
            voices.Add(info);
            byId[info.Id] = info;
            added++;
        }

        builder.VoiceCount = added;
    }

    private static VoiceInfo BuildVoice(
        VoicePackManifest manifest,
        PackVoice voice,
        string dir,
        string packReal,
        List<string> problems)
    {
        var reasons = new List<string>();
        var realizations = new List<VoiceRealization>();
        foreach (var declared in voice.Realizations)
        {
            var path = ResolveInside(dir, packReal, declared.File, out var pathProblem);
            string? problem = pathProblem;
            if (problem is null && declared.Kind == RealizationKind.StyleTensor)
            {
                problem = ValidateTensorFile(path!);
            }

            if (problem is null && !File.Exists(path))
            {
                problem = "file is missing";
            }

            if (problem is not null)
            {
                reasons.Add($"{declared.File}: {problem}");
            }

            string? transcript = null;
            if (declared.Transcript is { } transcriptRel)
            {
                var transcriptPath = ResolveInside(dir, packReal, transcriptRel, out var transcriptProblem);
                if (transcriptProblem is null && File.Exists(transcriptPath))
                {
                    transcript = transcriptPath;
                }
                else
                {
                    problems.Add($"Voice \"{voice.Id}\": transcript {transcriptRel} is unavailable.");
                }
            }

            realizations.Add(new VoiceRealization(
                declared.Kind,
                declared.Engine,
                path ?? Path.Combine(dir, declared.File),
                declared.Phonemizer,
                transcript,
                Available: problem is null));
        }

        string? preview = null;
        if (voice.PreviewFile is { } previewRel)
        {
            var previewPath = ResolveInside(dir, packReal, previewRel, out var previewProblem);
            if (previewProblem is null && File.Exists(previewPath))
            {
                preview = previewPath;
            }
            else
            {
                problems.Add($"Voice \"{voice.Id}\": preview {previewRel} is unavailable.");
            }
        }

        var available = realizations.Any(r => r.Available);
        string? unavailableReason = null;
        if (!available)
        {
            unavailableReason = string.Join("; ", reasons);
            problems.Add($"Voice \"{voice.Id}\" is unavailable ({unavailableReason}).");
        }
        else if (reasons.Count > 0)
        {
            problems.Add($"Voice \"{voice.Id}\" lost a realization ({string.Join("; ", reasons)}).");
        }

        return new VoiceInfo(
            voice.Id,
            voice.Name,
            voice.Accent,
            voice.Sex,
            voice.AgeBand,
            voice.Description,
            manifest.License.Spdx,
            manifest.License.Attribution,
            voice.Tags,
            voice.Quality,
            preview,
            manifest.Id,
            realizations,
            available,
            unavailableReason);
    }

    /// <summary>Absolute path of a pack-relative file, or null plus the reason when it would leave the pack folder.</summary>
    private static string? ResolveInside(string dir, string packReal, string relative, out string? problem)
    {
        problem = null;
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            problem = "path is invalid";
            return null;
        }

        var dirFull = Path.GetFullPath(dir);
        if (!IsUnder(dirFull, full))
        {
            problem = "path escapes the pack folder";
            return null;
        }

        // Symlink escape: every existing component below the pack folder must resolve
        // to somewhere still inside it.
        var current = new DirectoryInfo(dirFull);
        foreach (var segment in relative.Split('/'))
        {
            var next = Path.Combine(current.FullName, segment);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (!info.Exists)
            {
                break;
            }

            try
            {
                if (info.LinkTarget is not null
                    && info.ResolveLinkTarget(returnFinalTarget: true) is { } target
                    && !IsUnder(packReal, target.FullName))
                {
                    problem = "path escapes the pack folder through a link";
                    return null;
                }
            }
            catch (IOException)
            {
                problem = "link cannot be resolved";
                return null;
            }

            if (info is DirectoryInfo directory)
            {
                current = directory;
            }
        }

        return full;
    }

    private static bool IsUnder(string parent, string child)
    {
        var prefix = parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return child.StartsWith(prefix, comparison);
    }

    private static string RealPath(string dir)
    {
        var full = Path.GetFullPath(dir);
        try
        {
            return new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
        }
        catch (IOException)
        {
            return full;
        }
    }

    private static string? ValidateTensorFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[TensorHeaderProbeBytes];
            var read = stream.Read(buffer, 0, buffer.Length);
            var span = buffer.AsSpan(0, read);
            if (NpyHeader.ValidateStyleTensor(span) is { } reason)
            {
                return reason;
            }

            var info = NpyHeader.Parse(span)!;
            var expected = info.DataOffset + (long)NpyHeader.StyleRows * NpyHeader.StyleColumns * sizeof(float);
            return stream.Length < expected ? "file is truncated" : null;
        }
        catch (FileNotFoundException)
        {
            return "file is missing";
        }
        catch (DirectoryNotFoundException)
        {
            return "file is missing";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"cannot be read ({e.Message})";
        }
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static long DirectoryBytes(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private sealed class PackBuilder(VoicePackInfo info)
    {
        public VoicePackInfo Info { get; } = info;

        public List<string> Problems { get; } = [];

        public int? VoiceCount { get; set; }

        public VoicePackInfo Build() => this.Info with
        {
            VoiceCount = this.VoiceCount ?? this.Info.VoiceCount,
            Problems = [.. this.Problems],
        };
    }

    private sealed record Snapshot(
        IReadOnlyList<VoiceInfo> Voices,
        IReadOnlyList<VoicePackInfo> Packs,
        IReadOnlyDictionary<string, VoiceInfo> ById)
    {
        public static readonly Snapshot Empty = new(
            [], [], new Dictionary<string, VoiceInfo>(StringComparer.Ordinal));
    }
}
