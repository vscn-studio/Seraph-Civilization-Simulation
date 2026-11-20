using System.Globalization;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace SeraphCivilizationSimulation;

public sealed class TribeAuthorizationConfig
{
    public bool Enabled { get; set; } = true;
    public bool ConstructionEnabled { get; set; } = true;
    public bool BenefitsEnabled { get; set; } = true;
    // Read old configuration files without exposing the retired paper contract switch.
    public bool ParchmentContractsEnabled { get; set; }
    public bool ShouldSerializeParchmentContractsEnabled() => false;
    public bool BlockUnauthorizedReinforcement { get; set; } = true;
    public bool BlockUnauthorizedLocks { get; set; } = true;
    public bool ReinforcementExclusionsEnabled { get; set; } = true;
    public string[] ReinforcementExcludedBlocks { get; set; } =
    [
        "*:ladder*", "*:crudeladder*", "*:ropeladder*", "*:rope-ladder*", "*:scaffold*",
        "*:torch*", "*:lantern*", "*:paperlantern*", "*:candle*", "*:oillamp*",
        "*:carpet*", "*:rug-*", "*:snowlayer*", "*:loosestones*", "*:loosestick*", "*:leaflitter*",
        "*:tallgrass*", "*:flower-*", "*:fern-*", "*:mushroom-*", "*:crop-*", "*:sapling-*",
        "*:vine-*", "*:ivy-*", "*:reeds-*", "*:cattail-*"
    ];
    public bool OutsiderPlacementCooldownEnabled { get; set; }
    public double OutsiderPlacementCooldownSeconds { get; set; } = 2;
    public bool OutsiderBlockQuotaEnabled { get; set; }
    public int OutsiderBlockQuota { get; set; } = 64;
    public bool ProtectCriticalAreas { get; set; }
    public int CriticalAreaRadius { get; set; } = 5;
    public void Validate()
    {
        if (ReinforcementExcludedBlocks == null || ReinforcementExcludedBlocks.Length > 1024
            || ReinforcementExcludedBlocks.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 256 || !p.Contains(':'))
            || !double.IsFinite(OutsiderPlacementCooldownSeconds) || OutsiderPlacementCooldownSeconds is < 0 or > 86400
            || OutsiderBlockQuota is < 1 or > 100000 || CriticalAreaRadius is < 0 or > 1000)
            throw new ArgumentException("Invalid tribe authorization placement limits.");
    }
}

public sealed class PlacedBuilding
{
    public string PlacerUid { get; set; } = "";
    public string TribeId { get; set; } = "";
    public string TerritoryId { get; set; } = "";
    public string BlockCode { get; set; } = "";
    public string EnrollmentTribeId { get; set; } = "";
    public long EnrollmentStartedAtUtc { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public int Dimension { get; set; }
}

public sealed partial class VscnCivilizationModSystem
{
    private readonly Dictionary<string, long> outsiderPlacementTimes = [];
    private static long AuthorizationNow => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private bool Authorized(TribeRecord tribe, string uid, bool benefits = false)
    {
        var settings = config.TribeAuthorization;
        return settings.Enabled && (benefits ? settings.BenefitsEnabled : settings.ConstructionEnabled)
            && tribe.Authorizations.Any(a => a.Uid == uid && a.Active(AuthorizationNow) && (benefits ? a.Benefits : a.Construction));
    }

    private TribeRecord? TerritoryAt(BlockPos pos) => territoryIndex.At(new(pos.X, pos.Y, pos.Z), pos.dimension);
    private bool CanBuildFor(TribeRecord tribe, string uid) => tribe.Members.Contains(uid) || Authorized(tribe, uid);

