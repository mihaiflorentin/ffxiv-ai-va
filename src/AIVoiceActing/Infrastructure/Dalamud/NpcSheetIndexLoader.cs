namespace AIVoiceActing.Infrastructure.Dalamud;

using AIVoiceActing.Domain;
using global::Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

/// <summary>
/// Builds the <see cref="NpcIdentityIndex"/> from the game's sheets: ENpcResident carries
/// the name, ENpcBase (same row id) the race, gender, body type and model. Loaded once on a
/// background task at plugin start (a few tens of thousands of rows); until it finishes,
/// lookups simply miss.
/// </summary>
public sealed class NpcSheetIndexLoader
{
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private volatile NpcIdentityIndex? index;

    public NpcSheetIndexLoader(IDataManager data, IPluginLog log)
    {
        this.data = data;
        this.log = log;
    }

    /// <summary>The built index, or null while loading (or after a failed load).</summary>
    public NpcIdentityIndex? Index => this.index;

    public Task StartAsync() => Task.Run(() =>
    {
        try
        {
            var residents = this.data.GetExcelSheet<ENpcResident>();
            var bases = this.data.GetExcelSheet<ENpcBase>();
            var built = new NpcIdentityIndex();
            foreach (var resident in residents)
            {
                var name = resident.Singular.ExtractText();
                if (string.IsNullOrWhiteSpace(name) || bases.GetRowOrDefault(resident.RowId) is not { } npc)
                {
                    continue;
                }

                var race = npc.Race.RowId;
                var model = npc.ModelChara.RowId;
                built.Add(name, new NpcSheetIdentity(
                    Race: race == 0 ? null : (byte)race,
                    Tribe: npc.Tribe.RowId == 0 ? null : (byte)npc.Tribe.RowId,
                    Sex: npc.Gender,
                    BodyType: npc.BodyType == 0 ? null : npc.BodyType,
                    ModelCharaId: model == 0 ? null : (int)model));
            }

            this.index = built;
            this.log.Info($"NPC sheet index ready: {built.Count} names.");
        }
        catch (Exception ex)
        {
            this.log.Warning($"NPC sheet index failed to load ({ex.Message}); unspawned speakers stay unknown.");
        }
    });
}
