using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace SeraphCivilizationSimulation;

// A permanent coordinate frame: removing the first sheet must never move the others.
internal sealed class GroundMapPlacement
{
    internal double X = .25, Z = .25, Height;
    internal int MapX, MapZ, Width;
    internal float Rotation, PaperAngle;
    internal int Face = BlockFacing.UP.Index;
    internal Vec3d Outward => ToWorld(new(0, 1, 0));
    // A surface frame only; paper orientation is independent of compass directions.
    internal Vec3d ToWorld(Vec3d p) => Face switch
    {
        0 => new(-p.X, -p.Z, -p.Y), 1 => new(p.Y, -p.Z, -p.X),
        2 => new(p.X, -p.Z, p.Y), 3 => new(-p.Y, -p.Z, p.X),
        5 => new(p.X, -p.Y, -p.Z), _ => p.Clone()
    };
    internal Vec3d FromWorld(Vec3d p) => Face switch
    {
        0 => new(-p.X, -p.Z, -p.Y), 1 => new(-p.Z, p.X, -p.Y),
        2 => new(p.X, p.Z, -p.Y), 3 => new(p.Z, -p.X, -p.Y),
        5 => new(p.X, -p.Y, -p.Z), _ => p.Clone()
    };
    internal void SetReference(ItemStack first)
    {
        if (Width != 0) return;
        var map = RecordedPaperMap.Describe(first)!;
        MapX = map.CenterX; MapZ = map.CenterZ; Width = map.MapWidth; Rotation = map.Rotation;
    }
    internal void FacePlayer(Vec3d forward)
    {
        if (Face != BlockFacing.UP.Index && Face != BlockFacing.DOWN.Index) { PaperAngle = 0; return; }
        var direction = FromWorld(forward);
        if (double.IsFinite(direction.X) && double.IsFinite(direction.Z) && direction.X * direction.X + direction.Z * direction.Z > 1e-12)
            PaperAngle = (float)Math.Atan2(direction.X, -direction.Z);
    }
    internal MapPoint Ground(MapPoint point)
    {
        double dx = point.X - MapX, dz = point.Z - MapZ, c = Math.Cos(Rotation), s = Math.Sin(Rotation);
        double x = (c * dx + s * dz) * .5 / Width, z = (-s * dx + c * dz) * .5 / Width;
        c = Math.Cos(PaperAngle); s = Math.Sin(PaperAngle);
        return new(X + c * x - s * z, Z + s * x + c * z);
    }
    internal MapPoint Map(MapPoint point)
    {
        double x = (point.X - X) * Width * 2, z = (point.Z - Z) * Width * 2, c = Math.Cos(PaperAngle), s = Math.Sin(PaperAngle);
        double dx = c * x + s * z, dz = -s * x + c * z;
        c = Math.Cos(Rotation); s = Math.Sin(Rotation);
        return new(MapX + c * dx - s * dz, MapZ + s * dx + c * dz);
    }
    internal TreeAttribute Store()
    {
        var tree = new TreeAttribute(); tree.SetDouble("x", X); tree.SetDouble("z", Z); tree.SetDouble("height", Height);
        tree.SetInt("mapx", MapX); tree.SetInt("mapz", MapZ); tree.SetInt("width", Width); tree.SetFloat("rotation", Rotation);
        tree.SetFloat("paperangle", PaperAngle); tree.SetInt("face", Face); return tree;
    }
    internal static GroundMapPlacement Read(ITreeAttribute? tree) => tree == null ? new() : new()
    {
        X = tree.GetDouble("x", .25), Z = tree.GetDouble("z", .25), Height = tree.GetDouble("height"),
        MapX = tree.GetInt("mapx"), MapZ = tree.GetInt("mapz"), Width = tree.GetInt("width"), Rotation = tree.GetFloat("rotation"),
        PaperAngle = float.IsFinite(tree.GetFloat("paperangle")) ? tree.GetFloat("paperangle") : 0,
        Face = Math.Clamp(tree.GetInt("face", BlockFacing.UP.Index), 0, 5)
    };
}

internal readonly record struct GroundMapVertex(MapPoint Map, double X, double Y, double Z)
{
    internal Vec3d Position => new(X, Y, Z);
}

