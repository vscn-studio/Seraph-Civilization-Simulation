using Vintagestory.API.Server;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnCivilizationModSystem
{
    private string PreviewTerritory(IServerPlayer player, string id)
    {
        if (tribeState.FindMember(player.PlayerUID) == null) return "尚未加入部落。";
        SendCivilizationSnapshot(player, false); return "";
    }
    private void FillBuildingTerritorySnapshot(IServerPlayer player, CivilizationSnapshotPacket snapshot, TribeRecord? tribe)
    {
        if (tribe == null) return;
        snapshot.TerritoryArea = TerritoryArea(tribe); snapshot.TerritoryBudget = FixedTerritoryRules.Area;
        snapshot.TerritoryMemberCount = FixedTerritoryRules.Members(tribe);
        snapshot.TerritoryRevision = tribe.Sites.FirstOrDefault()?.Revision ?? 0;
        snapshot.TerritoryHint = "固定 16×16 格 · 中心向下 10 格 · 向上无限";
        // Legacy hall/shrink protocol fields stay reserved and empty.
    }
    internal static List<TerritoryBoundaryEdge> FixedTerritoryEdges(TribeRecord tribe, int worldHeight)
    {
        var result = new List<TerritoryBoundaryEdge>();
        foreach (var site in tribe.Sites)
        {
            var b = site.Bounds;
            int top = Math.Max(tribe.Y, worldHeight - 1);
            void Add(int x1, int z1, int x2, int z2) => result.Add(new()
            { X1 = x1, Z1 = z1, X2 = x2, Z2 = z2, SurfaceY = tribe.Y,
                BelowY = Math.Max(0, tribe.Y - FixedTerritoryRules.Depth), AboveY = top });
            if (site.Footprint != null)
            {
                foreach (var p in site.FootprintCells!)
                {
                    if (!site.ContainsColumn(p.X, p.Z - 1)) Add(p.X, p.Z, p.X + 1, p.Z);
                    if (!site.ContainsColumn(p.X + 1, p.Z)) Add(p.X + 1, p.Z, p.X + 1, p.Z + 1);
                    if (!site.ContainsColumn(p.X, p.Z + 1)) Add(p.X + 1, p.Z + 1, p.X, p.Z + 1);
                    if (!site.ContainsColumn(p.X - 1, p.Z)) Add(p.X, p.Z + 1, p.X, p.Z);
                }
                continue;
            }
            Add(b.MinX, b.MinZ, b.MaxX + 1, b.MinZ);
            Add(b.MaxX + 1, b.MinZ, b.MaxX + 1, b.MaxZ + 1);
            Add(b.MaxX + 1, b.MaxZ + 1, b.MinX, b.MaxZ + 1);
            Add(b.MinX, b.MaxZ + 1, b.MinX, b.MinZ);
        }
        return result;
    }
    private IEnumerable<TribeBoundaryView> RectangleBoundaries(IServerPlayer player)
    {
        foreach (var tribe in tribeState.Tribes.Where(t => t.Dimension == player.Entity.Pos.Dimension))
        {
            var pos = player.Entity.Pos;
            bool near = tribe.Sites.Any(site => pos.X >= site.Bounds.MinX - 192d && pos.X <= site.Bounds.MaxX + 193d
                && pos.Z >= site.Bounds.MinZ - 192d && pos.Z <= site.Bounds.MaxZ + 193d);
            if (!near) continue;
            yield return new() { TribeId = tribe.Id, Name = tribe.Name, RegionId = tribe.Id + ":fixed",
                Revision = tribe.Sites.FirstOrDefault()?.Revision ?? 0, X = tribe.X, Z = tribe.Z,
                Dimension = tribe.Dimension, Edges = FixedTerritoryEdges(tribe, sapi!.WorldManager.MapSizeY) };
        }
    }
}
