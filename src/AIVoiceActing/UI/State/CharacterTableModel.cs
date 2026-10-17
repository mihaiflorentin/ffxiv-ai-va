namespace AIVoiceActing.UI.State;

using AIVoiceActing.Ports;

public enum CharacterColumn
{
    Name,
    World,
    Voice,
}

/// <summary>The knobs of one character's assignment; held as a pending edit while a slider drags.</summary>
public sealed record CharacterEdit(string VoiceId, float Bias, float Pitch, float Speed, float Volume)
{
    public static CharacterEdit From(VoiceProfile profile) =>
        new(profile.ReferenceVoiceId, profile.ExaggerationBias, profile.Pitch, profile.Speed, profile.Volume);
}

/// <summary>One table row: the stored profile with the strings the table sorts and searches on.</summary>
public sealed record CharacterRow(VoiceProfile Profile, SpeakerKeyView Key, string WorldLabel, string VoiceName);

/// <summary>
/// The NPC or Player table's brain: the stored assignments of one speaker kind, sorted by a
/// column and filtered by a search, rebuilt only when something changed (a sort, a filter, a
/// write through this model, or the refresh interval, which picks up first-sight assignments the
/// game adds while the window is open). Slider edits sit in a pending slot and are written to
/// the store exactly once, when the drag is released. Deleting a row posts an Undo toast that
/// restores the assignment. Pure: it reads and writes through <see cref="IProfileStore"/> only.
/// </summary>
public sealed class CharacterTableModel
{
    /// <summary>How long the rows are reused before the store is read again.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(750);

    private readonly string keyPrefix;
    private readonly bool player;
    private readonly IProfileStore store;
    private readonly StatusCenter status;
    private readonly Func<uint?, string> worldLabel;
    private readonly Func<string, string> voiceName;
    private readonly Func<DateTimeOffset> clock;
    private readonly Dictionary<string, CharacterEdit> pending = new(StringComparer.Ordinal);

    private VoiceProfile[] loaded = [];
    private IReadOnlyList<CharacterRow> rows = [];
    private DateTimeOffset loadedAt = DateTimeOffset.MinValue;
    private bool reload = true;
    private bool rebuild = true;
    private string filter = string.Empty;
    private CharacterColumn column = CharacterColumn.Name;
    private bool sortAscending = true;

