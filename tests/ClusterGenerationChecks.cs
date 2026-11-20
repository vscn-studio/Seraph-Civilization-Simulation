using HarmonyLib;
using Newtonsoft.Json;
using SeraphCivilizationSimulation;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

internal static class ClusterGenerationChecks
{
    private sealed class SpawnEntity : EntityPlayer
    {
        public int Teleports;
        public override void TeleportToDouble(double x, double y, double z, Action? callback = null)
        { Teleports++; Pos.SetPos(x, y, z); callback?.Invoke(); }
    }
    private sealed class Fixture
    {
        public readonly VscnCivilizationModSystem System = new();
        public readonly Queue<ChunkLoadOptions> Loads = new();
        public readonly Queue<Action> Main = new();
        public readonly Dictionary<string, byte[]> Saved = [];
        public readonly SpawnEntity Entity = new();
        public readonly List<string> Messages = [];
        public IServerPlayer Player = null!;
        public bool FailLoad, MapsAvailable = true;
        public int Peeks;
        public readonly Queue<ChunkPeekOptions> PeekRequests = new();
        public Fixture(bool online = false)
        {
            var air = new Block { Replaceable = 10000 };
            var blocks = Proxy.Make<IBlockAccessor>((m,a) => m.Name switch
            { "GetTerrainMapheightAt" => 120, "GetClimateAt" => new ClimateCondition { Temperature = 20 },
                "IsSideSolid" => true, "GetBlock" => air, _ => null });
            var player = Player = Proxy.Make<IServerPlayer>((m,a) =>
            {
                if (m.Name == "SendMessage") Messages.Add((string)a![1]!);
                return m.Name switch
                { "get_PlayerUID" or "get_PlayerName" => "arrival", "get_Entity" => Entity, "get_LanguageCode" => "en", _ => null };
            });
            var world = Proxy.Make<IServerWorldAccessor>((m,a) => m.Name switch
            { "get_AllOnlinePlayers" => online ? new IPlayer[] { player } : Array.Empty<IPlayer>(),
                "get_BlockAccessor" => blocks, "get_SeaLevel" => 100, _ => null });
            Entity.World = world;
            var save = Proxy.Make<ISaveGame>((m,a) =>
            { if (m.Name == "StoreData") Saved[(string)a![0]!] = (byte[])a[1]!; return null; });
            var map = Proxy.Make<IServerMapChunk>((m,a) => m.Name == "get_WorldGenTerrainHeightMap" ? Enumerable.Repeat((ushort)120, 1024).ToArray() : null);
            var manager = Proxy.Make<IWorldManagerAPI>((m,a) =>
            {
                if (m.Name == "LoadChunkColumnPriority")
                { if (FailLoad) throw new IOException("test load failure"); Loads.Enqueue((ChunkLoadOptions)a![2]!); }
                if (m.Name == "PeekChunkColumn") { Peeks++; PeekRequests.Enqueue((ChunkPeekOptions)a![2]!); }
                return m.Name switch { "get_SaveGame" => save, "get_MapSizeX" or "get_MapSizeZ" => 100000,
                    "get_MapSizeY" => 384, "GetMapChunk" => MapsAvailable ? map : null, _ => null };
            });
            var events = Proxy.Make<IServerEventAPI>((m,a) =>
            { if (m.Name == "EnqueueMainThreadTask") Main.Enqueue((Action)a![0]!); return null; });
            var logger = Proxy.Make<ILogger>((m,a) => null);
            var loader = Proxy.Make<IModLoader>((m,a) => m.Name == "IsModEnabled" ? false : null);
            var api = Proxy.Make<ICoreServerAPI>((m,a) => m.Name switch
            { "get_World" => world, "get_WorldManager" => manager, "get_Event" => events, "get_Logger" => logger, "get_ModLoader" => loader, _ => null });
            Set("sapi", api);
            System.Config.Clusters.TotalClusters = 3;
            System.Config.Clusters.MinDistanceBetween = 0;
            if (online)
            {
                ((HashSet<string>)AccessTools.Field(System.GetType(), "readyPlayers").GetValue(System)!).Add("arrival");
                ((HashSet<string>)AccessTools.Field(System.GetType(), "verifiedPlayers").GetValue(System)!).Add("arrival");
            }
        }
        public void Set(string field, object value) => AccessTools.Field(System.GetType(), field).SetValue(System, value);
        public object? Call(string method, params object[] args) => AccessTools.Method(System.GetType(), method).Invoke(System, args);
        public bool Ready => (bool)AccessTools.Field(System.GetType(), "clustersReady").GetValue(System)!;
        public bool Generating => (bool)AccessTools.Field(System.GetType(), "generating").GetValue(System)!;
        public void PumpMain() { for (int i = 0; Main.Count > 0 && i < 100; i++) Main.Dequeue()(); }
        public void LoadOne() { Loads.Dequeue().OnLoaded(); PumpMain(); }
        public VscnState Snapshot() => JsonConvert.DeserializeObject<VscnState>(global::System.Text.Encoding.UTF8.GetString(Saved["clusterSpawnsState"]))!;
    }
    public static void Run(Action<bool, string> check)
    {
        Vintagestory.API.Config.Lang.AvailableLanguages["en"] = Proxy.Make<Vintagestory.API.Config.ITranslationService>((m,a) => m.Name.StartsWith("Get") ? a?[0] : null);
        var first = new Fixture(online: true); first.Call("InitializeClusters");
        check(!first.Ready && first.Loads.Count == 1, "No unvalidated cluster may become available.");
        first.LoadOne();
        check(first.Ready && first.Generating && first.System.State.Clusters.Count == 1, "First valid cluster must become usable while remaining generation is pending.");
        check(first.Snapshot().PendingClusterTarget == 3 && first.Snapshot().Clusters.Count == 1, "First cluster and unfinished target must be persisted immediately.");
        check(first.Peeks == 0, "Loaded terrain maps must not be regenerated for land validation.");
        first.LoadOne();
        check(first.Entity.Teleports == 1 && first.System.State.Clusters.Count == 1 && first.System.State.PlayerAssignments["arrival"] == 0,
            "A waiting player must land before the second cluster finishes.");
        var partial = first.Snapshot();
        check(first.Messages.Count(m => m == "vsscsmod:cluster.join") == 1, "First successful assignment must display cluster information once.");
        first.Messages.Clear();
        first.Call("HandleVerifiedPlayer", first.Player);
        check(first.Messages.Count == 0, "An assigned player must not receive cluster information on reconnect.");
        int count = first.System.State.CurrentClusterPlayerCount;
        while (first.Loads.Count > 0) first.LoadOne();
        check(!first.Generating && first.System.State.Clusters.Count == 3 && first.Snapshot().PendingClusterTarget == 0, "Background generation must finish and clear its resume marker.");
        check(first.Entity.Teleports == 1 && first.System.State.CurrentClusterPlayerCount == count, "Finishing background generation must not reassign players or reset sequential counts.");
        var resumed = new Fixture(online: true); resumed.Set("state", partial); resumed.Call("InitializeClusters");
        resumed.Call("HandleVerifiedPlayer", resumed.Player);
        check(resumed.Messages.Count == 0, "Saved assignments must suppress repeat greetings after server restart.");
        check(resumed.Ready && resumed.Generating && resumed.System.State.PlayerAssignments["arrival"] == 0, "Restart must keep published clusters usable and resume missing ones.");
        while (resumed.Loads.Count > 0) resumed.LoadOne();
        check(resumed.System.State.Clusters.Count == 3 && resumed.System.State.CurrentClusterPlayerCount == count, "Resume must preserve assignment counters.");
        var legacy = new Fixture(); legacy.System.State.Clusters.Add(new() { X = 50000, Y = 120, Z = 50000 }); legacy.Call("InitializeClusters");
        check(legacy.Ready && legacy.Loads.Count == 0, "Legacy worlds without a pending marker must not be regenerated.");
        var old = legacy.System.State.Clusters; legacy.System.State.PlayerAssignments["old"] = 0;
        legacy.Call("GenerateClusters", true); legacy.FailLoad = true; legacy.LoadOne();
        check(!legacy.Generating && ReferenceEquals(old, legacy.System.State.Clusters) && legacy.System.State.PlayerAssignments["old"] == 0,
            "Failed administrative reroll must retain the old layout and assignments atomically.");
        var failure = new Fixture { MapsAvailable = false }; failure.Call("InitializeClusters"); failure.LoadOne();
        check(failure.Peeks == 1 && !failure.Ready, "Missing terrain must still be sampled before publishing.");
        failure.PeekRequests.Dequeue().OnGenerated(new()); failure.PumpMain();
        check(!failure.Generating && !failure.Ready, "Missing terrain callback must finish generation failure instead of leaving it stuck.");
    }
}
