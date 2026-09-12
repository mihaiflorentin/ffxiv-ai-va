namespace AIVoiceActing.Domain.Pipeline;

/// <summary>Which half of a character name to say when the partial-name option is on.</summary>
public enum FirstOrLastName
{
    First,
    Last,
}

/// <summary>
/// Trigger/exclusion gate (pure port of TextToTalk's IsTextGood/IsTextBad): the lists are
/// read live per call so configuration edits apply immediately; entries with blank text are
/// ignored, an empty trigger list admits everything, and the pipeline checks exclusions
/// first so an exclusion wins over a trigger.
/// </summary>
public sealed class TextGate
{
    private readonly Func<IReadOnlyList<TriggerSpec>> good;
    private readonly Func<IReadOnlyList<TriggerSpec>> bad;

    public TextGate(
        Func<IReadOnlyList<TriggerSpec>> good,
        Func<IReadOnlyList<TriggerSpec>> bad)
    {
        this.good = good;
        this.bad = bad;
    }

    public bool IsTextGood(string text)
    {
        var good = this.good();
        if (good.Count == 0)
        {
            return true;
        }

        return good
            .Where(t => !string.IsNullOrWhiteSpace(t.Text))
            .Any(t => t.Match(text));
    }

    public bool IsTextBad(string text) =>
        this.bad()
            .Where(t => !string.IsNullOrWhiteSpace(t.Text))
            .Any(t => t.Match(text));
}

/// <summary>
/// From-you filters (pure port of MessageHandlerFilters.ShouldSayFromYou /
/// OnlyMessagesFromYou) over an injected local-player-name provider. TextToTalk's
/// null-local-player behavior is ported exactly: Contains("") matches everything, so an
/// unreadable local player name disables both filters' discriminating power.
/// </summary>
public sealed class FromYouGate
{
    private readonly Func<bool> skipMessagesFromYou;
    private readonly Func<bool> onlyMessagesFromYou;
    private readonly Func<string?> localPlayerName;

    public FromYouGate(
        Func<bool> skipMessagesFromYou,
        Func<bool> onlyMessagesFromYou,
        Func<string?> localPlayerName)
    {
        this.skipMessagesFromYou = skipMessagesFromYou;
        this.onlyMessagesFromYou = onlyMessagesFromYou;
        this.localPlayerName = localPlayerName;
    }

    /// <summary>False when the message comes from the local player and skipping is on.</summary>
    public bool ShouldSayFromYou(string? speaker)
    {
        if (string.IsNullOrEmpty(speaker))
        {
            return true;
        }

        return !this.skipMessagesFromYou() || !speaker.Contains(this.localPlayerName() ?? "");
    }

    /// <summary>False when the message does not come from the local player and filtering is on.</summary>
    public bool OnlyMessagesFromYou(string? speaker)
    {
        if (string.IsNullOrEmpty(speaker))
        {
            return true;
        }

        return !this.onlyMessagesFromYou() || speaker.Contains(this.localPlayerName() ?? "");
    }
}

/// <summary>
/// "says" prefix policy (pure port of MessageHandlerFilters' speaker bookkeeping plus
/// TalkUtils.GetPartialName): when enabled, the speaker's name is composed into the text
/// ("Merlwyb says …"), either every line or only when the speaker changed, optionally using
/// just the first or last name. The chat variant takes the NPC-dialogue channel fact as a
/// bool so Domain stays Dalamud-free.
/// </summary>
public sealed class SpeakerAnnouncer
{
    private readonly Func<bool> enableNameWithSay;
    private readonly Func<bool> nameNpcWithSay;
    private readonly Func<bool> disallowMultipleSay;
    private readonly Func<bool> sayPartialName;
    private readonly Func<FirstOrLastName> onlySayFirstOrLastName;
    private string? lastSpeaker;

    public SpeakerAnnouncer(
        Func<bool> enableNameWithSay,
        Func<bool> nameNpcWithSay,
        Func<bool> disallowMultipleSay,
        Func<bool> sayPartialName,
        Func<FirstOrLastName> onlySayFirstOrLastName)
    {
        this.enableNameWithSay = enableNameWithSay;
        this.nameNpcWithSay = nameNpcWithSay;
        this.disallowMultipleSay = disallowMultipleSay;
        this.sayPartialName = sayPartialName;
        this.onlySayFirstOrLastName = onlySayFirstOrLastName;
    }

    public bool IsSameSpeaker(string? speaker) => this.lastSpeaker == speaker;

    public void SetLastSpeaker(string? speaker) => this.lastSpeaker = speaker;

    /// <summary>Talk/BattleTalk variant (TTT ShouldSaySender(): NPCs always named when on).</summary>
    private bool ShouldSaySender() => this.enableNameWithSay() && this.nameNpcWithSay();

    /// <summary>
    /// True when the speaker's name should be composed into this line: only if the feature
    /// is on, and — with DisallowMultipleSay — only when the speaker actually changed.
    /// </summary>
    public bool ShouldProcessSpeaker(string? speaker)
    {
        if (!string.IsNullOrEmpty(speaker) && this.ShouldSaySender())
        {
            if (!this.disallowMultipleSay() || !this.IsSameSpeaker(speaker))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Chat variant: NPCDialogue (numeric 28) is named only when NameNpcWithSay is on.</summary>
    public bool ShouldSaySender(bool isNpcDialogue) =>
        this.enableNameWithSay() && (this.nameNpcWithSay() || !isNpcDialogue);

    /// <summary>Applies the partial-name option (identity when disabled).</summary>
    public string? PartialName(string? name) => this.sayPartialName()
        ? GetPartialName(name, this.onlySayFirstOrLastName())
        : name;

    public static string? GetPartialName(string? name, FirstOrLastName part)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var names = name.Split(' ');
        return part switch
        {
            FirstOrLastName.First => names[0],
            FirstOrLastName.Last => names.Length == 1 ? names[0] : names[^1],
            _ => throw new ArgumentOutOfRangeException(nameof(part), part, "Enumeration value is out of range."),
        };
    }

    public static string ComposeSaid(string speakerName, string text) => $"{speakerName} says {text}";
}
