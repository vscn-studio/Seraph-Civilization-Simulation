using System.Reflection;
using System.IO.Compression;
using Newtonsoft.Json;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using SeraphCivilizationSimulation;

int checks = 0;
void Check(bool pass, string message) { if (!pass) throw new Exception(message); checks++; }
object? Invoke(object target, string method, params object?[] args) => target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.Invoke(target, args);
void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
ConfigMigrationChecks.Run(Check);
MapDrawingFacesChecks.Run(Check);
if (args.Contains("--management-only"))
{
    CivilizationManagementChecks.Run(Check);
    TribeAuthorizationChecks.Run(Check);
    Console.WriteLine($"PASS: {checks} configuration permissions, hot reload, succession and window authorization checks.");
    return;
}
if (args.Contains("--management-gui-only"))
{
    Vintagestory.API.Config.Lang.AvailableLanguages["en"] = Proxy.Make<Vintagestory.API.Config.ITranslationService>((m,a)=>m.Name.StartsWith("Get")?a?[0]:null);
    Vintagestory.API.Config.Lang.ChangeLanguage("en");
    Environment.SetEnvironmentVariable("VSCN_MANAGEMENT_GUI_ONLY","1");
    NativeGuiChecks.Run(Check);
    Console.WriteLine($"PASS: {checks} native menu, administrator settings and authorization layout checks.");
    return;
}
CivilizationCoreChecks.Run(Check);
ClusterGenerationChecks.Run(Check);
foreach (var (ratio, expected) in new[] { (0f, 0), (0.6f, 15), (0.61f, 16), (1f, 25) })
    Check(VscnCivilizationModSystem.RequiredLandSamples(ratio) == expected, "Land ratio threshold must preserve the configured fraction.");
Check(!typeof(VscnCivilizationModSystem).Assembly.GetReferencedAssemblies().Any(a => a.Name == "AlgernonsTerrainSampler"), "Terrain sampler must not be a runtime dependency.");

if (args.Contains("--wildlife-only"))
{
    WildlifeCompatibilityChecks.Run(Check);
    Console.WriteLine($"PASS: {checks} wildlife geometry, native behavior loading and block destruction checks.");
    return;
}

if (args.Contains("--territory-renderer-only"))
{
    TerritoryRendererChecks.Run(Check);
    Console.WriteLine($"PASS: {checks} territory boundary geometry and color checks.");
    return;
}

if (args.Contains("--updates-only"))
{
    UpdatesChecks.Run(Check);
    ContentFilesChecks.Run(Check);
    Console.WriteLine($"PASS: {checks} retired update feature and content storage checks.");
    return;
}

if (args.Contains("--content-only"))
{
    ContentFilesChecks.Run(Check);
    Console.WriteLine($"PASS: {checks} ModData content storage, migration, isolation and backup recovery checks.");
    return;
}

if (args.Contains("--artist-only"))
{
    ArtistClassChecks.Run(Check);
    Console.WriteLine($"PASS: {checks} native profession filtering, server policy, saved character migration and patch lifetime checks.");
    return;
}

if (args.Contains("--lore-only"))
{
    LoreRestrictionChecks.Run(Check, Environment.GetEnvironmentVariable("VSCN_LORE_DLL_DIR")
        ?? throw new InvalidOperationException("Set VSCN_LORE_DLL_DIR to the audited pack DLL directory."));
    Console.WriteLine($"PASS: {checks} lore policy, independent switches, spawn, drops and installed mod compatibility checks.");
    return;
}

if (args.Contains("--building-only"))
{
    BuildingProtectionChecks.Run(Check);
    Console.WriteLine($"PASS: {checks} building reserve, recovery, material fire and installed mod integration checks.");
    return;
}

if (args.Contains("--clientlogs-only"))
{
    ClientLogUploadChecks.Run(Check);
    Console.WriteLine($"PASS: {checks} client log collection, consent, reconnect, transfer and storage checks.");
    return;
}

if (args.Contains("--ground-maps-only"))
{
    Vintagestory.API.Config.Lang.AvailableLanguages["en"] = Proxy.Make<Vintagestory.API.Config.ITranslationService>((m, a) => m.Name.StartsWith("Get") ? a?[0] : null);
    Vintagestory.API.Config.Lang.ChangeLanguage("en");
    PaperMapCompatibilityChecks.Run(Environment.GetEnvironmentVariable("VSCN_IMMERSIVEMAPS_ZIP")
        ?? throw new InvalidOperationException("Set VSCN_IMMERSIVEMAPS_ZIP to the installed package."), Check);
    Console.WriteLine($"PASS: {checks} native paper map and ground atlas checks.");
    return;
}

if (args.Contains("--economy-only"))
{
    EconomyChecks.Run(Check);
    if (Environment.GetEnvironmentVariable("VSCN_COINAGE_ZIP") is { } coins) CoinageEconomyChecks.Run(coins, Check);
    Console.WriteLine($"PASS: {checks} economy checks.");
    return;
}

if (args.Contains("--statuscopy-only"))
{
    StatusCopyChecks.Run(Check);
    Console.WriteLine($"PASS: {checks} checks; ordinary-player status logs and client/server diagnostics validated.");
    return;
}

if (args.Contains("--sleep-only"))
{
    SleepInteractionChecks.Run(Check);
    Console.WriteLine($"PASS: {checks} checks; bed use and sleeping interaction restrictions validated.");
    return;
}

if (args.Contains("--respawn-only"))
{
    RectangularTerritoryChecks.Run(Check);
BuildingTerritoryChecks.Run(Check);
RespawnChecks.Run(Check);
    Console.WriteLine($"PASS: {checks} checks; native tribe respawn and wounded revival validated.");
    return;
}

if (args.Contains("--guidance-only"))
{
    GuidanceChecks.Run(Check);
    EntryInteractionChecks.Run(Check);
    Console.WriteLine($"PASS: {checks} civilization identity, lives, reincarnation and reinforcement checks.");
    return;
}

if (args.Contains("--authorization-only"))
{
    TribeAuthorizationChecks.Run(Check);
    Console.WriteLine($"PASS: {checks} tribe authorization and reinforcement enrollment checks.");
    return;
}

if (args.Contains("--chiseltools-only"))
{
    ChiselToolsSurvivalChecks.Run(Environment.GetEnvironmentVariable("VSCN_CHISELTOOLS_ZIP")
        ?? throw new InvalidOperationException("Set VSCN_CHISELTOOLS_ZIP to the QP package to test."), Check);
    Console.WriteLine($"PASS: {checks} checks; QP native tool operations, cleanup, undo and remaining survival rules validated with the installed DLL.");
    return;
}

if (args.Contains("--roofing-only"))
{
    RoofingPlacementChecks.Run(Environment.GetEnvironmentVariable("VSCN_ROOFING_ZIP")
        ?? throw new InvalidOperationException("Set VSCN_ROOFING_ZIP to the Roofing package to test."), Check);
    Console.WriteLine($"PASS: {checks} checks; Roofing menus, facing placement and shared patch lifetime validated.");
    return;
}

var reset = new ResetState();
reset.Start(1);
Check(reset.NeedsReset("a") && reset.NeedsReset("b"), "Each player must have their own allowance.");
reset.Applied("a", true);
Check(!reset.NeedsReset("a") && reset.NeedsReset("b"), "Rejoining cannot reuse a one-reset allowance.");
reset = JsonConvert.DeserializeObject<ResetState>(JsonConvert.SerializeObject(reset))!;
Check(!reset.NeedsReset("a"), "Allowance must survive a server restart.");
reset.Start(2);
reset.Applied("a", true); Check(reset.NeedsReset("a"), "Second allowance lost.");
reset.Applied("a", true); Check(!reset.NeedsReset("a"), "Two-reset allowance exceeded.");
reset.Stop(); Check(!reset.NeedsReset("b"), "Stop must stop join reset.");
reset.AllGeneration++; Check(reset.NeedsReset("a") && reset.NeedsReset("offline"), "Reset all must reach offline players.");
reset.Applied("offline", true); Check(!reset.NeedsReset("offline"), "Global reset must be consumed once.");
reset.PendingPlayers.Add("a"); reset.Applied("a", false); Check(!reset.PendingPlayers.Contains("a"), "Individual queued reset not consumed.");

