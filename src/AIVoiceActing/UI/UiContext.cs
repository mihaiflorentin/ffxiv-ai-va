namespace AIVoiceActing.UI;

using AIVoiceActing.Domain;
using AIVoiceActing.Domain.Handlers;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;

/// <summary>
/// Everything a page needs, bundled once by the plugin root (replaces the window's 24-argument
/// constructor). Ports and delegates are the same ones the old window received, resolved lazily
/// through the container; the shared UI services (status centre, modal host, toasts, navigation)
/// and the extracted state models are created by the root and shared by every page. A page takes
/// what it needs from here and never constructs adapters or reaches past the ports.
/// </summary>
public sealed record UiContext
{
    // ---- configuration ----
    public required Configuration Config { get; init; }

    /// <summary>Persists <see cref="Config"/>.</summary>
    public required Action Save { get; init; }

    // ---- ports (container-resolved) ----
    public required SpeechRequestHandler Speech { get; init; }

    public required Func<ISpeechSynthesizer> Synthesizer { get; init; }

    public required Action InvalidateSynthesizer { get; init; }

    public required ISpeechQueue Queue { get; init; }

    public required IProfileStore Profiles { get; init; }

    public required Func<IReadOnlyList<(ModelAsset Asset, string Group)>> ModelAssets { get; init; }

    public required Func<IModelProvisioner> Provisioner { get; init; }

    public required Func<IModelStore> ModelStore { get; init; }

    public required Func<RaceVoiceMap> VoiceMap { get; init; }

    public required Func<DialogueSessionFactory> Sessions { get; init; }

    public required Func<ICastingPresetStore> PresetStore { get; init; }

    public required Action InvalidateVoiceMap { get; init; }

    public required Func<IReadOnlyList<VoiceCatalogEntry>> VoiceCatalog { get; init; }

    public required IEngineCatalog EngineCatalog { get; init; }

    public UnboundModelLog? UnboundModels { get; init; }

    /// <summary>
    /// What the setup flow knows about the graphics card. Reports "no GPU info" until the device
    /// probe and setup advisor land (tickets #12/#13); the plugin root then assigns a real source
    /// here and the recommendation follows, with no change to Home.
    /// </summary>
    public Func<SetupDeviceInfo> DeviceInfo { get; init; } = () => SetupDeviceInfo.Unknown;

    // ---- environment delegates ----
    public required Func<string> ModelsDir { get; init; }

    public required Func<string> VoicesDir { get; init; }

    public required Func<(string Name, ushort World)?> LocalPlayer { get; init; }

    public required Func<string, bool> OpenDirectory { get; init; }

    /// <summary>Routes an error to the external reporter (the log sink's warning channel).</summary>
    public required Action<string> ReportError { get; init; }

    public required Action<string> LogInfo { get; init; }

    public required Action<string, Exception?> LogError { get; init; }

    // ---- shared UI services ----
    public required StatusCenter Status { get; init; }

    public required ModalHost Modals { get; init; }

    public required IToastSink Toasts { get; init; }

    public required NavModel Nav { get; init; }

    // ---- shared state models ----
    public required DownloadQueueModel Downloads { get; init; }

    public required EngineSessionModel EngineSession { get; init; }

    public required CachedProbes Probes { get; init; }

    public required PreviewController Preview { get; init; }

    public required WorldDirectory Worlds { get; init; }

    // ---- derived views ----

    /// <summary>Live synthesizer for the selected engine (rebuilt when engine/EP changes).</summary>
    public ISpeechSynthesizer Synth => this.Synthesizer();

    /// <summary>Why the engine cannot speak right now (tooltip text).</summary>
    public string EngineReason() => EngineSessionModel.Reason(this.Synth);

    /// <summary>Canonical key of the selected engine.</summary>
    public string PrimaryKey => this.EngineCatalog.Resolve(this.Config.Engine.Primary);

    /// <summary>Catalog entry of the selected engine.</summary>
    public EngineInfo ActiveEngine => this.EngineCatalog.Get(this.PrimaryKey);

    /// <summary>
    /// Posts a one-line outcome to the status centre; errors are sticky and also go to the
    /// external reporter, matching the old status line.
    /// </summary>
    public void Report(string message, bool isError)
    {
        if (isError)
        {
            this.Status.Post(Severity.Error, message);
            this.ReportError(message);
        }
        else
        {
            this.Status.Post(Severity.Success, message);
        }
    }

    /// <summary>Opens a folder, reporting failure.</summary>
    public void TryOpen(string directory)
    {
        if (!this.OpenDirectory(directory))
        {
            this.Report($"Could not open \"{directory}\".", isError: true);
        }
    }

    /// <summary>True when a test line may start: the engine is ready and no render is in flight.</summary>
    public bool CanPreview => this.Synth.IsReady && !this.Preview.Busy;

    /// <summary>Disabled reason for preview buttons; null when <see cref="CanPreview"/>.</summary>
    public string? PreviewBlockedReason =>
        this.Preview.Busy ? "Synthesizing…" : this.Synth.IsReady ? null : this.EngineReason();

    /// <summary>
    /// Per-profile test target (the voice tables): the courtesy line on the row's voice with the
    /// configured default exaggeration plus the row's bias.
    /// </summary>
    public void PreviewVoice(VoiceProfile profile) =>
        _ = this.Preview.StartDirect(
            profile.ReferenceVoiceId,
            new SpeakerIdentity(
                profile.SpeakerKey,
                SpeakerKeyView.Parse(profile.SpeakerKey).Name,
                null, null, null, null),
            TestBenchModel.BuildVoiceTestRequest(
                profile.ReferenceVoiceId,
                Math.Clamp(this.Config.DefaultExaggeration + profile.ExaggerationBias, 0f, 1f),
                profile.Pitch,
                profile.Speed,
                profile.Volume));
}
