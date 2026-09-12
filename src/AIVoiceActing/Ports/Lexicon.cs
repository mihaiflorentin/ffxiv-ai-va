namespace AIVoiceActing.Ports;

/// <summary>Driven port applying user lexicons (pronunciation replacements) to text before synthesis.</summary>
public interface ILexicon
{
    string Apply(string text);
}