    public CharacterTableModel(
        bool player,
        IProfileStore store,
        StatusCenter status,
        Func<uint?, string> worldLabel,
        Func<string, string> voiceName,
        Func<DateTimeOffset>? clock = null)
    {
        this.player = player;
        this.keyPrefix = player ? "pc:" : "npc:";
        this.store = store;
        this.status = status;
        this.worldLabel = worldLabel;
        this.voiceName = voiceName;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public bool IsPlayer => this.player;

    public string KeyPrefix => this.keyPrefix;

    public CharacterColumn SortColumn => this.column;

    public bool SortAscending => this.sortAscending;

    public string Filter
    {
        get => this.filter;
        set
        {
            var next = value ?? string.Empty;
            if (next != this.filter)
            {
                this.filter = next;
                this.rebuild = true;
            }
        }
    }

    /// <summary>Every stored assignment of this kind, ignoring the filter.</summary>
    public int Total
    {
        get
        {
            this.Refresh();
            return this.loaded.Length;
        }
    }

    /// <summary>The rows to draw: sorted, filtered, cached between changes.</summary>
    public IReadOnlyList<CharacterRow> Rows
    {
        get
        {
            this.Refresh();
            return this.rows;
        }
    }

    /// <summary>The stored profiles of this kind (for export and the add form's duplicate check).</summary>
    public IReadOnlyList<VoiceProfile> Stored
    {
        get
        {
            this.Refresh();
            return this.loaded;
        }
    }

    /// <summary>"12 characters" or "3 of 12 shown".</summary>
    public string CountText
    {
        get
        {
            var shown = this.Rows.Count;
            var noun = this.player ? "player" : "NPC";
            return this.filter.Trim().Length > 0
                ? $"{shown} of {this.Total} shown"
                : $"{this.Total} {noun}{(this.Total == 1 ? string.Empty : "s")}";
        }
    }

    public EmptyDescriptor? Empty
    {
        get
        {
            if (this.Rows.Count > 0)
            {
                return null;
            }

            if (this.Total == 0)
            {
                var who = this.player ? "players" : "NPCs";
                return new EmptyDescriptor(
                    ListEmptyKind.Nothing,
                    $"No {who} cast yet.",
                    this.player
                        ? "Players get a voice the first time they speak. You can also add one by name above."
                        : "NPCs get a voice the first time they speak. You can also add one by name above.",
                    null);
            }

            return new EmptyDescriptor(
                ListEmptyKind.NoMatches,
                $"No one matches \"{this.filter.Trim()}\".",
                "Try a shorter search.",
                "Clear search");
        }
    }

    /// <summary>Sorts by <paramref name="by"/>; choosing the same column again keeps the direction given.</summary>
    public void SortBy(CharacterColumn by, bool ascending)
    {
        if (by == this.column && ascending == this.sortAscending)
        {
            return;
        }

        this.column = by;
        this.sortAscending = ascending;
        this.rebuild = true;
    }

    /// <summary>Forces the next read to go back to the store (after an import, a clear or a reload).</summary>
    public void Invalidate() => this.reload = true;

    // ---- pending edits ----

    /// <summary>The edit being dragged for this speaker, or its stored values.</summary>
    public CharacterEdit Effective(VoiceProfile profile) =>
        this.pending.TryGetValue(profile.SpeakerKey, out var edit) ? edit : CharacterEdit.From(profile);

    public bool HasPending(string speakerKey) => this.pending.ContainsKey(speakerKey);

    public int PendingCount => this.pending.Count;

    /// <summary>Holds a live slider value; nothing is written until <see cref="CommitPending"/>.</summary>
    public void SetPending(string speakerKey, CharacterEdit edit) => this.pending[speakerKey] = edit;

    /// <summary>Writes the held edit through the store once and drops it. False when nothing was held.</summary>
    public bool CommitPending(string speakerKey)
    {
        if (!this.pending.Remove(speakerKey, out var edit))
        {
            return false;
        }

        this.Write(speakerKey, edit);
        return true;
    }

    /// <summary>Writes a discrete change (a picked voice) at once, dropping any held slider values.</summary>
    public void Commit(string speakerKey, CharacterEdit edit)
    {
        this.pending.Remove(speakerKey);
        this.Write(speakerKey, edit);
    }

    // ---- delete and undo ----

    /// <summary>
    /// Removes one character and offers Undo for a few seconds; undo restores the assignment as a
    /// manual override. False when the speaker was not stored.
    /// </summary>
    public bool Delete(string speakerKey)
    {
        this.pending.Remove(speakerKey);
        var profile = this.Stored.FirstOrDefault(p => p.SpeakerKey == speakerKey)
            ?? this.store.Entries.FirstOrDefault(p => p.SpeakerKey == speakerKey);
        if (profile is null || !this.store.Remove(speakerKey))
        {
            return false;
        }

        this.reload = true;
        var name = SpeakerKeyView.Parse(speakerKey).Name;
        this.status.PostUndo(
            $"Removed {name}",
            () =>
            {
                this.Write(speakerKey, CharacterEdit.From(profile));
            },
            "They pick a new voice the next time they speak.");
        return true;
    }

    /// <summary>Removes every stored character of this kind (the caller has confirmed). Returns how many went.</summary>
    public int RemoveAll()
    {
        var removed = this.Stored.Count(p => this.store.Remove(p.SpeakerKey));
        this.pending.Clear();
        this.reload = true;
        return removed;
    }

    // ---- internals ----

    private void Write(string speakerKey, CharacterEdit edit)
    {
        this.store.SetOverride(
            speakerKey,
            edit.VoiceId,
            Math.Clamp(edit.Bias, 0f, 1f),
            Math.Clamp(edit.Volume, 0f, 2f),
            edit.Pitch,
            edit.Speed);
        this.reload = true;
    }

    private void Refresh()
    {
        var now = this.clock();
        if (this.reload || now - this.loadedAt >= RefreshInterval)
        {
            this.loaded =
            [
                .. this.store.Entries.Where(e => e.SpeakerKey.StartsWith(this.keyPrefix, StringComparison.Ordinal)),
            ];
            this.loadedAt = now;
            this.reload = false;
            this.rebuild = true;
        }

        if (this.rebuild)
        {
            this.rows = this.Build();
            this.rebuild = false;
        }
    }

    private IReadOnlyList<CharacterRow> Build()
    {
        var words = this.filter.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var all = this.loaded.Select(profile =>
        {
            var key = SpeakerKeyView.Parse(profile.SpeakerKey);
            var voice = this.Effective(profile).VoiceId;
            return new CharacterRow(profile, key, this.worldLabel(key.World), this.voiceName(voice));
        });

        var kept = all.Where(row => words.All(word => Matches(row, word, this.Effective(row.Profile).VoiceId)));
        Func<CharacterRow, string> sortKey = this.column switch
        {
            CharacterColumn.World => row => row.WorldLabel,
            CharacterColumn.Voice => row => row.VoiceName,
            _ => row => row.Key.Name,
        };

        var ordered = this.sortAscending
            ? kept.OrderBy(sortKey, StringComparer.OrdinalIgnoreCase)
            : kept.OrderByDescending(sortKey, StringComparer.OrdinalIgnoreCase);
        return [.. ordered.ThenBy(row => row.Key.Name, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Profile.SpeakerKey, StringComparer.Ordinal)];
    }

    private static bool Matches(CharacterRow row, string word, string voiceId) =>
        row.Key.Name.Contains(word, StringComparison.OrdinalIgnoreCase)
        || row.WorldLabel.Contains(word, StringComparison.OrdinalIgnoreCase)
        || row.VoiceName.Contains(word, StringComparison.OrdinalIgnoreCase)
        || voiceId.Contains(word, StringComparison.OrdinalIgnoreCase);
}