    private string AuthorizePlayer(IServerPlayer leader, CivilizationRequestPacket request)
    {
        var tribe = tribeState.FindMember(leader.PlayerUID);
        if (tribe == null || !HasTribePermission(tribe, leader.PlayerUID, TribePermission.Authorize)) return Ui(leader, "leader-only");
        var settings = config.TribeAuthorization;
        if (!settings.Enabled) return Ui(leader, "auth-disabled");
        if (!long.TryParse(request.DurationMinutes, NumberStyles.Integer, CultureInfo.InvariantCulture, out long minutes)
            || minutes != -1 && minutes is not (> 0 and <= 5256000)) return Ui(leader, "auth-invalid-time");
        bool construction = request.Construction && settings.ConstructionEnabled;
        bool benefits = request.Benefits && settings.BenefitsEnabled;
        var permissions = request.Permissions & ~(TribePermission.Outposts | TribePermission.Territory);
        if ((permissions & ~TribePermission.All) != 0) return Ui(leader, "invalid-action");
        if (!construction && !benefits && permissions == TribePermission.None) return Ui(leader, "auth-no-scope");
        string name = (request.Name ?? "").Trim(), uid;
        if (name.Length > 0)
        {
            var matches = lifeState.Players.Where(p => p.Value.Accepted && p.Value.FullName.Equals(name, StringComparison.Ordinal)).ToList();
            if (matches.Count != 1) return Ui(leader, "auth-name-not-unique");
            uid = matches[0].Key;
        }
        else
        {
            BlockSelection? obstruction = null;
            EntitySelection? selection = null;
            var at = leader.Entity.Pos;
            sapi!.World.RayTraceForSelection(at.XYZ.Add(leader.Entity.LocalEyePos), at.Pitch, at.Yaw, 3,
                ref obstruction, ref selection, efilter: e => e != leader.Entity && e.Pos.Dimension == at.Dimension);
            var target = selection?.Entity;
            var faced = target == null ? null : Online.FirstOrDefault(p => p.Entity == target);
            if (faced?.Entity == null || DistanceTo(leader, faced) > 3) return Ui(leader, "auth-face-player");
            uid = faced.PlayerUID;
            if (!lifeState.Players.TryGetValue(uid, out var identity) || !identity.Accepted) return Ui(leader, "auth-face-player");
        }
        bool member = tribe.Members.Contains(uid);
        if (permissions != TribePermission.None && (!member || tribe.LeaderUid != leader.PlayerUID)) return Ui(leader, "leader-only");
        if (member && (uid == tribe.LeaderUid || tribe.LeaderUid != leader.PlayerUID)) return Ui(leader, "leader-only");
        if (!member && permissions != TribePermission.None) return Ui(leader, "invalid-action");
        EndAuthorizations(tribe, a => a.Uid == uid, "replaced");
        var issued = IssueAuthorization(tribe, leader.PlayerUID, uid, minutes, construction, benefits);
        issued.Permissions = permissions; tribe.Authorizations.Last().Permissions = permissions;
        SaveState(); NotifyAuthorization(tribe, issued); RefreshCivilizationViews();
        return Ui(leader, "auth-granted", PlayerName(uid));
    }

    private string RevokeAuthorization(IServerPlayer leader, string uid)
    {
        var tribe = tribeState.FindMember(leader.PlayerUID);
        if (tribe == null || !HasTribePermission(tribe, leader.PlayerUID, TribePermission.Authorize)) return Ui(leader, "leader-only");
        if (tribe.LeaderUid != leader.PlayerUID && tribe.Authorizations.Any(a => (a.Id == uid || a.Uid == uid)
            && (tribe.Members.Contains(a.Uid) || a.Permissions != TribePermission.None))) return Ui(leader, "leader-only");
        EndAuthorizations(tribe, a => a.Id == uid || a.Uid == uid, "revoked");
        SaveState(); RefreshCivilizationViews();
        return Ui(leader, "auth-revoked");
    }

    private bool PruneAuthorizations()
    {
        int removed = 0;
        foreach (var tribe in tribeState.Tribes) removed += EndAuthorizations(tribe, a => !a.Pending && !a.Active(AuthorizationNow), "expired");
        if (removed > 0) SaveState();
        return removed > 0;
    }

