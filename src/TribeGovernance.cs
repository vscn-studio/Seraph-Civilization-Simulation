using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnCivilizationModSystem
{
    private TribePermission PermissionsFor(TribeRecord tribe, string uid)
    {
        if (!tribe.Members.Contains(uid)) return TribePermission.None;
        if (tribe.LeaderUid == uid) return TribePermission.All;
        if (!config.TribeAuthorization.Enabled) return TribePermission.None;
        return tribe.Authorizations.Where(a => a.Uid == uid && a.Active(AuthorizationNow))
            .Aggregate(TribePermission.None, (value, grant) => value | grant.Permissions);
    }
    private bool HasTribePermission(TribeRecord tribe, string uid, TribePermission permission)
        => (PermissionsFor(tribe, uid) & permission) == permission;

    private long OutpostWait(TribeRecord tribe) => Math.Max(0,
        tribe.OutpostsChangedAt + config.Territory.CenterCooldownMinutes * 60000L - AuthorizationNow);

    private string RemoveOutpost(IServerPlayer player,string id) => "附庸地管理已移除，已有领地保留。";

    private string TransferLeader(IServerPlayer player, string target)
    {
        var tribe = tribeState.FindMember(player.PlayerUID);
        if (tribe == null || !HasTribePermission(tribe, player.PlayerUID, TribePermission.TransferLeader)) return Ui(player, "leader-only");
        if (!tribe.Members.Contains(target) || target == tribe.LeaderUid) return Ui(player, "invalid-action");
        ChangeTribeLeader(tribe, target);
        PruneMembershipRequests(); SaveState(); RefreshCivilizationViews();
        return Ui(player, "leader-transferred", PlayerName(target));
    }

    private string AdoptBuilding(IServerPlayer player)
    {
        BlockSelection? block = null; EntitySelection? entity = null;
        var at = player.Entity.Pos;
        sapi!.World.RayTraceForSelection(at.XYZ.Add(player.Entity.LocalEyePos), at.Pitch, at.Yaw, 5, ref block, ref entity);
        if (block == null) return Ui(player, "adopt-target");
        var tribe = TerritoryAt(block.Position);
        if (tribe == null || !CanBuildFor(tribe, player.PlayerUID)) return Ui(player, "adopt-denied");
        if (!lifeState.PlacedBuildings.TryGetValue(WearKey(block.Position), out var placed)
            || !CanBuildFor(tribe, placed.PlacerUid)
            || placed.BlockCode != sapi.World.BlockAccessor.GetBlock(block.Position).Code?.ToString()) return Ui(player, "adopt-denied");
        if (ExcludedFromTribeReinforcement(placed.BlockCode)) return Ui(player, "adopt-excluded");
        placed.TribeId = tribe.Id; placed.EnrollmentTribeId = ""; placed.EnrollmentStartedAtUtc = 0;
        SaveState(); return Ui(player, "adopt-done");
    }

    private void NotifyAuthorization(TribeRecord tribe, TribeAuthorizationHistory record)
    {
        if (record.Notified || record.Status != "active" || record.ExpiresAt != -1 && record.ExpiresAt <= AuthorizationNow
            || ByUid(record.Uid) is not { } player || !readyPlayers.Contains(record.Uid)) return;
        var active = tribe.Authorizations.FirstOrDefault(a => a.Id == record.Id && a.Active(AuthorizationNow));
        if (active == null || !config.TribeAuthorization.Enabled) return;
        var permissions = tribe.Members.Contains(record.Uid) ? active.Permissions : TribePermission.None;
        var rights = new List<string>();
        if (active.Construction && config.TribeAuthorization.ConstructionEnabled) rights.Add(Ui(player, "auth-construction"));
        if (active.Benefits && config.TribeAuthorization.BenefitsEnabled) rights.Add(Ui(player, "auth-benefits"));
        foreach (var flag in Enum.GetValues<TribePermission>().Where(p => p != TribePermission.None && p != TribePermission.All && p != TribePermission.Outposts && p != TribePermission.Territory))
            if ((permissions & flag) != 0) rights.Add(Ui(player, "permission-" + flag.ToString().ToLowerInvariant()));
        if (rights.Count == 0) return;
        sapi!.SendIngameDiscovery(player, "vsscsmod-authorization-" + record.Id, Ui(player, "auth-discovery", tribe.Name, string.Join("、", rights)));
        sapi.World.PlaySoundFor(new AssetLocation("game:sounds/effect/deepbell"), player, false, 5, .5f);
        record.Notified = true; SaveState();
    }

    private void NotifyPendingAuthorizations(IServerPlayer player)
    {
        foreach (var tribe in tribeState.Tribes)
            foreach (var record in tribe.AuthorizationHistory.Where(h => h.Uid == player.PlayerUID && !h.Notified).ToArray())
                NotifyAuthorization(tribe, record);
    }
}
