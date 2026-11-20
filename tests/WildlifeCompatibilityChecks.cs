using Newtonsoft.Json.Linq;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using SeraphCivilizationSimulation;

internal static class WildlifeCompatibilityChecks
{
    internal static void Run(Action<bool, string> check)
    {
        check(BearBlockBreakingBehavior.CanBreak("packeddirt", EnumBlockMaterial.Soil, 2.2f),
            "Bears must break vanilla packed dirt.");
        check(BearBlockBreakingBehavior.CanBreak("packeddirt-trampledearth", EnumBlockMaterial.Soil, 2.2f),
            "Bears must break packed dirt variants used for paths.");
        check(BearBlockBreakingBehavior.CanBreak("drypackeddirt", EnumBlockMaterial.Soil, 2.2f),
            "Bears must break dry packed dirt.");
        check(BearBlockBreakingBehavior.CanBreak("soil-medium-normal", EnumBlockMaterial.Soil, 0.5f),
            "Bears must break ordinary grass-covered soil.");
        check(BearBlockBreakingBehavior.CanBreak("cob-none", EnumBlockMaterial.Soil, 1.8f)
            && BearBlockBreakingBehavior.CanBreak("cob-normal", EnumBlockMaterial.Soil, 1.8f)
            && BearBlockBreakingBehavior.CanBreak("strawbedding", EnumBlockMaterial.Soil, 1.8f),
            "Bears must break the native cob and straw bedding families named in the feature request.");
        check(BearBlockBreakingBehavior.CanBreak("rammed-light-plain", EnumBlockMaterial.Soil, 3f)
            && !BearBlockBreakingBehavior.CanBreak("rammed-light-plain", EnumBlockMaterial.Soil, 4f)
            && !BearBlockBreakingBehavior.CanBreak("mudbrick", EnumBlockMaterial.Soil, 1f),
            "Rammed earth must use its native resistance without allowing unrelated soil construction blocks.");
        check(!BearBlockBreakingBehavior.CanBreak("stone-granite", EnumBlockMaterial.Stone, 1f)
            && !BearBlockBreakingBehavior.CanBreak("soil-medium-normal", EnumBlockMaterial.Soil, 3f),
            "Bear block breaking must exclude stone and high-resistance blocks.");
        check(BearBlockBreakingBehavior.CanScan(EnumAppSide.Server, true, true)
            && !BearBlockBreakingBehavior.CanScan(EnumAppSide.Client, true, true)
            && !BearBlockBreakingBehavior.CanScan(EnumAppSide.Server, false, true),
            "Bear obstacle scanning must run for living server agents without requiring movement input.");
        var frontBlocks = BearBlockBreakingBehavior.GetFrontBlockPositions(0.5, 4, 1.5, 0, 1.3f, 1.2f).ToArray();
        check(frontBlocks.Any(pos => pos.X == -1 && pos.Y == 4 && pos.Z == 1)
            && frontBlocks.Any(pos => pos.X == 1 && pos.Y == 5 && pos.Z == 1),
            "Bear obstacle detection must cover the full front width and collision height.");
        var pursuitBlocks = BearBlockBreakingBehavior.GetPursuitBlockPositions(0.5, 4, 0.5,
            1.3f, 1.2f, 0.5, 8, 2.5).ToArray();
        check(Enumerable.Range(4, 4).All(y => pursuitBlocks.Contains(new BlockPos(0, y, 2))),
            "Chasing a target above a roofed three-block pillar must detect the supporting dirt and roof, before the native path reaches them.");
        check(pursuitBlocks.All(p => p.Z >= 0 && p.Z <= 3 && p.Y >= 4 && p.Y <= 7),
            "Pursuit scanning must stay in the target direction and exclude the ground below the bear.");
        check(BearBlockBreakingBehavior.IsWithinBreakingReach(0.5, 0.5, 1.3f, new BlockPos(0,4,2))
            && !BearBlockBreakingBehavior.IsWithinBreakingReach(0.5, 0.5, 1.3f, new BlockPos(0,4,4)),
            "A distant obstacle may prompt approach but must not be destroyed from beyond digging reach.");

        JArray patches = JArray.Parse(File.ReadAllText("assets/vsscsmod/patches/wildlife.json"));
        check((double?)patches[0]?["value"] == 0.95
            && (string?)patches[0]?["file"] == "game:entities/animal/mammal/wolf-adult.json",
            "The wolf collision patch must narrowly target adult wolves and a one-block width.");
        check((string?)patches[1]?["file"] == "game:entities/animal/mammal/bear-adult.json"
            && (string?)patches[1]?["value"]?["code"] == "vscncs.bearblockbreak"
            && (string?)patches[1]?["path"] == "/server/behaviors/-"
            && (string?)patches[1]?["side"] == "Server",
            "Bear block breaking must be attached to server-side bear entities.");
        CheckRuntime(patches, check);
    }

