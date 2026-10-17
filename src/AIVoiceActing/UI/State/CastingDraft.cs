namespace AIVoiceActing.UI.State;

using AIVoiceActing.Ports;

/// <summary>What <see cref="CastingDraft.Save"/> did, for the confirmation line.</summary>
public sealed record CastingSaveOutcome(string Name, bool CopiedFromDefault);

/// <summary>
/// The casting edits in progress: a <see cref="CastingTabModel"/> loaded from the store plus the
/// rules for saving it. The built-in Default preset cannot be changed, so saving edits made on
/// top of it stores them as a new preset ("My casting") and makes that one active; saving any
/// other preset writes it in place. Either way the voice map is rebuilt so the change takes
/// effect on the next line. One instance is shared by the Voices page and the preset panel.
/// </summary>
public sealed class CastingDraft
{
    public const string FirstCopyName = "My casting";

    private readonly Func<ICastingPresetStore> storeFactory;
    private readonly Action invalidateVoiceMap;
    private bool loaded;

    public CastingDraft(Func<ICastingPresetStore> storeFactory, Action invalidateVoiceMap)
    {
        this.storeFactory = storeFactory;
        this.invalidateVoiceMap = invalidateVoiceMap;
    }

    /// <summary>The edit buffer and selection. Call <see cref="EnsureLoaded"/> before drawing from it.</summary>
    public CastingTabModel Model { get; } = new();

    public bool Dirty => this.Model.Dirty;

    /// <summary>The preset being edited.</summary>
    public string Name => this.Model.SelectedName;

    /// <summary>True when the preset being edited is the one speakers are cast from.</summary>
    public bool IsActive => this.storeFactory().ActivePresetName == this.Model.SelectedName;

    /// <summary>The name of the preset speakers are cast from.</summary>
    public string ActiveName => this.storeFactory().ActivePresetName;

    /// <summary>Loads the active preset the first time it is needed.</summary>
    public void EnsureLoaded()
    {
        if (this.loaded)
        {
            return;
        }

        var store = this.storeFactory();
        this.Select(store.ActivePresetName);
    }

    /// <summary>Starts editing <paramref name="name"/>, dropping unsaved edits.</summary>
    public void Select(string name)
    {
        var store = this.storeFactory();
        this.Model.Load(name, LoadPreset(store, name));
        this.loaded = true;
    }

    /// <summary>Drops unsaved edits by reloading the preset from the store.</summary>
    public void Discard() => this.Select(this.Model.SelectedName);

    /// <summary>Persists the edits; throws <see cref="CastingPresetException"/> when the store refuses.</summary>
    public CastingSaveOutcome Save()
    {
        this.EnsureLoaded();
        var store = this.storeFactory();
        var copied = false;
        if (this.Model.SelectedName == CastingPreset.DefaultPresetName)
        {
            var name = UniqueName(store.PresetNames);
            this.Model.Fork(CastingPreset.DefaultPresetName, this.Model.EditBuffer, name);
            store.Save(this.Model.EditBuffer);
            store.Activate(name);
            copied = true;
        }
        else
        {
            store.Save(this.Model.EditBuffer);
        }

        this.Model.MarkSaved();
        if (store.ActivePresetName == this.Model.SelectedName)
        {
            this.invalidateVoiceMap();
        }

        return new CastingSaveOutcome(this.Model.SelectedName, copied);
    }

    /// <summary>"My casting", then "My casting 2", "My casting 3"... skipping names already taken.</summary>
    public static string UniqueName(IEnumerable<string> existing)
    {
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(FirstCopyName))
        {
            return FirstCopyName;
        }

        for (var n = 2; ; n++)
        {
            var candidate = $"{FirstCopyName} {n}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>The built-in Default is derived from the base map; everything else comes from the file (a vanished preset falls back to Default).</summary>
    private static CastingPreset LoadPreset(ICastingPresetStore store, string name)
    {
        if (name == CastingPreset.DefaultPresetName)
        {
            return store.GetDefault();
        }

        try
        {
            return store.Get(name);
        }
        catch (CastingPresetException)
        {
            return store.GetDefault();
        }
    }
}
