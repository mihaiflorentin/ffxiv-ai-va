namespace AIVoiceActing.Infrastructure.Onnx;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using Microsoft.ML.OnnxRuntime;

/// <summary>What one Turbo decode needs from the reference clip and the prompt.</summary>
/// <param name="ConditionEmbedding">Encoder output, [1, <paramref name="ConditionFrames"/>, width] flattened.</param>
/// <param name="TextIds">Tokenized prompt (ids below the speech range).</param>
public sealed record TurboDecodeInput(float[] ConditionEmbedding, int ConditionFrames, long[] TextIds);

/// <summary>
/// Greedy speech-token loop of Chatterbox Turbo: embed the prompt, run the language model with a key-value
/// cache, pick the next speech token, repeat until STOP. Built on <see cref="OrtValue"/> and
/// <see cref="OrtIoBinding"/> so the cache never visits managed memory: every step's "present" tensors are
/// bound straight back as the next step's "past" inputs, in <paramref name="kvMemory"/> (the GPU buffer
/// allocator for the WebGPU language model, the CPU allocator otherwise). The cache is Float16 for the
/// fp16 and q4f16 graphs and Float for fp32; the element type is read from the graph.
/// Measured on the verified runtime (docs/plans/2026-10-gpu-spike-results.md S2): a device-resident cache
/// is 3 times faster than copying it through managed arrays and stays flat as the context grows.
/// </summary>
public sealed class TurboDecodeLoop
{
    public const int StartSpeechToken = 6561;
    public const int StopSpeechToken = 6562;
    /// <summary>Hard ceiling on new speech tokens per call: 40 s of audio (25 tokens a second), as upstream Turbo.</summary>
    public const int MaxNewTokens = 1000;

    /// <summary>The least budget any line gets (about 10 s of audio).</summary>
    public const int MinTokenBudget = 256;

    /// <summary>
    /// The speech-token budget for a prompt of <paramref name="textTokens"/> text tokens. Speech runs at about seven
    /// speech tokens per text token; the budget allows more than twice that so pauses and slow delivery never cut a
    /// line short (a fixed 256 stopped 150-character lines mid-sentence), while a model that never says STOP still
    /// ends near the line's own length instead of babbling for the full ceiling.
    /// </summary>
    public static int TokenBudget(int textTokens) => Math.Clamp(60 + (16 * textTokens), MinTokenBudget, MaxNewTokens);
    public const int MinSpeechTokens = 30;
    public const float RepetitionPenalty = 1.2f;

    /// <summary>
    /// The repetition penalty looks at the last this many tokens only. Over the whole history (the ONNX card's
    /// recipe) greedy q4f16 decoding cannot reuse the sounds of a repeated word ("eating well, sleeping well"),
    /// drifts into seconds of silence or babble and often never says STOP; a 32-token window brought it level
    /// with fp32 (docs/plans/2026-10-gpu-audio-artifacts.md).
    /// </summary>
    public const int PenaltyWindow = 32;

    /// <summary>A token picked this many times in a row is masked for the next step (a stuck silence or hum).</summary>
    public const int MaxRun = 12;

    /// <summary>Names the decoding recipe in render-cache keys, so audio made by an older recipe is not replayed.</summary>
    public const string Recipe = "w32r12";
    public const int NumKvHeads = 16;
    public const int HeadDim = 64;

    // Some exports expose the attention-mask preprocessing's total length as an input; feed it when present.
    private const string TotalSequenceLengthInput = "/model/attn_mask_reformat/attn_mask_subgraph/Gather/Cast/output_0";

    private readonly InferenceSession embed;
    private readonly InferenceSession lm;
    private readonly OrtMemoryInfo kvMemory;
    private readonly bool kvIsHalf;
    private readonly bool hasTotalLength;
    private readonly string[] pastNames;
    private readonly string[] presentNames;
    private bool outputOrderVerified;

    public TurboDecodeLoop(InferenceSession embed, InferenceSession lm, OrtMemoryInfo kvMemory, int layers = 24)
    {
        this.embed = embed;
        this.lm = lm;
        this.kvMemory = kvMemory;
        this.pastNames = new string[layers * 2];
        this.presentNames = new string[layers * 2];
        // Layer-major, key then value: the graph's present outputs come in this order.
        for (var layer = 0; layer < layers; layer++)
        {
            this.pastNames[layer * 2] = $"past_key_values.{layer}.key";
            this.pastNames[(layer * 2) + 1] = $"past_key_values.{layer}.value";
            this.presentNames[layer * 2] = $"present.{layer}.key";
            this.presentNames[(layer * 2) + 1] = $"present.{layer}.value";
        }

        if (!lm.InputMetadata.TryGetValue(this.pastNames[0], out var past))
        {
            throw new SpeechSynthesisException($"The language model has no input \"{this.pastNames[0]}\"; wrong model for Chatterbox Turbo.");
        }

        this.kvIsHalf = past.ElementType == typeof(Float16);
        this.hasTotalLength = lm.InputMetadata.ContainsKey(TotalSequenceLengthInput);
    }

