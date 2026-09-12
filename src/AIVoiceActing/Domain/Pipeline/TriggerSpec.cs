namespace AIVoiceActing.Domain.Pipeline;

using System.Text.RegularExpressions;

/// <summary>
/// Pure port of TextToTalk's Trigger: a plain substring or regex match against message
/// text; an invalid regex never matches (TTT catches ArgumentException). (TTT's
/// <c>ShouldRemove</c> is UI bookkeeping and stays with the configuration UI in a later
/// step.)
/// </summary>
public sealed record TriggerSpec(string Text, bool IsRegex)
{
    public bool Match(string? test)
    {
        if (test is null)
        {
            return false;
        }

        if (!this.IsRegex)
        {
            return test.Contains(this.Text);
        }

        try
        {
            return Regex.Match(test, this.Text).Success;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
