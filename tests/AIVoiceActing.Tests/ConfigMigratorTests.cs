namespace AIVoiceActing.Tests;

using System.Text.Json;
using AIVoiceActing;
using AIVoiceActing.Tests.Mock;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Xunit;

/// <summary>
/// v1 -> v2 configuration migration: every legacy engine and execution-provider value, the
/// owner's real file, idempotency, and both serializers (System.Text.Json here, Newtonsoft in Dalamud).
/// </summary>
public sealed class ConfigMigratorTests
{
    private static readonly MigrationContext Native = new(IsWine: false);
    private static readonly MigrationContext Wine = new(IsWine: true);

    private static string OwnerFixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "config-v1-owner.json"));

    private static Configuration V1(
        string? engine = "kokoro",
        string? ep = "auto",
        string? cpu = "medium",
        bool expressive = false,
        bool fp32 = false)
    {
#pragma warning disable CS0618
        return new Configuration
        {
            Version = 1,
            SelectedEngine = engine,
            SelectedEp = ep,
            CpuImpact = cpu,
            ExpressiveEngine = expressive,
            UseFp32LanguageModel = fp32,
        };
#pragma warning restore CS0618
    }

    // ---- Owner's real file ----

    [Fact]
    public void Owner_file_fixture_migrates_to_kokoro_auto_with_one_change_line()
    {
        // SelectedEp "coreml", UseFp32LanguageModel true, SelectedEngine kokoro, ExpressiveEngine false.
        var config = System.Text.Json.JsonSerializer.Deserialize<Configuration>(OwnerFixture())!;
        Assert.Equal(1, config.Version);

        var result = ConfigMigrator.Migrate(config, Native);

        Assert.True(result.Changed);
        Assert.Equal(2, config.Version);
        Assert.Equal("kokoro", config.Engine.Primary);
        Assert.False(config.Engine.Expressive.Enabled);
        Assert.Equal(DeviceSettings.ModeAuto, config.Device.Mode);
        Assert.Equal("medium", config.Device.CpuImpact);
        var change = Assert.Single(result.Changes, c => c.Area != "Voices");
        Assert.Equal("Device", change.Area);
        Assert.Equal("coreml", change.Before);
        Assert.Contains("CoreML", change.Message);
        Assert.Equal(result.Changes, config.Migration.Changes);
        Assert.Contains(result.Changes, c => c.Area == "Voices");
        Assert.Equal(1, config.Migration.FromVersion);
        Assert.Equal(2, config.Migration.ToVersion);

        // Flat settings survive untouched.
        Assert.True(config.Enabled);
        Assert.True(config.ShowNowSpeaking);
        Assert.False(config.SkipVoicedBattleText);
        Assert.Equal(20, config.StaleLineSeconds);
        Assert.Equal("|", config.StyleTag);
        Assert.Equal("kokoro", new Infrastructure.Engines.EngineCatalog().Resolve(config.Engine.Primary));
    }

    // ---- Engine mapping ----

    [Theory]
    [InlineData(null, "kokoro", false)]
    [InlineData("", "kokoro", false)]
    [InlineData("kokoro", "kokoro", false)]
    [InlineData("turbo", "chatterbox-turbo", true)]
    [InlineData("f5", "kokoro", true)]
    [InlineData("chatterbox", "kokoro", true)]
    [InlineData("mystery-engine", "kokoro", true)]
    public void Engine_mapping(string? legacy, string expectedPrimary, bool expectLine)
    {
        var config = V1(engine: legacy);

        var result = ConfigMigrator.Migrate(config, Native);

        Assert.Equal(expectedPrimary, config.Engine.Primary);
        Assert.Equal(expectLine, result.Changes.Any(c => c.Area == "Engine"));
    }

    [Fact]
    public void F5_report_says_it_was_retired()
    {
        var result = ConfigMigrator.Migrate(V1(engine: "f5"), Native);

        var line = Assert.Single(result.Changes, c => c.Area != "Voices");
        Assert.Contains("F5 was retired", line.Message);
    }

    [Theory]
    [InlineData("chatterbox", true)]
    [InlineData("chatterbox", false)]
    [InlineData("f5", true)]
    [InlineData("f5", false)]
    [InlineData("kokoro", true)]
    [InlineData("kokoro", false)]
    public void Expressive_routing_to_turbo_is_kept_only_if_it_was_enabled(string engine, bool expressive)
    {
        var config = V1(engine: engine, expressive: expressive);

        ConfigMigrator.Migrate(config, Native);

        Assert.Equal("kokoro", config.Engine.Primary);
        Assert.Equal(expressive, config.Engine.Expressive.Enabled);
        Assert.Equal("chatterbox-turbo", config.Engine.Expressive.EngineId);
    }

    [Fact]
    public void Fp32_flag_is_dropped_and_reported_only_for_legacy_chatterbox()
    {
        var chatterbox = ConfigMigrator.Migrate(V1(engine: "chatterbox", fp32: true), Native);
        var kokoro = ConfigMigrator.Migrate(V1(engine: "kokoro", fp32: true), Native);

        Assert.Contains(chatterbox.Changes, c => c.Message.Contains("full-precision"));
        Assert.Empty(kokoro.Changes.Where(c => c.Area != "Voices"));
    }

    // ---- EP mapping ----

    [Theory]
    [InlineData("auto", false, "auto", "auto", false)]
    [InlineData(null, false, "auto", "auto", false)]
    [InlineData("cpu", false, "cpu", "auto", false)]
    [InlineData("coreml", false, "auto", "auto", true)]
    [InlineData("coreml", true, "auto", "auto", true)]
    [InlineData("directml", false, "gpu", "directml", true)]
    [InlineData("directml", true, "auto", "auto", true)]
    [InlineData("weird", false, "auto", "auto", true)]
    public void Ep_mapping(string? ep, bool wine, string mode, string backend, bool expectLine)
    {
        var config = V1(ep: ep);

        var result = ConfigMigrator.Migrate(config, wine ? Wine : Native);

        Assert.Equal(mode, config.Device.Mode);
        Assert.Equal(backend, config.Device.Backend);
        Assert.Equal(expectLine, result.Changes.Any(c => c.Area == "Device"));
    }

    [Theory]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData("high", "high")]
    [InlineData("ludicrous", "medium")]
    [InlineData(null, "medium")]
    public void CpuImpact_moves_into_Device(string? legacy, string expected)
    {
        var config = V1(cpu: legacy);

        ConfigMigrator.Migrate(config, Native);

        Assert.Equal(expected, config.Device.CpuImpact);
    }

    [Fact]
    public void Legacy_fields_are_nulled()
    {
        var config = V1(engine: "f5", ep: "cpu", cpu: "high", expressive: true, fp32: true);

        ConfigMigrator.Migrate(config, Native);

#pragma warning disable CS0618
        Assert.Null(config.SelectedEngine);
        Assert.Null(config.SelectedEp);
        Assert.Null(config.CpuImpact);
        Assert.Null(config.ExpressiveEngine);
        Assert.Null(config.UseFp32LanguageModel);
#pragma warning restore CS0618
    }

    // ---- Lifecycle ----

    [Fact]
    public void Migration_is_idempotent()
    {
        var config = V1(engine: "chatterbox", ep: "directml", cpu: "high", expressive: true);

        var first = ConfigMigrator.Migrate(config, Native);
        var afterFirst = System.Text.Json.JsonSerializer.Serialize(config);
        var second = ConfigMigrator.Migrate(config, Native);

        Assert.True(first.Changed);
        Assert.False(second.Changed);
        Assert.Empty(second.Changes);
        Assert.Equal(afterFirst, System.Text.Json.JsonSerializer.Serialize(config));
    }

    [Fact]
    public void Future_version_is_left_untouched()
    {
        var config = V1(engine: "f5", ep: "directml");
        config.Version = 3;
        var before = System.Text.Json.JsonSerializer.Serialize(config);

        var result = ConfigMigrator.Migrate(config, Native);

        Assert.False(result.Changed);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(config));
    }

    [Fact]
    public void Unique_voice_per_character_defaults_on_for_new_and_old_files_and_old_files_get_a_report_line()
    {
        Assert.True(new Configuration().Voices.UniquePerCharacter);

        var old = System.Text.Json.JsonSerializer.Deserialize<Configuration>("""{"Version":1,"Enabled":true}""")!;
        old.Voices.UniquePerCharacter = false; // whatever the file said, v1 had no such setting
        var result = ConfigMigrator.Migrate(old, Native);

        Assert.True(old.Voices.UniquePerCharacter);
        var line = Assert.Single(result.Changes, c => c.Area == "Voices");
        Assert.Contains("unique voice", line.After);

        // A current-version file that never mentioned the setting also reads as on, and a saved "off" survives.
        var current = System.Text.Json.JsonSerializer.Deserialize<Configuration>("""{"Version":2,"Voices":{"DisabledPacks":[]}}""")!;
        Assert.True(current.Voices.UniquePerCharacter);
        var off = System.Text.Json.JsonSerializer.Deserialize<Configuration>("""{"Version":2,"Voices":{"UniquePerCharacter":false}}""")!;
        Assert.False(ConfigMigrator.Migrate(off, Native).Changed);
        Assert.False(off.Voices.UniquePerCharacter);
    }

    [Fact]
    public void Fresh_install_is_current_with_no_changes()
    {
        var config = new Configuration();

        var result = ConfigMigrator.Migrate(config, Native);

        Assert.Equal(Configuration.CurrentVersion, config.Version);
        Assert.False(result.Changed);
        Assert.Empty(config.Migration.Changes);
    }

    [Fact]
    public void V1_json_without_new_sections_deserializes_to_defaults()
    {
        const string json = """{"Version":1,"Enabled":true}""";

        var config = System.Text.Json.JsonSerializer.Deserialize<Configuration>(json)!;
        ConfigMigrator.Migrate(config, Native);

        Assert.Equal("kokoro", config.Engine.Primary);
        Assert.Equal("auto", config.Device.Mode);
        Assert.Equal("balanced", config.Device.GpuImpact);
        Assert.True(config.Device.FallbackToCpu);
        Assert.Empty(config.Voices.DisabledPacks);
        Assert.Empty(config.Engine.PerEngine);
    }

    [Fact]
    public void Ack_clears_pending_changes_through_a_round_trip()
    {
        var config = V1(ep: "coreml");
        ConfigMigrator.Migrate(config, Native);
        config.Migration.Changes.Clear();
        config.MigrationNoticeAckedVersion = Configuration.CurrentVersion;

        var restored = System.Text.Json.JsonSerializer.Deserialize<Configuration>(
            System.Text.Json.JsonSerializer.Serialize(config))!;

        Assert.Empty(restored.Migration.Changes);
        Assert.Equal(2, restored.MigrationNoticeAckedVersion);
    }

    [Fact]
    public void Pending_changes_survive_a_round_trip_until_acknowledged()
    {
        var config = V1(ep: "coreml");
        ConfigMigrator.Migrate(config, Native);

        var restored = System.Text.Json.JsonSerializer.Deserialize<Configuration>(
            System.Text.Json.JsonSerializer.Serialize(config))!;

        var change = Assert.Single(restored.Migration.Changes, c => c.Area != "Voices");
        Assert.Equal("coreml", change.Before);
        Assert.Equal(1, restored.Migration.FromVersion);
    }

    // ---- Backup seam ----

    [Fact]
    public void MigrateAndPersist_backs_up_once_then_saves()
    {
        var files = new FakeConfigFileOps();
        files.Files.Add("/cfg/AIVoiceActing.json");
        var saves = 0;

        ConfigMigrator.MigrateAndPersist(V1(), Native, "/cfg/AIVoiceActing.json", files, () => saves++);

        var copy = Assert.Single(files.Copies);
        Assert.Equal("/cfg/AIVoiceActing.json", copy.Source);
        Assert.Equal("/cfg/AIVoiceActing.json.v1.bak", copy.Destination);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void MigrateAndPersist_never_overwrites_an_existing_backup()
    {
        var files = new FakeConfigFileOps();
        files.Files.Add("/cfg/AIVoiceActing.json");
        files.Files.Add("/cfg/AIVoiceActing.json.v1.bak");

        ConfigMigrator.MigrateAndPersist(V1(), Native, "/cfg/AIVoiceActing.json", files, () => { });

        Assert.Empty(files.Copies);
    }

    [Fact]
    public void MigrateAndPersist_does_nothing_for_a_current_config()
    {
        var files = new FakeConfigFileOps();
        files.Files.Add("/cfg/AIVoiceActing.json");
        var saves = 0;

        var result = ConfigMigrator.MigrateAndPersist(
            new Configuration(), Native, "/cfg/AIVoiceActing.json", files, () => saves++);

        Assert.False(result.Changed);
        Assert.Empty(files.Copies);
        Assert.Equal(0, saves);
    }

    // ---- Newtonsoft (what Dalamud uses) ----

    /// <summary>Maps the persisted type names onto the test assembly, and back, so $type stays byte-stable.</summary>
    private sealed class PersistedTypeBinder : ISerializationBinder
    {
        private static readonly DefaultSerializationBinder Fallback = new();

        public Type BindToType(string? assemblyName, string typeName)
        {
            if (typeName == "AIVoiceActing.PluginConfiguration")
            {
                return typeof(TestPluginConfiguration);
            }

            // Persisted names say "AIVoiceActing"; in the test host that code lives in the test assembly.
            return typeof(Configuration).Assembly.GetType(typeName)
                ?? Fallback.BindToType(assemblyName, typeName);
        }

        public void BindToName(Type serializedType, out string? assemblyName, out string? typeName)
        {
            if (serializedType == typeof(TestPluginConfiguration))
            {
                (assemblyName, typeName) = ("AIVoiceActing", "AIVoiceActing.PluginConfiguration");
            }
            else if (serializedType.Assembly == typeof(Configuration).Assembly)
            {
                (assemblyName, typeName) = ("AIVoiceActing", serializedType.FullName);
            }
            else
            {
                Fallback.BindToName(serializedType, out assemblyName, out typeName);
            }
        }
    }

    private sealed class TestPluginConfiguration : Configuration
    {
    }

    private static JsonSerializerSettings NewtonsoftSettings() => new()
    {
        TypeNameHandling = TypeNameHandling.Objects,
        SerializationBinder = new PersistedTypeBinder(),
        Formatting = Formatting.Indented,
    };

    [Fact]
    public void Newtonsoft_typed_roundtrip_migrates_like_System_Text_Json()
    {
        var viaNewtonsoft = (Configuration)JsonConvert.DeserializeObject(OwnerFixture(), NewtonsoftSettings())!;
        var viaStj = System.Text.Json.JsonSerializer.Deserialize<Configuration>(OwnerFixture())!;

        var a = ConfigMigrator.Migrate(viaNewtonsoft, Native);
        var b = ConfigMigrator.Migrate(viaStj, Native);

        Assert.IsType<TestPluginConfiguration>(viaNewtonsoft);
        Assert.Equal(b.Changes, a.Changes);
        Assert.Equal(viaStj.Engine.Primary, viaNewtonsoft.Engine.Primary);
        Assert.Equal(viaStj.Device.Mode, viaNewtonsoft.Device.Mode);
        Assert.Equal(viaStj.Device.CpuImpact, viaNewtonsoft.Device.CpuImpact);
        Assert.Equal(2, viaNewtonsoft.Version);

        // And the migrated config reloads from what Newtonsoft writes.
        var json = JsonConvert.SerializeObject(viaNewtonsoft, NewtonsoftSettings());
        var reloaded = (Configuration)JsonConvert.DeserializeObject(json, NewtonsoftSettings())!;
        Assert.Equal(2, reloaded.Version);
        Assert.Equal("kokoro", reloaded.Engine.Primary);
        Assert.Equal("auto", reloaded.Device.Mode);
        Assert.Equal(a.Changes, reloaded.Migration.Changes);
        Assert.False(ConfigMigrator.Migrate(reloaded, Native).Changed);
    }

    [Fact]
    public void Newtonsoft_type_names_of_the_new_sections_are_stable()
    {
        var json = JsonConvert.SerializeObject(new TestPluginConfiguration(), NewtonsoftSettings());

        Assert.Contains("\"$type\": \"AIVoiceActing.PluginConfiguration, AIVoiceActing\"", json);
        Assert.Contains("\"$type\": \"AIVoiceActing.EngineSettings, AIVoiceActing\"", json);
        Assert.Contains("\"$type\": \"AIVoiceActing.ExpressiveSettings, AIVoiceActing\"", json);
        Assert.Contains("\"$type\": \"AIVoiceActing.DeviceSettings, AIVoiceActing\"", json);
        Assert.Contains("\"$type\": \"AIVoiceActing.VoiceSettings, AIVoiceActing\"", json);
        Assert.Contains("\"$type\": \"AIVoiceActing.MigrationState, AIVoiceActing\"", json);
        Assert.Contains("\"$type\": \"AIVoiceActing.EngineOptionsMap, AIVoiceActing\"", json);
    }

    [Fact]
    public void Newtonsoft_persists_pending_migration_changes()
    {
        var config = V1(ep: "coreml");
        ConfigMigrator.Migrate(config, Native);

        var json = JsonConvert.SerializeObject(config, NewtonsoftSettings());
        var restored = (Configuration)JsonConvert.DeserializeObject(json, NewtonsoftSettings())!;

        var change = Assert.Single(restored.Migration.Changes, c => c.Area != "Voices");
        Assert.Equal("coreml", change.Before);
    }
}
