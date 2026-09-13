namespace AIVoiceActing.Infrastructure.Kokoro;

using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Ports;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using Microsoft.ML.OnnxRuntime;

/// <summary>
/// Kokoro-82M adapter (KokoroSharp, MIT): CPU real-time synthesis with 50+ accent voices.
/// The model file (<c>kokoro-v1.0.onnx</c>, fp32) is provisioned via the Models tab; the
/// voice banks ship inside the KokoroSharp package (<c>voices/*.npy</c> next to the plugin
/// assembly). KokoroWavSynthesizer never touches audio devices (we own playback via
/// NAudioSink) and serializes inference internally. Session options come from the
/// <paramref name="intraOpThreadsFactory"/> CPU-impact knob; phonemization is native C#
/// (MisakiSharp) — no espeak-ng anywhere.
/// </summary>
public sealed class KokoroSynthesizer : ISpeechSynthesizer, IDisposable
{
    private const int KokoroSampleRate = 24000;
    private const string DefaultVoiceName = "af_heart";

    private readonly Func<string> modelsDirFactory;
    private readonly Func<int> intraOpThreadsFactory;
    private readonly ILogSink? log;

    private readonly object gate = new();
    private KokoroWavSynthesizer? engine;
    private string? initError;
    private bool initializing;
    private bool disposed;

    public KokoroSynthesizer(
        Func<string> modelsDirFactory,
        Func<int> intraOpThreadsFactory,
        ILogSink? log = null)
    {
        this.modelsDirFactory = modelsDirFactory ?? throw new ArgumentNullException(nameof(modelsDirFactory));
        this.intraOpThreadsFactory = intraOpThreadsFactory ?? throw new ArgumentNullException(nameof(intraOpThreadsFactory));
        this.log = log;
    }

    /// <summary>Absolute path of the Kokoro ONNX model under the models directory.</summary>
    public static string ModelPathFor(string modelsDir) =>
        Path.Combine(modelsDir, ModelCatalog.KokoroModelFileName);

    /// <summary>Absolute path of the packaged voice-bank directory (voices/*.npy).</summary>
    public static string VoicesDirFor() => Path.Combine(
        Path.GetDirectoryName(typeof(KokoroSynthesizer).Assembly.Location)
            ?? AppContext.BaseDirectory,
        "voices");

    public bool IsReady => this.FindNotReadyReason() is null;

    public string NotReadyReason => this.FindNotReadyReason() ?? string.Empty;

    /// <summary>
    /// Readiness means "all assets present; a line would synthesize" — NOT "the ONNX
    /// session is already built". The session builds lazily on first use (or the login
    /// pre-warm), so gating the Test tab on construction would deadlock: the buttons
    /// that would trigger the build stay disabled until something else builds it.
    /// </summary>
    private string? FindNotReadyReason()
    {
        lock (this.gate)
        {
            if (this.engine is not null)
            {
                return null;
            }

            if (this.initializing)
            {
                return "Loading Kokoro model…";
            }

            if (this.initError is { } error)
            {
                return error;
            }

            var modelsDir = this.modelsDirFactory();
            return !File.Exists(ModelPathFor(modelsDir))
                ? "Kokoro model not downloaded — use the Models tab."
                : !Directory.Exists(VoicesDirFor())
                    ? "Kokoro voices missing — reinstall the plugin."
                    : null;
        }
    }

