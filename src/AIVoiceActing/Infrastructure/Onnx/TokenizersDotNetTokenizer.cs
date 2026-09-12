namespace AIVoiceActing.Infrastructure.Onnx;

using Tokenizers.DotNet;

/// <summary>
/// ITextTokenizer over Tokenizers.DotNet (Rust huggingface/tokenizers binding), loading a
/// tokenizer.json directly. Behind the seam so a fallback tokenizer can replace it without
/// touching session code.
/// </summary>
public sealed class TokenizersDotNetTokenizer : ITextTokenizer
{
    private readonly Tokenizer tokenizer;

    /// <param name="tokenizerJsonPath">Path to the downloaded tokenizer.json.</param>
    public TokenizersDotNetTokenizer(string tokenizerJsonPath)
    {
        this.tokenizer = new Tokenizer(vocabPath: tokenizerJsonPath);
    }

    public int[] Encode(string text) =>
        [.. this.tokenizer.Encode(text).Select(id => unchecked((int)id))];
}
