namespace AIVoiceActing.Tests.Mock;

using AIVoiceActing.Ports;

/// <summary>Fake ILexicon (census fake pattern): records calls, applies Func when set.</summary>
public sealed class FakeLexicon : ILexicon
{
    public Func<string, string>? ApplyFunc { get; set; }

    public List<string> Calls { get; } = [];

    public string Apply(string text)
    {
        this.Calls.Add(text);
        return this.ApplyFunc?.Invoke(text) ?? text;
    }
}
