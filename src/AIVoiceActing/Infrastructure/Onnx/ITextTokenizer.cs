namespace AIVoiceActing.Infrastructure.Onnx;

/// <summary>
/// Engine-side tokenizer seam: encode dialogue text to model token ids. Decode is not
/// part of the contract — the Chatterbox recipe only consumes ids (speech tokens come
/// from the language model, never text decoding).
/// </summary>
public interface ITextTokenizer
{
    /// <summary>Encodes text to token ids (Chatterbox text vocabulary, ids &lt; 6561).</summary>
    int[] Encode(string text);
}
