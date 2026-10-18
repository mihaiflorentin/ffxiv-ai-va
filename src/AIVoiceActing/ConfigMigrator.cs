namespace AIVoiceActing;

/// <summary>Facts about the host the migration rules depend on.</summary>
public sealed record MigrationContext(bool IsWine);

public sealed record ConfigMigrationResult(bool Changed, IReadOnlyList<MigrationChange> Changes);

/// <summary>File seam for the one-time v1 backup so tests can fake IO.</summary>
public interface IConfigFileOps
{
    bool Exists(string path);

    void Copy(string source, string destination);
}

public sealed class SystemConfigFileOps : IConfigFileOps
{
    public bool Exists(string path) => File.Exists(path);

    public void Copy(string source, string destination) => File.Copy(source, destination, overwrite: false);
}

/// <summary>
/// Typed, pure, idempotent upgrade of a loaded <see cref="Configuration"/> to
/// <see cref="Configuration.CurrentVersion"/>. Runs only when the file's version is older;
/// newer files are left untouched (downgrade-safe). Maps the v1 engine/EP/CPU/expressive
/// fields into the v2 sections, nulls the legacy fields, and records what changed in
/// <see cref="Configuration.Migration"/> until the UI acknowledges it.
/// </summary>
public static class ConfigMigrator
{
    public const string KokoroId = "kokoro";
    public const string TurboId = "chatterbox-turbo";

    public static ConfigMigrationResult Migrate(Configuration config, MigrationContext context)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Version >= Configuration.CurrentVersion)
        {
            return new ConfigMigrationResult(false, []);
        }

#pragma warning disable CS0618 // The migrator is the one legitimate reader of the v1 fields.
        var changes = new List<MigrationChange>();
        var fromVersion = config.Version;

        MigrateEngine(config, changes);
        MigrateExecutionProvider(config, context, changes);

        config.Voices.UniquePerCharacter = true;
        changes.Add(new MigrationChange(
            "Voices", "shared voices", "unique voice per character",
            "Every character now gets a voice of their own, blended from the cast on first speech. Turn it off in Settings."));

        config.Device.CpuImpact = config.CpuImpact is "low" or "medium" or "high" ? config.CpuImpact : "medium";

        config.SelectedEngine = null;
        config.SelectedEp = null;
        config.CpuImpact = null;
        config.ExpressiveEngine = null;
        config.UseFp32LanguageModel = null;
#pragma warning restore CS0618

        config.Version = Configuration.CurrentVersion;
        config.Migration = new MigrationState
        {
            FromVersion = fromVersion,
            ToVersion = Configuration.CurrentVersion,
            Changes = changes,
        };
        return new ConfigMigrationResult(true, changes);
    }

    /// <summary>
    /// Migrates, and when something changed writes <c>&lt;config&gt;.v1.bak</c> once (never
    /// overwritten) and then calls <paramref name="save"/>. Downgrade = restore the backup.
    /// </summary>
    public static ConfigMigrationResult MigrateAndPersist(
        Configuration config,
        MigrationContext context,
        string configFilePath,
        IConfigFileOps files,
        Action save)
    {
        // The file on disk is still the untouched v1 original until save() runs.
        var willMigrate = config.Version < Configuration.CurrentVersion;
        var backupPath = configFilePath + ".v1.bak";
        if (willMigrate && files.Exists(configFilePath) && !files.Exists(backupPath))
        {
            files.Copy(configFilePath, backupPath);
        }

        var result = Migrate(config, context);
        if (result.Changed)
        {
            save();
        }

        return result;
    }

#pragma warning disable CS0618
    private static void MigrateEngine(Configuration config, List<MigrationChange> changes)
    {
        var legacy = config.SelectedEngine;
        var expressiveWasOn = config.ExpressiveEngine == true;
        string primary;
        switch (legacy?.Trim().ToLowerInvariant())
        {
            case null or "" or "kokoro":
                primary = KokoroId;
                break;
            case "turbo" or "chatterbox-turbo":
                primary = TurboId;
                if (legacy != TurboId)
                {
                    changes.Add(new MigrationChange("Engine", legacy!, TurboId, "Engine id renamed to chatterbox-turbo."));
                }

                break;
            case "f5":
                primary = KokoroId;
                changes.Add(new MigrationChange(
                    "Engine", "f5", KokoroId,
                    "F5 was retired; switched to Kokoro."));
                break;
            case "chatterbox":
                primary = KokoroId;
                changes.Add(new MigrationChange(
                    "Engine", "chatterbox", KokoroId,
                    "Legacy Chatterbox was retired; switched to Kokoro. Old files can be deleted on the Models page."));
                if (config.UseFp32LanguageModel == true)
                {
                    changes.Add(new MigrationChange(
                        "Engine", "full-precision language model", "removed",
                        "The full-precision language model option went away with legacy Chatterbox."));
                }

                break;
            default:
                primary = KokoroId;
                changes.Add(new MigrationChange(
                    "Engine", legacy!, KokoroId,
                    $"Unknown engine \"{legacy}\" replaced by Kokoro."));
                break;
        }

        config.Engine.Primary = primary;
        config.Engine.Expressive.Enabled = expressiveWasOn;
        config.Engine.Expressive.EngineId = TurboId;
    }

    private static void MigrateExecutionProvider(
        Configuration config, MigrationContext context, List<MigrationChange> changes)
    {
        var legacy = config.SelectedEp;
        switch (legacy?.Trim().ToLowerInvariant())
        {
            case null or "" or "auto":
                config.Device.Mode = DeviceSettings.ModeAuto;
                break;
            case "cpu":
                config.Device.Mode = DeviceSettings.ModeCpu;
                break;
            case "directml" when !context.IsWine:
                config.Device.Mode = DeviceSettings.ModeGpu;
                config.Device.Backend = "directml";
                changes.Add(new MigrationChange("Device", "directml", "gpu (directml)", "GPU choice kept (DirectML)."));
                break;
            case "directml":
                config.Device.Mode = DeviceSettings.ModeAuto;
                changes.Add(new MigrationChange(
                    "Device", "directml", "auto",
                    "DirectML cannot run under Wine; now using automatic GPU selection."));
                break;
            case "coreml":
                config.Device.Mode = DeviceSettings.ModeAuto;
                changes.Add(new MigrationChange(
                    "Device", "coreml", "auto",
                    "CoreML was never available in game; now automatic."));
                break;
            default:
                config.Device.Mode = DeviceSettings.ModeAuto;
                changes.Add(new MigrationChange(
                    "Device", legacy!, "auto",
                    $"Unknown execution provider \"{legacy}\"; now automatic."));
                break;
        }
    }
#pragma warning restore CS0618
}
