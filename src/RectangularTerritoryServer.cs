using System.Text;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnCivilizationModSystem
{
    private readonly BuildingTerritoryIndex territoryIndex = new();
    private BuildingTerritoryDetector? territoryDetector;
    private long TerritoryBudget(TribeRecord tribe) => FixedTerritoryRules.Area;
    private int TerritoryArea(TribeRecord tribe) => tribe.Sites.Sum(s => s.ColumnCount);
    private bool WithinTerritory(TribeRecord tribe, double x, double y, double z, int dimension)
        => territoryIndex.At(new((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z)), dimension, tribe.Id) != null;
    private void ReindexTerritories() => territoryIndex.Rebuild(tribeState.Tribes);
    private bool CanManageTerritory(TribeRecord tribe, string uid, string purpose)
        => HasTribePermission(tribe, uid, purpose == "move" ? TribePermission.MoveCenter : TribePermission.Territory);
    private void StartBuildingTerritories() => territoryDetector = new(sapi!);
    private void StopBuildingTerritories() => territoryDetector = null;

    private void MigrateBuildingTerritories(byte[]? oldState)
    {
        string before = JsonConvert.SerializeObject(tribeState);
        bool legacy = tribeState.Tribes.Any(t => t.TerritoryVersion < FixedTerritoryRules.Version);
        if (legacy && oldState is { Length: > 0 })
            sapi!.WorldManager.SaveGame.StoreData("vsscsFixedTerritoryBackup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), oldState);
        try
        {
            foreach (var tribe in tribeState.Tribes)
            {
                if (tribe.TerritoryVersion > FixedTerritoryRules.Version) throw new InvalidDataException("Unsupported territory version.");
                if (tribe.TerritoryVersion < FixedTerritoryRules.Version) FixedTerritoryRules.Apply(tribe);
                FixedTerritoryRules.Validate(tribe);
            }
            if (legacy) sapi!.WorldManager.SaveGame.StoreData(TribeStateKey, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(tribeState)));
            ReindexTerritories();
        }
        catch
        {
            tribeState = JsonConvert.DeserializeObject<TribeState>(before)!; ReindexTerritories();
            if (oldState != null) sapi!.WorldManager.SaveGame.StoreData(TribeStateKey, oldState);
            throw;
        }
    }
    private bool ClaimAllowsTribe(LandClaim claim, TribeRecord tribe)
    {
        // Offline members count and expansion does not depend on anyone being online.
        // Use the leader's recorded claim permissions; never inherit an admin bypass.
        string uid = tribe.LeaderUid;
        if (claim.OwnedByPlayerUid == uid) return true;
        if (claim.PermittedPlayerUids.TryGetValue(uid, out var access) && (access & EnumBlockAccessFlags.BuildOrBreak) == EnumBlockAccessFlags.BuildOrBreak) return true;
        var groups = sapi!.PlayerData.GetPlayerDataByUid(uid)?.PlayerGroupMemberships;
        return groups != null && (claim.OwnedByPlayerGroupUid > 0 && groups.ContainsKey((int)claim.OwnedByPlayerGroupUid)
            || claim.PermittedPlayerGroupIds.Any(p => groups.ContainsKey(p.Key) && (p.Value & EnumBlockAccessFlags.BuildOrBreak) == EnumBlockAccessFlags.BuildOrBreak));
    }
    private string DetectionError(Exception error)
    {
        if (error is TerritoryDetectionException) return error.Message;
        sapi!.Logger.Error("Territory operation failed: {0}", error); return "检测或保存失败，归属未改变，请稍后重试。";
    }
}