    private bool EndReincarnationAuthorizations(string uid)
    {
        int ended = 0;
        foreach (var tribe in tribeState.Tribes)
            ended += EndAuthorizations(tribe, a => a.Uid == uid, "reincarnated");
        return ended > 0;
    }

    private TribeAuthorizationView AuthorizationView(TribeRecord tribe, TribeAuthorization grant)
    {
        var history = tribe.AuthorizationHistory.FirstOrDefault(h => h.Id == grant.Id && grant.Id.Length > 0);
        return new()
        {
            Permissions = grant.Permissions, Id = grant.Id, Uid = grant.Uid, Name = PlayerName(grant.Uid), TribeName = tribe.Name, ExpiresAt = grant.ExpiresAt,
            IssuedAt = history?.IssuedAt ?? 0, SignerName = history?.SignerName ?? "", Parchment = history?.ContractText.Length > 0,
            Construction = grant.Construction && config.TribeAuthorization.Enabled && config.TribeAuthorization.ConstructionEnabled,
            Benefits = grant.Benefits && config.TribeAuthorization.Enabled && config.TribeAuthorization.BenefitsEnabled
        };
    }

    private TribeAuthorizationHistory IssueAuthorization(TribeRecord tribe, string signer, string uid, long minutes,
        bool construction, bool benefits, string text = "")
    {
        long now = AuthorizationNow;
        var record = new TribeAuthorizationHistory { Id = Guid.NewGuid().ToString("N"), Uid = uid, Name = PlayerName(uid),
            SignerUid = signer, SignerName = PlayerName(signer), IssuedAt = now,
            ExpiresAt = minutes == -1 ? -1 : now + minutes * 60000, Construction = construction, Benefits = benefits, ContractText = text };
        tribe.AuthorizationHistory.Add(record);
        tribe.Authorizations.Add(new() { Id = record.Id, Uid = uid, ExpiresAt = record.ExpiresAt, Construction = construction, Benefits = benefits });
        return record;
    }

    private int EndAuthorizations(TribeRecord tribe, Predicate<TribeAuthorization> matches, string status)
    {
        foreach (var grant in tribe.Authorizations.Where(a => matches(a)))
        {
            var record = tribe.AuthorizationHistory.FirstOrDefault(h => h.Id == grant.Id && grant.Id.Length > 0);
            if (record == null) continue;
            record.Status = status; record.EndedAt = status == "expired" ? grant.ExpiresAt : AuthorizationNow;
            if (record.ContractText.Length > 0) record.ContractText = ParchmentContract.ZeroTime(record.ContractText);
        }
        return tribe.Authorizations.RemoveAll(matches);
    }

    private TribeAuthorizationView AuthorizationHistoryView(TribeRecord tribe, TribeAuthorizationHistory h) => new()
    { Permissions = h.Permissions, Id = h.Id, Uid = h.Uid, Name = h.Name, TribeName = tribe.Name, ExpiresAt = h.ExpiresAt,
        Construction = h.Construction, Benefits = h.Benefits, IssuedAt = h.IssuedAt, EndedAt = h.EndedAt,
        Status = h.Status == "active" && h.ExpiresAt != -1 && h.ExpiresAt <= AuthorizationNow ? "expired" : h.Status,
        SignerName = h.SignerName, Parchment = h.ContractText.Length > 0 };

    private void RecordBuilding(IPlayer player, BlockPos pos)
    {
        var tribe = TerritoryAt(pos);
        if (tribe != null && !CanBuildFor(tribe, player.PlayerUID)) outsiderPlacementTimes[tribe.Id + ":" + player.PlayerUID] = AuthorizationNow;

        var placed = lifeState.PlacedBuildings[WearKey(pos)] = new() { PlacerUid = player.PlayerUID,
            TribeId = tribe != null && CanBuildFor(tribe, player.PlayerUID) ? tribe.Id : "",
            TerritoryId = tribe?.Id ?? "", BlockCode = sapi!.World.BlockAccessor.GetBlock(pos).Code?.ToString() ?? "",
            X = pos.X, Y = pos.Y, Z = pos.Z, Dimension = pos.dimension };
        UpdateBuildingEnrollment(placed, tribe, AuthorizationNow);
    }

