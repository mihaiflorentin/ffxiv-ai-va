namespace AIVoiceActing.Infrastructure.Onnx;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Tokenizers.DotNet;

/// <summary>
/// Emotion director backed by a GoEmotions text classifier (SamLowe/roberta-base-go_emotions,
/// MIT, fp32 ONNX; the int8 export is avoided for the same Zen 5 kernel crashes that keep
/// Kokoro fp32). One short line runs in a few tens of milliseconds on two CPU threads, on
/// the speech worker, never the game thread. The session loads on first use; any failure
/// (missing files, bad export) logs once and falls back to the rules table for good.
/// </summary>
public sealed class EmotionClassifierDirector : IEmotionDirector, IDisposable
{
    private const int MaxTokens = 128;
    private const long BosId = 0;
    private const long EosId = 2;

    private readonly Func<string> modelsDirFactory;
    private readonly ILogSink? log;
    private readonly object gate = new();
    private InferenceSession? session;
    private Tokenizer? tokenizer;
    private bool failed;

    public EmotionClassifierDirector(Func<string> modelsDirFactory, ILogSink? log = null)
    {
        this.modelsDirFactory = modelsDirFactory;
        this.log = log;
    }

    /// <summary>Both classifier files are on disk.</summary>
    public static bool IsInstalled(string modelsDir) =>
        File.Exists(Path.Combine(modelsDir, ModelCatalog.EmotionModelFileName))
        && File.Exists(Path.Combine(modelsDir, ModelCatalog.EmotionTokenizerFileName));

    public Task<EmotionPlan> PlanAsync(EmotionContext context, CancellationToken cancellationToken)
    {
        var rules = EmotionRules.Plan(context.Line);
        if (string.IsNullOrWhiteSpace(context.Line))
        {
            return Task.FromResult(rules);
        }

        try
        {
            if (this.Classify(context.Line) is { } probabilities)
            {
                return Task.FromResult(EmotionBlend.Plan(probabilities, rules));
            }
        }
        catch (Exception ex)
        {
            this.log?.Warn($"Emotion classifier failed on a line ({ex.Message}); using the rules table.");
        }

        return Task.FromResult(rules);
    }

    private float[]? Classify(string line)
    {
        lock (this.gate)
        {
            if (!this.EnsureLoaded())
            {
                return null;
            }

            var ids = this.tokenizer!.Encode(line).Select(id => (long)id).ToList();
            if (ids.Count == 0 || ids[0] != BosId)
            {
                ids.Insert(0, BosId);
            }

            if (ids[^1] != EosId)
            {
                ids.Add(EosId);
            }

            if (ids.Count > MaxTokens)
            {
                ids = [.. ids.Take(MaxTokens - 1), EosId];
            }

            var inputIds = new DenseTensor<long>(ids.ToArray(), [1, ids.Count]);
            var mask = new DenseTensor<long>(Enumerable.Repeat(1L, ids.Count).ToArray(), [1, ids.Count]);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                NamedOnnxValue.CreateFromTensor("attention_mask", mask),
            };
            if (this.session!.InputMetadata.ContainsKey("token_type_ids"))
            {
                // Some exports keep the (all-zero for RoBERTa) segment input.
                inputs.Add(NamedOnnxValue.CreateFromTensor(
                    "token_type_ids", new DenseTensor<long>(new long[ids.Count], [1, ids.Count])));
            }

            using var results = this.session!.Run(inputs);
            var logits = results.First().AsEnumerable<float>().ToArray();
            return EmotionBlend.Sigmoid(logits);
        }
    }

    private bool EnsureLoaded()
    {
        if (this.session is not null)
        {
            return true;
        }

        if (this.failed)
        {
            return false;
        }

        try
        {
            var dir = this.modelsDirFactory();
            var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1 };
            this.session = new InferenceSession(Path.Combine(dir, ModelCatalog.EmotionModelFileName), options);
            this.tokenizer = new Tokenizer(vocabPath: Path.Combine(dir, ModelCatalog.EmotionTokenizerFileName));
            this.log?.Info("Emotion classifier loaded.");
            return true;
        }
        catch (Exception ex)
        {
            this.failed = true;
            this.session?.Dispose();
            this.session = null;
            this.log?.Warn($"Emotion classifier unavailable ({ex.Message}); using the rules table.");
            return false;
        }
    }

    public void Dispose()
    {
        lock (this.gate)
        {
            this.session?.Dispose();
            this.session = null;
        }
    }
}