var self = new ClientModItem { ModId = "vsscsmod", Version = "1.0.4" };
var extra = new ClientModItem { ModId = "example", Version = "2.0.0" };
var server = new[] { new ModRule { ModId = self.ModId, Version = self.Version } };
var core = new Dictionary<string, string> { ["game"] = "1.22.3" };
var policy = new ModPolicyConfig();
var handshakeRules = new[] { new ModRule { ModId = "servermod", Version = "1.2.3" } };
Check(ModHandshakePolicyCheck(policy, handshakeRules).Contains("servermod@1.2.3"), "Whitelist handshake did not include server mod version.");
Check(ModHandshakePolicyCheck(new ModPolicyConfig { IncludeServerMods = false }, server).Contains("vsscsmod@1.0.4"), "Whitelist handshake omitted the loaded policy mod version.");
Check(ModHandshakePolicyCheck(policy, handshakeRules, new Dictionary<string, string> { ["game"] = "1.22.7", ["survival"] = "1.22.7", ["creative"] = "1.22.7" }).Contains("game@1.22.7"), "Whitelist handshake omitted the game core.");
policy.Mode = ModPolicyMode.Blacklist;
policy.Mods.Add(new ModRule { ModId = extra.ModId, Version = extra.Version });
Check(ModHandshakeBlacklistCheck(policy).Contains("example"), "Blacklist handshake did not include blocked mod ID.");
policy.Mods.Clear();
policy.Mode = ModPolicyMode.Whitelist;
Check(ModPolicyEvaluator.Validate(policy, [self], server, core).Count == 0, "Default server whitelist rejected self.");
Check(ModPolicyEvaluator.Validate(policy, [self, extra], server, core).Count == 1, "Unknown client mod accepted.");
policy.Mods.Add(new ModRule { ModId = extra.ModId, Version = extra.Version });
Check(ModPolicyEvaluator.Validate(policy, [self, extra], server, core).Count == 0, "Extra whitelist rule rejected.");
extra.Version = "2.1.0"; Check(ModPolicyEvaluator.Validate(policy, [self, extra], server, core).Count == 1, "Loose mode must check exact version.");
extra.Version = "2.0.0";
Check(ModPolicyEvaluator.Validate(policy, [self, extra], server, core).Count == 0, "ID + version whitelist rejected a matching mod.");
policy.Mode = ModPolicyMode.Blacklist;
Check(ModPolicyEvaluator.Validate(policy, [self, extra], server, core).Count == 1, "Blacklist match must reject and not blacklist server mods automatically.");
extra.Version = "9.0.0"; Check(ModPolicyEvaluator.Validate(policy, [self, extra], server, core).Count == 0, "Blacklist version matching failed.");
policy.Mods[0].Version = "*";
Check(ModPolicyEvaluator.Validate(policy, [self, extra], server, core).Count == 1, "Wildcard blacklist must reject any version of the matching ID.");
policy.Mode = ModPolicyMode.Whitelist;
foreach (var version in new[] { "2.0.0", "2.1.0", "9.0.0", "10.0.0-pre.1" })
{
    extra.Version = version;
    Check(ModPolicyEvaluator.Validate(policy, [self, extra], server, core).Count == 0, "Wildcard whitelist rejected a version of the matching ID.");
}
var wildcardHandshake = ModHandshakePolicyCheck(policy, server);
Check(wildcardHandshake.Contains("example@") && !wildcardHandshake.Contains("example@*"), "Wildcard handshake must use the native version-independent ID@ format.");
extra.ModId = "exampleother";
Check(ModPolicyEvaluator.Validate(policy, [self, extra], server, core).Count == 1, "Wildcard version must not allow another mod ID.");
extra.ModId = "example";
extra.Version = "";
Check(ModPolicyEvaluator.Validate(policy, [self, extra], server, core).Count > 0, "Wildcard version must not accept a malformed inventory entry.");
extra.Version = "9.0.0";
policy.Mods.Add(new ModRule { ModId = "game", Version = "*" });
Check(ModPolicyEvaluator.Validate(policy, [self, new ClientModItem { ModId = "game", Version = "1.22.7" }], server, core).Count == 1, "Wildcard rule must not bypass core version validation.");
Check(ModPolicyEvaluator.Validate(policy, [], server, core).Count > 0, "Empty report accepted.");
Check(ModPolicyEvaluator.Validate(policy, [self, self], server, core).Count > 0, "Duplicate inventory accepted.");
Check(ModPolicyEvaluator.Validate(policy, [self, null!], server, core).Count > 0, "Malformed inventory accepted.");

var catalogRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vscncs-catalog-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(catalogRoot);
try
{
    string clientZip = System.IO.Path.Combine(catalogRoot, "client-only.zip");
    using (var archive = ZipFile.Open(clientZip, ZipArchiveMode.Create))
    using (var writer = new StreamWriter(archive.CreateEntry("modinfo.json").Open()))
        writer.Write("{\"type\":\"Code\",\"modid\":\"clientonly\",\"version\":\"2.3.4\",\"side\":\"Client\"}");
    string universalZip = System.IO.Path.Combine(catalogRoot, "universal.zip");
    using (var archive = ZipFile.Open(universalZip, ZipArchiveMode.Create))
    using (var writer = new StreamWriter(archive.CreateEntry("modinfo.json").Open()))
        writer.Write("{\"type\":\"Code\",\"modid\":\"universal\",\"version\":\"1.0\",\"side\":\"Universal\"}");
    string clientFolder = System.IO.Path.Combine(catalogRoot, "client-folder");
    Directory.CreateDirectory(clientFolder);
    File.WriteAllText(System.IO.Path.Combine(clientFolder, "modinfo.json"), "{\"type\":\"Content\",\"modid\":\"clientfolder\",\"version\":\"3.0\",\"side\":\"client\"}");
    var looseCatalog = ClientOnlyModCatalog.Read([catalogRoot]);
    Check(looseCatalog.Count == 2 && looseCatalog.Any(r => r.ModId == "clientonly" && r.Version == "2.3.4") && looseCatalog.Any(r => r.ModId == "clientfolder"), "Server directory client mods were not cataloged.");
    var reportedClient = new ClientModItem { ModId = "clientonly", Version = "2.3.4" };
    Check(ModPolicyEvaluator.Validate(new ModPolicyConfig(), [self, reportedClient], server.Concat(looseCatalog), core).Count == 0, "Default whitelist rejected a client-only ZIP from a server directory.");
}
finally { Directory.Delete(catalogRoot, true); }

if (args.Length > 0 && !args.Contains("--mod-policy-only"))
{
    var actual = ClientOnlyModCatalog.Read([args[0]]);
    string[] previouslyRejected = ["betterentityinteraction", "betterhandbook", "chineselinebreak", "fontsettings", "pinyinsearch"];
    foreach (var id in previouslyRejected)
        Check(actual.Any(rule => rule.ModId == id), $"Installed client-only ZIP missing from server directory catalog: {id}");
    Console.WriteLine($"Server directory catalog: {actual.Count} client-only mods; all five installed compatibility ZIPs found.");

}

