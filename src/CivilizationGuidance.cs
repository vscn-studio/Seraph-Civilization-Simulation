using HarmonyLib;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnCivilizationModSystem
{
    private const string LifeStateKey = "vscncsCivilizationLives";
    internal const string TribeAttribute = "vscncsTribeId";
    private CivilizationLifeState lifeState = new();
    private readonly Dictionary<string, string> guidanceOffers = [];
    private readonly HashSet<string> reincarnating = [];
    private readonly Dictionary<string, object> reincarnationSessions = [];
    private readonly HashSet<string> rebornTeleports = [];
    private long lifeListener;
    private TribeReinforcementCompatibility? tribeReinforcement;
    private readonly Dictionary<string, long> reinforcementRequests = [];

    private CivilizationIdentity Identity(string uid)
    {
        if (!lifeState.Players.TryGetValue(uid, out var identity)) lifeState.Players[uid] = identity = new();
        return identity;
    }

    private int RemainingLives(IServerPlayer player) => Identity(player.PlayerUID).Remaining(config.Respawn, GetDeaths(player));

    private void StartGuidance()
    {
        sapi!.Event.PlayerDeath += OnCivilizationDeath;
        tribeReinforcement ??= new();
        sapi.Event.DidBreakBlock += OnTerritoryBroken;
        sapi.Event.DidPlaceBlock += OnTerritoryPlaced;
        StartBuildingProtection();
        lifeListener = sapi.Event.RegisterGameTickListener(dt => TickCivilizationLives(), 1000);
        respawnHarmony!.Patch(AccessTools.Method(typeof(Vintagestory.Server.ServerSystemEntitySimulation), "OnPlayerRespawn"),
            prefix: new HarmonyMethod(typeof(VscnCivilizationModSystem), nameof(BeforeNativeRespawn)));
        respawnHarmony.Patch(AccessTools.Method(typeof(Vintagestory.Server.ServerSystemEntitySimulation), "HandleSpecialKey"),
            prefix: new HarmonyMethod(typeof(VscnCivilizationModSystem), nameof(BeforeRespawnKey)));
        respawnHarmony.Patch(AccessTools.Method(typeof(Vintagestory.Server.ServerSystemEntitySimulation), "EventManager_OnPlayerChat"),
            transpiler: new HarmonyMethod(typeof(VscnCivilizationModSystem), nameof(UseGameNameInChat)));
    }

    private static bool BeforeRespawnKey(Vintagestory.Server.ConnectedClient client, Vintagestory.Server.ServerMain ___server)
    {
        var system = instance;
        if (system == null || system.disposed) return true;
        if (system.RemainingLives(client.Player) == 0) system.SendGuidance(client.Player);
        else ___server.EventManager.TriggerPlayerRespawn(client.Player);
        return false;
    }

    private static bool BeforeNativeRespawn(IServerPlayer player)
    {
        var system = instance;
        if (system == null || system.disposed) return true;
        if (system.Identity(player.PlayerUID).RebornSpawn != null) { system.CompleteReincarnation(player); return false; }
        if (system.RemainingLives(player) != 0) return true;
        system.SendGuidance(player);
        return false;
    }

    private void LoadCivilizationLives()
    {
        var data = sapi!.WorldManager.SaveGame.GetData(LifeStateKey);
        if (data is { Length: > 0 })
            lifeState = JsonConvert.DeserializeObject<CivilizationLifeState>(System.Text.Encoding.UTF8.GetString(data))
                ?? throw new InvalidDataException("Null civilization life state.");
        if (lifeState.Players == null || lifeState.BlockWear == null) throw new InvalidDataException("Invalid civilization life state.");
        lifeState.BuildingFires ??= [];
        lifeState.PlacedBuildings ??= [];
        // Heat does not survive a restart; permanent charring does.
        foreach (var fire in lifeState.BuildingFires.Values) { fire.Heat = 0; fire.Ignited = false; }
        foreach (var wear in lifeState.BlockWear.Values)
            if (wear.LastRecoverySeconds == 0) wear.LastAttackSeconds = wear.LastRecoverySeconds = lifeState.ProtectionSeconds;
    }

    private void SaveCivilizationLives() => sapi!.WorldManager.SaveGame.StoreData(LifeStateKey,
        System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(lifeState)));

    // HandleCommand checks controlserver before entering this administrator-only route.
    private TextCommandResult HandleNameCommand(CmdArgs words)
    {
        const string usage = "用法: /vsscsmod name reset <账号名、玩家游戏名或UID>";
        if (!words.PopWord("").Equals("reset", StringComparison.OrdinalIgnoreCase)) return TextCommandResult.Error(usage);
        string target = words.PopAll().Trim();
        if (target.Length == 0) return TextCommandResult.Error(usage);
        var online = Online.ToList();
        var accounts = sapi!.PlayerData?.PlayerDataByUid;
        // PlayerData is server-wide; only select offline identities belonging to this world.
        var known = lifeState.Players.Keys.Concat(online.Select(p => p.PlayerUID)).Distinct().ToList();
        var matches = known.Contains(target) ? [target] : known.Where(uid =>
            online.Any(p => p.PlayerUID == uid && p.PlayerName.Equals(target, StringComparison.OrdinalIgnoreCase))
            || (lifeState.Players.TryGetValue(uid, out var identity) && identity.FullName.Equals(target, StringComparison.OrdinalIgnoreCase))
            || (accounts != null && accounts.TryGetValue(uid, out var account) && target.Equals(account.LastKnownPlayername, StringComparison.OrdinalIgnoreCase)))
            .Take(2).ToList();
        if (matches.Count != 1) return TextCommandResult.Error("玩家不存在或名称重复，请使用当前世界的玩家 UID。\n" + usage);
        string uid = matches[0];
        bool existed = lifeState.Players.ContainsKey(uid);
        var record = Identity(uid);
        bool accepted = record.Accepted;
        record.Accepted = false;
        try { SaveCivilizationLives(); }
        catch (Exception ex)
        {
            record.Accepted = accepted;
            if (!existed) lifeState.Players.Remove(uid);
            sapi.Logger.Error("Seraph Civilization Simulation name reset failed for {0}: {1}", uid, ex);
            return TextCommandResult.Error("姓名重置保存失败，已恢复原记录。");
        }
        // Rotate even an outstanding challenge so earlier submissions cannot bypass this reset.
        guidanceOffers[uid] = Guid.NewGuid().ToString("N");
        var player = online.FirstOrDefault(p => p.PlayerUID == uid);
        if (player != null) SendGuidance(player);
        bool opened = player != null && readyPlayers.Contains(uid) && channel != null;
        return TextCommandResult.Success($"已重置玩家 {target}（UID: {uid}）的姓名确认；{(opened ? "已重新打开指导窗口" : "下次进入游戏时重新打开指导窗口")}，需重新填写玩家游戏名并确认。部落、重生次数和出生点保留。");
    }

    private void PublishIdentity(IServerPlayer player)
    {
        if (player.Entity == null) return;
        var identity = Identity(player.PlayerUID);
        if (identity.Accepted && CivilizationIdentity.ValidName(identity.FullName)
            && player.Entity.GetBehavior<EntityBehaviorNameTag>() is { } tag && tag.DisplayName != identity.FullName)
            tag.SetName(identity.FullName);
        string tribeId = tribeState.FindMember(player.PlayerUID)?.Id ?? "";
        if (player.Entity.WatchedAttributes.GetString(TribeAttribute, "") != tribeId)
        {
            player.Entity.WatchedAttributes.SetString(TribeAttribute, tribeId);
            player.Entity.WatchedAttributes.MarkPathDirty(TribeAttribute);
        }
    }

    private void SendGuidance(IServerPlayer player, string error = "")
    {
        if (channel == null) return;
        var identity = Identity(player.PlayerUID);
        bool required = !identity.Accepted || !CivilizationIdentity.ValidName(identity.FullName);
        if (required && !guidanceOffers.ContainsKey(player.PlayerUID)) guidanceOffers[player.PlayerUID] = Guid.NewGuid().ToString("N");
        channel.SendPacket(new GuidancePacket
        {
            Nonce = required ? guidanceOffers[player.PlayerUID] : "", Required = required,
            Title = identity.RebornTribe.Length == 0 ? "欢迎来到文明模拟服—复古物语中文社区" : $"欢迎转生 {identity.RebornTribe} 部落",
            LivesLeft = RemainingLives(player), Error = error
        }, player);
    }

    private void OnGuidanceDecision(IServerPlayer player, GuidanceDecision packet)
    {
        sapi!.Logger.Notification("Seraph Civilization Simulation guidance decision received: uid={0}, agree={1}, ready={2}",
            player.PlayerUID, packet.Agree, readyPlayers.Contains(player.PlayerUID));
        if (!guidanceOffers.TryGetValue(player.PlayerUID, out var nonce) || nonce != packet.Nonce) return;
        // Old clients may still send a decline; it never disconnects the player.
        if (!packet.Agree) return;
        if (!CivilizationIdentity.ValidName(packet.GameName))
        { SendGuidance(player, "玩家游戏名需 1–32 个字符，可用文字、数字、空格、下划线、连字符、撇号或间隔号，首尾不能有空格。"); return; }
        string fullName = packet.GameName;
        string nameKey = GameNameKey(fullName);
        // Offline identities and names awaiting an administrator-requested rename remain reserved.
        if (lifeState.Players.Any(p => p.Key != player.PlayerUID && CivilizationIdentity.ValidName(p.Value.FullName)
            && string.Equals(GameNameKey(p.Value.FullName), nameKey, StringComparison.OrdinalIgnoreCase)))
        { SendGuidance(player, "这个玩家游戏名已被使用，请选择另一个。"); return; }
        var identity = Identity(player.PlayerUID);
        identity.GameName = fullName; identity.Surname = identity.GivenName = "";
        identity.Accepted = true; identity.RebornTribe = "";
        guidanceOffers.Remove(player.PlayerUID);
        SaveState(); PublishIdentity(player); SendGuidance(player); RefreshCivilizationViews();
        sapi.Logger.Notification("Seraph Civilization Simulation guidance accepted: uid={0}", player.PlayerUID);
    }

    private void OnCivilizationDeath(IServerPlayer player, DamageSource source)
    {
        var identity = Identity(player.PlayerUID);
        identity.Living = false; identity.LastDeaths = GetDeaths(player);
        SaveState(); SendGuidance(player); RefreshCivilizationViews();
    }

    private int LivingMembers(TribeRecord tribe) => tribe.Members.Count(uid =>
        ByUid(uid)?.Entity is { } entity ? entity.Alive : !lifeState.Players.TryGetValue(uid, out var p) || p.Living);

    private List<TerritoryEffectView> BuildCivilizationEffects(IServerPlayer player, bool member, bool home)
    {
        var effects = BuildTerritoryEffects(player, member, home);
        if (tribeState.FindMember(player.PlayerUID) is { } homeTribe)
            effects.Add(new() { Code = "effect-reinforcement-recovery", Value = (float)BuildingProtectionRules.RecoveryRate(config.Territory, LivingMembers(homeTribe)) });
        if (tribeState.FindMember(player.PlayerUID) is { } tribe && LivingMembers(tribe) >= config.Respawn.NewbornMinimumLivingMembers)
        {
            effects.Add(new() { Code = "effect-newborn", Value = (float)(config.Respawn.ReincarnationChance * 100) });
            effects.Add(new() { Code = "effect-homecoming", Value = (float)(config.Respawn.HomecomingChance(LivingMembers(tribe)) * 100) });
        }
        return effects;
    }

    private TribeRecord SelectReincarnationTribe(string uid, List<TribeRecord> eligible, double roll)
    {
        if (eligible.Count == 1) return eligible[0];
        var own = tribeState.FindMember(uid);
        if (own != null && eligible.Contains(own))
        {
            double chance = config.Respawn.HomecomingChance(LivingMembers(own));
            if (roll < chance) return own;
            var others = eligible.Where(t => t != own).ToList();
            // Rescale the remaining probability so other eligible tribes share it equally.
            return others[Math.Min(others.Count - 1, (int)((roll - chance) / (1 - chance) * others.Count))];
        }
        return eligible[Math.Min(eligible.Count - 1, (int)(roll * eligible.Count))];
    }

    private void TickCivilizationLives()
    {
        if (disposed) return;
        bool populationChanged = false;
        foreach (var player in Online)
        {
            if (player.Entity == null || !readyPlayers.Contains(player.PlayerUID)) continue;
            var identity = Identity(player.PlayerUID);
            PublishIdentity(player);
            if (identity.RebornSpawn != null) { CompleteReincarnation(player); continue; }
            bool changed = identity.Living != player.Entity.Alive || identity.LastDeaths != GetDeaths(player);
            identity.Living = player.Entity.Alive; identity.LastDeaths = GetDeaths(player);
            if (changed) { SaveState(); SendGuidance(player); populationChanged = true; }
        }
        if (populationChanged) RefreshCivilizationViews();
    }

    // Called only from PlayerNowPlaying. Even an ineligible entry consumes this session's check.
    private void TryEntryReincarnation(IServerPlayer player)
    {
        if (disposed || player.Entity == null || !readyPlayers.Contains(player.PlayerUID)) return;
        object session = new();
        if (!reincarnationSessions.TryAdd(player.PlayerUID, session)) return;
        if (Identity(player.PlayerUID).RebornSpawn != null)
        { CompleteReincarnation(player); return; }
        if (player.Entity.Alive || RemainingLives(player) != 0) return;
        var tribes = tribeState.Tribes.Where(t => t.Dimension == 0 && t.Sites.Any(s => s.Id == "main") && LivingMembers(t) >= config.Respawn.NewbornMinimumLivingMembers).ToList();
        if (tribes.Count == 0 || random.NextDouble() >= config.Respawn.ReincarnationChance) return;
        BeginReincarnation(player, SelectReincarnationTribe(player.PlayerUID, tribes, random.NextDouble()), session);
    }

    private void EndReincarnationSession(IServerPlayer player)
    {
        reincarnationSessions.Remove(player.PlayerUID);
        reincarnating.Remove(player.PlayerUID);
    }

    private void BeginReincarnation(IServerPlayer player, TribeRecord tribe, object session)
    {
        reincarnating.Add(player.PlayerUID);
        bool CurrentSession() => reincarnationSessions.TryGetValue(player.PlayerUID, out var current) && ReferenceEquals(current, session);
        void Finish() { if (CurrentSession()) reincarnating.Remove(player.PlayerUID); }
        bool Active()
        {
            bool active = !disposed && CurrentSession() && reincarnating.Contains(player.PlayerUID) && readyPlayers.Contains(player.PlayerUID)
                && ByUid(player.PlayerUID) == player && player.Entity?.Alive == false && RemainingLives(player) == 0
                && tribeState.Find(tribe.Id) == tribe && tribe.Dimension == 0 && LivingMembers(tribe) >= config.Respawn.NewbornMinimumLivingMembers;
            if (!active) Finish();
            return active;
        }
        FindSafeLanding(ReincarnationCandidates(tribe), Active, (x, y, z) =>
        {
            if (!Active()) return;
            var identity = Identity(player.PlayerUID);
            EndReincarnationAuthorizations(player.PlayerUID);
            var old = tribeState.FindMember(player.PlayerUID);
            if (old != null) tribeState.RemoveMember(old, player.PlayerUID, config.Tribes.AllowSoloCreation);
            InvalidatePendingSpawn(player.PlayerUID);
            identity.AdditionalLives = checked(GetDeaths(player) - config.Respawn.InitialLives + config.Respawn.ReincarnationLives);
            identity.Accepted = false; identity.GameName = identity.Surname = identity.GivenName = "";
            identity.RebornTribe = tribe.Name; identity.RebornSpawn = new() { X = x, Y = y, Z = z };
            identity.ReincarnationHome = identity.RebornSpawn;
            guidanceOffers.Remove(player.PlayerUID);
            SaveState(); CompleteReincarnation(player); Finish();
            RefreshCivilizationViews();
        }, Finish, (x, z) => IsReincarnationOutside(tribe, x, z));
    }

    private void CompleteReincarnation(IServerPlayer player)
    {
        var identity = Identity(player.PlayerUID);
        if (identity.RebornSpawn is not { } point || player.Entity == null || !rebornTeleports.Add(player.PlayerUID)) return;
        var entity = player.Entity;
        try {
        // Also clean up pending landings saved by an older version before completing their reincarnation.
        if (EndReincarnationAuthorizations(player.PlayerUID)) { SaveState(); }
        entity.TeleportTo(new Vintagestory.API.Common.Entities.EntityPos(point.X + .5, point.Y, point.Z + .5), () =>
        {
            if (disposed || player.Entity != entity || !readyPlayers.Contains(player.PlayerUID) || ByUid(player.PlayerUID) != player) return;
            if (identity.RebornSpawn != point) return;
            rebornTeleports.Remove(player.PlayerUID);
            entity.Revive(); entity.Pos.Motion.Set(0, 0, 0);
            player.SetSpawnPosition(new PlayerSpawnPos(point.X, point.Y, point.Z));
            identity.RebornSpawn = null; identity.Living = true;
            SaveState(); player.BroadcastPlayerData(); SendGuidance(player);
        }); }
        catch (Exception ex)
        {
            rebornTeleports.Remove(player.PlayerUID);
            sapi!.Logger.Warning("Seraph Civilization Simulation reincarnation teleport failed for {0}: {1}", player.PlayerUID, ex.Message);
        }
    }

    private static string WearKey(BlockPos pos) => $"{pos.dimension}:{pos.X}:{pos.Y}:{pos.Z}";
    private TribeReinforcementPacket TerritoryReinforcement(IPlayer? player, BlockPos pos)
    {
        var block = sapi!.World.BlockAccessor.GetBlock(pos);
        var result = new TribeReinforcementPacket
        { Block = new() { X = pos.X, Y = pos.Y, Z = pos.Z, Dimension = pos.dimension }, BlockId = block.Id };
        if (block.Id == 0) return result;
        if (ExcludedFromTribeReinforcement(block.Code?.ToString() ?? "")) return result;
        var own = player == null ? null : tribeState.FindMember(player.PlayerUID);
        bool ownTerritory = own != null && WithinTerritory(own, pos.X, pos.Y, pos.Z, pos.dimension);
        var tribe = TerritoryAt(pos);
        if (tribe == null) return result;
        if (lifeState.PlacedBuildings.TryGetValue(WearKey(pos), out var placed)
            && placed.TribeId != tribe.Id)
        {
            placed.BlockCode = block.Code?.ToString() ?? "";
            result.PendingSeconds = UpdateBuildingEnrollment(placed, tribe, AuthorizationNow);
            if (placed.TribeId != tribe.Id)
            { if (result.PendingSeconds > 0) result.Tribe = tribe.Name; return result; }
        }
        if (ownTerritory || player != null && Authorized(tribe, player.PlayerUID)) return result;
        int capacity = (int)Math.Min(config.Territory.ReinforcementMaximum, (long)LivingMembers(tribe) * config.Territory.ReinforcementPerLivingMember);
        result.Tribe = tribe.Name;
        result.TribeId = tribe.Id;
        string key = WearKey(pos);
        double remaining = capacity;
        if (lifeState.BlockWear.TryGetValue(key, out var wear) && WearMatches(wear, block))
        {
            wear.TribeId = tribe.Id;
            wear.BlockCode = block.Code?.ToString() ?? "";
            remaining = BuildingProtectionRules.Remaining(wear, capacity, lifeState.ProtectionSeconds,
                BuildingProtectionRules.RecoveryRate(config.Territory, LivingMembers(tribe)), config.Territory.ReinforcementRecoveryDelaySeconds);
        }
        result.Strength = (int)Math.Ceiling(Math.Max(0, remaining) - 1e-8);
        return result;
    }

    internal static TribeReinforcementPacket? NativeTribeReinforcement(IWorldAccessor world, IPlayer player, BlockPos pos)
        => instance is { disposed: false } system && ReferenceEquals(system.sapi?.World, world)
            ? system.TerritoryReinforcement(player, pos) : null;

    internal static void ConsumeNativeTribeReinforcement(IWorldAccessor world, IPlayer player, BlockPos pos, int amount)
    {
        if (instance is not { disposed: false } system || !ReferenceEquals(system.sapi?.World, world)) return;
        var extra = system.TerritoryReinforcement(player, pos);
        if (extra.TribeId.Length == 0 || amount <= 0) return;
        system.ConsumeBuildingReserve(pos, player, amount);
        if (player is IServerPlayer serverPlayer) system.SendTribeReinforcement(serverPlayer, pos);
    }

    private void SendTribeReinforcement(IServerPlayer player, BlockPos pos)
        => channel!.SendPacket(TerritoryReinforcement(player, pos), player);

    private void OnTribeReinforcementRequest(IServerPlayer player, TribeReinforcementRequest request)
    {
        if (!readyPlayers.Contains(player.PlayerUID) || player.Entity == null || request == null) return;
        long now = Environment.TickCount64;
        if (reinforcementRequests.TryGetValue(player.PlayerUID, out long last) && now - last < 150) return;
        reinforcementRequests[player.PlayerUID] = now;
        var pos = request.Position;
        var at = player.Entity.Pos;
        if (pos.dimension != at.Dimension || pos.Y < 0 || pos.Y >= sapi!.WorldManager.MapSizeY
            || !WithinWorld(pos.X, pos.Z) || at.SquareDistanceTo(pos.X + .5, pos.Y + .5, pos.Z + .5) > 16 * 16) return;
        SendTribeReinforcement(player, pos);
    }

    private void OnTerritoryBroken(IServerPlayer player, int oldId, BlockSelection selection)
    {
        // DidBreakBlock also fires when reinforcement absorbs the mining attempt.
        // Preserve depletion and burn damage until the original block actually changes.
        if (sapi!.World.BlockAccessor.GetBlock(selection.Position).Id != oldId) ClearBuildingState(selection.Position);
    }
    private void OnTerritoryPlaced(IServerPlayer player, int oldId, BlockSelection selection, ItemStack stack)
    {
        if (stack?.Block?.ForFluidsLayer == true) return;
        ClearBuildingState(selection.Position);
        if (sapi!.World.BlockAccessor.GetBlock(selection.Position).Id != 0) RecordBuilding(player, selection.Position);
    }

    private void StopGuidance()
    {
        if (sapi == null) return;
        StopBuildingProtection();
        sapi.Event.PlayerDeath -= OnCivilizationDeath;
        tribeReinforcement?.Dispose(); tribeReinforcement = null; reinforcementRequests.Clear();
        sapi.Event.DidBreakBlock -= OnTerritoryBroken;
        sapi.Event.DidPlaceBlock -= OnTerritoryPlaced;
        if (lifeListener != 0) sapi.Event.UnregisterGameTickListener(lifeListener);
        guidanceOffers.Clear(); reincarnating.Clear(); reincarnationSessions.Clear(); rebornTeleports.Clear();
    }
}