    private bool ExcludedFromTribeReinforcement(string code) => config.TribeAuthorization.ReinforcementExclusionsEnabled
        && config.TribeAuthorization.ReinforcementExcludedBlocks.Any(pattern =>
            System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, code, true));

    // UTC timestamps survive saves and restarts; querying cannot extend a countdown.
    private int UpdateBuildingEnrollment(PlacedBuilding placed, TribeRecord? tribe, long now)
    {
        // Ownership is explicit. Old automatic-enrollment timestamps never grant protection.
        placed.EnrollmentTribeId = ""; placed.EnrollmentStartedAtUtc = 0;
        return 0;
    }

    private void TickBuildingEnrollment() { }

    private string? OutsiderPlacementFailure(IPlayer player, BlockPos pos)
    {
        var tribe = TerritoryAt(pos);
        if (tribe == null || CanBuildFor(tribe, player.PlayerUID)) return null;
        var settings = config.TribeAuthorization;
        string key = tribe.Id + ":" + player.PlayerUID;
        if (settings.OutsiderPlacementCooldownEnabled && outsiderPlacementTimes.TryGetValue(key, out long last)
            && AuthorizationNow - last < settings.OutsiderPlacementCooldownSeconds * 1000) return "vsscsmod:auth-placement-cooldown";
        return OutsiderFootprintFailure(player, [pos]);
    }

    private string? OutsiderFootprintFailure(IPlayer player, IEnumerable<BlockPos> positions)
    {
        var settings = config.TribeAuthorization;
        var proposed = positions.DistinctBy(WearKey).Select(pos => (Pos: pos, Tribe: TerritoryAt(pos)))
            .Where(p => p.Tribe != null && !CanBuildFor(p.Tribe, player.PlayerUID)).ToList();
        foreach (var (pos, tribe) in proposed)
            if (settings.ProtectCriticalAreas && new[] { new TribeOutpost { X = tribe!.X, Y = tribe.Y, Z = tribe.Z, Dimension = tribe.Dimension } }
                .Any(p => p.Dimension == pos.dimension
                    && Math.Pow(p.X - pos.X, 2) + Math.Pow(p.Y - pos.Y, 2) + Math.Pow(p.Z - pos.Z, 2) <= Math.Pow(settings.CriticalAreaRadius, 2)))
                return "vsscsmod:auth-critical-area";
        if (settings.OutsiderBlockQuotaEnabled)
            foreach (var group in proposed.GroupBy(p => p.Tribe!.Id))
            {
                bool Counts(PlacedBuilding placed) => placed.TerritoryId == group.Key && placed.PlacerUid == player.PlayerUID && placed.TribeId.Length == 0;
                int count = lifeState.PlacedBuildings.Values.Count(Counts);
                int added = group.Count(p => !lifeState.PlacedBuildings.TryGetValue(WearKey(p.Pos), out var existing) || !Counts(existing));
                if (count + added > settings.OutsiderBlockQuota) return "vsscsmod:auth-placement-quota";
            }
        return null;
    }

    private static bool AuthorizationReinforce(BlockPos pos, IPlayer byPlayer, ref bool __result)
        => AuthorizationManual(pos, byPlayer, false, ref __result);
    private static bool AuthorizationLock(BlockPos pos, IPlayer byPlayer, ref bool __result)
        => AuthorizationManual(pos, byPlayer, true, ref __result);
    private static bool AuthorizationManual(BlockPos pos, IPlayer player, bool locking, ref bool result)
    {
        var system = instance;
        if (system == null || system.disposed) return true;
        var settings = system.config.TribeAuthorization;
        if (!(locking ? settings.BlockUnauthorizedLocks : settings.BlockUnauthorizedReinforcement)) return true;
        var tribe = system.TerritoryAt(pos);
        if (tribe == null || system.CanBuildFor(tribe, player.PlayerUID)) return true;
        result = false;
        return false;
    }
}
