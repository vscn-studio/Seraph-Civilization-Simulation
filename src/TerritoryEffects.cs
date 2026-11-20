using HarmonyLib;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace SeraphCivilizationSimulation;

[ProtoContract]
public sealed class TerritoryDiscoveryPacket
{
    [ProtoMember(1)] public string Name { get; set; } = "";
}

public sealed partial class VscnCivilizationModSystem
{
    private const string TerritoryStatCode = "vscncs-territory";
    private readonly Dictionary<string, bool> territoryPresence = [];
    private readonly Dictionary<string, string> territoryDiscoveryPresence = [];
    private List<TerritoryEffectView> BuildTerritoryEffects(IServerPlayer player, bool member, bool home)
    {
        var effects = new List<TerritoryEffectView>();
        void Add(string code, float value) { if (value != 0) effects.Add(new() { Code = code, Value = value }); }
        if (!member && !home) return effects;
        if (home)
        {
            Add("effect-health", config.Territory.HealthBonus);
        }
        return effects;
    }

    private void StartTerritoryEffects()
    {
        sapi!.Event.RegisterGameTickListener(_ => UpdateTerritoryEffects(), 1000);
    }

    private void UpdateTerritoryEffects()
    {
        if (disposed) return;
        bool expired = PruneAuthorizations();
        foreach (var player in Online)
        {
            if (player.Entity == null) continue;
            var pos = player.Entity.Pos;
            var current = tribeState.Tribes.SelectMany(t => t.Sites.Select(site => (tribe: t, site)))
                .FirstOrDefault(pair => pair.site.Contains(new TerritoryCell((int)Math.Floor(pos.X), (int)Math.Floor(pos.Y), (int)Math.Floor(pos.Z)), pos.Dimension));
            string key = "", name = "";
            if (current.tribe != null)
            {
                var cell = new TerritoryCell((int)Math.Floor(pos.X), 0, (int)Math.Floor(pos.Z));
                var area = current.site.NamedAreas?.FirstOrDefault(a => a.Cells.Contains(cell));
                name = area?.Name ?? current.tribe.Name;
                key = current.tribe.Id + ":" + current.site.Id + ":" + (area == null ? "-1" : current.site.NamedAreas.IndexOf(area).ToString());
            }
            if (key.Length > 0 && territoryDiscoveryPresence.GetValueOrDefault(player.PlayerUID) != key && name.Length > 0)
                channel!.SendPacket(new TerritoryDiscoveryPacket { Name = name }, player);
            territoryDiscoveryPresence[player.PlayerUID] = key;
            var (member, home) = TerritoryStatus(player);
            SetTerritoryStat(player.Entity, "walkspeed", 0); // Clear persisted bonuses from older saves.
            float previousHealth = player.Entity.Stats.GetBlended("maxhealthExtraPoints");
            SetTerritoryStat(player.Entity, "maxhealthExtraPoints", home ? config.Territory.HealthBonus : 0);
            if (Math.Abs(previousHealth - player.Entity.Stats.GetBlended("maxhealthExtraPoints")) > .0001f
                && player.Entity.GetBehavior("health") is { } health)
                AccessTools.Method(health.GetType(), "UpdateMaxHealth")?.Invoke(health, null);
            bool changed = territoryPresence.TryGetValue(player.PlayerUID, out bool previous) && previous != home;
            territoryPresence[player.PlayerUID] = home;
            if (changed || expired) SendCivilizationSnapshot(player, false);
        }
    }

    private static void SetTerritoryStat(EntityPlayer entity, string name, float value)
    {
        var category = entity.Stats.FirstOrDefault(pair => pair.Key == name).Value;
        float previous = category != null && category.ValuesByKey.TryGetValue(TerritoryStatCode, out var stat) ? stat.Value : 0;
        if (Math.Abs(previous - value) < .0001f) return;
        if (Math.Abs(value) < 0.0001f) entity.Stats.Remove(name, TerritoryStatCode);
        else entity.Stats.Set(name, TerritoryStatCode, value);
    }

}
