namespace AIVoiceActing.Domain;

/// <summary>
/// Turns a GoEmotions classifier's 28 label probabilities into the plugin's delivery
/// plan, blended with the surface rules. The classifier reads meaning ("I lost her" is
/// sad with no ellipsis); the rules keep the literal signals it cannot hear (laugh
/// tokens, "sigh", shouting). Below <see cref="MinConfidence"/> the rules plan stands,
/// because GoEmotions is noisy on short game lines (reported macro F1 ≈ 0.5).
/// </summary>
public static class EmotionBlend
{
    /// <summary>GoEmotions label order (config.json id2label of SamLowe/roberta-base-go_emotions).</summary>
    public static readonly string[] Labels =
    [
        "admiration", "amusement", "anger", "annoyance", "approval", "caring", "confusion",
        "curiosity", "desire", "disappointment", "disapproval", "disgust", "embarrassment",
        "excitement", "fear", "gratitude", "grief", "joy", "love", "nervousness", "optimism",
        "pride", "realization", "relief", "remorse", "sadness", "surprise", "neutral",
    ];

    public const float MinConfidence = 0.35f;

    /// <summary>Plugin emotion each label feeds (null: no delivery change).</summary>
    private static readonly Dictionary<string, string?> LabelToEmotion = new(StringComparer.Ordinal)
    {
        ["anger"] = "angry", ["annoyance"] = "angry", ["disgust"] = "angry", ["disapproval"] = "angry",
        ["sadness"] = "sad", ["grief"] = "sad", ["disappointment"] = "sad", ["remorse"] = "sad",
        ["embarrassment"] = "sad",
        ["fear"] = "afraid", ["nervousness"] = "afraid",
        ["amusement"] = "amused",
        ["excitement"] = "excited", ["joy"] = "excited", ["surprise"] = "excited", ["pride"] = "excited",
        ["curiosity"] = "curious", ["confusion"] = "curious",
        ["caring"] = "warm", ["love"] = "warm", ["gratitude"] = "warm", ["relief"] = "warm",
        ["admiration"] = "warm", ["optimism"] = "warm", ["approval"] = "warm", ["desire"] = "warm",
        ["realization"] = null, ["neutral"] = null,
    };

    public static EmotionPlan Plan(IReadOnlyList<float> probabilities, EmotionPlan rules)
    {
        if (probabilities.Count != Labels.Length)
        {
            return rules;
        }

        // Strongest label per plugin emotion, then the strongest emotion overall.
        var best = new Dictionary<string, (float Score, string Label)>(StringComparer.Ordinal);
        for (var i = 0; i < Labels.Length; i++)
        {
            if (LabelToEmotion[Labels[i]] is { } emotion
                && (!best.TryGetValue(emotion, out var current) || probabilities[i] > current.Score))
            {
                best[emotion] = (probabilities[i], Labels[i]);
            }
        }

        var top = best.OrderByDescending(kv => kv.Value.Score).FirstOrDefault();
        if (top.Key is null || top.Value.Score < MinConfidence)
        {
            return rules;
        }

        // A literal laugh or sigh in the text is the stronger signal for those two.
        if (rules.Emotion is "amused" or "angry" && rules.Tags.Count > 0)
        {
            return rules;
        }

        var confidence = top.Value.Score;
        var plan = top.Key switch
        {
            "angry" => new EmotionPlan("angry", 0.6f + (0.3f * confidence), [], 0, 1.05f),
            "sad" => new EmotionPlan(
                "sad", 0.3f - (0.15f * confidence),
                top.Value.Label is "grief" or "sadness" && confidence > 0.6f ? ["sigh"] : [],
                PauseBeforeMs: 250, Volume: 0.95f),
            "afraid" => new EmotionPlan("afraid", 0.55f + (0.3f * confidence), confidence > 0.6f ? ["gasp"] : [], 0, 1f),
            "amused" => new EmotionPlan("amused", 0.6f, confidence > 0.6f ? ["chuckle"] : [], 0, 1f),
            "excited" => new EmotionPlan("excited", 0.6f + (0.3f * confidence), [], 0, 1.05f),
            "curious" => new EmotionPlan("curious", 0.6f, [], 0, 1f),
            _ => new EmotionPlan("warm", 0.45f, [], 0, 1f),
        };

        // Keep literal tags from the text (e.g. a written [sigh]) alongside the model's.
        var tags = plan.Tags.Concat(rules.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return plan with { Tags = tags };
    }

    /// <summary>Logistic sigmoid over raw logits (the classifier is multi-label).</summary>
    public static float[] Sigmoid(ReadOnlySpan<float> logits)
    {
        var output = new float[logits.Length];
        for (var i = 0; i < logits.Length; i++)
        {
            output[i] = 1f / (1f + MathF.Exp(-logits[i]));
        }

        return output;
    }
}
