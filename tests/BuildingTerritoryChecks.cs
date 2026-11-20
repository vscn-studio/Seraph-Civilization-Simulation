using System.Reflection;
using HarmonyLib;
using SeraphCivilizationSimulation;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

// Real native RoomRegistry flood fill over an isolated block world; no game save is touched.
internal sealed class TerritoryTestWorld
{
    internal readonly Dictionary<(int D, int X, int Y, int Z), Block> Blocks = [];
    internal readonly HashSet<(int D, int X, int Y, int Z)> Unloaded = [];
    internal readonly RoomRegistry Registry = new();
    internal static readonly Block Air = new TestBlock(false);
    internal static readonly Block Stone = new TestBlock(true);
    internal ICoreServerAPI Api { get; }
    internal IBlockAccessor Accessor { get; }
    internal readonly List<LandClaim> Claims = [];
    internal int RainHeight;
    internal int TerrainHeight = 100;
    internal int LoadChecks;
    internal TerritoryTestWorld(ICoreServerAPI? original = null)
    {
        var chunk = Proxy.Make<IWorldChunk>((m, a) => null);
        IWorldChunk? Loaded(BlockPos pos) { LoadChecks++; return Unloaded.Contains(Key(pos)) ? null : chunk; }
        object? Access(MethodInfo m, object?[]? a)
        {
            BlockPos Pos() => a![0] is BlockPos p ? p : new BlockPos((int)a[0]!, (int)a[1]! % BlockPos.DimensionBoundary, (int)a[2]!, (int)a[1]! / BlockPos.DimensionBoundary);
            return m.Name switch
            {
                "GetBlock" => a!.Length == 2 && (int)a[1]! == BlockLayersAccess.Fluid ? Air : Get(Pos()),
                "IsSideSolid" => Get(Pos()).SideSolid[((BlockFacing)a![3]!).Index],
                "IsValidPos" => Pos().Y is >= 0 and < 384,
                "GetChunkAtBlockPos" => Loaded(Pos()),
                "get_LastChunkLoaded" => true,
                "GetRainMapHeightAt" => RainHeight,
                "GetTerrainMapheightAt" => TerrainHeight,
                _ => null
            };
        }
        var cache = Proxy.Make<ICachingBlockAccessor>(Access); Accessor = cache;
        var claims = Proxy.Make<ILandClaimAPI>((m, a) => m.Name switch { "get_All" => Claims, "TestAccess" => EnumWorldAccessResponse.Granted, _ => null });
        ICoreServerAPI? api = null;
        var world = Proxy.Make<IServerWorldAccessor>((m, a) => m.Name switch
        {
            "get_BlockAccessor" => cache, "GetCachingBlockAccessor" => cache, "get_Claims" => claims,
            "get_SunBrightness" => 22, "get_Api" => api,
            _ => original == null ? null : m.Invoke(original.World, a)
        });
        var loader = Proxy.Make<IModLoader>((m, a) => m.Name == "GetModSystem" && m.IsGenericMethod && m.GetGenericArguments()[0] == typeof(RoomRegistry)
            ? Registry : original?.ModLoader == null ? null : m.Invoke(original.ModLoader, a));
        var manager = Proxy.Make<IWorldManagerAPI>((m, a) => m.Name == "get_MapSizeY" ? 384 : original?.WorldManager == null ? null : m.Invoke(original.WorldManager, a));
        api = Proxy.Make<ICoreServerAPI>((m, a) => m.Name switch
        {
            "get_World" => world, "get_ModLoader" => loader, "get_WorldManager" => manager,
            _ => original == null ? null : m.Invoke(original, a)
        });
        Api = api;
        AccessTools.Field(typeof(RoomRegistry), "api").SetValue(Registry, api);
        AccessTools.Field(typeof(RoomRegistry), "blockAccessor").SetValue(null, null);
    }
    internal static (int D, int X, int Y, int Z) Key(BlockPos p) => (p.dimension, p.X, p.Y, p.Z);
    internal Block Get(BlockPos p) => Blocks.GetValueOrDefault(Key(p), Air);
    internal void Box(int x, int y, int z, int width = 4, int depth = 4, int height = 3, int dimension = 0)
    {
        for (int dx = -1; dx <= width; dx++) for (int dz = -1; dz <= depth; dz++) for (int dy = -1; dy <= height; dy++)
            Blocks[(dimension, x + dx, y + dy, z + dz)] = dx == -1 || dx == width || dz == -1 || dz == depth || dy == -1 || dy == height ? Stone : Air;
    }
    internal void Attach(VscnCivilizationModSystem system)
    {
        AccessTools.Field(system.GetType(), "sapi").SetValue(system, Api);
        AccessTools.Field(system.GetType(), "territoryDetector").SetValue(system, new BuildingTerritoryDetector(Api));
    }
    internal static BuildingTerritoryRegion Region(TribeRecord tribe, int width = 6, int height = 4, string site = "main", bool root = true)
    {
        var cells = from y in Enumerable.Range(tribe.Y, height) from z in Enumerable.Range(tribe.Z, width) from x in Enumerable.Range(tribe.X, width) select new TerritoryCell(x, y, z);
        var floors = from z in Enumerable.Range(tribe.Z, width) from x in Enumerable.Range(tribe.X, width) select new TerritoryCell(x, tribe.Y - 1, z);
        var r = BuildingTerritoryDetector.Make("room", new(tribe.X, tribe.Y, tribe.Z, tribe.Dimension), cells, floors, floors);
        r.SiteId = site; r.Root = root; tribe.Regions.Add(r); tribe.TerritoryVersion = 1; return r;
    }
    internal static void Index(VscnCivilizationModSystem system) => AccessTools.Method(system.GetType(), "ReindexTerritories").Invoke(system, null);
    internal sealed class TestBlock : Block
    {
        private readonly bool solid;
        internal TestBlock(bool solid, Cuboidf[]? boxes = null)
        {
            this.solid = solid; BlockId = solid ? 1 : 0; Code = new(solid ? "game:stone" : "game:air");
            SideSolid = new(solid ? 63 : 0); CollisionBoxes = boxes ?? (solid ? [new(0, 0, 0, 1, 1, 1)] : []);
            Replaceable = solid ? 0 : 10000;
        }
        public override int GetRetention(BlockPos pos, BlockFacing face, EnumRetentionType type) => solid ? 1 : 0;
        public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blocks, BlockPos pos) => CollisionBoxes;
    }
}