    /// <summary>Element type of the key-value cache the loaded graph uses.</summary>
    public string KvType => this.kvIsHalf ? "float16" : "float32";

    /// <summary>
    /// Generates speech tokens. The result starts with the seeded START token and ends with STOP when the model
    /// stopped by itself (the same shape the reference recipe slices from).
    /// <paramref name="onToken"/>, when given, hears every speech token the moment it is picked (never the
    /// seeded START or the closing STOP), so a decoder can start on the first tokens while the loop goes on.
    /// <paramref name="timer"/>, when given, hears how long the first step (the prefill) took and the duration of
    /// every later language model step, which is the number a graphics card shared with the game inflates.
    /// <paramref name="steps"/> hears the same step durations, for a caller that has no timer (the engine's choice of device).
    /// <paramref name="maxNewTokens"/> stops the loop early (warm-up runs a handful of steps); without it the loop
    /// runs to STOP within <see cref="TokenBudget"/> of the prompt's text tokens.
    /// </summary>
    public List<long> Run(TurboDecodeInput input, CancellationToken ct, Action<long>? onToken = null, LineTimer? timer = null, int? maxNewTokens = null, LatencyHistogram? steps = null)
    {
        var generated = new List<long> { StartSpeechToken };
        var past = new OrtValue?[this.pastNames.Length];
        IDisposableReadOnlyCollection<OrtValue>? previous = null;
        using var runOptions = new RunOptions();
        try
        {
            long[] ids = input.TextIds;
            long lastPosition = 0;
            long totalLength = 0;
            var budget = Math.Min(maxNewTokens ?? TokenBudget(input.TextIds.Length), MaxNewTokens);
            for (var step = 0; step < budget; step++)
            {
                ct.ThrowIfCancellationRequested();
                var textEmbeds = this.EmbedTokens(ids, out var width);

                float[] embeds;
                int frames;
                long[] positions;
                if (step == 0)
                {
                    frames = input.ConditionFrames + ids.Length;
                    embeds = new float[frames * width];
                    input.ConditionEmbedding.AsSpan().CopyTo(embeds);
                    textEmbeds.AsSpan().CopyTo(embeds.AsSpan(input.ConditionFrames * width));
                    positions = new long[frames];
                    for (var i = 0; i < frames; i++)
                    {
                        positions[i] = i;
                    }

                    totalLength = frames;
                    lastPosition = frames - 1;
                }
                else
                {
                    frames = 1;
                    embeds = textEmbeds;
                    lastPosition++;
                    positions = [lastPosition];
                    totalLength++;
                }

                var mask = new long[totalLength];
                Array.Fill(mask, 1L);

                var stepStart = timer is null && steps is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
                var logits = this.RunLanguageModel(runOptions, embeds, frames, width, mask, positions, past, ref previous);
                if (timer is not null || steps is not null)
                {
                    var stepMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(stepStart).TotalMilliseconds;
                    if (step == 0)
                    {
                        timer?.AddPrefill(stepMs);
                    }
                    else
                    {
                        timer?.StepLatency.Add(stepMs);
                        steps?.Add(stepMs);
                    }
                }

                ApplyRepetitionPenalty(logits, RecentDistinct(generated, PenaltyWindow));
                // DEVIATION (documented): on short prompts this export can emit STOP as its very first token,
                // yielding zero speech tokens and no audio. FFXIV dialogue lines are short, so while under
                // MinSpeechTokens (~1 s of speech) the STOP logit is masked to guarantee a minimum utterance.
                if (generated.Count - 1 < MinSpeechTokens && StopSpeechToken < logits.Length)
                {
                    logits[StopSpeechToken] = float.NegativeInfinity;
                }

                CapRun(logits, generated, MaxRun);
                var next = Argmax(logits);
                generated.Add(next);
                if (next == StopSpeechToken)
                {
                    break;
                }

                onToken?.Invoke(next);

                ids = [next];
            }
        }
        finally
        {
            previous?.Dispose();
        }

        return generated;
    }

    private float[] EmbedTokens(long[] ids, out int width)
    {
        using var idValue = OrtValue.CreateTensorValueFromMemory(ids, [1L, ids.Length]);
        using var outputs = this.embed.Run(
            new RunOptions(),
            new Dictionary<string, OrtValue> { ["input_ids"] = idValue },
            this.embed.OutputNames);
        var shape = outputs[0].GetTensorTypeAndShape().Shape;
        width = (int)shape[^1];
        return outputs[0].GetTensorDataAsSpan<float>().ToArray();
    }