// Collision-box top surfaces are sampled at the game's 1/16 block modelling resolution.
// Uncovered paper folds down at the edge; it is never drawn suspended across empty space.
internal sealed class GroundMapSurface
{
    internal const double Step = 1d / 16;
    internal readonly List<List<List<GroundMapVertex>>> Patches = [];
    internal Vec3d Outward { get; private set; } = new(0, 1, 0);
    internal AtlasViewport MarkerFrame { get; }
    internal bool Supported => Patches.Any(p => p.Count > 0);
    internal GroundMapSurface(GroundMapLayout layout, GroundMapPlacement placement, System.Func<double, double, double?> top, bool drape = true)
    {
        MarkerFrame = new(new(layout.CenterX, layout.CenterZ),
            (int)Math.Ceiling(layout.Span * (Math.Abs(Math.Cos(placement.Rotation)) + Math.Abs(Math.Sin(placement.Rotation)))),
            placement.Rotation, layout.Shapes[0].Dimension);
        var heights = new Dictionary<(int, int), double?>();
        double? Height(int x, int z)
        {
            if (!heights.TryGetValue((x, z), out var y)) heights[(x, z)] = y = top((x + .5) * Step, (z + .5) * Step);
            return y;
        }
        foreach (var sheet in layout.Pieces)
        {
            var patches = new List<List<GroundMapVertex>>(); Patches.Add(patches);
            foreach (var source in sheet)
            {
                var polygon = source.Select(placement.Ground).ToList();
                int minX = (int)Math.Floor(polygon.Min(p => p.X) / Step), maxX = (int)Math.Ceiling(polygon.Max(p => p.X) / Step);
                int minZ = (int)Math.Floor(polygon.Min(p => p.Z) / Step), maxZ = (int)Math.Ceiling(polygon.Max(p => p.Z) / Step);
                for (int x = minX; x < maxX; x++) for (int z = minZ; z < maxZ; z++)
                {
                    var height = Height(x, z); if (height == null || !double.IsFinite(height.Value)) continue;
                    var cell = new List<MapPoint> { new(x * Step, z * Step), new((x + 1) * Step, z * Step), new((x + 1) * Step, (z + 1) * Step), new(x * Step, (z + 1) * Step) };
                    var clipped = MapPolygon.Intersect(polygon, cell); if (MapPolygon.Area(clipped) < 1e-10) continue;
                    patches.Add(clipped.Select(p => new GroundMapVertex(placement.Map(p), p.X, height.Value + .006, p.Z)).ToList());
                    for (int edge = 0; drape && edge < clipped.Count; edge++)
                    {
                        var a = clipped[edge]; var b = clipped[(edge + 1) % clipped.Count]; var mid = (a + b) * .5;
                        int nx = 0, nz = 0;
                        if (Math.Abs(a.X - x * Step) < 1e-8 && Math.Abs(b.X - x * Step) < 1e-8) nx = -1;
                        else if (Math.Abs(a.X - (x + 1) * Step) < 1e-8 && Math.Abs(b.X - (x + 1) * Step) < 1e-8) nx = 1;
                        else if (Math.Abs(a.Z - z * Step) < 1e-8 && Math.Abs(b.Z - z * Step) < 1e-8) nz = -1;
                        else if (Math.Abs(a.Z - (z + 1) * Step) < 1e-8 && Math.Abs(b.Z - (z + 1) * Step) < 1e-8) nz = 1;
                        if (nx == 0 && nz == 0) continue;
                        var direction = new MapPoint(nx, nz);
                        double available = Reach(polygon, mid, direction);
                        if (available < 1e-6) continue;
                        var next = Height(x + nx, z + nz);
                        if (next.HasValue && !double.IsFinite(next.Value)) continue; // A wall is not an unsupported edge.
                        double dropA = Math.Min(Reach(polygon, a, direction), height.Value - (next ?? height.Value - available));
                        double dropB = Math.Min(Reach(polygon, b, direction), height.Value - (next ?? height.Value - available));
                        if (Math.Max(dropA, dropB) < .001) continue;
                        // Source UVs follow the folded paper instead of repeating the edge pixels.
                        patches.Add([
                            new(placement.Map(a), a.X + nx * .006, height.Value + .006, a.Z + nz * .006),
                            new(placement.Map(b), b.X + nx * .006, height.Value + .006, b.Z + nz * .006),
                            new(placement.Map(b + direction * dropB), b.X + nx * .006, height.Value - dropB + .006, b.Z + nz * .006),
                            new(placement.Map(a + direction * dropA), a.X + nx * .006, height.Value - dropA + .006, a.Z + nz * .006)
                        ]);
                    }
                }
            }
        }
    }
    private static double Reach(IReadOnlyList<MapPoint> polygon, MapPoint origin, MapPoint direction)
    {
        double max = .5;
        for (int i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count]; var d = b - a;
            double cross = d.X * (origin.Z - a.Z) - d.Z * (origin.X - a.X);
            double slope = d.X * direction.Z - d.Z * direction.X;
            if (cross < -1e-8 || (Math.Abs(cross) < 1e-8 && slope < -1e-8)) return 0;
            if (slope < -1e-10) max = Math.Min(max, cross / -slope);
        }
        return Math.Max(0, max);
    }
    internal static GroundMapSurface Build(IWorldAccessor world, BlockPos anchor, GroundMapLayout layout, GroundMapPlacement placement)
    {
        var boxes = new Dictionary<(int, int, int), Cuboidf[]>();
        var surface = new GroundMapSurface(layout, placement, (x, z) =>
        {
            int bx = (int)Math.Floor(x), bz = (int)Math.Floor(z);
            for (int y = (int)Math.Floor(placement.Height + .5); y >= (int)Math.Floor(placement.Height - 1); y--)
            {
                var offset = placement.ToWorld(new(bx + .5, y + .5, bz + .5));
                var pos = anchor.AddCopy((int)Math.Floor(offset.X), (int)Math.Floor(offset.Y), (int)Math.Floor(offset.Z));
                if (!boxes.TryGetValue((bx, y, bz), out var collision))
                {
                    var block = world.BlockAccessor.GetBlock(pos);
                    collision = block is BlockGroundMap ? [] : block?.GetCollisionBoxes(world.BlockAccessor, pos) ?? [];
                    if (placement.Face != BlockFacing.UP.Index)
                        collision = collision.Select(box =>
                        {
                            var a = placement.FromWorld(new(pos.X - anchor.X + box.X1, pos.Y - anchor.Y + box.Y1, pos.Z - anchor.Z + box.Z1));
                            var b = placement.FromWorld(new(pos.X - anchor.X + box.X2, pos.Y - anchor.Y + box.Y2, pos.Z - anchor.Z + box.Z2));
                            return new Cuboidf((float)(Math.Min(a.X, b.X) - bx), (float)(Math.Min(a.Y, b.Y) - y), (float)(Math.Min(a.Z, b.Z) - bz),
                                (float)(Math.Max(a.X, b.X) - bx), (float)(Math.Max(a.Y, b.Y) - y), (float)(Math.Max(a.Z, b.Z) - bz));
                        }).ToArray();
                    boxes[(bx, y, bz)] = collision;
                }
                double top = double.NegativeInfinity;
                foreach (var box in collision)
                    if (x - bx >= box.X1 && x - bx <= box.X2 && z - bz >= box.Z1 && z - bz <= box.Z2)
                    {
                        if (y + box.Y1 <= placement.Height + .501 && y + box.Y2 > placement.Height + .501) return double.NaN;
                        if (y + box.Y2 <= placement.Height + .501 && y + box.Y2 >= placement.Height - 1) top = Math.Max(top, y + box.Y2);
                    }
                if (double.IsFinite(top) && world.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid)?.Id is null or 0) return top;
            }
            return null;
        }, placement.Face == BlockFacing.UP.Index);
        surface.Outward = placement.Outward;
        if (placement.Face != BlockFacing.UP.Index)
            foreach (var sheet in surface.Patches) foreach (var patch in sheet)
                for (int i = 0; i < patch.Count; i++)
                {
                    var vertex = patch[i]; var point = placement.ToWorld(vertex.Position);
                    patch[i] = new(vertex.Map, point.X, point.Y, point.Z);
                }
        return surface;
    }
    internal (int Index, Vec3d Point, double Distance)? Hit(Vec3d origin, Vec3d direction, double limit = 6)
    {
        (int Index, Vec3d Point, double Distance)? result = null;
        for (int index = 0; index < Patches.Count; index++) foreach (var polygon in Patches[index])
            for (int j = 1; j < polygon.Count - 1; j++)
            {
                var a = polygon[0].Position; var e1 = polygon[j].Position - a; var e2 = polygon[j + 1].Position - a;
                var p = direction.Cross(e2); double det = e1.Dot(p); if (Math.Abs(det) < 1e-10) continue;
                var t = origin - a; double u = t.Dot(p) / det; if (u < -1e-7 || u > 1.0000001) continue;
                var q = t.Cross(e1); double v = direction.Dot(q) / det; if (v < -1e-7 || u + v > 1.0000001) continue;
                double distance = e2.Dot(q) / det;
                if (distance >= 0 && distance <= limit && (result == null || distance < result.Value.Distance)) result = (index, origin + direction * distance, distance);
            }
        return result;
    }
    internal static bool Visible(IWorldAccessor world, BlockPos anchor, Vec3d from, Vec3d to)
    {
        // Traverse actual collision shapes, since a furniture selection box can include empty air.
        var origin = from.AddCopy(anchor.X, anchor.Y, anchor.Z);
        var end = to.AddCopy(anchor.X, anchor.Y, anchor.Z);
        var direction = end - origin; double distance = direction.Length();
        if (distance < .001) return true;
        direction /= distance;
        int[] cell = [(int)Math.Floor(origin.X), (int)Math.Floor(origin.Y), (int)Math.Floor(origin.Z)];
        double[] o = [origin.X, origin.Y, origin.Z], d = [direction.X, direction.Y, direction.Z];
        var next = new double[3]; var delta = new double[3];
        for (int axis = 0; axis < 3; axis++)
        {
            delta[axis] = Math.Abs(d[axis]) < 1e-12 ? double.PositiveInfinity : Math.Abs(1 / d[axis]);
            next[axis] = Math.Abs(d[axis]) < 1e-12 ? double.PositiveInfinity : (cell[axis] + (d[axis] > 0 ? 1 : 0) - o[axis]) / d[axis];
        }
        double travelled = 0;
        while (travelled < distance - .025)
        {
            var pos = new BlockPos(cell[0], cell[1], cell[2], anchor.dimension);
            var block = world.BlockAccessor.GetBlock(pos);
            foreach (var box in block is BlockGroundMap ? [] : block?.GetCollisionBoxes(world.BlockAccessor, pos) ?? [])
            {
                double near = 0, far = distance - .025;
                double[] min = [cell[0] + box.X1, cell[1] + box.Y1, cell[2] + box.Z1];
                double[] max = [cell[0] + box.X2, cell[1] + box.Y2, cell[2] + box.Z2];
                for (int axis = 0; axis < 3 && near <= far; axis++)
                {
                    if (Math.Abs(d[axis]) < 1e-12) { if (o[axis] < min[axis] || o[axis] > max[axis]) near = double.PositiveInfinity; }
                    else
                    {
                        double a = (min[axis] - o[axis]) / d[axis], b = (max[axis] - o[axis]) / d[axis];
                        near = Math.Max(near, Math.Min(a, b)); far = Math.Min(far, Math.Max(a, b));
                    }
                }
                if (near <= far) return false;
            }
            int stepAxis = next[0] < next[1] ? (next[0] < next[2] ? 0 : 2) : (next[1] < next[2] ? 1 : 2);
            travelled = next[stepAxis]; cell[stepAxis] += Math.Sign(d[stepAxis]); next[stepAxis] += delta[stepAxis];
        }
        return true;
    }
    internal IEnumerable<(Vec3d A, Vec3d B)> Outline(int index, bool whole = false)
    {
        var edges = new Dictionary<((long, long, long), (long, long, long)), (Vec3d A, Vec3d B, int Count)>();
        (long, long, long) Key(Vec3d p) => ((long)Math.Round(p.X * 1000000), (long)Math.Round(p.Y * 1000000), (long)Math.Round(p.Z * 1000000));
        foreach (var polygon in whole ? Patches.SelectMany(p => p) : Patches[index]) for (int j = 0; j < polygon.Count; j++)
        {
            var a = polygon[j].Position; var b = polygon[(j + 1) % polygon.Count].Position;
            var ak = Key(a); var bk = Key(b); var key = ak.CompareTo(bk) < 0 ? (ak, bk) : (bk, ak);
            edges[key] = edges.TryGetValue(key, out var old) ? (a, b, old.Count + 1) : (a, b, 1);
        }
        return edges.Values.Where(e => e.Count == 1).Select(e => (e.A, e.B));
    }
}