var config = new VscnConfig(); config.Validate();
Check(JsonConvert.SerializeObject(config).Contains("Whitelist"), "Config enums must serialize as names.");
config.ModPolicy.Mods.Add(new ModRule { ModId = "example", Version = "1.0" });
config.Validate();
config.ModPolicy.Mods[0].Version = "*";
config.Validate();
var wildcardConfig = JsonConvert.DeserializeObject<VscnConfig>(JsonConvert.SerializeObject(config))!;
wildcardConfig.Validate();
Check(wildcardConfig.ModPolicy.Mods[0].Version == "*", "Wildcard version must survive configuration serialization.");
foreach (var emptyVersion in new string[] { "", " ", null! })
{
    config.ModPolicy.Mods[0].Version = emptyVersion;
    bool rejected = false;
    try { config.Validate(); } catch (ArgumentException) { rejected = true; }
    Check(rejected, "Empty versions must still be rejected; use '*' explicitly.");
}
config.ModPolicy.Mods[0].Version = "*";
config.ModPolicy.Mods[0].ModId = "";
bool emptyIdRejected = false;
try { config.Validate(); } catch (ArgumentException) { emptyIdRejected = true; }
Check(emptyIdRejected, "Wildcard version must not permit an empty mod ID.");
config.ModPolicy.Mods[0].ModId = "example";

if (args.Contains("--mod-policy-only"))
{
    Console.WriteLine($"PASS: {checks} checks; mod policy configuration, audit and handshake validated.");
    return;
}

// Exercise actual persistence hooks against an old ClusterSpawns payload.
var saved = new Dictionary<string, byte[]>
{
    ["clusterSpawnsState"] = System.Text.Encoding.UTF8.GetBytes("{\"Clusters\":[{\"X\":511000,\"Y\":120,\"Z\":512000}],\"CurrentActiveIndex\":0,\"CurrentClusterPlayerCount\":9,\"ClusterPlayerCounts\":{\"0\":1},\"PlayerAssignments\":{\"oldplayer\":0}}"),
    ["vscncsAnnouncement"] = System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new AnnouncementState { Html = "<p>旧公告</p>", Revision = 1 })),
    ["vscncsTutorial"] = System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new TutorialState
    { Revision = 1, Entries = [new AnnouncementEntry { Id = "legacy-tutorial", Title = "旧教程", Html = "<p>教程正文</p>" }] }))
};
var saveApi = Proxy.Make<ISaveGame>((m,a) => m.Name switch { "GetData" => saved.GetValueOrDefault((string)a![0]!), "StoreData" => Store(a!), _ => null });
object? Store(object?[] a) { saved[(string)a[0]!] = (byte[])a[1]!; return null; }
var manager = Proxy.Make<IWorldManagerAPI>((m,a) => m.Name == "get_SaveGame" ? saveApi : null);
var loader = Proxy.Make<IModLoader>((m,a) => m.Name == "IsModEnabled" ? false : null);
var logger = Proxy.Make<ILogger>((m,a) => null);
string contentDataPath = Path.Combine(Path.GetTempPath(), "vscncs-integrated-content-" + Guid.NewGuid().ToString("N"));
AppDomain.CurrentDomain.ProcessExit += (_, _) => { if (Directory.Exists(contentDataPath)) Directory.Delete(contentDataPath, recursive: true); };
var worldAccessor = Proxy.Make<IServerWorldAccessor>((m,a) => m.Name == "get_SavegameIdentifier" ? "checks-save" : null);
var sapi = Proxy.Make<ICoreServerAPI>((m,a) => m.Name switch
{
    "get_WorldManager" => manager, "get_ModLoader" => loader, "get_Logger" => logger,
    "get_World" => worldAccessor,
    "GetOrCreateDataPath" => contentDataPath,
    _ => null
});
var system = new VscnCivilizationModSystem(); Set(system, "sapi", sapi);
Invoke(system, "LoadState"); Invoke(system, "InitializeClusters"); Invoke(system, "SaveState");
var migratedContent = new ModDataContentStore(contentDataPath, "checks-save");
Check(JsonConvert.DeserializeObject<AnnouncementState>(File.ReadAllText(migratedContent.AnnouncementsPath))!.Entries!.Single().Html == "<p>旧公告</p>"
    && JsonConvert.DeserializeObject<TutorialState>(File.ReadAllText(migratedContent.TutorialsPath))!.Entries.Single().Id == "legacy-tutorial"
    && saved["vscncsAnnouncement"].Length == 0 && saved["vscncsTutorial"].Length == 0,
    "Legacy announcement and tutorial data must migrate to ModData before old save data is cleared.");
var inherited = JsonConvert.DeserializeObject<VscnState>(System.Text.Encoding.UTF8.GetString(saved["clusterSpawnsState"]))!;
Check(inherited.Clusters[0].X == 511000 && inherited.PlayerAssignments["oldplayer"] == 0 && inherited.CurrentClusterPlayerCount == 9, "Legacy coordinates, assignments and sequential counters must remain intact.");

// Searching for a safe landing must not consume a sequential slot if every candidate fails.
system.State.Clusters.Add(new ClusterPoint { X = 513000, Y = 120, Z = 512000 });
system.Config.Clusters.MaxPlayersPerCluster = 10;
Check((int)Invoke(system, "ChooseCluster")! == 0 && system.State.CurrentClusterPlayerCount == 9,
    "Choosing a cluster before landing changed the saved sequential count.");
Invoke(system, "CommitClusterAssignment", 0, false);
Check(system.State.CurrentClusterPlayerCount == 10 && (int)Invoke(system, "ChooseCluster")! == 1,
    "A successful landing did not advance the sequential cursor at capacity.");
Invoke(system, "CommitClusterAssignment", 1, false);
Check(system.State.CurrentActiveIndex == 1 && system.State.CurrentClusterPlayerCount == 1,
    "The next successful cluster assignment did not commit its cursor.");
Invoke(system, "CommitClusterAssignment", 0, true);
Check(system.State.CurrentActiveIndex == 1 && system.State.CurrentClusterPlayerCount == 1,
    "An administrator's fixed-cluster assignment changed sequential rotation.");
