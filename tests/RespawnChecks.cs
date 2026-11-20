using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.Server;
using SeraphCivilizationSimulation;

internal static class RespawnChecks
{
    public static void Run(Action<bool, string> check)
    {
        string game = Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? "E:/vintagestory/Vintagestory";
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            foreach (string directory in new[] { game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods") })
            {
                string file = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(file)) return context.LoadFromAssemblyPath(Path.GetFullPath(file));
            }
            return null;
        }
        AssemblyLoadContext.Default.Resolving += Resolve;
        try { CheckRespawn(check); }
        finally { AssemblyLoadContext.Default.Resolving -= Resolve; }
    }

    private static void CheckRespawn(Action<bool, string> check)
    {
        const string injury = "vscncsInjuryRespawn";
        var type = typeof(VscnCivilizationModSystem);
        var system = new VscnCivilizationModSystem();
        var previousInstance = AccessTools.Field(type, "instance").GetValue(null);
        object? Invoke(string method, params object?[] args) => AccessTools.Method(type, method).Invoke(system, args);
        void Set(string field, object value) => AccessTools.Field(type, field).SetValue(system, value);
        var tribe = new TribeRecord { Id = "home", LeaderUid = "member", Members = ["member", "chief"], X = 400, Y = 110, Z = 500 };
        var tribes = new TribeState { Tribes = [tribe] };
        Set("tribeState", tribes);
        Set("instance", system);
        var entity = new RespawnPlayer();
        entity.WatchedAttributes.SetString("playerUID", "member");
        var data = new ServerWorldPlayerData { Deaths = 1, EntityPlayer = entity, SpawnPosition = new PlayerSpawnPos(100, 111, 100) };
        AccessTools.Field(typeof(ServerWorldPlayerData), "PlayerUID").SetValue(data, "member");
        var nativePlayer = (ServerPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ServerPlayer));
        AccessTools.Field(typeof(ServerPlayer), "worlddata").SetValue(nativePlayer, data);
        var callbacks = new Queue<Action<float>>();
        int broadcasts = 0;
        var connection = EnumClientState.Playing;
        var player = Proxy.Make<IServerPlayer>((m, a) => m.Name switch
        {
            "get_PlayerUID" => "member", "get_Entity" => entity, "get_WorldData" => data,
            "get_ConnectionState" => connection,
            "SetSpawnPosition" => SetSpawn((PlayerSpawnPos)a![0]!),
            "BroadcastPlayerData" => Broadcast(), _ => null
        });
        object? SetSpawn(PlayerSpawnPos spawn) { data.SpawnPosition = spawn; return null; }
        object? Broadcast() { broadcasts++; return null; }
        var config = new TreeAttribute();
        config.SetString("playerlives", "3");
        var world = Proxy.Make<IServerWorldAccessor>((m, a) => m.Name switch
        {
            "get_Side" => EnumAppSide.Server, "get_Config" => config, "PlayerByUid" => player,
            "RegisterCallback" => QueueCallback((Action<float>)a![0]!), _ => null
        });
        object QueueCallback(Action<float> callback) { callbacks.Enqueue(callback); return 1L; }
        var logger = Proxy.Make<ILogger>((m, a) => null);
        var api = Proxy.Make<ICoreServerAPI>((m, a) => m.Name switch { "get_World" => world, "get_Logger" => logger, _ => null });
        entity.World = world;
        Set("sapi", api);
        var roomWorld = new TerritoryTestWorld(api); roomWorld.Box(400, 110, 500, 6, 6); roomWorld.Attach(system);
        TerritoryTestWorld.Region(tribe); TerritoryTestWorld.Index(system);
        var protection = new VscnRespawnBehavior(entity);
        AccessTools.Method(typeof(VscnRespawnBehavior), "Bind").Invoke(protection, [system]);
        var pending = (Dictionary<string, Guid>)AccessTools.Field(type, "pendingSpawns").GetValue(system)!;
        var sequential = (Dictionary<string, (int Index, long Order)>)AccessTools.Field(type, "pendingSequentialAssignments").GetValue(system)!;
        try
        {
            Invoke("InstallRespawnIntegration");
            var target = AccessTools.Method(typeof(ServerPlayer), nameof(ServerPlayer.GetSpawnPosition));
            check(Harmony.GetPatchInfo(target)?.Owners.Contains("vsscsmod.respawn") == true,
                "Native respawn position integration was not installed.");
            pending["member"] = Guid.NewGuid();
            sequential["member"] = (0, 1);
            bool createCorpse = (bool)Invoke("BeforeDeadCorpseHandling", player)!;
            check(!createCorpse && entity.WatchedAttributes.GetBool(injury), "First tribe death must keep the no-corpse injury marker.");
            var targetPos = nativePlayer.GetSpawnPosition(consumeSpawnUse: true);
            check(targetPos.X == 400.5 && targetPos.Y == 110 && targetPos.Z == 500.5,
                "First native respawn used the stale cluster spawn instead of the tribe.");
            check(targetPos.Radius == 0 && targetPos.UsesLeft == -1, "Tribe spawn must be exact and must not consume personal spawn uses.");
            check(data.Deaths == 1, "Selecting a tribe spawn consumed another life.");
            check(!pending.ContainsKey("member") && !sequential.ContainsKey("member"), "Pending cluster teleport could overwrite the tribe respawn.");

            // The engine queues its teleport before notifying mod PlayerRespawn handlers.
            entity.TeleportTo(targetPos, () =>
            {
                entity.Alive = true;
                entity.Health.Health = 20;
                protection.OnEntityRevive();
                entity.Health.Health = 20;
            });
            Invoke("OnPlayerRespawn", player);
            check(entity.Teleports == 1, "Mod respawn handler queued a competing teleport.");
            check(data.SpawnPosition.x == tribe.X && data.SpawnPosition.z == tribe.Z, "Persistent spawn was not synchronized to the tribe.");
            check(entity.WatchedAttributes.GetBool(injury), "Injury marker was cleared before actual revival.");
            entity.CompleteTeleport();
            check(entity.Pos.X == 400.5 && entity.Pos.Z == 500.5, "Async engine teleport did not finish in the tribe.");
            check(entity.Health.Health == 20 && callbacks.Count == 1, "Wounded health must wait until all revival behaviors finish.");
            callbacks.Dequeue()(0);
            check(entity.Health.Health == 4 && !entity.WatchedAttributes.GetBool(injury) && broadcasts == 1,
                "Actual revival must apply 20 percent health, clear the marker and synchronize once.");

            // Reincarnation bypasses PlayerRespawn: no injury marker and no second death.
            entity.WatchedAttributes.SetBool("downeddied", true);
            entity.WatchedAttributes.SetBool("downedexecuted", true);
            entity.WatchedAttributes.SetDouble("lastDownedTotalHours", 12);
            object? nativeDowned = null;
            MethodInfo? animationAllowed = null;
            if (Environment.GetEnvironmentVariable("VSCN_DOWNED_DLL") is { } downedDll)
            {
                var downedAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(downedDll));
                var downedType = downedAssembly.GetType("Downed.EntityBehaviorDowned", true)!;
                nativeDowned = Activator.CreateInstance(downedType, entity)!;
                animationAllowed = AccessTools.Method(downedType, "IsAnimationAllowed");
                check(!(bool)animationAllowed.Invoke(nativeDowned, [new AnimationMetaData { Code = "walk" }])!,
                    "The installed Downed behavior must reproduce the stale death flag blocking walking.");
            }
            entity.Alive = false;
            Invoke("OnPlayerRevived", entity);
            check(entity.WatchedAttributes.GetBool("downeddied"), "A dead player must retain the native corpse pose.");
            entity.Alive = true;
            protection.OnEntityRevive();
            check(!entity.WatchedAttributes.GetBool("downeddied") && !entity.WatchedAttributes.HasAttribute("downedexecuted")
                && entity.WatchedAttributes.GetDouble("lastDownedTotalHours") == 12 && callbacks.Count == 0 && data.Deaths == 1,
                "Direct reincarnation must clear Downed's death animation gate without another death, healing or cooldown reset.");
            if (nativeDowned != null)
                check((bool)animationAllowed!.Invoke(nativeDowned, [new AnimationMetaData { Code = "walk" }])!,
                    "The installed Downed behavior must allow walking immediately after the first revival.");