    private float[] RunLanguageModel(
        RunOptions runOptions,
        float[] embeds,
        int frames,
        int width,
        long[] mask,
        long[] positions,
        OrtValue?[] past,
        ref IDisposableReadOnlyCollection<OrtValue>? previous)
    {
        var owned = new List<OrtValue>();
        try
        {
            using var binding = this.lm.CreateIoBinding();
            owned.Add(OrtValue.CreateTensorValueFromMemory(embeds, [1L, frames, width]));
            binding.BindInput("inputs_embeds", owned[^1]);
            owned.Add(OrtValue.CreateTensorValueFromMemory(mask, [1L, mask.Length]));
            binding.BindInput("attention_mask", owned[^1]);
            owned.Add(OrtValue.CreateTensorValueFromMemory(positions, [1L, positions.Length]));
            binding.BindInput("position_ids", owned[^1]);
            if (this.hasTotalLength)
            {
                owned.Add(OrtValue.CreateTensorValueFromMemory(new[] { mask.Length }, Array.Empty<long>()));
                binding.BindInput(TotalSequenceLengthInput, owned[^1]);
            }

            // First step: zero-length CPU tensors seed the cache (ORT copies them to the device itself).
            for (var i = 0; i < this.pastNames.Length; i++)
            {
                if (past[i] is { } existing)
                {
                    binding.BindInput(this.pastNames[i], existing);
                }
                else
                {
                    owned.Add(this.EmptyCache());
                    binding.BindInput(this.pastNames[i], owned[^1]);
                }
            }

            // Output order is the binding order: logits first, then the present tensors.
            binding.BindOutputToDevice("logits", OrtMemoryInfo.DefaultInstance);
            foreach (var name in this.presentNames)
            {
                binding.BindOutputToDevice(name, this.kvMemory);
            }

            this.lm.RunWithBinding(runOptions, binding);
            binding.SynchronizeBoundOutputs();
            var outputs = binding.GetOutputValues();
            if (!this.outputOrderVerified)
            {
                var names = binding.GetOutputNames();
                if (names.Length != outputs.Count || names[0] != "logits" || !names.Skip(1).SequenceEqual(this.presentNames))
                {
                    outputs.Dispose();
                    throw new SpeechSynthesisException("The language model returned its outputs in an unexpected order.");
                }

                this.outputOrderVerified = true;
            }

            if (outputs.Count != this.presentNames.Length + 1)
            {
                outputs.Dispose();
                throw new SpeechSynthesisException($"The language model returned {outputs.Count} outputs, expected {this.presentNames.Length + 1}.");
            }

            var vocab = (int)outputs[0].GetTensorTypeAndShape().Shape[^1];
            var lastLogits = outputs[0].GetTensorDataAsSpan<float>()[^vocab..].ToArray();
            for (var i = 0; i < past.Length; i++)
            {
                past[i] = outputs[1 + i];
            }

            // The new inputs no longer reference the previous step's buffers; free them now.
            previous?.Dispose();
            previous = outputs;
            return lastLogits;
        }
        finally
        {
            foreach (var value in owned)
            {
                value.Dispose();
            }
        }
    }

    private OrtValue EmptyCache() => this.kvIsHalf
        ? OrtValue.CreateTensorValueFromMemory(Array.Empty<Float16>(), [1L, NumKvHeads, 0L, HeadDim])
        : OrtValue.CreateTensorValueFromMemory(Array.Empty<float>(), [1L, NumKvHeads, 0L, HeadDim]);

    /// <summary>Card's RepetitionPenaltyLogitsProcessor over the distinct generated ids: negative scores times the penalty, positive divided.</summary>
    internal static void ApplyRepetitionPenalty(float[] logits, IEnumerable<long> seen)
    {
        foreach (var token in seen)
        {
            if (token >= 0 && token < logits.Length)
            {
                ref var score = ref logits[(int)token];
                score = score < 0 ? score * RepetitionPenalty : score / RepetitionPenalty;
            }
        }
    }

    /// <summary>The distinct tokens among the last <paramref name="window"/> of <paramref name="generated"/>.</summary>
    internal static HashSet<long> RecentDistinct(List<long> generated, int window)
    {
        var recent = new HashSet<long>();
        for (var i = Math.Max(0, generated.Count - window); i < generated.Count; i++)
        {
            recent.Add(generated[i]);
        }

        return recent;
    }

    /// <summary>Masks the last token when it already ends a run of <paramref name="maxRun"/> identical picks.</summary>
    internal static void CapRun(float[] logits, List<long> generated, int maxRun)
    {
        if (generated.Count < maxRun)
        {
            return;
        }

        var last = generated[^1];
        for (var i = generated.Count - maxRun; i < generated.Count; i++)
        {
            if (generated[i] != last)
            {
                return;
            }
        }

        if (last >= 0 && last < logits.Length)
        {
            logits[(int)last] = float.NegativeInfinity;
        }
    }

    internal static long Argmax(ReadOnlySpan<float> logits)
    {
        var best = 0;
        var bestScore = float.NegativeInfinity;
        for (var i = 0; i < logits.Length; i++)
        {
            if (logits[i] > bestScore)
            {
                bestScore = logits[i];
                best = i;
            }
        }

        return best;
    }
}
