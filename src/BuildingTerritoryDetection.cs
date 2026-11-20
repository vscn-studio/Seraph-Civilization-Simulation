using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace SeraphCivilizationSimulation;

internal sealed class TerritoryDetectionException(string message) : Exception(message);

// All accesses run on the server thread; detectors never request or generate chunks.
internal sealed class BuildingTerritoryDetector(ICoreAPI api)
{
    // 1.22's public registry cache key omits dimension and may retain a room until
    // ChunkDirty. Confirmation must run the native flood fill against current blocks.
    private static readonly System.Func<RoomRegistry, BlockPos, ChunkRooms, Room> NativeScan =
        (typeof(RoomRegistry).GetMethod("FindRoomForPosition", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingMethodException("Unsupported native RoomRegistry version."))
        .CreateDelegate<System.Func<RoomRegistry, BlockPos, ChunkRooms, Room>>();
    private IBlockAccessor Blocks => api.World.BlockAccessor;
    private bool SolidFloor(BlockPos pos)
    {
        if (Blocks.IsSideSolid(pos.X, pos.InternalY, pos.Z, BlockFacing.UP)) return true;
        // Vanilla farmland is 15/16 high and explicitly marks its top as non-solid.
        // Its full footprint still supports a garden surface and a safe standing position.
        return Blocks.GetBlock(pos).GetCollisionBoxes(Blocks, pos)?.Any(b => b.X1 <= .01 && b.Z1 <= .01
            && b.X2 >= .99 && b.Z2 >= .99 && b.Y2 >= .875) == true;
    }
    internal void RequireLoaded(BlockPos pos)
    {
        if (!Blocks.IsValidPos(pos)) throw new TerritoryDetectionException("超出世界边界。");
        if (Blocks.GetChunkAtBlockPos(pos) == null) throw new TerritoryDetectionException("区域区块未加载，请靠近后重试。");
    }
    internal BuildingTerritoryRegion Room(BlockPos seed)
    {
        RequireLoaded(seed);
        if (!ClearBody(seed)) throw new TerritoryDetectionException("请站在房间内部检测。");
        var room = NativeScan(api.ModLoader.GetModSystem<RoomRegistry>(), seed, new ChunkRooms());
        if (room == null || room.AnyChunkUnloaded != 0) throw new TerritoryDetectionException("房间尚未完整加载。");
        if (room.ExitCount != 0) throw new TerritoryDetectionException("房间不密闭或超出原版房间检测尺寸，请关闭门窗、修补缺口或分隔房间。");
        var cells = new HashSet<TerritoryCell>();
        // Native Room.Contains uses InternalY for its bounds but its bitmap uses local Y.
        // Read the local bitmap with dimension zero; world accesses below keep seed.dimension.
        for (int y = room.Location.Y1; y <= room.Location.Y2; y++)
        for (int z = room.Location.Z1; z <= room.Location.Z2; z++)
        for (int x = room.Location.X1; x <= room.Location.X2; x++)
            if (room.Contains(new BlockPos(x, y, z, 0))) cells.Add(new(x, y, z));
        if (cells.Count == 0 || cells.Count > 32768) throw new TerritoryDetectionException("房间检测结果无效。");
        var shell = new HashSet<TerritoryCell>(); var floors = new HashSet<TerritoryCell>();
        foreach (var cell in cells)
        {
            var pos = cell.Position(seed.dimension); RequireLoaded(pos);
            foreach (var adjacent in cell.Neighbors())
            {
                if (cells.Contains(adjacent)) continue;
                var next = adjacent.Position(seed.dimension); RequireLoaded(next);
                if (Blocks.GetBlock(next).Id != 0) shell.Add(adjacent);
            }
            var below = cell with { Y = cell.Y - 1 };
            // Count the physical surface even under furniture/crops. Heat-permeable
            // ground (farmland) can itself be present in the native room bitmap.
            if (SolidFloor(below.Position(seed.dimension)) && !SolidFloor(pos)) floors.Add(below);
        }
        if (floors.Count == 0) throw new TerritoryDetectionException("房间没有可使用的地面。");
        return Make("room", seed, cells, shell, floors);
    }
    internal bool ClearBody(BlockPos pos)
    {
        RequireLoaded(pos);
        var block = Blocks.GetBlock(pos);
        return !Blocks.GetBlock(pos, BlockLayersAccess.Fluid).IsLiquid()
            && (block.GetCollisionBoxes(Blocks, pos)?.Length ?? 0) == 0;
    }
    internal bool Safe(BlockPos feet)
    {
        RequireLoaded(feet); RequireLoaded(feet.DownCopy()); RequireLoaded(feet.UpCopy());
        return SolidFloor(feet.DownCopy())
            && !Blocks.GetBlock(feet.DownCopy(), BlockLayersAccess.Fluid).IsLiquid() && ClearBody(feet) && ClearBody(feet.UpCopy());
    }
    internal static BuildingTerritoryRegion Make(string kind, BlockPos seed, IEnumerable<TerritoryCell> inside,
        IEnumerable<TerritoryCell> shell, IEnumerable<TerritoryCell> floors) => new()
    {
        Kind = kind, Dimension = seed.dimension, X = seed.X, Y = seed.Y, Z = seed.Z, Status = "valid",
        Interior = TerritoryGeometry.Encode(inside), Structure = TerritoryGeometry.Encode(shell), Floors = TerritoryGeometry.Encode(floors)
    };

    internal CourtyardScan Courtyard(BlockPos seed, int maximum) => new(this, seed, maximum);
    internal sealed class CourtyardScan
    {
        private readonly BuildingTerritoryDetector detector;
        private readonly BlockPos seed;
        private readonly int maximum;
        private readonly Queue<TerritoryCell> pending = new();
        private readonly Dictionary<(int X, int Z), TerritoryCell> surface = [];
        private readonly HashSet<TerritoryCell> walls = [];
        internal BuildingTerritoryRegion? Result { get; private set; }
        internal CourtyardScan(BuildingTerritoryDetector detector, BlockPos seed, int maximum)
        {
            this.detector = detector; this.seed = seed.Copy(); this.maximum = maximum;
            var floor = FindFloor(seed.X, seed.Y - 1, seed.Z);
            if (floor == null) throw new TerritoryDetectionException("请站在院落地面上检测。");
            surface[(floor.Value.X, floor.Value.Z)] = floor.Value; pending.Enqueue(floor.Value);
        }
        private TerritoryCell? FindFloor(int x, int y, int z)
        {
            foreach (int offset in new[] { 0, -1, 1 })
            {
                var floor = new BlockPos(x, y + offset, z, seed.dimension); detector.RequireLoaded(floor);
                bool support = detector.SolidFloor(floor);
                // Shallow irrigation channels remain part of the yard; deep water does not.
                if (!support && detector.Blocks.GetBlock(floor, BlockLayersAccess.Fluid).IsLiquid())
                { detector.RequireLoaded(floor.DownCopy()); support = detector.SolidFloor(floor.DownCopy()); }
                if (support
                    && detector.ClearBody(floor.UpCopy()) && detector.ClearBody(floor.UpCopy(2))) return new(x, floor.Y, z);
            }
            return null;
        }
        // Require a full horizontal collision barrier at ankle and waist height. A crop,
        // isolated furniture leg, open gate or thin disconnected post cannot seal a boundary.
        private bool Barrier(TerritoryCell floor, BlockFacing facing, out TerritoryCell wall)
        {
            wall = new(floor.X + facing.Normali.X, floor.Y + 1, floor.Z + facing.Normali.Z);
            var pos = wall.Position(seed.dimension); detector.RequireLoaded(pos);
            var block = detector.Blocks.GetBlock(pos);
            var boxes = block.GetCollisionBoxes(detector.Blocks, pos);
            if (boxes == null || boxes.Length == 0) return false;
            bool crossesX = facing.Normali.X != 0;
            // Union projected collision intervals: accept fences made of multiple rail boxes.
            foreach (float height in new[] { .25f, .75f })
            {
                var intervals = boxes.Where(b => b.Y1 <= height && b.Y2 >= height)
                    .Select(b => crossesX ? (Start: b.Z1, End: b.Z2) : (Start: b.X1, End: b.X2)).OrderBy(b => b.Start);
                float end = 0;
                foreach (var interval in intervals) { if (interval.Start > end + .01f) break; end = Math.Max(end, interval.End); }
                if (end < .99f) return false;
            }
            return true;
        }
        internal bool Step(int budget)
        {
            for (int i = 0; i < budget && pending.TryDequeue(out var floor); i++)
            foreach (var facing in BlockFacing.HORIZONTALS)
            {
                int x = floor.X + facing.Normali.X, z = floor.Z + facing.Normali.Z;
                if (surface.ContainsKey((x, z))) continue;
                if (Barrier(floor, facing, out var wall))
                {
                    walls.Add(wall);
                    // Include continuous wall body only, capped independently of yard volume.
                    for (int h = 1; h < 14; h++)
                    {
                        var upper = wall with { Y = wall.Y + h }; var p = upper.Position(seed.dimension); detector.RequireLoaded(p);
                        if ((detector.Blocks.GetBlock(p).GetCollisionBoxes(detector.Blocks, p)?.Length ?? 0) == 0) break;
                        walls.Add(upper);
                    }
                    continue;
                }
                var next = FindFloor(x, floor.Y, z);
                if (next == null) throw new TerritoryDetectionException("院落边界存在缺口、深沟或无法识别的围护，请修补后重试。");
                if (surface.Count >= maximum) throw new TerritoryDetectionException("院落不封闭或超过单院面积上限。");
                surface[(x, z)] = next.Value; pending.Enqueue(next.Value);
            }
            if (pending.Count != 0) return false;
            if (walls.Count == 0) throw new TerritoryDetectionException("没有检测到完整围墙。");
            if (!surface.Values.Any(p => detector.Blocks.GetRainMapHeightAt(p.Position(seed.dimension)) <= p.Y + 1))
                throw new TerritoryDetectionException("院落需要露天空间；有完整屋顶的建筑请按房间检测。");
            var inside = new HashSet<TerritoryCell>();
            foreach (var floor in surface.Values)
                for (int h = -1; h <= 3; h++) inside.Add(floor with { Y = floor.Y + h });
            Result = Make("courtyard", seed, inside, walls, surface.Values);
            return true;
        }
    }
}