            tribe.X = 600; tribe.Dimension = 1;
            tribe.Regions.Clear(); TerritoryTestWorld.Region(tribe); TerritoryTestWorld.Index(system);
            roomWorld.Box(600, 110, 500, 6, 6, dimension: 1);
            var moved = nativePlayer.GetSpawnPosition(true);
            check(moved.X == 600.5 && moved.Dimension == 1, "Respawn did not follow a moved tribe or its dimension.");

            // A legacy or partially rebuilt tribe may temporarily have no usable
            // main hall region. Its persisted center must still remain the home
            // respawn location instead of falling back to a settlement cluster.
            tribe.Regions.Clear();
            var centerFallback = nativePlayer.GetSpawnPosition(true);
            check(centerFallback.X == 600.5 && centerFallback.Y == 110 && centerFallback.Z == 500.5
                && centerFallback.Dimension == 1, "A tribe without a usable main hall must respawn at its persisted tribe center.");
            check((bool)Invoke("ShouldSuppressTribeDeath", player, false)!,
                "A tribe center fallback must preserve injury respawn when the main hall is unavailable.");
            object?[] args = [player, false, targetPos];
            check((bool)Invoke("BeforeGetRespawnPosition", args)! && ReferenceEquals(args[2], targetPos), "Non-respawn spawn queries must keep native behavior.");
            tribes.Tribes.Clear();
            args = [player, true, targetPos];
            check((bool)Invoke("BeforeGetRespawnPosition", args)! && ReferenceEquals(args[2], targetPos), "Players without tribes must keep native spawn behavior.");
            check((bool)Invoke("BeforeDeadCorpseHandling", player)!, "Players without tribes must keep normal corpse creation.");
            tribes.Tribes.Add(tribe);
            data.Deaths = 3;
            check((bool)Invoke("BeforeDeadCorpseHandling", player)!, "Final life must keep normal corpse creation.");
            data.Deaths = 2;
            check(!(bool)Invoke("ShouldSuppressTribeDeath", player, false)!, "Downed final-life decision must account for the upcoming death.");
            config.SetString("playerlives", "-1");
            check(!(bool)Invoke("ShouldSuppressTribeDeath", player, false)!, "Native unlimited-lives setting must not override civilization lives.");
            system.Config.Respawn.InitialLives = -1;
            check((bool)Invoke("ShouldSuppressTribeDeath", player, false)!, "Unlimited lives must retain tribe injury recovery.");