    private static void CheckRuntime(JArray patches, Action<bool, string> check)
    {
        foreach (float yaw in new[] { 0f, GameMath.PIHALF, GameMath.PI, 3 * GameMath.PIHALF, GameMath.PI / 4 })
        foreach (var size in new[] { (1.3f, 1.2f), (1.6f, 1.3f), (1f, 0.9f) })
        {
            var obstacles = new Dictionary<BlockPos, Block>();
            var broken = new List<BlockPos>();
            var air = new Block { Code = new("game:air"), BlockMaterial = EnumBlockMaterial.Air };
            var dirt = new Block { Code = new("game:packeddirt"), BlockMaterial = EnumBlockMaterial.Soil, Resistance = 2.2f };
            var stone = new Block { Code = new("game:rock-granite"), BlockMaterial = EnumBlockMaterial.Stone, Resistance = 1f };
            var accessor = Proxy.Make<IBlockAccessor>((m, a) =>
            {
                if (m.Name == "GetBlock") return obstacles.GetValueOrDefault((BlockPos)a![0]!, air);
                if (m.Name == "BreakBlock")
                {
                    check(a![1] == null && (float)a[2]! == 1, "Bear destruction must use native block breaking with the normal drop multiplier.");
                    var pos = ((BlockPos)a[0]!).Copy();
                    broken.Add(pos); obstacles.Remove(pos);
                }
                return null;
            });
            var registry = Proxy.Make<IClassRegistryAPI>((m, a) => m.Name switch
            {
                "GetEntityBehaviorClass" => typeof(BearBlockBreakingBehavior),
                "CreateEntityBehavior" => new BearBlockBreakingBehavior((Entity)a![0]!), _ => null
            });
            var world = Proxy.Make<IServerWorldAccessor>((m, a) => m.Name switch
            {
                "get_BlockAccessor" => accessor, "get_Side" => EnumAppSide.Server, "get_ClassRegistry" => registry,
                "SpawnEntity" => throw new Exception("Bear destruction must delegate item drops to native block breaking instead of spawning entities directly."), _ => null
            });
            var api = Proxy.Make<ICoreServerAPI>((m, a) => m.Name switch
            { "get_World" => world, "get_Side" => EnumAppSide.Server, _ => null });
            var bear = new EntityAgent { World = world, Api = api };
            bear.Pos.SetFrom(new EntityPos(0.5, 4, 0.5, yaw));
            bear.SetCollisionBox(size.Item1, size.Item2);
            var properties = new EntityProperties
            {
                Code = new("game:bear-brown-adult-male"),
                Server = new EntityServerProperties([new JsonObject(patches[1]!["value"]!.DeepClone())], new())
            };
            AccessTools.Property(typeof(Entity), "Properties").SetValue(bear, properties);
            properties.Server.loadBehaviors(bear, properties, world);
            var behavior = bear.GetBehavior<BearBlockBreakingBehavior>();
            check(behavior != null, "The native entity behavior loader must instantiate the patched bear behavior.");

            // Place a wall where the native path traverser walks, opposite to AheadCopy().
            // With a roof above, a three-block wall must still be opened from ground level.
            int wallX = Math.Abs(Math.Sin(yaw)) > 0.7 ? (Math.Sin(yaw) > 0 ? 1 : -1) : 0;
            int wallZ = Math.Abs(Math.Cos(yaw)) > 0.7 ? (Math.Cos(yaw) > 0 ? 1 : -1) : 0;
            for (int y = 4; y <= 6; y++) obstacles[new BlockPos(wallX, y, wallZ)] = dirt;
            obstacles[new BlockPos(0, 6, 0)] = dirt;
            var behind = new BlockPos(-wallX, 4, -wallZ);
            obstacles[behind] = dirt;
            for (int i = 0; i < 9; i++) behavior!.OnGameTick(0.25f);
            check(broken.Count == 1 && broken[0].Equals(new BlockPos(wallX, 4, wallZ)),
                $"Bear tick must destroy the front wall at yaw {yaw}, width {size.Item1}, even without movement controls.");
            check(obstacles.ContainsKey(behind) && obstacles.ContainsKey(new BlockPos(0, 6, 0)),
                "Bear destruction must leave blocks behind it and a roof above its collision height intact.");
            if (size.Item2 > 1)
            {
                for (int i = 0; i < 9; i++) behavior!.OnGameTick(0.25f);
                check(broken.Count == 2 && broken[1].Y == 5, "A bear must also destroy the upper wall block that obstructs its body.");
            }
            obstacles[new BlockPos(wallX, 4, wallZ)] = stone;
            int count = broken.Count;
            for (int i = 0; i < 20; i++) behavior!.OnGameTick(0.25f);
            check(broken.Count == count, "Bear tick must preserve stone walls.");
        }
        CheckTurningAndPursuit(check);
    }

