using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.Server;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnCivilizationModSystem : ModSystem
{
    public const string ChannelName = "vsscsmod.channel";
    private const string StateKey = "clusterSpawnsState";
    private const string ResetStateKey = "vscncsResetState";
    private const string TribeStateKey = "vscncsTribeState";
    private const string TechnologyStateKey = "vscncsTechnologyState";
    private const string InjuryRespawnAttribute = "vscncsInjuryRespawn";
    private const string ConfigFile = ConfigMigration.Main;
    private readonly Random random = new();
    private readonly Dictionary<string, string> pendingReports = [];
    private readonly HashSet<string> readyPlayers = [];
    private readonly HashSet<string> verifiedPlayers = [];
    private ICoreServerAPI? sapi;
    private IServerNetworkChannel? channel;
    private VscnConfig config = new();
    private VscnState state = new();
    private ResetState resetState = new();
    private TribeState tribeState = new();
    private TechnologyState technologyState = new();
    private List<ModRule> serverRules = [];
    private Dictionary<string, string> coreVersions = new(StringComparer.OrdinalIgnoreCase);
    private bool clustersReady;
    private bool disposed;
    private Harmony? harmony;
    private Harmony? respawnHarmony;
    private RoofingPlacementCompatibility? roofingCompatibility;
    private ChiselToolsSurvivalCompatibility? chiselToolsCompatibility;
    private Vintagestory.API.Client.ICoreClientAPI? chiselToolsClientApi;
    private static VscnCivilizationModSystem? instance;
    public VscnConfig Config => config;
    public VscnState State => state;

    public override void Start(ICoreAPI api)
    {
        if (api.ModLoader.IsModEnabled("vsscmmod"))
            throw new InvalidOperationException("Seraph Civilization Simulation: remove the previous vsscmmod package before loading vsscsmod.");
        if (api.ModLoader.IsModEnabled("vscncivilizationsimulation"))
            throw new InvalidOperationException("Seraph Civilization Simulation cannot run alongside VSCN Civilization Simulation. Remove the old mod.");
        api.RegisterItemClass("VscnCivilizationCore", typeof(ItemCivilizationCore));
        api.RegisterBlockClass("VscnGroundMap", typeof(BlockGroundMap));
        api.RegisterBlockEntityClass("VscnGroundMap", typeof(BlockEntityGroundMap));
        api.RegisterEntityBehaviorClass("vscncs.respawn", typeof(VscnRespawnBehavior));
        api.RegisterEntityBehaviorClass("vscncs.bearblockbreak", typeof(BearBlockBreakingBehavior));
        if (api.World is Vintagestory.Common.GameMain game)
            for (int page = 1; page <= CivilizationMapInventory.MaxPages; page++)
                game.ClassRegistryInt.RegisterInventoryClass(CivilizationMapInventory.PageClass(page), typeof(CivilizationMapInventory));
        api.Network.RegisterChannel(ChannelName).RegisterMessageType<ClientModReportPacket>()
            .RegisterMessageType<GuidancePacket>().RegisterMessageType<GuidanceDecision>()
            .RegisterMessageType<TribeReinforcementRequest>().RegisterMessageType<TribeReinforcementPacket>()
            .RegisterMessageType<ModReportRequest>().RegisterMessageType<LivesResetPacket>()
            .RegisterMessageType<CivilizationRequestPacket>().RegisterMessageType<CivilizationSnapshotPacket>()
            .RegisterMessageType<StatusCopyPacket>().RegisterMessageType<MapMarkerClipboardRequest>().RegisterMessageType<MapDrawingSaveRequest>().RegisterMessageType<MapMarkerScaleRequest>().RegisterMessageType<MapEditResult>().RegisterMessageType<GroundMapTakeRequest>()
            .RegisterMessageType<GroundMapPlaceRequest>()
            .RegisterMessageType<EconomyRequest>().RegisterMessageType<EconomySnapshot>().RegisterMessageType<EconomyTradeView>()
            .RegisterMessageType<EconomyCatalogPacket>()
            .RegisterMessageType<ServerAnnouncementPacket>().RegisterMessageType<TribeRosterPacket>()
            .RegisterMessageType<AdminSettingsRequest>().RegisterMessageType<AdminSettingsPacket>().RegisterMessageType<TerritoryDiscoveryPacket>();
        if (api.ModLoader.IsModEnabled("coinage")) coinageEconomy = new CoinageEconomyCompatibility(api);
        if (api.ModLoader.IsModEnabled("immersivemaps"))
        {
            paperMapCompatibility = new PaperMapDrawingCompatibility();
            immersiveMapCompatibility = new ImmersiveMapCompatibility();
        }
        if (api.ModLoader.IsModEnabled("vsroofing"))
        {
            roofingCompatibility = new RoofingPlacementCompatibility();
            api.Logger.Notification("Seraph Civilization Simulation: VS Roofing frames now follow player facing; fixed direction selection is disabled.");
        }
        if (api.ModLoader.IsModEnabled("chiseltools"))
        {
            chiselToolsCompatibility = new ChiselToolsSurvivalCompatibility();
            if (api is Vintagestory.API.Client.ICoreClientAPI client)
            {
                chiselToolsClientApi = client;
                client.Event.BlockTexturesLoaded += FinalizeClientChiselTools;
            }
        }
    }

    public override void AssetsFinalize(ICoreAPI api)
    {
        FinalizeBuildingAssets(api);
        if (api.Side == EnumAppSide.Server && chiselToolsCompatibility != null)
            ChiselToolsSurvivalCompatibility.FinalizeAssets(api);
    }

    private void FinalizeClientChiselTools()
    {
        if (chiselToolsClientApi != null) ChiselToolsSurvivalCompatibility.FinalizeAssets(chiselToolsClientApi);
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        StartBuildingTerritories();
        instance = this;
        try { config = ReadConfig(true); }
        catch (Exception ex) { throw new InvalidOperationException("Seraph Civilization Simulation configuration invalid; correct " + ConfigFile, ex); }
        channel = api.Network.GetChannel(ChannelName).SetMessageHandler<ClientModReportPacket>(OnClientModReport)
            .SetMessageHandler<CivilizationRequestPacket>(OnCivilizationRequest)
            .SetMessageHandler<MapMarkerClipboardRequest>(OnMapMarkerClipboard)
            .SetMessageHandler<MapDrawingSaveRequest>(OnMapDrawingSave)
            .SetMessageHandler<MapMarkerScaleRequest>(OnMapMarkerScale)
            .SetMessageHandler<GroundMapTakeRequest>(OnGroundMapTake)
            .SetMessageHandler<GroundMapPlaceRequest>(OnGroundMapPlace)
            .SetMessageHandler<EconomyRequest>(OnEconomyRequest)
            .SetMessageHandler<GuidanceDecision>(OnGuidanceDecision);
        channel.SetMessageHandler<TribeReinforcementRequest>(OnTribeReinforcementRequest);
        channel.SetMessageHandler<AdminSettingsRequest>(OnAdminSettings);
        api.Event.SaveGameLoaded += LoadState;
        api.Event.GameWorldSave += SaveState;
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, InitializeClusters);
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, RefreshCivilizationViews);
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, UpdateTribeSuccession);
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, InitializeEconomyCatalog);
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, StartAnnouncementRelay);
        api.Event.PlayerJoin += OnJoin;
        api.Event.PlayerNowPlaying += OnPlaying;
        api.Event.PlayerRespawn += OnPlayerRespawn;
        api.Event.PlayerDisconnect += OnDisconnect;
        api.Event.OnEntitySpawn += AttachBehavior;
        api.Event.OnEntityLoaded += AttachBehavior;
        RegisterCommands(api);
        RefreshServerRules();
        InstallHandshakePolicy();
        StartTerritoryEffects();
        StartTribeSuccession();
        StartEconomy();
        InstallSleepInteractionRestrictions();
        InstallRespawnIntegration();
        StartGuidance();
    }

    private void InstallRespawnIntegration()
    {
        // Dead owns corpse creation in the modpack. Downed owns the final
        // bleed-out decision. Both integrations are optional at runtime.
        try
        {
            respawnHarmony = new Harmony("vsscsmod.respawn");
            respawnHarmony.Patch(AccessTools.Method(typeof(ServerPlayer), nameof(ServerPlayer.GetSpawnPosition)),
                prefix: new HarmonyMethod(typeof(VscnCivilizationModSystem), nameof(BeforeGetRespawnPosition)));
            var dead = AccessTools.TypeByName("Dead.DeadModSystem");
            var death = dead == null ? null : AccessTools.Method(dead, "OnPlayerDeath");
            if (death != null)
                respawnHarmony.Patch(death, prefix: new HarmonyMethod(typeof(VscnCivilizationModSystem), nameof(BeforeDeadCorpseHandling)));

            var downed = AccessTools.TypeByName("Downed.EntityBehaviorDowned");
            var decision = downed == null ? null : AccessTools.Method(downed, "ApplyDeathDecision");
            if (decision != null)
                respawnHarmony.Patch(decision, prefix: new HarmonyMethod(typeof(VscnCivilizationModSystem), nameof(BeforeDownedDeathDecision)));
        }
        catch (Exception ex)
        {
            sapi?.Logger.Warning("Seraph Civilization Simulation respawn integration unavailable: {0}", ex.Message);
        }
    }

    private static bool BeforeGetRespawnPosition(IServerPlayer __instance, bool consumeSpawnUse, ref FuzzyEntityPos __result)
    {
        var system = instance;
        var tribe = system?.tribeState.FindMember(__instance.PlayerUID);
        if (system == null || system.disposed || !consumeSpawnUse) return true;
        if (tribe == null)
        {
            if (system.Identity(__instance.PlayerUID).ReincarnationHome is not { } home) return true;
            system.InvalidatePendingSpawn(__instance.PlayerUID);
            __result = new FuzzyEntityPos(home.X + .5, home.Y, home.Z + .5) { Radius = 0, UsesLeft = -1 };
            return false;
        }
        var tribeSpawn = system.TribeSpawn(tribe);
        if (tribeSpawn == null) return true;
        // The engine reads this before the mod PlayerRespawn event and queues its teleport.
        system.InvalidatePendingSpawn(__instance.PlayerUID);
        __result = new FuzzyEntityPos(tribeSpawn.X + .5, tribeSpawn.Y, tribeSpawn.Z + .5)
        {
            Dimension = tribeSpawn.dimension,
            Radius = 0,
            UsesLeft = -1
        };
        return false;
    }

    private static bool BeforeDeadCorpseHandling(IServerPlayer player)
    {
        var system = instance;
        if (system == null || !system.ShouldSuppressTribeDeath(player, deathAlreadyCounted: true)) return true;
        MarkInjuryRespawn(player);
        return false;
    }

    private static void BeforeDownedDeathDecision(object __instance, ref bool die)
    {
        var system = instance;
        if (system == null || !die) return;
        var entity = __instance.GetType().GetProperty("entity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(__instance) as EntityPlayer
            ?? AccessTools.Field(__instance.GetType(), "entity")?.GetValue(__instance) as EntityPlayer;
        var player = entity?.Player as IServerPlayer;
        if (player == null || !system.ShouldSuppressTribeDeath(player, deathAlreadyCounted: false)) return;
        IncrementDeaths(player);
        system.Identity(player.PlayerUID).LastDeaths = GetDeaths(player);
        system.SaveState(); system.SendGuidance(player);
        MarkInjuryRespawn(player);
        die = false;
        system.sapi?.World.RegisterCallback(_ => system.RecoverTribePlayer(player), 0);
    }

    private bool ShouldSuppressTribeDeath(IServerPlayer player, bool deathAlreadyCounted)
    {
        var homeTribe = tribeState.FindMember(player.PlayerUID);
        if (homeTribe == null || TribeSpawn(homeTribe) == null) return false;
        int lives = config.Respawn.InitialLives < 0 ? -1 : config.Respawn.InitialLives + Identity(player.PlayerUID).AdditionalLives;
        if (lives < 0) return true;
        int deaths = GetDeaths(player) + (deathAlreadyCounted ? 0 : 1);
        return deaths < lives;
    }

    private static int GetDeaths(IServerPlayer player)
    {
        var type = player.WorldData.GetType();
        var field = type.GetField("Deaths", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field?.GetValue(player.WorldData) is int value) return value;
        var property = type.GetProperty("Deaths", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return property?.GetValue(player.WorldData) is int result ? result : 0;
    }

    private static void SetDeaths(IServerPlayer player, int value)
    {
        var type = player.WorldData.GetType();
        var field = type.GetField("Deaths", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field?.FieldType == typeof(int)) { field.SetValue(player.WorldData, value); return; }
        var property = type.GetProperty("Deaths", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property?.CanWrite == true && property.PropertyType == typeof(int)) property.SetValue(player.WorldData, value);
    }

    private static void IncrementDeaths(IServerPlayer player) => SetDeaths(player, GetDeaths(player) + 1);

    private void RecoverTribePlayer(IServerPlayer player)
    {
        if (player.Entity == null) return;
        var tribe = tribeState.FindMember(player.PlayerUID);
        if (tribe == null) return;
        player.Entity.WatchedAttributes.RemoveAttribute(InjuryRespawnAttribute);
        player.Entity.WatchedAttributes.MarkPathDirty(InjuryRespawnAttribute);
        var tribeSpawn = TribeSpawn(tribe);
        if (tribeSpawn == null) { AssignAndTeleport(player); return; }
        player.Entity.TeleportTo(new EntityPos(tribeSpawn.X + .5, tribeSpawn.Y, tribeSpawn.Z + .5) { Dimension = tribeSpawn.dimension });
        player.Entity.Pos.Motion.Set(0, 0, 0);
        player.Entity.Pos.Dimension = tribeSpawn.dimension;
        var spawn = new PlayerSpawnPos(tribeSpawn.X, tribeSpawn.Y, tribeSpawn.Z);
        player.SetSpawnPosition(spawn);
        SetWoundedHealth(player.Entity);
        player.BroadcastPlayerData();
    }

    private static void MarkInjuryRespawn(IServerPlayer player)
    {
        if (player.Entity == null) return;
        player.Entity.WatchedAttributes.SetBool(InjuryRespawnAttribute, true);
        player.Entity.WatchedAttributes.MarkPathDirty(InjuryRespawnAttribute);
    }

    private void OnPlayerRespawn(IServerPlayer player)
    {
        var entity = player.Entity;
        if (entity == null) return;
        entity.WatchedAttributes.SetBool("vsscsCoreSelected", false);
        if (RemainingLives(player) == 0) return;

        if (Identity(player.PlayerUID).RebornSpawn != null) { CompleteReincarnation(player); return; }
        var tribe = tribeState.FindMember(player.PlayerUID);
        if (tribe != null && TribeSpawn(tribe) is { } tribeSpawn)
        {
            InvalidatePendingSpawn(player.PlayerUID);
            var spawn = new PlayerSpawnPos(tribeSpawn.X, tribeSpawn.Y, tribeSpawn.Z);
            player.SetSpawnPosition(spawn);
            return;
        }

        if (Identity(player.PlayerUID).ReincarnationHome is { } home)
        {
            InvalidatePendingSpawn(player.PlayerUID);
            player.SetSpawnPosition(new PlayerSpawnPos(home.X, home.Y, home.Z));
            return;
        }

        if (state.PlayerAssignments.TryGetValue(player.PlayerUID, out int index)
            && index >= 0 && index < state.Clusters.Count)
        {
            var center = state.Clusters[index];
            AssignAndTeleport(player, index, center.X, center.Z);
        }
        else if (clustersReady && state.Clusters.Count > 0)
        {
            AssignAndTeleport(player);
        }
    }

    internal void OnPlayerRevived(Entity entity)
    {
        if (sapi == null || entity is not EntityPlayer revived || !revived.Alive) return;
        // Reincarnation calls Entity.Revive directly, without Downed's PlayerRespawn
        // event. Its persistent death flag otherwise rejects every living animation.
        ClearDownedDeathState(revived);
        if (!entity.WatchedAttributes.GetBool(InjuryRespawnAttribute, false)) return;
        // Other revival behaviors can restore health, so finish after the engine's revival callback.
        sapi.World.RegisterCallback(_ =>
        {
            if (disposed || !revived.Alive || revived.Player is not IServerPlayer player || player.Entity != revived
                || player.ConnectionState == EnumClientState.Offline) return;
            if (!revived.WatchedAttributes.GetBool(InjuryRespawnAttribute, false)) return;
            revived.WatchedAttributes.RemoveAttribute(InjuryRespawnAttribute);
            revived.WatchedAttributes.MarkPathDirty(InjuryRespawnAttribute);
            SetWoundedHealth(revived);
            player.BroadcastPlayerData();
        }, 0);
    }

    private static void ClearDownedDeathState(EntityPlayer entity)
    {
        if (!entity.WatchedAttributes.GetBool("downeddied") && !entity.WatchedAttributes.HasAttribute("downedexecuted")) return;
        entity.WatchedAttributes.SetBool("downeddied", false);
        entity.WatchedAttributes.RemoveAttribute("downedexecuted");
        entity.WatchedAttributes.MarkPathDirty("downeddied");
        entity.WatchedAttributes.MarkPathDirty("downedexecuted");
        if (entity.AnimManager?.Animator != null) entity.AnimManager.StopAnimation("deathprone");
        if (entity.SelfFpAnimManager?.Animator != null) entity.SelfFpAnimManager.StopAnimation("deathprone");
        // Downed listens to this path to update its HUD and stop disallowed client
        // animations. Do not change an active rescue/countdown or its cooldown.
        entity.WatchedAttributes.MarkPathDirty("downed");
    }

    private static void SetWoundedHealth(EntityPlayer entity)
    {
        var health = entity.GetBehavior("health");
        var property = health?.GetType().GetProperty("Health", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property?.CanWrite != true || property.PropertyType != typeof(float)) return;
        var maxProperty = health?.GetType().GetProperty("MaxHealth", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        float max = maxProperty?.GetValue(health) is float maxValue ? maxValue : 0f;
        float current = property.GetValue(health) is float currentValue ? currentValue : 0f;
        property.SetValue(health, Math.Max(1f, max > 0 ? max * .2f : current * .2f));
    }

    private VscnConfig ReadConfig(bool initial)
    {
        var loaded = ConfigMigration.LoadMain(sapi!);
        if (loaded == null && !initial) throw new ArgumentException("Configuration file is missing.");
        var result = loaded ?? new VscnConfig();
        // Import the previous mod's layout on the first installation only.
        if (loaded == null && sapi.LoadModConfig<Newtonsoft.Json.Linq.JObject>("clusterspawns_config.json") is { } legacy)
        {
            var general = legacy["General"]?.ToObject<ClusterConfig>();
            if (general != null) result.Clusters = general;
            JsonConvert.PopulateObject(legacy["SpawnRules"]?.ToString() ?? "{}", result.Clusters);
            result.Clusters.ManualClusters = legacy["ManualClusters"]?.ToObject<List<ClusterPoint>>() ?? [];
        }
        result.Validate();
        if (initial && loaded == null) sapi.StoreModConfig(result, ConfigFile);
        return result;
    }

    private void RefreshServerRules()
    {
        var nextRules = new List<ModRule>();
        var nextCore = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["game"] = GameVersion.ShortGameVersion };
        var loadedMods = sapi!.ModLoader.Mods.ToList();
        foreach (var mod in loadedMods)
        {
            if (mod.Info == null || string.IsNullOrEmpty(mod.Info.ModID)) continue;
            // Engine components ship as DLLs, so ZIP equality does not apply to them.
            if (mod.Info.ModID is "game" or "survival" or "creative") nextCore[mod.Info.ModID] = mod.Info.Version;
            else nextRules.Add(new ModRule { ModId = mod.Info.ModID, Version = mod.Info.Version });
        }
        if (config.ModPolicy.Enabled && config.ModPolicy.Mode == ModPolicyMode.Whitelist && config.ModPolicy.IncludeServerMods)
        {
            var searchPaths = sapi.ModLoader.GetType().GetProperty("ModSearchPaths")?.GetValue(sapi.ModLoader) as IEnumerable<string>;
            searchPaths ??= loadedMods.Select(mod => Path.GetDirectoryName(mod.SourcePath)).OfType<string>()
                .Concat([GamePaths.BinariesMods, GamePaths.DataPathMods]);
            var clientRules = ClientOnlyModCatalog.Read(searchPaths,
                warning => sapi.Logger.Warning("Seraph Civilization Simulation: {0}", warning));
            nextRules.AddRange(clientRules);
            sapi.Logger.Notification("Seraph Civilization Simulation whitelist includes {0} client-only mods from server mod directories.", clientRules.Count);
        }
        serverRules = nextRules; coreVersions = nextCore;
    }

    private void LoadState()
    {
        LoadEconomy();
        LoadCivilizationLives();
        var data = sapi!.WorldManager.SaveGame.GetData(StateKey);
        if (data is { Length: > 0 })
        {
            // A corrupt existing state must never be replaced with fresh clusters.
            state = JsonConvert.DeserializeObject<VscnState>(System.Text.Encoding.UTF8.GetString(data)) ?? throw new InvalidDataException("Null ClusterSpawns state.");
            if (state.Clusters == null || state.PlayerAssignments == null) throw new InvalidDataException("Invalid ClusterSpawns state.");
            RebuildClusterCounts();
            sapi.Logger.Notification("Seraph Civilization Simulation inherited {0} clusters and {1} assignments.", state.Clusters.Count, state.PlayerAssignments.Count);
        }
        var resets = sapi.WorldManager.SaveGame.GetData(ResetStateKey);
        if (resets is { Length: > 0 }) resetState = JsonConvert.DeserializeObject<ResetState>(System.Text.Encoding.UTF8.GetString(resets)) ?? new();
        var tribes = sapi.WorldManager.SaveGame.GetData(TribeStateKey);
        if (tribes is { Length: > 0 }) tribeState = JsonConvert.DeserializeObject<TribeState>(System.Text.Encoding.UTF8.GetString(tribes)) ?? throw new InvalidDataException("Null tribe state.");
        if (tribeState.Tribes == null || tribeState.Tribes.Any(t => t.Members == null || t.Members.Count < 1 || !t.Members.Contains(t.LeaderUid) || t.Outposts == null))
            throw new InvalidDataException("Invalid tribe state.");
        MigrateBuildingTerritories(tribes);
        foreach (var tribe in tribeState.Tribes)
        {
            tribe.Authorizations ??= []; tribe.AuthorizationHistory ??= []; tribe.MemberActivity ??= [];
            EndAuthorizations(tribe, grant => grant.Pending, "revoked");
        }
        if (economy.MigrateIssuers()) SaveEconomy();
        var technology = sapi.WorldManager.SaveGame.GetData(TechnologyStateKey);
        var announcement = sapi.WorldManager.SaveGame.GetData(AnnouncementStateKey);
        var tutorials = sapi.WorldManager.SaveGame.GetData(TutorialStateKey);
        LoadCivilizationContent(announcement, tutorials);
        if (technology is { Length: > 0 }) technologyState = JsonConvert.DeserializeObject<TechnologyState>(System.Text.Encoding.UTF8.GetString(technology)) ?? new();
        bool soloChanged = false;
        if (config.Tribes.AllowSoloCreation)
            foreach (var tribe in tribeState.Tribes.Where(t => t.Members.Count == 1 && !t.AllowsSolo))
            { tribe.AllowsSolo = true; soloChanged = true; }
        if (tribeState.Tribes.RemoveAll(t => t.Members.Count < 2 && !t.AllowsSolo) > 0 || soloChanged) SaveState();
    }

    private void SaveState()
    {
        if (sapi == null || disposed) return;
        if (clustersReady) sapi.WorldManager.SaveGame.StoreData(StateKey, System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(state)));
        sapi.WorldManager.SaveGame.StoreData(ResetStateKey, System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(resetState)));
        sapi.WorldManager.SaveGame.StoreData(TribeStateKey, System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(tribeState)));
        sapi.WorldManager.SaveGame.StoreData(TechnologyStateKey, System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(technologyState)));
        SaveEconomy();
        SaveCivilizationLives();
    }

    private void OnJoin(IServerPlayer player)
    {
        player.Entity?.WatchedAttributes.SetBool("vsscsCoreSelected", false);
        SyncSleepInteractionSetting(player);
        if (resetState.NeedsReset(player.PlayerUID) && ResetDeaths(player, broadcast: false))
        {
            resetState.Applied(player.PlayerUID, true);
            SaveState();
        }
        if (config.ModPolicy.Enabled) RequestReport(player, false);
        // Send before PlayerNowPlaying: native character creation gates player readiness.
        SendGuidance(player);
    }

    private void OnPlaying(IServerPlayer player)
    {
        RecordTribeActivity(player);
        WatchEconomyInventory(player); economyCatalogReaders[player.PlayerUID] = 0; InvalidateEconomyViews();
        SyncSleepInteractionSetting(player);
        readyPlayers.Add(player.PlayerUID);
        NotifyPendingAuthorizations(player);
        AttachBehavior(player.Entity);
        if (config.ModPolicy.Enabled)
        {
            if (pendingReports.TryGetValue(player.PlayerUID, out var nonce))
                ScheduleReportTimeout(player, nonce);
            else if (!verifiedPlayers.Contains(player.PlayerUID)) RequestReport(player);
        }
        else verifiedPlayers.Add(player.PlayerUID);
        HandleVerifiedPlayer(player);
        ReturnLegacyMaps(player);
        SendCivilizationSnapshot(player, false);
        SendLoginAnnouncement(player);
        Identity(player.PlayerUID).Living = player.Entity?.Alive == true;
        PublishIdentity(player); SendGuidance(player); TryEntryReincarnation(player);
    }

    private void HandleVerifiedPlayer(IServerPlayer player)
    {
        if (!readyPlayers.Contains(player.PlayerUID)) return;
        if (!verifiedPlayers.Contains(player.PlayerUID) &&
            !(config.ModPolicy.Enabled && pendingReports.ContainsKey(player.PlayerUID))) return;
        // Saved assignments already identify returning players, including after server restart.
        if (!state.PlayerAssignments.ContainsKey(player.PlayerUID)) AssignIfNeeded(player);
    }

    private void OnDisconnect(IServerPlayer player)
    {
        RecordTribeActivity(player, true);
        SaveState();
        rosterRequests.Remove(player.PlayerUID);
        reinforcementRequests.Remove(player.PlayerUID);
        markerClipboards.Remove(player.PlayerUID);
        guidanceOffers.Remove(player.PlayerUID); EndReincarnationSession(player); rebornTeleports.Remove(player.PlayerUID);
        if (player.Entity != null) Identity(player.PlayerUID).Living = player.Entity.Alive;
        SaveCivilizationLives();
        ReturnLegacyMaps(player);
        DisconnectEconomy(player.PlayerUID);
        readyPlayers.Remove(player.PlayerUID);
        verifiedPlayers.Remove(player.PlayerUID);
        pendingReports.Remove(player.PlayerUID);
        InvalidatePendingSpawn(player.PlayerUID);
        RemovePendingTribeActions(player.PlayerUID);
        territoryPresence.Remove(player.PlayerUID);
        territoryDiscoveryPresence.Remove(player.PlayerUID);
        territoryDebugModes.Remove(player.PlayerUID);
        territoryDebugMembers.Remove(player.PlayerUID);
    }

    private void RequestReport(IServerPlayer player, bool startTimeout = true)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        pendingReports[player.PlayerUID] = nonce;
        verifiedPlayers.Remove(player.PlayerUID);
        channel!.SendPacket(new ModReportRequest { Nonce = nonce }, player);
        if (startTimeout) ScheduleReportTimeout(player, nonce);
    }

    private void ScheduleReportTimeout(IServerPlayer player, string nonce)
    {
        sapi!.Event.RegisterCallback(_ =>
        {
            if (!disposed && config.ModPolicy.Enabled && pendingReports.TryGetValue(player.PlayerUID, out var current) && current == nonce)
            {
                pendingReports.Remove(player.PlayerUID);
                sapi.Logger.Warning("Seraph Civilization Simulation did not receive a mod report from {0}; the native handshake policy remains authoritative.", player.PlayerName);
            }
        }, config.ModPolicy.ReportTimeoutSeconds * 1000);
    }

    private void OnClientModReport(IServerPlayer player, ClientModReportPacket packet)
    {
        if (disposed || !config.ModPolicy.Enabled || !pendingReports.TryGetValue(player.PlayerUID, out var nonce)) return;
        if (packet.Nonce != nonce) return;
        var errors = ModPolicyEvaluator.Validate(config.ModPolicy, packet.Mods ?? [], serverRules, coreVersions);
        pendingReports.Remove(player.PlayerUID);
        if (errors.Count > 0)
        {
            string reason = "Seraph Civilization Simulation mod policy mismatch: " + string.Join("; ", errors.Take(8));
            sapi!.Logger.Warning("{0} ({1}): {2}. The client handshake should have disabled the disallowed mod before loading.", player.PlayerName, player.PlayerUID, reason);
            return;
        }
        verifiedPlayers.Add(player.PlayerUID);
        HandleVerifiedPlayer(player);
    }

    private void AttachBehavior(Entity entity)
    {
        if (entity is not EntityPlayer) return;
        if (entity.GetBehavior<VscnRespawnBehavior>() is { } existing) { existing.Bind(this); return; }
        var behavior = new VscnRespawnBehavior(entity);
        behavior.Bind(this);
        var behaviors = entity.SidedProperties?.Behaviors;
        if (behaviors == null) entity.AddBehavior(behavior);
        else { behaviors.Insert(0, behavior); entity.CacheServerBehaviors(); }
    }

    private bool ResetDeaths(IServerPlayer player, bool broadcast = true)
    {
        try
        {
            var field = player.WorldData.GetType().GetField("Deaths", BindingFlags.Instance | BindingFlags.Public);
            if (field?.FieldType != typeof(int)) throw new NotSupportedException("Engine Deaths field unavailable.");
            field.SetValue(player.WorldData, 0);
            Identity(player.PlayerUID).AdditionalLives = 0;
            // PlayerJoin precedes the engine's first player-data packet, which includes inventories.
            if (broadcast) player.BroadcastPlayerData();
            if (readyPlayers.Contains(player.PlayerUID))
            {
                int lives = RemainingLives(player);
                SendGuidance(player);
                channel!.SendPacket(new LivesResetPacket { LivesLeft = lives }, player);
            }
            return true;
        }
        catch (Exception ex) { sapi!.Logger.Error("Seraph Civilization Simulation reset failed for {0}: {1}", player.PlayerName, ex.Message); return false; }
    }

    private TextCommandResult ResetAllPlayers()
    {
        resetState.AllGeneration++;
        int count = 0;
        foreach (var player in sapi!.World.AllOnlinePlayers.OfType<IServerPlayer>())
            if (ResetDeaths(player)) { resetState.Applied(player.PlayerUID, false); count++; }
        SaveState();
        return TextCommandResult.Success($"已重置 {count} 名在线玩家；全部离线玩家将在下次连接时重置。");
    }

    private TextCommandResult ResetPlayer(string name)
    {
        var player = FindOnline(name);
        if (player != null)
        {
            if (!ResetDeaths(player)) return TextCommandResult.Error("重置失败，请检查服务器日志。");
            resetState.Applied(player.PlayerUID, false); SaveState();
            return TextCommandResult.Success($"已重置 {player.PlayerName} 的死亡计数，恢复世界配置的重生次数。");
        }
        var record = sapi!.PlayerData.GetPlayerDataByLastKnownName(name);
        if (record == null) return TextCommandResult.Error("找不到该玩家。");
        resetState.PendingPlayers.Add(record.PlayerUID); SaveState();
        return TextCommandResult.Success($"已登记 {name}，下次连接时重置。");
    }

    private IServerPlayer? FindOnline(string name) => sapi!.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault(p => p.PlayerName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private void RegisterCommands(ICoreServerAPI api)
    {
        foreach (string name in new[] { "vsscsmod", "vscncs" })
            api.ChatCommands.Create(name).WithDescription("Seraph Civilization Simulation")
                .RequiresPrivilege(Privilege.chat).WithArgs(api.ChatCommands.Parsers.OptionalAll("arguments")).HandleWith(HandleCommand);
    }

    private TextCommandResult HandleCommand(TextCommandCallingArgs args)
    {
        var words = new CmdArgs(args.Parsers.Count > 0 ? args[0] as string ?? "" : "");
        string sub = words.PopWord("").ToLowerInvariant();
        var caller = args.Caller.Player as IServerPlayer;
        string action = sub == "cluster" ? words.PopWord("info").ToLowerInvariant() : "";
        if (sub == "cluster" && action == "info") return ClusterInfo(caller);
        if (sub == "statuscopy") return ExportPlayerStatus(words, caller);
        if (args.Caller.Type != EnumCallerType.Console && !args.Caller.HasPrivilege(Privilege.controlserver)) return TextCommandResult.Error("需要 controlserver 管理员权限。");
        if (sub == "name") return HandleNameCommand(words);
        if (sub == "territory") return HandleTerritoryDebug(words, caller);
        if (sub == "tribe") return HandleTribeCommand(words);
        if (sub == "sleep") return HandleSleepCommand(words);
        if (sub == "cluster") return HandleClusterCommand(action, words, caller, caller?.LanguageCode ?? args.LanguageCode ?? Lang.CurrentLocale);
        if (sub == "content") return HandleContentCommand(words, caller);
        if (sub == "resetall") return ResetAllPlayers();
        if (sub == "reset")
        {
            string target = words.PopWord("");
            if (target.Equals("start", StringComparison.OrdinalIgnoreCase))
            {
                string value = words.PopWord("");
                if (!int.TryParse(value, out int n) || n < 1) return TextCommandResult.Error("用法: /vsscsmod reset start <正整数>");
                resetState.Start(n); SaveState();
                return TextCommandResult.Success($"已开启登录重置：从现在起每位玩家最多重置 {n} 次。");
            }
            if (target.Equals("stop", StringComparison.OrdinalIgnoreCase)) { resetState.Stop(); SaveState(); return TextCommandResult.Success("已停止登录重置。"); }
            if (string.IsNullOrEmpty(target)) return TextCommandResult.Error("用法: /vsscsmod reset <玩家名>|start n|stop");
            return ResetPlayer(target);
        }
        if (sub == "reload")
        {
            string target = words.PopWord("config").ToLowerInvariant();
            if (target == "content") return HandleReloadContent(words);
            if (target != "config") return TextCommandResult.Error("用法: /vsscsmod reload config|content [all|announcements|tutorials]");
            try
            {
                var next = ReadConfig(false);
                var previous = config;
                ApplyConfiguration(next);
                return TextCommandResult.Success("配置已热重载；现有出生聚落坐标和分配保留，在线玩家重新校验模组。"
                    + (previous.DisableVintageCanvasArtistClass != config.DisableVintageCanvasArtistClass
                        ? "画家职业限制开关需重启游戏／服务器后生效。" : ""));
            }
            catch (Exception ex) { return TextCommandResult.Error("重载失败，保留有效配置: " + ex.Message); }
        }
        return TextCommandResult.Success("/vsscsmod statuscopy; name reset <账号名、玩家游戏名或UID>; sleep on|off|toggle|status; territory debug; tribe list|delete <部落名>|solo on|off|toggle|status; content announcements|tutorials list|add|edit|delete|clear; reset <玩家名>|start n|stop; resetall; reload config|content [all|announcements|tutorials]; cluster info|infoall|list|who|tp|assign|reset|resetall|reroll");
    }

    public override void Dispose()
    {
        if (successionListener != 0) sapi?.Event.UnregisterGameTickListener(successionListener);
        activityTicks.Clear();
        StopBuildingTerritories();
        announcementRelay?.Dispose(); announcementRelay = null;
        StopGuidance();
        StopEconomy();
        coinageEconomy?.Dispose(); coinageEconomy = null;
        disposed = true;
        markerClipboards.Clear();
        if (sapi != null) sapi.Event.PlayerRespawn -= OnPlayerRespawn;
        UninstallSleepInteractionRestrictions();
        harmony?.UnpatchAll("vsscsmod.handshake");
        respawnHarmony?.UnpatchAll("vsscsmod.respawn");
        harmony = null;
        respawnHarmony = null;
        roofingCompatibility?.Dispose();
        paperMapCompatibility?.Dispose();
        paperMapCompatibility = null;
        immersiveMapCompatibility?.Dispose(); immersiveMapCompatibility = null;
        roofingCompatibility = null;
        if (chiselToolsClientApi != null) chiselToolsClientApi.Event.BlockTexturesLoaded -= FinalizeClientChiselTools;
        chiselToolsClientApi = null;
        chiselToolsCompatibility?.Dispose();
        chiselToolsCompatibility = null;
        instance = null;
        pendingReports.Clear(); readyPlayers.Clear(); verifiedPlayers.Clear();
        pendingInvites.Clear(); pendingFoundings.Clear();
    }
}
