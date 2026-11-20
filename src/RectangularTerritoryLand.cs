using Newtonsoft.Json;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnCivilizationModSystem
{
    private string? CheckFoundingTerritory(TribeRecord tribe)
    {
        try { ValidateFixedTerritory(tribe); return null; }
        catch (Exception e) { return DetectionError(e); }
    }
    private void ValidateFixedTerritory(TribeRecord tribe)
    {
        var site = FixedTerritoryRules.Create(tribe);
        // All heights above the base belong to the tribe; vertically stacked claims conflict too.
        if (tribeState.Tribes.Any(t => t.Id != tribe.Id && t.Dimension == tribe.Dimension
            && t.Sites.Any(other => site.Surface().Any(p => other.ContainsColumn(p.X, p.Z)))))
            throw new TerritoryDetectionException("固定范围与其他部落重叠，请更换位置。");
        int offset = checked(tribe.Dimension * BlockPos.DimensionBoundary);
        var volume = new Cuboidi(site.Bounds.MinX, Math.Max(0, tribe.Y - FixedTerritoryRules.Depth) + offset, site.Bounds.MinZ,
            site.Bounds.MaxX + 1, BlockPos.DimensionBoundary - 1 + offset, site.Bounds.MaxZ + 1);
        if (sapi!.World.Claims.All.Any(c => c.Intersects(volume) && !ClaimAllowsTribe(c, tribe)))
            throw new TerritoryDetectionException("固定范围与禁止建造的原版领地重叠，请更换位置。");
        territoryDetector ??= new(sapi);
        if (!territoryDetector.Safe(new(tribe.X, tribe.Y, tribe.Z, tribe.Dimension)))
            throw new TerritoryDetectionException("部落中心需要安全站立位置，请站在稳固地面上。");
    }
    private string BeginFounding(IServerPlayer founder, TribeRecord tribe, Action published)
        => SetFixedTerritory(founder, tribe, true, published);

    private string RequestTerritoryDetection(IServerPlayer player, string purpose, string target, string kind)
    {
        var tribe = tribeState.FindMember(player.PlayerUID);
        if (tribe == null) return "尚未加入部落。";
        if (purpose != "move") return "部落范围固定为 16×16 格，无需主厅或扩张。";
        if (!CanManageTerritory(tribe, player.PlayerUID, "move")) return "没有迁移部落中心的权限。";
        return SetFixedTerritory(player, tribe, false, () => RefreshCivilizationViews());
    }
    private string SetFixedTerritory(IServerPlayer player, TribeRecord tribe, bool founding, Action published)
    {
        try
        {
            if (!founding && AuthorizationNow < tribe.CenterChangedAt + config.Territory.CenterCooldownMinutes * 60000L)
                return "部落中心迁移仍在冷却。";
            var proposed = JsonConvert.DeserializeObject<TribeRecord>(JsonConvert.SerializeObject(tribe))!;
            if (!founding)
            {
                var pos = player.Entity.Pos.AsBlockPos;
                proposed.X = pos.X; proposed.Y = pos.Y; proposed.Z = pos.Z; proposed.Dimension = pos.dimension;
            }
            ValidateFixedTerritory(proposed);
            FixedTerritoryRules.Apply(proposed);
            if (!TrySaveTerritoryChange(tribe, () =>
            {
                tribe.X = proposed.X; tribe.Y = proposed.Y; tribe.Z = proposed.Z; tribe.Dimension = proposed.Dimension;
                tribe.Sites = proposed.Sites; tribe.Regions = []; tribe.RoomObservations = []; tribe.Outposts = [];
                tribe.PopulationTerritory = null; tribe.LandExpandedAt = 0; tribe.TerritoryVersion = FixedTerritoryRules.Version;
                tribe.CenterChangedAt = AuthorizationNow;
                if (founding) tribeState.Tribes.Add(tribe);
            })) return "保存失败，原部落范围保留。";
            published();
            return founding ? Ui(player, tribe.AllowsSolo ? "solo-founded" : "founded") : "部落中心和固定范围已迁移。";
        }
        catch (Exception e) { return DetectionError(e); }
    }
    private string ConfirmTerritory(IServerPlayer player, string id) => "部落范围固定为 16×16 格，无需确认扩张。";
}
