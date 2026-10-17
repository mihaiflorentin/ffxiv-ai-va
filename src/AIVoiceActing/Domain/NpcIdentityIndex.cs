namespace AIVoiceActing.Domain;

/// <summary>Race/sex/age facts for one NPC as the game's sheets list them.</summary>
public sealed record NpcSheetIdentity(byte? Race, byte? Tribe, byte? Sex, byte? BodyType, int? ModelCharaId);

/// <summary>
/// Name → identity index over the game's NPC sheets (ENpcResident names joined to
/// ENpcBase appearance rows), used when a speaker is not spawned. Many generic names
/// ("Ul'dahn merchant") map to several different NPCs: a name whose rows disagree on race
/// or sex is ambiguous and resolves to nothing; rows that agree but differ in age or model
/// keep the agreed facts and drop the rest. Case-insensitive; pure and thread-safe after
/// construction (built once off the game thread).
/// </summary>
public sealed class NpcIdentityIndex
{
    private readonly Dictionary<string, NpcSheetIdentity?> byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of names that resolve to an identity.</summary>
    public int Count => this.byName.Values.Count(v => v is not null);

    /// <summary>Adds one sheet row. Rows without race and model are ignored (invisible or prop NPCs).</summary>
    public void Add(string name, NpcSheetIdentity identity)
    {
        var key = name.Trim();
        if (key.Length == 0 || (identity.Race is null && identity.ModelCharaId is null))
        {
            return;
        }

        if (!this.byName.TryGetValue(key, out var existing))
        {
            this.byName[key] = identity;
            return;
        }

        if (existing is null)
        {
            return;
        }

        if (existing.Race != identity.Race || existing.Sex != identity.Sex)
        {
            this.byName[key] = null;
            return;
        }

        this.byName[key] = existing with
        {
            Tribe = existing.Tribe == identity.Tribe ? existing.Tribe : null,
            BodyType = existing.BodyType == identity.BodyType ? existing.BodyType : null,
            ModelCharaId = existing.ModelCharaId == identity.ModelCharaId ? existing.ModelCharaId : null,
        };
    }

    public bool TryGet(string name, out NpcSheetIdentity identity)
    {
        if (this.byName.TryGetValue(name.Trim(), out var found) && found is not null)
        {
            identity = found;
            return true;
        }

        identity = null!;
        return false;
    }
}
