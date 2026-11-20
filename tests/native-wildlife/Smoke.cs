using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

// This mod is loaded ONLY into a disposable, headless native server. It changes
// blocks/entities in that isolated save and shuts the server down after assertions.
public sealed class WildlifeSmoke : ModSystem
{
    private sealed record Scenario(string Name, string Material, string Species, bool Roof = false,
        bool Far = false, bool Wall = false, bool Protected = false, bool Tunnel = false, bool Shielded = false,
        bool RetrySeek = true);
    private sealed record Arena(Scenario Scenario, EntityAgent Animal, EntityAgent Prey,
        AiTaskSeekEntity Seek, int X, int Z, List<BlockPos> Required, Dictionary<BlockPos, int> Preserved);

    private readonly List<Arena> arenas = [];
    private ICoreServerAPI api = null!;
    private int ticks;
    private bool finished;
    private const int Ground = 220;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;
    public override double ExecuteOrder() => 100;

    public override void StartServerSide(ICoreServerAPI serverApi)
    {
        api = serverApi;
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () =>
            api.WorldManager.LoadChunkColumnPriority(15, 15, 19, 18,
                new ChunkLoadOptions { KeepLoaded = true, OnLoaded = Setup }));
    }

    private void Setup()
    {
        try
        {
            // A rerun must not count entities or item drops left by a previous run.
            foreach (Entity old in api.World.LoadedEntities.Values.ToArray())
                if (old.Pos.X >= 500 && old.Pos.X <= 600 && old.Pos.Z >= 500 && old.Pos.Z <= 585)
                    api.World.DespawnEntity(old, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
            var cases = new List<Scenario>();
            foreach (string material in new[] { "packeddirt", "rammed-light-plain", "cob-none",
                "strawbedding", "drypackeddirt", "soil-medium-normal" })
            {
                cases.Add(new($"{material}/pillar", material, "bear-brown-adult-male"));
                cases.Add(new($"{material}/roof-far", material, "bear-brown-adult-male", Roof: true, Far: true));
            }
            cases.Add(new("packed-variant/polar", "packeddirt-trampledearth", "bear-polar-adult-male", Roof: true));
            cases.Add(new("cob-normal/sun", "cob-normal", "bear-sun-adult-female", Roof: true));
            cases.Add(new("packed-wall/close-prey", "packeddirt", "bear-brown-adult-male", Wall: true));
            cases.Add(new("stone/unchanged", "rock-granite", "bear-brown-adult-male", Protected: true));
            cases.Add(new("dirt-behind-stone/unchanged", "packeddirt", "bear-brown-adult-male", Protected: true, Shielded: true));
            cases.Add(new("wolf/1x1-tunnel", "rock-granite", "wolf-eurasian-adult-male", Tunnel: true));
            cases.Add(new("rammed/pillar-single-seek", "rammed-light-plain", "bear-brown-adult-male", RetrySeek: false));
            int index = 0;
            foreach (Scenario scenario in cases)
            {
                int x = 520 + index % 5 * 16, z = 520 + index / 5 * 16;
                index++;
                SetupArena(scenario, x, z);
            }
            api.Event.RegisterGameTickListener(Tick, 250);
            // Headless servers skip ordinary physics without tracked clients. Invoke the
            // actual native physics implementation, removing its normal registration first.
            api.Event.RegisterGameTickListener(dt =>
            {
                if (finished) return;
                foreach (Arena arena in arenas)
                {
                    arena.Animal.State = EnumEntityState.Active;
                    var physics = arena.Animal.GetBehavior<EntityBehaviorControlledPhysics>()!;
                    physics.OnPhysicsTick(dt);
                    physics.AfterPhysicsTick(dt);
                }
            }, 20);
        }
        catch (Exception ex)
        {
            api.Logger.Error("WILDLIFE SMOKE RESULT: FAIL (setup): {0}", ex);
            api.Server.ShutDown();
        }
    }

    private void SetupArena(Scenario scenario, int x, int z)
    {
        var blocks = api.World.BlockAccessor;
        Block material = api.World.GetBlock(new AssetLocation(scenario.Material))
            ?? throw new InvalidOperationException($"Missing native block {scenario.Material}");
        int stone = api.World.GetBlock(new AssetLocation("rock-granite"))!.Id;
        for (int bx = x - 6; bx <= x + 6; bx++)
        for (int bz = z - 7; bz <= z + 6; bz++)
        for (int by = Ground - 1; by <= Ground + 6; by++)
            blocks.SetBlock(by == Ground - 1 ? stone : 0, new BlockPos(bx, by, bz));

        List<BlockPos> required = [];
        Dictionary<BlockPos, int> preserved = [];
        void Put(int bx, int by, int bz, int id, bool keep = false, bool remove = false)
        {
            var pos = new BlockPos(bx, by, bz);
            blocks.SetBlock(id, pos);
            if (keep) preserved[pos] = id;
            if (remove) required.Add(pos);
        }
        if (scenario.Tunnel)
        {
            for (int bz = z - 4; bz <= z + 3; bz++)
            {
                Put(x - 1, Ground, bz, stone, keep: true);
                Put(x + 1, Ground, bz, stone, keep: true);
                Put(x, Ground + 1, bz, stone, keep: true);
            }
        }
        else
        {
            for (int bx = x - (scenario.Wall ? 4 : 0); bx <= x + (scenario.Wall ? 4 : 0); bx++)
            for (int by = Ground; by <= Ground + 2; by++)
                Put(bx, by, z, material.Id, keep: scenario.Protected,
                    remove: !scenario.Protected && bx == x && (!scenario.Wall || by < Ground + 2));
            if (scenario.Roof)
                for (int bx = x - 4; bx <= x + 4; bx++)
                for (int bz = z - 4; bz <= z + 4; bz++)
                    Put(bx, Ground + 3, bz, material.Id, remove: bx == x && bz == z);
            if (scenario.Shielded)
                for (int bx = x - 2; bx <= x + 2; bx++)
                for (int bz = z - 1; bz <= z + 2; bz++)
                for (int by = Ground; by <= Ground + 4; by++)
                    // Enclose the dirt so native pathfinding cannot legitimately walk
                    // around the shield and invalidate the through-wall assertion.
                    if (bx == x - 2 || bx == x + 2 || bz == z - 1 || bz == z + 2)
                        Put(bx, by, bz, stone, keep: true);
        }
        preserved[new BlockPos(x, Ground - 1, z)] = stone;

        EntityProperties animalType = api.World.GetEntityType(new AssetLocation(scenario.Species))
            ?? throw new InvalidOperationException($"Missing native entity {scenario.Species}");
        var animal = (EntityAgent)api.World.ClassRegistry.CreateEntity(animalType);
        animal.AlwaysActive = true;
        animal.Pos.SetPos(x + 0.5, Ground, z - (scenario.Far || scenario.Tunnel ? 5 : scenario.Shielded ? 3 : 1));
        animal.Pos.Yaw = 0;
        api.World.SpawnEntity(animal);
        api.Server.RemovePhysicsTickable(animal.GetBehavior<EntityBehaviorControlledPhysics>());
        var manager = animal.GetBehavior<EntityBehaviorTaskAI>()!.TaskManager;
        AiTaskSeekEntity seek = manager.AllTasks.OfType<AiTaskSeekEntity>().First();
        // Deterministic target acquisition; native seek, task lifecycle, A*, turning and
        // obstacle handling still execute. Other tasks must not choose random prey.
        seek.NowSeekRange = 16;
        manager.AllTasks.Clear();
        var prey = new EntityAgent { World = api.World, Api = api };
        prey.SetSelectionBox(0.6f, 1.8f);
        prey.Pos.SetPos(x + 0.5, scenario.Wall || scenario.Tunnel ? Ground :
            Ground + (scenario.Roof ? 4 : 3), z + (scenario.Tunnel ? 5.5 : scenario.Wall ? 1.35 : 0.5));
        arenas.Add(new(scenario, animal, prey, seek, x, z, required, preserved));
        api.Logger.Notification("WILDLIFE CASE START {0}: material={1}, resistance={2}, box={3}",
            scenario.Name, material.Code, material.Resistance, animal.CollisionBox);
    }

    private void Tick(float dt)
    {
        if (finished) return;
        if (ticks++ % 4 == 0)
            foreach (Arena arena in arenas)
            {
                if (!arena.Scenario.RetrySeek && ticks > 1) continue;
                var manager = arena.Animal.GetBehavior<EntityBehaviorTaskAI>()!.TaskManager;
                if (!manager.ActiveTasksBySlot.Contains(arena.Seek))
                {
                    arena.Seek.Notify("seekEntity", arena.Prey);
                    manager.ExecuteTask(arena.Seek, arena.Seek.Slot);
                }
            }
        if (ticks % 20 == 0)
            foreach (Arena arena in arenas)
            {
                var behavior = arena.Animal.SidedProperties.Behaviors.FirstOrDefault(b => b.GetType().Name == "BearBlockBreakingBehavior");
                object? Read(string field) => behavior?.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(behavior);
                api.Logger.Notification("WILDLIFE PROGRESS {0}: tick={1}, remaining={2}, target={3}, progress={4}, pos={5}",
                    arena.Scenario.Name, ticks, arena.Required.Count(p => api.World.BlockAccessor.GetBlock(p).Id != 0),
                    Read("currentTarget"), Read("breakProgress"), arena.Animal.Pos);
            }
        // A far approach under a broad rammed-earth roof can require more than fourteen
        // native three-second breaks before the central support is completely removed.
        if (ticks < 240) return;
        finished = true;
        bool allPass = true;
        foreach (Arena arena in arenas)
        {
            int remaining = arena.Required.Count(p => api.World.BlockAccessor.GetBlock(p).Id != 0);
            int changed = arena.Preserved.Count(p => api.World.BlockAccessor.GetBlock(p.Key).Id != p.Value);
            EntityItem[] drops = api.World.LoadedEntities.Values.OfType<EntityItem>()
                .Where(e => Math.Abs(e.Pos.X - arena.X - 0.5) < 7 && Math.Abs(e.Pos.Z - arena.Z - 0.5) < 7).ToArray();
            bool pass = remaining == 0 && changed == 0 && (arena.Scenario.Protected || arena.Scenario.Tunnel || drops.Length > 0);
            if (arena.Scenario.Tunnel) pass &= arena.Animal.Pos.Z > arena.Z + 3.5 && arena.Animal.CollisionBox.XSize < 1;
            allPass &= pass;
            api.Logger.Notification("WILDLIFE CASE RESULT {0}: {1}, remaining={2}, protectedChanged={3}, nativeItemEntities={4}, pos={5}",
                arena.Scenario.Name, pass ? "PASS" : "FAIL", remaining, changed, drops.Length, arena.Animal.Pos);
        }
        api.Logger.Notification("WILDLIFE SMOKE RESULT: {0} ({1} native AI scenarios)", allPass ? "PASS" : "FAIL", arenas.Count);
        api.Server.ShutDown();
    }
}