    public async Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken cancellationToken)
    {
        var engine = this.EnsureEngine();
        var voice = this.ResolveVoice(request.ReferenceVoiceId);
        var text = StripInlineTags(request.Text);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new SpeechSynthesisException("Nothing to speak after tag stripping.");
        }

        // Exaggeration maps to pace: flat reads stay brisk, theatrical ones slow down.
        var speed = 1.05f - (0.20f * Math.Clamp(request.Exaggeration, 0f, 1f));

        byte[] pcm16;
        try
        {
            // KokoroSharp synthesizes without cancellation support; ct gates the wait below.
            var config = new KokoroTTSPipelineConfig { Speed = speed };
            pcm16 = await engine.SynthesizeAsync(text, voice, config).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new SpeechSynthesisException($"Kokoro synthesis failed for voice \"{voice.Name}\".", ex);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new SynthesisResult(Pcm16ToFloat(pcm16), KokoroSampleRate);
    }

    /// <summary>
    /// Loads the model and voices and runs a short warm-up line so the first real line
    /// does not pay session creation or phonemizer dictionary decompression. Safe to
    /// call repeatedly; failures surface through <see cref="NotReadyReason"/>.
    /// </summary>
    public Task WarmUpAsync(CancellationToken cancellationToken)
    {
        this.EnsureEngine();
        if (this.IsReady)
        {
            return this.SynthesizeAsync(
                new SynthesisRequest(DefaultVoiceName, "Ready.", Exaggeration: 0, Tags: []),
                cancellationToken);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.engine?.Dispose();
            this.engine = null;
        }
    }

    private KokoroWavSynthesizer EnsureEngine()
    {
        lock (this.gate)
        {
            if (this.engine is not null)
            {
                return this.engine;
            }

            if (this.disposed)
            {
                throw new SpeechSynthesisException("Kokoro synthesizer was disposed.");
            }

            var modelPath = ModelPathFor(this.modelsDirFactory());
            if (!File.Exists(modelPath))
            {
                throw new SpeechSynthesisException(
                    "Kokoro model not downloaded — use the Models tab.");
            }

            this.initializing = true;
            try
            {
                var options = new SessionOptions
                {
                    // CPU-impact knob: bounded intra-op parallelism protects the frame rate.
                    IntraOpNumThreads = Math.Max(1, this.intraOpThreadsFactory()),
                    InterOpNumThreads = 1,
                    EnableMemoryPattern = true,
                };

                this.log?.Info($"Initializing Kokoro engine ({modelPath}, intra-op threads: {options.IntraOpNumThreads}).");
                var engine = KokoroWavSynthesizer.LoadModel(modelPath, options);

                // Voices ship with the package next to the plugin assembly; in-game the
                // process base directory is the game folder, so resolve explicitly.
                var voicesDir = VoicesDirFor();
                if (Directory.Exists(voicesDir))
                {
                    KokoroVoiceManager.LoadVoicesFromPath(voicesDir);
                }
                else
                {
                    this.log?.Warn($"Kokoro voices directory missing: {voicesDir}.");
                }

                this.engine = engine;
                this.initError = null;
                this.log?.Info("Kokoro engine ready.");
                return engine;
            }
            catch (Exception ex)
            {
                this.initError = $"Kokoro engine failed to load: {ex.Message}";
                this.log?.Error("Kokoro engine initialization failed.", ex);
                throw new SpeechSynthesisException(this.initError, ex);
            }
            finally
            {
                this.initializing = false;
            }
        }
    }

    private KokoroVoice ResolveVoice(string referenceVoiceId)
    {
        var name = string.IsNullOrWhiteSpace(referenceVoiceId) ? DefaultVoiceName : referenceVoiceId;
        try
        {
            return KokoroVoiceManager.GetVoice(name);
        }
        catch (Exception ex)
        {
            this.log?.Warn($"Kokoro voice \"{name}\" unavailable ({ex.Message}); falling back to {DefaultVoiceName}.");
            return KokoroVoiceManager.GetVoice(DefaultVoiceName);
        }
    }

    /// <summary>Defensive: inline [tag] text would be phonemized literally by Kokoro.</summary>
    private static string StripInlineTags(string text)
    {
        if (!text.Contains('['))
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        var depth = 0;
        foreach (var ch in text)
        {
            if (ch == '[')
            {
                depth++;
            }
            else if (ch == ']' && depth > 0)
            {
                depth--;
            }
            else if (depth == 0)
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static float[] Pcm16ToFloat(byte[] pcm16)
    {
        var samples = new float[pcm16.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToInt16(pcm16, i * 2) / 32768f;
        }

        return samples;
    }
}