    private static void CheckTurningAndPursuit(Action<bool, string> check)
    {
        var obstacles = new Dictionary<BlockPos, Block>();
        var broken = new List<BlockPos>();
        var air = new Block { Code = new("game:air") };
        var dirt = new Block { Code = new("game:rammed-light-plain"), BlockMaterial = EnumBlockMaterial.Soil, Resistance = 3 };
        var accessor = Proxy.Make<IBlockAccessor>((m, a) =>
        {
            if (m.Name == "GetBlock") return obstacles.GetValueOrDefault((BlockPos)a![0]!, air);
            if (m.Name == "BreakBlock")
            {
                var pos = ((BlockPos)a![0]!).Copy();
                broken.Add(pos); obstacles.Remove(pos);
            }
            return null;
        });
        var world = Proxy.Make<IServerWorldAccessor>((m, a) => m.Name == "get_BlockAccessor" ? accessor : null);
        var api = Proxy.Make<ICoreServerAPI>((m, a) => m.Name switch
            { "get_World" => world, "get_Side" => EnumAppSide.Server, _ => null });
        var bear = new EntityAgent { World = world, Api = api };
        AccessTools.Property(typeof(Entity), "Properties").SetValue(bear, new EntityProperties
        {
            Server = new EntityServerProperties([], new())
        });
        bear.Pos.SetPos(0.5, 4, 0.5);
        bear.SetCollisionBox(1.3f, 1.2f);
        var behavior = new BearBlockBreakingBehavior(bear);
        var wall = new BlockPos(0,4,1);
        obstacles[wall] = dirt;
        // A brief native turn previously discarded all accumulated damage.
        for (int i = 0; i < 20; i++)
        {
            bear.Pos.Yaw = i % 4 == 2 ? GameMath.PI : 0;
            behavior.OnGameTick(0.25f);
        }
        check(broken.Count == 1, "Short turns must pause digging instead of resetting all progress on a dirt pillar.");

        var prey = new EntityAgent();
        prey.Pos.SetPos(0.5, 8, 2.5);
        AccessTools.Field(typeof(BearBlockBreakingBehavior), "pursuitTarget").SetValue(behavior, prey);
        AccessTools.Field(typeof(BearBlockBreakingBehavior), "pursuitMemory").SetValue(behavior, 5f);
        bear.Pos.Yaw = GameMath.PI;
        for (int y = 4; y <= 7; y++) obstacles[new BlockPos(0,y,2)] = dirt;
        for (int i = 0; i < 60; i++) behavior.OnGameTick(0.25f);
        check(Enumerable.Range(4,4).All(y => !obstacles.ContainsKey(new BlockPos(0,y,2))),
            "A roofed pillar supporting prey must be broken even when failed native pathfinding has turned the bear away.");
    }
}