internal static class BuildingTerritoryChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var world = new TerritoryTestWorld(); world.Box(100, 101, 100, 8, 8);
        var detector = new BuildingTerritoryDetector(world.Api);
        var room = detector.Room(new(100, 101, 100));
        check(room.FloorCells.Count == 64 && room.Inside.Count == 192, "Native non-cellar room must retain its actual floor and volume.");
        check(!room.Cells.Contains(new(110, 101, 110)) && !room.Cells.Contains(new(100, 120, 100)), "Room must not become a bounding circle or vertical column.");
        void Reject(Action action, string message) { try { action(); check(false, message); } catch (TerritoryDetectionException) { check(true, message); } }
        world.Blocks[(0, 99, 102, 100)] = TerritoryTestWorld.Air;
        Reject(() => detector.Room(new(100, 101, 100)), "Unclosed room was accepted after an earlier native detection.");
        world.Blocks[(0, 99, 102, 100)] = TerritoryTestWorld.Stone;
        world.Box(100, 101, 100, 3, 3, dimension: 1);
        check(detector.Room(new(100, 101, 100, 1)).FloorCells.Count == 9, "Native cache leaked dimensions.");
        world.Unloaded.Add((0, 100, 101, 100));
        Reject(() => detector.Room(new(100, 101, 100)), "Unloaded territory should defer without loading chunks."); world.Unloaded.Clear();
        world.Box(200, 101, 200, 4, 4, 1);
        for (int x = 199; x <= 204; x++) for (int z = 199; z <= 204; z++) world.Blocks[(0, x, 102, z)] = TerritoryTestWorld.Air;
        BuildingTerritoryRegion Yard()
        { var scan = detector.Courtyard(new(200, 101, 200), 16); int ticks = 0; while (!scan.Step(2) && ++ticks < 100) { } return scan.Result!; }
        var yard = Yard(); check(yard.FloorCells.Count == 16 && yard.Inside.Contains(new(200, 99, 200)) && !yard.Inside.Contains(new(200, 104, 200)), "Courtyard surface volume or incremental scan failed.");
        world.Blocks[(0, 200, 100, 200)] = new TerritoryTestWorld.TestBlock(false, [new(0, 0, 0, 1, .9375f, 1)]);
        check(Yard().FloorCells.Count == 16, "Vanilla farmland's non-solid top must still count as a courtyard floor.");
        world.Blocks[(0, 199, 101, 201)] = new TerritoryTestWorld.TestBlock(true, [new(0, 0, 0, .1f, 1, 1)]);
        check(Yard().FloorCells.Count == 16, "Closed edge-mounted gate must seal courtyard.");
        world.Blocks[(0, 199, 101, 201)] = new TerritoryTestWorld.TestBlock(true, [new(0, 0, 0, 1, 1, .1f)]);
        Reject(() => Yard(), "Open rotated gate must leave a gap.");
        world.Blocks[(0, 199, 101, 201)] = TerritoryTestWorld.Stone; world.RainHeight = 103;
        Reject(() => Yard(), "Roofed room must not bypass native limits as courtyard."); world.RainHeight = 0;
        var runs = TerritoryGeometry.Encode(Enumerable.Range(-34, 100).Select(x => new TerritoryCell(x, 2, 3)));
        check(TerritoryGeometry.Decode(runs).Count() == 100 && runs.All(r => (r.X >> 5) == ((r.X + r.Length - 1) >> 5)), "Chunk compression lost negative cells.");
        var tribe = new TribeRecord { X = 100, Y = 101, Z = 100, Regions = [room] }; room.Root = true;
        var index = new BuildingTerritoryIndex(); index.Rebuild([tribe]);
        check(index.At(new(100, 101, 100), 0) == tribe && index.At(new(100, 101, 100), 1) == null && index.At(new(100, 120, 100), 0) == null, "3D territory index crossed floor/dimension.");
        check(TerritoryGeometry.UsedArea([room, room]) == 64, "Overlapping floors charged twice.");
        room.Status = "repair"; check(index.At(new(100, 101, 100), 0) == tribe && !TerritoryGeometry.Reachable([room]).Any(), "Repair must retain ownership but stop expansion.");
        room.Status = "valid";
        var neighbor = BuildingTerritoryDetector.Make("room", new(109, 101, 100), [new(109, 101, 100)], [new(108, 101, 100)], [new(109, 100, 100)]);
        check(TerritoryGeometry.Connected(room, neighbor), "Opposite interiors sharing one wall must connect.");
        neighbor.Interior = TerritoryGeometry.Encode([new TerritoryCell(109, 101, 99)]); neighbor.Invalidate();
        check(!TerritoryGeometry.Connected(room, neighbor), "Corner-only enclosure contact incorrectly connects rooms.");
        FixedTerritoryChecks.Run(check);
    }

}