            entity.WatchedAttributes.SetBool(injury, true);
            entity.Health.Health = 20;
            protection.OnEntityRevive();
            connection = EnumClientState.Offline;
            callbacks.Dequeue()(0);
            check(entity.Health.Health == 20 && broadcasts == 1, "Deferred revival must not modify a disconnected player.");
        }
        finally
        {
            (AccessTools.Field(type, "respawnHarmony").GetValue(system) as Harmony)?.UnpatchAll("vsscsmod.respawn");
            AccessTools.Field(type, "instance").SetValue(null, previousInstance);
        }
    }

    private sealed class RespawnPlayer : EntityPlayer
    {
        private EntityPos? target;
        private Action? onTeleported;
        public int Teleports { get; private set; }
        public TestHealth Health { get; }
        public RespawnPlayer() { Health = new TestHealth(this); Alive = false; }
        public override EntityBehavior? GetBehavior(string name) => name == "health" ? Health : null;
        public override void TeleportTo(EntityPos position, Action? callback = null)
        {
            Teleports++;
            target = position.Copy();
            onTeleported = callback;
        }
        public override void TeleportToDouble(double x, double y, double z, Action? callback = null) =>
            TeleportTo(new EntityPos(x, y, z), callback);
        public void CompleteTeleport() { Pos.SetFrom(target!); onTeleported?.Invoke(); }
    }

    private sealed class TestHealth(Entity entity) : EntityBehavior(entity)
    {
        public float Health { get; set; } = 20;
        public float MaxHealth => 20;
        public override string PropertyName() => "health";
    }
}