system.State.CurrentActiveIndex = 0; system.State.CurrentClusterPlayerCount = 9;
var reservations = (Dictionary<string, (int Index, long Order)>)system.GetType()
    .GetField("pendingSequentialAssignments", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(system)!;
reservations["in-flight"] = (0, 1);
Check((int)Invoke(system, "ChooseCluster")! == 1 && system.State.CurrentClusterPlayerCount == 9,
    "A concurrent join overfilled the sequential cluster or persisted an unlanded reservation.");
Invoke(system, "InvalidatePendingSpawn", "in-flight");
Check((int)Invoke(system, "ChooseCluster")! == 0,
    "Cancelling a pending spawn did not release its sequential slot.");

int terrainY = 110;
bool solidTop = true, blockedHead = false, wetFeet = false;
var airBlock = new Block { Replaceable = 10000 };
var solidBlock = new Block { Replaceable = 0 };
var liquidBlock = new Block { BlockMaterial = EnumBlockMaterial.Water, MatterState = EnumMatterState.Liquid };
var landingBlocks = Proxy.Make<IBlockAccessor>((m, a) => m.Name switch
{
    "GetTerrainMapheightAt" => terrainY,
    "IsSideSolid" => solidTop,
    "GetBlock" when a?.Length == 2 => wetFeet && ((Vintagestory.API.MathTools.BlockPos)a[0]!).Y == terrainY + 1 ? liquidBlock : airBlock,
    "GetBlock" => blockedHead && ((Vintagestory.API.MathTools.BlockPos)a![0]!).Y == terrainY + 2 ? solidBlock : airBlock,
    _ => null
});
var landingWorld = Proxy.Make<IServerWorldAccessor>((m, a) => m.Name switch { "get_BlockAccessor" => landingBlocks, "get_SeaLevel" => 110, _ => null });
var landingManager = Proxy.Make<IWorldManagerAPI>((m, a) => m.Name == "get_MapSizeY" ? 384 : null);
var landingApi = Proxy.Make<ICoreServerAPI>((m, a) => m.Name switch { "get_World" => landingWorld, "get_WorldManager" => landingManager, _ => null });
var landingSystem = new VscnCivilizationModSystem(); Set(landingSystem, "sapi", landingApi);
Check((int?)Invoke(landingSystem, "SafeLandingY", 100, 100) == 111, "A dry, solid surface at sea level was rejected.");
solidTop = false;
Check(Invoke(landingSystem, "SafeLandingY", 100, 100) == null, "Unsupported ground was accepted.");
solidTop = true; blockedHead = true;
Check(Invoke(landingSystem, "SafeLandingY", 100, 100) == null, "Blocked head space was accepted.");
blockedHead = false; wetFeet = true;
Check(Invoke(landingSystem, "SafeLandingY", 100, 100) == null, "Liquid at foot level was accepted.");
wetFeet = false; terrainY = 382;
Check(Invoke(landingSystem, "SafeLandingY", 100, 100) == null, "A surface at world height limit was accepted.");

// ClusterSpawns assigns at PlayerNowPlaying. Keep the validation gate, but do not wait
// for PlayerReady (which occurs after character selection and delays new players).
var newcomer = Proxy.Make<IServerPlayer>((m,a) => m.Name switch { "get_PlayerUID" => "newcomer", "get_PlayerName" => "Newcomer", _ => null });
var readySet = (HashSet<string>)system.GetType().GetField("readyPlayers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(system)!;
var verifiedSet = (HashSet<string>)system.GetType().GetField("verifiedPlayers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(system)!;
var pendingSpawnSet = (Dictionary<string, Guid>)system.GetType().GetField("pendingSpawns", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(system)!;
Invoke(system, "AssignIfNeeded", newcomer);
Check(!pendingSpawnSet.ContainsKey("newcomer"), "Spawn started before PlayerNowPlaying and validation.");
verifiedSet.Add("newcomer"); Invoke(system, "AssignIfNeeded", newcomer);
Check(!pendingSpawnSet.ContainsKey("newcomer"), "Spawn started before PlayerNowPlaying.");
verifiedSet.Remove("newcomer"); readySet.Add("newcomer"); Invoke(system, "AssignIfNeeded", newcomer);
Check(!pendingSpawnSet.ContainsKey("newcomer"), "Spawn started before mod validation completed.");
verifiedSet.Add("newcomer");
Set(system, "tribeState", new TribeState { Tribes = [new TribeRecord { Id = "existing", LeaderUid = "newcomer", Members = ["newcomer"] }] });
Invoke(system, "AssignIfNeeded", newcomer);
Check(!pendingSpawnSet.ContainsKey("newcomer"), "An existing tribe member was sent to a spawn settlement because their old assignment was missing.");
Set(system, "tribeState", new TribeState());
readySet.Remove("newcomer");

// Run the server's real join/reset handlers, not only the allowance data class.
int broadcasts = 0;
var dataProxy = Proxy.Make<IWorldPlayerData>((m,a) => null);
var deaths = (Proxy)(object)dataProxy;
deaths.Deaths = 7;
var player = Proxy.Make<IServerPlayer>((m,a) => m.Name switch
{
    "get_PlayerUID" => "joined", "get_PlayerName" => "JoinedPlayer", "get_WorldData" => dataProxy,
    "BroadcastPlayerData" => Broadcast(), _ => null
});
object? Broadcast() { broadcasts++; return null; }
var allowances = new ResetState(); allowances.Start(1); Set(system, "resetState", allowances);
system.Config.ModPolicy.Enabled = false;
Invoke(system, "OnJoin", player);
Check(deaths.Deaths == 0 && broadcasts == 0, "Join reset must wait for the engine's first inventory-bearing player data packet.");
deaths.Deaths = 3; Invoke(system, "OnJoin", player);
Check(deaths.Deaths == 3 && broadcasts == 0, "A second join must not reset a consumed allowance.");
allowances.Start(2); Invoke(system, "OnJoin", player); deaths.Deaths = 4; Invoke(system, "OnJoin", player);
Check(deaths.Deaths == 0 && broadcasts == 0, "n=2 must reset the same player on two joins without an early broadcast.");
deaths.Deaths = 4; Invoke(system, "OnJoin", player); Check(deaths.Deaths == 4, "n=2 must not reset on a third join.");
Invoke(system, "ResetDeaths", player, true);
Check(deaths.Deaths == 0 && broadcasts == 1, "Online reset must still synchronize player data immediately.");
system.Config.ModPolicy.Enabled = true;

// Validate permissions with the actual command dispatcher.
var normalPlayer = Proxy.Make<IServerPlayer>((m,a) => m.Name switch { "get_PlayerUID" => "normal", "HasPrivilege" => false, _ => null });
var parser = Proxy.Make<ICommandArgumentParser>((m,a) => m.Name == "GetValue" ? "resetall" : null);
var denied = (TextCommandResult)Invoke(system, "HandleCommand", new TextCommandCallingArgs { Caller = new Caller { Player = normalPlayer }, Parsers = [parser] })!;
Check(denied.Status == EnumCommandStatus.Error, "Normal player can run resetall.");
var viewParser = Proxy.Make<ICommandArgumentParser>((m,a) => m.Name == "GetValue" ? "cluster view" : null);
var deniedView = (TextCommandResult)Invoke(system, "HandleCommand", new TextCommandCallingArgs { Caller = new Caller { Player = normalPlayer }, Parsers = [viewParser] })!;
Check(deniedView.Status == EnumCommandStatus.Error, "Normal player can obtain the administrator cluster packet.");
foreach (string command in new[] { "tribe list", "tribe delete 任意部落" })
{
    var tribeParser = Proxy.Make<ICommandArgumentParser>((m, a) => m.Name == "GetValue" ? command : null);
    var deniedTribe = (TextCommandResult)Invoke(system, "HandleCommand", new TextCommandCallingArgs
    { Caller = new Caller { Player = normalPlayer }, Parsers = [tribeParser] })!;
    Check(deniedTribe.Status == EnumCommandStatus.Error, "Normal player can use administrator tribe commands.");
}

// Stale challenges and payload-claimed identities must not authorize a player.
Set(system, "channel", Proxy.Make<IServerNetworkChannel>((m,a) => null));
Set(system, "serverRules", server.ToList()); Set(system, "coreVersions", core);
var pending = new Dictionary<string,string> { ["joined"] = "expected" }; Set(system, "pendingReports", pending);
Invoke(system, "OnClientModReport", player, new ClientModReportPacket { Nonce = "stale", Mods = [self] });
Check(pending.ContainsKey("joined"), "A stale report consumed the current challenge.");
Invoke(system, "OnClientModReport", player, new ClientModReportPacket { Nonce = "expected", PlayerUid = "forgeduid", Mods = [self] });
var verified = (HashSet<string>)system.GetType().GetField("verifiedPlayers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(system)!;
Check(verified.Contains("joined") && !verified.Contains("forgeduid"), "Report identity must come from the authenticated connection.");

// Verify damage/healing and expiration through the real protection behavior.
long now = 100;
var world = Proxy.Make<IWorldAccessor>((m,a) => m.Name == "get_ElapsedMilliseconds" ? now : null);
var entity = new EntityPlayer { World = world };
var protection = new VscnRespawnBehavior(entity); Invoke(protection, "Bind", system); protection.OnEntityRevive();
var damageSource = new DamageSource { Type = EnumDamageType.Gravity, IgnoreInvFrames = true };
float damage = 4; protection.OnEntityReceiveDamage(damageSource, ref damage); Check(damage == 0, "IgnoreInvFrames damage bypassed protection.");
float healing = 4; protection.OnEntityReceiveDamage(new DamageSource { Type = EnumDamageType.Heal }, ref healing); Check(healing == 4, "Revival healing must not be blocked.");
now += 10001; damage = 4; protection.OnEntityReceiveDamage(damageSource, ref damage); Check(damage == 4, "Protection failed to expire.");

RectangularTerritoryChecks.Run(Check);
BuildingTerritoryChecks.Run(Check);
RespawnChecks.Run(Check);
GuidanceChecks.Run(Check);

using (var stream = new MemoryStream()) { Serializer.Serialize(stream, new ClientModReportPacket { Nonce = "challenge", Mods = [self] }); stream.Position = 0; var read = Serializer.Deserialize<ClientModReportPacket>(stream); Check(read.Nonce == "challenge" && read.Mods[0].Version == "1.0.4", "Protocol roundtrip failed."); }

var tribeState = new TribeState { Tribes = [new TribeRecord { Id = "a", Name = "A", LeaderUid = "leader", Members = ["leader", "member", "other", "third"] }] };
tribeState.RemoveMember(tribeState.Tribes[0], "leader");
Check(tribeState.Tribes.Count == 1 && tribeState.Tribes[0].LeaderUid == "member", "Leader departure must promote a remaining member.");
tribeState.RemoveMember(tribeState.Tribes[0], "other");
Check(tribeState.Tribes.Count == 1 && tribeState.Tribes[0].Members.Count == 2, "A two-member tribe must remain active.");
tribeState.RemoveMember(tribeState.Tribes[0], "third");
Check(tribeState.Tribes.Count == 0, "A tribe must dissolve when only one member remains.");
var territoryConfig = new TerritoryConfig();
Check(territoryConfig.OutpostLimit(2) == 0 && territoryConfig.OutpostLimit(3) == 0 && territoryConfig.OutpostLimit(4) == 1 && territoryConfig.OutpostLimit(5) == 1 && territoryConfig.OutpostLimit(6) == 2 && territoryConfig.OutpostLimit(100) == 2, "Outpost limits must follow tribe membership thresholds.");
territoryConfig.FirstOutpostMembers = 2; territoryConfig.SecondOutpostMembers = 3; territoryConfig.MaxOutposts = 1;
Check(territoryConfig.OutpostLimit(2) == 1 && territoryConfig.OutpostLimit(3) == 1, "Configured outpost thresholds and cap were ignored.");
territoryConfig.HealthBonus = float.NaN;
try { territoryConfig.Validate(); Check(false, "NaN territory modifier was accepted."); } catch (ArgumentException) { Check(true, "NaN rejected."); }

var territorySystem = new VscnCivilizationModSystem();
var territoryTribe = new TribeRecord { Id = "home", LeaderUid = "chief", Members = ["chief"], X = 100, Y = 110, Z = 100,
    Outposts = [new() { X = 300, Y = 110, Z = 300, Dimension = 1 }, new() { X = 600, Y = 110, Z = 600 }] };
var territoryState = new TribeState { Tribes = [territoryTribe] };
Set(territorySystem, "tribeState", territoryState);
TerritoryTestWorld.Region(territoryTribe);
var outpostFixture = new TribeRecord { X = 300, Y = 110, Z = 300, Dimension = 1 };
territoryTribe.Regions.Add(TerritoryTestWorld.Region(outpostFixture, site: territoryTribe.Outposts[0].Id));
TerritoryTestWorld.Index(territorySystem);
Check((bool)Invoke(territorySystem, "WithinTerritory", territoryTribe, 101d, 110d, 101d, 0)!, "Registered room missing.");
Check(!(bool)Invoke(territorySystem, "WithinTerritory", territoryTribe, 130d, 110d, 140d, 0)!, "Legacy circle remained active.");
Check(!(bool)Invoke(territorySystem, "WithinTerritory", territoryTribe, 101d, 130d, 101d, 0)!, "Room protected unrelated storey.");
Check(!(bool)Invoke(territorySystem, "WithinTerritory", territoryTribe, 101d, 110d, 101d, 1)!, "Territory crossed dimensions.");
Check((bool)Invoke(territorySystem, "WithinTerritory", territoryTribe, 301d, 110d, 301d, 1)!, "Outpost room did not count as home.");
var emptyWorld = Proxy.Make<IServerWorldAccessor>((m, a) => m.Name == "get_AllOnlinePlayers" ? Array.Empty<IPlayer>() : null);
var territoryApi = Proxy.Make<ICoreServerAPI>((m, a) => m.Name switch { "get_World" => emptyWorld, "get_WorldManager" => manager, "get_ModLoader" => loader, _ => null });
Set(territorySystem, "sapi", territoryApi);
Invoke(territorySystem, "RefreshCivilizationViews");
Check(territoryTribe.Outposts.Count == 2, "Population reduction must preserve confirmed outposts.");
// Exercise leader authorization and migration cooldown through the real handlers.
Vintagestory.API.Config.Lang.AvailableLanguages["en"] = Proxy.Make<Vintagestory.API.Config.ITranslationService>((m, a) => m.Name.StartsWith("Get") ? a?[0] : null);
Vintagestory.API.Config.Lang.ChangeLanguage("en");
ServerAnnouncementChecks.Run(Check);
var chiefEntity = new EntityPlayer(); chiefEntity.Pos.SetPos(900, 110, 900);
chiefEntity.Stats = new EntityStats(chiefEntity);
var recruitEntity = new EntityPlayer(); recruitEntity.Pos.SetPos(901, 110, 900);
recruitEntity.Stats = new EntityStats(recruitEntity);
var chief = Proxy.Make<IServerPlayer>((m, a) => m.Name switch
{ "get_PlayerUID" => "chief", "get_PlayerName" => "Chief", "get_Entity" => chiefEntity, "get_LanguageCode" => "en", _ => null });
var recruit = Proxy.Make<IServerPlayer>((m, a) => m.Name switch
{ "get_PlayerUID" => "recruit", "get_PlayerName" => "Recruit", "get_Entity" => recruitEntity, "get_LanguageCode" => "en", _ => null });
var chiefWorld = Proxy.Make<IServerWorldAccessor>((m, a) => m.Name switch
{ "get_AllOnlinePlayers" => new IPlayer[] { chief, recruit }, "get_Side" => EnumAppSide.Server, _ => null });
foreach (var territoryEntity in new[] { chiefEntity, recruitEntity })
{
    territoryEntity.World = chiefWorld;
    HarmonyLib.AccessTools.Property(typeof(Entity), "Properties").SetValue(territoryEntity,
        new EntityProperties { Server = new EntityServerProperties([], new()) });
}
var chiefApi = Proxy.Make<ICoreServerAPI>((m, a) => m.Name switch
{ "get_World" => chiefWorld, "get_WorldManager" => manager, "get_ModLoader" => loader, _ => null });
var territoryWorld = new TerritoryTestWorld(chiefApi);
foreach (int coord in new[] { 900, 905, 1200, 1400 }) territoryWorld.Box(coord, 110, coord);
territoryWorld.Attach(territorySystem);
void TerritoryAction(string action)
{
    Invoke(territorySystem, action, chief);
    var preview = (CivilizationSnapshotPacket)Invoke(territorySystem, "BuildCivilizationSnapshot", chief, false, "", "", false)!;
    if (preview.TerritoryCandidates.FirstOrDefault() is { } candidate)
    {
        Invoke(territorySystem, "ConfirmTerritory", chief, candidate.Id);
    }
}
CivilizationSnapshotPacket? foundingSnapshot = null;
Set(territorySystem, "channel", Proxy.Make<IServerNetworkChannel>((m, a) =>
{
    if (m.Name == "SendPacket" && a?[0] is CivilizationSnapshotPacket sent) foundingSnapshot = sent;
    return null;
}));
territoryState.Tribes.Clear();
recruitEntity.Pos.SetPos(1000, 110, 1000);
Invoke(territorySystem, "CreateTribe", chief, "新部落");
Check(territoryState.Tribes.Count == 0 && foundingSnapshot == null,
    "Founding without another nearby player must not create a tribe or invitation.");
recruitEntity.Pos.SetPos(901, 110, 900);
Invoke(territorySystem, "CreateTribe", chief, "新部落");
Check(territoryState.Tribes.Count == 0 && foundingSnapshot!.Invitations.Count == 1,
    "A founding invitation must not create a one-member tribe.");
Invoke(territorySystem, "RespondInvite", recruit, foundingSnapshot!.Invitations[0].TribeId, false);
Check(territoryState.Tribes.Count == 0, "Declining the founding invitation must leave no tribe.");
Invoke(territorySystem, "CreateTribe", chief, "新部落");
Invoke(territorySystem, "RespondInvite", recruit, foundingSnapshot!.Invitations[0].TribeId, true);
var createdTribe = territoryState.FindMember("chief")!;
Check(createdTribe != null && createdTribe.LeaderUid == "chief" && createdTribe.Members.Count == 2 && createdTribe.X == 900,
    "The first accepted invitation must form a two-member tribe at the founder's original position.");
chiefEntity.Pos.SetPos(905, 110, 905);
TerritoryAction("MoveCenter");
Check(createdTribe!.X == 900, "A newly created territory bypassed migration cooldown.");
territorySystem.Config.Territory.CenterCooldownMinutes = 0;
TerritoryAction("MoveCenter");
Check(createdTribe.X == 905, "Hot-reloaded cooldown did not allow migration.");
chiefEntity.Pos.SetPos(1400, 110, 1400);
TerritoryAction("CreateOutpost");
Check(createdTribe.Outposts.Count == 0, "A two-member tribe created an outpost before the threshold.");
var nonLeader = Proxy.Make<IServerPlayer>((m, a) => m.Name switch
{ "get_PlayerUID" => "member", "get_Entity" => chiefEntity, "get_LanguageCode" => "en", _ => null });
createdTribe.Members.AddRange(["member", "three", "four"]);
Invoke(territorySystem, "CreateOutpost", nonLeader);
Invoke(territorySystem, "MoveCenter", nonLeader);
Check(createdTribe.Outposts.Count == 0 && createdTribe.X == 905, "Nonleader altered territory.");
createdTribe.Members.RemoveRange(2, 3);
territorySystem.Config.Territory.FirstOutpostMembers = 1;
TerritoryAction("CreateOutpost");
Check(createdTribe.Outposts.Count == 0, "Removed outpost creation must not remain accessible.");

chiefEntity.Pos.SetPos(905,110,905);
// Actual outgoing packets must expose only the current tribe and its own world markers.
CivilizationSnapshotPacket? markerSnapshot = null;
Set(territorySystem, "channel", Proxy.Make<IServerNetworkChannel>((m, a) =>
{
    if (m.Name == "SendPacket" && a?[0] is CivilizationSnapshotPacket sent) markerSnapshot = sent;
    return null;
}));
var foreignTribe = new TribeRecord { Id = "foreign", Name = "Foreign", LeaderUid = "foreign-chief", Members = ["foreign-chief"],
    X = 9000, Z = 9000, Outposts = [new() { X = 9500, Z = 9500 }] };
territoryState.Tribes.Add(foreignTribe);
Invoke(territorySystem, "SendCivilizationSnapshot", chief, false, "", "", false);
Check(markerSnapshot!.Territories.Count == 1 && markerSnapshot.Territories[0].X == 905,
    "Member snapshots must contain only their own center and outpost world markers.");
Check(markerSnapshot.MyTribeId == createdTribe.Id && markerSnapshot.Members.All(p => p.Uid != "foreign-chief"),
    "The tribe-only snapshot must not expose foreign tribes or members.");
Check(markerSnapshot.Boundaries.Count > 0 && markerSnapshot.Boundaries.All(b => b.Edges.Count > 0 && b.Cells.Count == 0 && b.TribeId == createdTribe.Id),
    "Boundary packets must contain actual polygon edges without legacy room geometry.");
Invoke(territorySystem, "SendCivilizationSnapshot", nonLeader, false, "", "", false);
Check(markerSnapshot!.Territories.Count == 0, "Nonmembers must not receive own-site markers.");
Check(markerSnapshot.Boundaries.Count > 0 && markerSnapshot.BoundaryDisplayEnabled, "Nearby building boundaries should remain visible to nonmembers.");
territorySystem.Config.Territory.BoundaryDisplayEnabled = false;
Invoke(territorySystem, "SendCivilizationSnapshot", chief, false, "", "", false);
Check(markerSnapshot!.Boundaries.Count == 0 && !markerSnapshot.BoundaryDisplayEnabled,
    "Disabling the territory boundary setting must stop boundary synchronization.");
territorySystem.Config.Territory.BoundaryDisplayEnabled = true;
createdTribe.Members.Remove("chief");
Invoke(territorySystem, "SendCivilizationSnapshot", chief, false, "", "", false);
Check(markerSnapshot!.Territories.Count == 0, "A former member must receive an empty marker list to clear stale icons.");
createdTribe.Members.Add("chief");
territoryState.Tribes.Remove(foreignTribe);

TextCommandResult DebugCommand(string command, IServerPlayer who, bool admin = true)
{
    var commandParser = Proxy.Make<ICommandArgumentParser>((m, a) => m.Name == "GetValue" ? "territory debug " + command : null);
    return (TextCommandResult)Invoke(territorySystem, "HandleCommand", new TextCommandCallingArgs
    { Caller = new Caller { Player = who, Type = admin ? EnumCallerType.Console : EnumCallerType.Player }, Parsers = [commandParser] })!;
}
Check(DebugCommand("home", normalPlayer, false).Status == EnumCommandStatus.Error, "Nonadmin can enable territory debug effects.");
var debugHomeResult = DebugCommand("home", chief);
Check(debugHomeResult.Status == EnumCommandStatus.Success && Math.Abs(chiefEntity.Stats.GetBlended("walkspeed") - 1f) < .001f,
    "Debug home mode did not apply the real movement stat: " + debugHomeResult.StatusMessage);
Check(DebugCommand("away", chief).Status == EnumCommandStatus.Success && Math.Abs(chiefEntity.Stats.GetBlended("walkspeed") - 1) < .001f,
    "Debug away mode retained the home speed bonus.");
Check(DebugCommand("members 6", chief).Status == EnumCommandStatus.Error && createdTribe.Members.Count == 2 && markerSnapshot!.OutpostLimit == 0,
    "Outpost testing must override thresholds without fake saved members.");
Check(DebugCommand("set HealthBonus 2", chief).Status == EnumCommandStatus.Success, "Debug config setter failed.");
DebugCommand("home", chief);
Check(Math.Abs(chiefEntity.Stats.GetBlended("maxhealthExtraPoints") - 3f) < .001f,
    "Debug config setter did not immediately apply the retained health bonus.");
Check(DebugCommand("set HealthBonus NaN", chief).Status == EnumCommandStatus.Error && territorySystem.Config.Territory.HealthBonus == 2,
    "Invalid debug config changed live settings.");
DebugCommand("set HealthBonus 0", chief);
DebugCommand("reset", chief);
Check(markerSnapshot!.OutpostLimit == 0
    && createdTribe.Outposts.Count <= markerSnapshot.OutpostLimit, "Debug reset did not restore real member limits.");

Invoke(territorySystem, "LeaveTribe", recruit);
Check(territoryState.Tribes.Count == 0
    && JsonConvert.DeserializeObject<TribeState>(System.Text.Encoding.UTF8.GetString(saved["vscncsTribeState"]))!.Tribes.Count == 0,
    "Leaving a two-member tribe must dissolve it and persist the removal.");
territoryState.Tribes.Add(new TribeRecord { Id = "kick-test", Name = "Kick", LeaderUid = "chief", Members = ["chief", "recruit"] });
Invoke(territorySystem, "KickMember", chief, "recruit");
Check(territoryState.Tribes.Count == 0, "Kicking from a two-member tribe must dissolve it.");
var destinationChiefEntity = new EntityPlayer(); destinationChiefEntity.Pos.SetPos(2000, 110, 2000);
destinationChiefEntity.Stats = new EntityStats(destinationChiefEntity);
var destinationMemberEntity = new EntityPlayer(); destinationMemberEntity.Pos.SetPos(2001, 110, 2000);
destinationMemberEntity.Stats = new EntityStats(destinationMemberEntity);
var destinationChief = Proxy.Make<IServerPlayer>((m, a) => m.Name switch
{ "get_PlayerUID" => "destination-chief", "get_PlayerName" => "Destination Chief", "get_Entity" => destinationChiefEntity, "get_LanguageCode" => "en", _ => null });
var destinationMember = Proxy.Make<IServerPlayer>((m, a) => m.Name switch
{ "get_PlayerUID" => "destination-member", "get_PlayerName" => "Destination Member", "get_Entity" => destinationMemberEntity, "get_LanguageCode" => "en", _ => null });
var transferWorld = Proxy.Make<IServerWorldAccessor>((m, a) => m.Name == "get_AllOnlinePlayers"
    ? new IPlayer[] { chief, recruit, destinationChief, destinationMember } : null);
var transferApi = Proxy.Make<ICoreServerAPI>((m, a) => m.Name switch
{ "get_World" => transferWorld, "get_WorldManager" => manager, "get_ModLoader" => loader, _ => null });
Set(territorySystem, "sapi", transferApi);
territoryState.Tribes.Add(new TribeRecord { Id = "source", Name = "Source", LeaderUid = "chief", Members = ["chief", "recruit"], X = 900, Z = 900 });
territoryState.Tribes.Add(new TribeRecord { Id = "destination", Name = "Destination", LeaderUid = "destination-chief",
    Members = ["destination-chief", "destination-member"], X = 2000, Z = 2000 });
recruitEntity.Pos.SetPos(2001, 110, 2000);
foreach (string removedAction in new[] { "transfer", "approve", "reject" })
{
    Invoke(territorySystem, "OnCivilizationRequest", recruit, new CivilizationRequestPacket { Action = removedAction, Target = "destination" });
    Check(territoryState.Find("source")!.Members.Contains("recruit") && !territoryState.Find("destination")!.Members.Contains("recruit")
        && markerSnapshot!.Message.EndsWith("ui.invalid-action"), "Removed membership-request packets must be rejected without changing membership.");
}
Invoke(territorySystem, "LeaveTribe", recruit);
Invoke(territorySystem, "InviteNearby", destinationChief);
Invoke(territorySystem, "RespondInvite", recruit, "destination", true);
Check(territoryState.Find("source") == null && territoryState.Find("destination")!.Members.Contains("recruit"),
    "Leaving a two-member tribe must dissolve it; an invitation can then join another tribe.");

var extraEntity = new EntityPlayer(); extraEntity.Pos.SetPos(450, 110, 475);
extraEntity.Stats = new EntityStats(extraEntity);
var extraPlayer = Proxy.Make<IServerPlayer>((m, a) => m.Name switch
{ "get_PlayerUID" => "extra", "get_PlayerName" => "Extra", "get_Entity" => extraEntity, "get_LanguageCode" => "en", _ => null });
var outsiderEntity = new EntityPlayer(); outsiderEntity.Pos.SetPos(2002, 110, 2000);
outsiderEntity.Stats = new EntityStats(outsiderEntity);
var outsider = Proxy.Make<IServerPlayer>((m, a) => m.Name switch
{ "get_PlayerUID" => "outsider", "get_PlayerName" => "Outsider", "get_Entity" => outsiderEntity, "get_LanguageCode" => "en", _ => null });
var adminSpawn = new EntityPos(); adminSpawn.SetPos(500, 110, 500);
var adminWorld = Proxy.Make<IServerWorldAccessor>((m, a) => m.Name switch
{
    "get_AllOnlinePlayers" => new IPlayer[] { chief, recruit, destinationChief, destinationMember, extraPlayer, outsider },
    "get_DefaultSpawnPosition" => adminSpawn,
    _ => null
});
var adminApi = Proxy.Make<ICoreServerAPI>((m, a) => m.Name switch
{ "get_World" => adminWorld, "get_WorldManager" => manager, "get_ModLoader" => loader, _ => null });
Set(territorySystem, "sapi", adminApi);
territoryState.Tribes.Add(new TribeRecord { Id = "other", Name = "另一部落", LeaderUid = "chief",
    Members = ["chief", "extra"], X = 450, Y = 99, Z = 475 });
TextCommandResult AdminTribeCommand(string command)
{
    var commandParser = Proxy.Make<ICommandArgumentParser>((m, a) => m.Name == "GetValue" ? "tribe " + command : null);
    return (TextCommandResult)Invoke(territorySystem, "HandleCommand", new TextCommandCallingArgs
    { Caller = new Caller { Type = EnumCallerType.Console }, Parsers = [commandParser] })!;
}
var tribeList = AdminTribeCommand("list");
Check(tribeList.Status == EnumCommandStatus.Success && tribeList.StatusMessage.Contains("Destination")
    && tribeList.StatusMessage.Contains("人数=3") && tribeList.StatusMessage.Contains("相对 X=1500, Z=1500")
    && tribeList.StatusMessage.Contains("另一部落") && tribeList.StatusMessage.Contains("相对 X=-50, Z=-25")
    && tribeList.StatusMessage.Contains("Y=99") && tribeList.StatusMessage.Contains("维度=0"),
    "Tribe list must show every tribe with membership and world-spawn-relative coordinates.");
var inGameAdmin = Proxy.Make<IServerPlayer>((m, a) => m.Name == "HasPrivilege" ? true : null);
var inGameListParser = Proxy.Make<ICommandArgumentParser>((m, a) => m.Name == "GetValue" ? "tribe list" : null);
var inGameList = (TextCommandResult)Invoke(territorySystem, "HandleCommand", new TextCommandCallingArgs
{ Caller = new Caller { Player = inGameAdmin }, Parsers = [inGameListParser] })!;
Check(inGameList.Status == EnumCommandStatus.Success, "A player with controlserver privilege cannot list tribes.");
Check(AdminTribeCommand("delete").Status == EnumCommandStatus.Error
    && AdminTribeCommand("delete 不存在").Status == EnumCommandStatus.Error && territoryState.Tribes.Count == 2,
    "Missing or unknown tribe names must not delete anything.");
var refreshedSnapshots = new Dictionary<string, CivilizationSnapshotPacket>();
Set(territorySystem, "channel", Proxy.Make<IServerNetworkChannel>((m, a) =>
{
    if (m.Name == "SendPacket" && a?[0] is CivilizationSnapshotPacket snapshot
        && a[1] is IEnumerable<IServerPlayer> recipients)
        foreach (var recipient in recipients) refreshedSnapshots[recipient.PlayerUID] = snapshot;
    return null;
}));
Invoke(territorySystem, "InviteNearby", destinationChief);
var pendingTribeInvites = (System.Collections.ICollection)territorySystem.GetType()
    .GetField("pendingInvites", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(territorySystem)!;
Check(pendingTribeInvites.Count == 1, "The tribe invitation setup did not reach an eligible nearby player.");
var deletedTribe = AdminTribeCommand("delete Destination");
Check(deletedTribe.Status == EnumCommandStatus.Success && territoryState.Find("destination") == null
    && territoryState.Find("other") != null
    && JsonConvert.DeserializeObject<TribeState>(System.Text.Encoding.UTF8.GetString(saved["vscncsTribeState"]))!.Tribes.Single().Id == "other",
    "Deleting a tribe must persist removal without affecting other tribes.");
Check(pendingTribeInvites.Count == 0 && !AdminTribeCommand("list").StatusMessage.Contains("Destination"),
    "Deleting a tribe must clear its invitations and remove it from listings.");
Check(refreshedSnapshots["recruit"].MyTribeId == null && refreshedSnapshots["destination-chief"].Territories.Count == 0,
    "Deleting a tribe must clear former members' active tribe and territory views.");

saved["vscncsTribeState"] = System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new TribeState
{ Tribes = [new TribeRecord { Id = "legacy-solo", Name = "Legacy", LeaderUid = "chief", Members = ["chief"] }] }));
var legacySystem = new VscnCivilizationModSystem(); Set(legacySystem, "sapi", sapi);
Invoke(legacySystem, "LoadState");
Check(JsonConvert.DeserializeObject<TribeState>(System.Text.Encoding.UTF8.GetString(saved["vscncsTribeState"]))!.Tribes.Count == 0,
    "Loading an old one-member tribe must dissolve it and persist the cleanup.");

ArtistClassChecks.Run(Check);
EntryInteractionChecks.Run(Check);
WildlifeCompatibilityChecks.Run(Check);
UpdatesChecks.Run(Check);
ContentFilesChecks.Run(Check);
LoreRestrictionChecks.Run(Check);
SoloTribeChecks.Run(Check);
TribeAuthorizationChecks.Run(Check);
BuildingPlacementChecks.Run(Check);
EconomyChecks.Run(Check);
CivilizationPageChecks.Run(Check);
AnnouncementImageChecks.Run(Check);
AnnouncementMarkdownChecks.Run(Check);
AnnouncementHtmlChecks.Run(Check);
PaperMapStorageChecks.Run(Check);
LegacyMapRetirementChecks.Run(Check);
SleepInteractionChecks.Run(Check);
if (Environment.GetEnvironmentVariable("VSCN_ROPING_DLL") is { } ropingDll)
    TerritoryIntegrationChecks.Run(ropingDll, Check);
var bronze = TechnologyCatalog.Nodes.Single(n => n.Id == "bronzeanvil");
Check(TechnologyCatalog.CanComplete(bronze, new HashSet<string> { "game:anvil-tinbronze" }, new HashSet<string> { "tinbronze" }), "Either bronze branch should unlock its anvil.");
Check(!TechnologyCatalog.CanComplete(bronze, new HashSet<string> { "game:anvil-tinbronze" }, new HashSet<string>()), "Obtaining a product must not bypass prerequisites.");
var firing = TechnologyCatalog.Nodes.Single(n => n.Id == "firing");
Check(!TechnologyCatalog.CanComplete(firing, new HashSet<string> { "game:crucible-red-fired" }, new HashSet<string> { "clay", "fire" }), "A fired crucible alone must not finish the kiln stage.");
Check(TechnologyCatalog.CanComplete(firing, new HashSet<string> { "game:crucible-red-fired", "game:toolmold-red-fired-pickaxe" }, new HashSet<string> { "clay", "fire" }), "Both kiln outputs should finish the kiln stage.");
Check(TechnologyCatalog.Nodes.Any(n => n.Id == "pemmican" && n.Mod == "longtermfood") && TechnologyCatalog.Nodes.Any(n => n.Id == "quarry" && n.Mod == "stonequarryrepckfipil"), "Optional branch IDs must match installed mod manifests.");
var farm = TechnologyCatalog.Nodes.Single(n => n.Id == "farming");
var farmOutputs = new HashSet<string> { "game:vegetable-carrot" };
var farmPrevious = new HashSet<string> { "knapping" };
Check(!TechnologyCatalog.CanComplete(farm, farmOutputs, farmPrevious), "A traded crop cannot prove harvesting.");
Check(!TechnologyCatalog.CanComplete(farm, farmOutputs, farmPrevious, new HashSet<string> { "place:game:crop-carrot-1" }), "Sowing alone cannot prove a harvest.");
Check(TechnologyCatalog.CanComplete(farm, farmOutputs, farmPrevious, new HashSet<string> { "break:game:crop-carrot-7" }), "Harvesting and crop output should finish farming.");
Check(TechnologyCatalog.Matches("game:angledgears-*", "game:angledgears-s") && !TechnologyCatalog.Matches("game:angledgears-*", "game:angledgear-s"), "Mechanical pattern must match the actual plural block prefix.");
Check(typeof(GuiDialog).IsAssignableFrom(typeof(VscnClusterWindow)), "Civilization window must use the native game dialog.");
Check(!typeof(VscnClusterWindow).Assembly.GetReferencedAssemblies().Any(a => a.Name?.Contains("Rml", StringComparison.OrdinalIgnoreCase) == true),
    "Mod assembly must load without the removed GUI dependency.");
var localeRoot = System.IO.Path.GetFullPath("assets/vsscsmod/lang");
foreach (string file in new[] { "en.json", "zh-cn.json" })
{
    var translations = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(System.IO.Path.Combine(localeRoot, file)));
    foreach (string key in new[] { "cluster.join", "cluster.assigning", "cluster.landed", "cluster.reroll-broadcast", "cluster.infoall-title", "ui.civilization", "ui.tribes", "ui.create-tribe", "ui.invitations", "ui.invalid-action", "ui.create-solo-hint", "ui.solo-founded", "ui.left-dissolved" })
        Check(translations[key] != null, $"Missing {key} in {file}.");
}
StatusCopyChecks.Run(Check);
var modInfo = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText("modinfo.json"));
Check((string?)modInfo["modid"] == "vsscsmod" && (string?)modInfo["version"] == "1.0.0", "Package identity must match the standalone mod.");
foreach (string domain in new[] { "vsscsmod", "vscncivilizationsimulation" })
    foreach (string file in new[] { "blocktypes/groundmaps.json", "shapes/block/groundmaps.json" })
        Check(File.Exists(Path.Combine("assets", domain, file)), $"Missing ground map compatibility resource: {domain}/{file}");
Check(!Directory.Exists("assets/vscncivilizationsimulation/patches"), "Legacy resources must not apply patches twice.");
Check(typeof(VscnCivilizationModSystem).Assembly.GetTypes().All(t => !t.FullName!.Contains("BodyPhys", StringComparison.OrdinalIgnoreCase)), "Removed Body Physiology integration must not be present in the assembly.");
ClientLogUploadChecks.Run(Check);
TerritoryRendererChecks.Run(Check);
if (Environment.GetEnvironmentVariable("VSCN_NATIVE_GUI_SMOKE") == "1") NativeGuiChecks.Run(Check);
if (Environment.GetEnvironmentVariable("VSCN_IMMERSIVEMAPS_ZIP") is { } mapsZip) PaperMapCompatibilityChecks.Run(mapsZip, Check);
if (Environment.GetEnvironmentVariable("VSCN_ROOFING_ZIP") is { } roofingZip) RoofingPlacementChecks.Run(roofingZip, Check);
if (Environment.GetEnvironmentVariable("VSCN_CHISELTOOLS_ZIP") is { } qpZip) ChiselToolsSurvivalChecks.Run(qpZip, Check);
if (Environment.GetEnvironmentVariable("VSCN_COINAGE_ZIP") is { } coinsZip) CoinageEconomyChecks.Run(coinsZip, Check);
Console.WriteLine($"PASS: {checks} checks; native civilization GUI, announcements, map marker compatibility, membership and status exports validated.");

static List<string> ModHandshakePolicyCheck(ModPolicyConfig policy, IEnumerable<ModRule> rules, IReadOnlyDictionary<string, string>? core = null) =>
    ModPolicyMode.Whitelist == policy.Mode ? VscnCivilizationModSystem.BuildServerWhitelist(policy, rules, core) : [];
static List<string> ModHandshakeBlacklistCheck(ModPolicyConfig policy) => VscnCivilizationModSystem.BuildServerBlacklist(policy);

public class Proxy : DispatchProxy
{
    static Proxy()
    {
        // VS 1.22.7 adds an internal IPlayer interface member that DispatchProxy cannot normally implement.
        Create<IWorldPlayerData, Proxy>();
        var generator = typeof(DispatchProxy).Assembly.GetType("System.Reflection.DispatchProxyGenerator")!;
        var map = generator.GetField("s_alcProxyAssemblyMap", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        object?[] args = [System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(Proxy).Assembly), null];
        map.GetType().GetMethod("TryGetValue")!.Invoke(map, args);
        args[1]!.GetType().GetMethod("GenerateInstanceOfIgnoresAccessChecksToAttribute", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(args[1], [typeof(IPlayer).Assembly.GetName().Name]);
    }

    public int Deaths;
    public System.Func<MethodInfo, object?[]?, object?> Handler = null!;
    public static T Make<T>(System.Func<MethodInfo, object?[]?, object?> handler) where T : class { var proxy = Create<T, Proxy>(); ((Proxy)(object)proxy).Handler = handler; return proxy; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        object? result = Handler(method!, args);
        return result ?? (method!.ReturnType != typeof(void) && method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);
    }
}
