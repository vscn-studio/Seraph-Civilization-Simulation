using Newtonsoft.Json;
using ProtoBuf;
using Vintagestory.API.MathTools;

namespace SeraphCivilizationSimulation;

public readonly record struct TerritoryCell(int X, int Y, int Z)
{
    public BlockPos Position(int dimension) => new(X, Y, Z, dimension);
    public IEnumerable<TerritoryCell> Neighbors()
    {
        yield return this with { X = X - 1 }; yield return this with { X = X + 1 };
        yield return this with { Y = Y - 1 }; yield return this with { Y = Y + 1 };
        yield return this with { Z = Z - 1 }; yield return this with { Z = Z + 1 };
    }
}

// Runs never cross a 32-block chunk boundary. Interior/structure/floor sets remain distinct.
[ProtoContract]
public sealed class TerritoryRun
{
    [ProtoMember(1)] public int X { get; set; }
    [ProtoMember(2)] public int Y { get; set; }
    [ProtoMember(3)] public int Z { get; set; }
    [ProtoMember(4)] public int Length { get; set; }
}

public sealed class BuildingTerritoryRegion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SiteId { get; set; } = "main";
    public bool Root { get; set; }
    public string Kind { get; set; } = "room";
    public int Dimension { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public long Revision { get; set; } = 1;
    public string Status { get; set; } = "loading";
    public List<TerritoryRun> Interior { get; set; } = [];
    public List<TerritoryRun> Structure { get; set; } = [];
    public List<TerritoryRun> Floors { get; set; } = [];
    [JsonIgnore] public HashSet<TerritoryCell> Cells => cells ??= TerritoryGeometry.Decode(Interior).Concat(TerritoryGeometry.Decode(Structure)).ToHashSet();
    private HashSet<TerritoryCell>? cells;
    [JsonIgnore] public HashSet<TerritoryCell> FloorCells => floors ??= TerritoryGeometry.Decode(Floors).ToHashSet();
    private HashSet<TerritoryCell>? floors;
    [JsonIgnore] public HashSet<TerritoryCell> Inside => inside ??= TerritoryGeometry.Decode(Interior).ToHashSet();
    private HashSet<TerritoryCell>? inside;
    [JsonIgnore] internal Dictionary<(int X, int Y, int Z), List<TerritoryRun>> Chunks => chunks ??= Cells
        .GroupBy(p => (p.X >> 5, p.Y >> 5, p.Z >> 5)).ToDictionary(g => g.Key, g => TerritoryGeometry.Encode(g));
    private Dictionary<(int X, int Y, int Z), List<TerritoryRun>>? chunks;
    public void Invalidate() { cells = floors = inside = null; chunks = null; }
}

public sealed class BuildingTerritoryConfig
{
    public int BaseArea { get; set; } = 256;
    public int AreaPerMember { get; set; } = 128;
    public int MaxCourtyardArea { get; set; } = 1024;
    public int MaxRegions { get; set; } = 256;
    public int MaxTerritoryCells { get; set; } = 500000;
    public int DetectionBatchSize { get; set; } = 128;
    public int MaxQueuedDetections { get; set; } = 128;
    public void Validate()
    {
        if (BaseArea is < 1 or > 100000 || AreaPerMember is < 0 or > 10000 || MaxCourtyardArea is < 1 or > 4096
            || MaxRegions is < 1 or > 1024 || MaxTerritoryCells is < 4096 or > 2000000
            || DetectionBatchSize is < 16 or > 512 || MaxQueuedDetections is < 8 or > 512)
            throw new ArgumentException("Invalid building territory limits.");
    }
}

internal static class TerritoryGeometry
{
    internal static List<TerritoryRun> Encode(IEnumerable<TerritoryCell> cells)
    {
        var result = new List<TerritoryRun>(); TerritoryRun? run = null;
        foreach (var p in cells.Distinct().OrderBy(p => p.Y).ThenBy(p => p.Z).ThenBy(p => p.X))
        {
            if (run != null && run.Y == p.Y && run.Z == p.Z && run.X + run.Length == p.X && (run.X >> 5) == (p.X >> 5)) run.Length++;
            else { run = new() { X = p.X, Y = p.Y, Z = p.Z, Length = 1 }; result.Add(run); }
        }
        return result;
    }
    internal static IEnumerable<TerritoryCell> Decode(IEnumerable<TerritoryRun> runs)
    {
        foreach (var run in runs)
        {
            if (run.Length is < 1 or > 32 || run.X > int.MaxValue - run.Length
                || (run.X >> 5) != ((run.X + run.Length - 1) >> 5)) throw new InvalidDataException("Invalid territory run.");
            for (int i = 0; i < run.Length; i++) yield return new(run.X + i, run.Y, run.Z);
        }
    }
    internal static bool Connected(BuildingTerritoryRegion a, BuildingTerritoryRegion b)
    {
        if (a.Dimension != b.Dimension) return false;
        // Common enclosing blocks (shared wall/floor) or a shared interior face count;
        // touching only at an edge/corner never connects separate buildings.
        if (a.Inside.Overlaps(b.Inside)) return true;
        // Interiors must meet directly, or lie on opposite sides of a shared wall.
        // Merely sharing a corner of two enclosing shells does not connect them.
        return a.Inside.Any(p => BlockFacing.ALLFACES.Any(face =>
        {
            var n = new TerritoryCell(p.X + face.Normali.X, p.Y + face.Normali.Y, p.Z + face.Normali.Z);
            return b.Inside.Contains(n) || a.Cells.Contains(n) && b.Cells.Contains(n)
                && b.Inside.Contains(new(n.X + face.Normali.X, n.Y + face.Normali.Y, n.Z + face.Normali.Z));
        }));
    }
    internal static HashSet<string> Reachable(IEnumerable<BuildingTerritoryRegion> source)
    {
        var regions = source.Where(r => r.Status == "valid").ToArray();
        var reached = regions.Where(r => r.Root).Select(r => r.Id).ToHashSet();
        bool changed;
        do
        {
            changed = false;
            foreach (var region in regions.Where(r => !reached.Contains(r.Id)))
                if (regions.Any(r => r.SiteId == region.SiteId && reached.Contains(r.Id) && Connected(r, region)))
                    changed |= reached.Add(region.Id);
        } while (changed);
        return reached;
    }
    internal static int UsedArea(IEnumerable<BuildingTerritoryRegion> regions) => regions
        .SelectMany(r => r.FloorCells.Select(p => (r.Dimension, p))).Distinct().Count();
    internal static bool SameShape(BuildingTerritoryRegion a, BuildingTerritoryRegion b) => a.Dimension == b.Dimension
        && a.Inside.SetEquals(b.Inside) && a.Cells.SetEquals(b.Cells) && a.FloorCells.SetEquals(b.FloorCells);
}

// Chunk buckets avoid walking every region/voxel for each block protection or player effect query.
internal sealed class BuildingTerritoryIndex
{
    private readonly Dictionary<(int D, int X, int Y, int Z), List<(TribeRecord Tribe, BuildingTerritoryRegion Region)>> buckets = [];
    private readonly Dictionary<(int D, int X, int Z), List<(TribeRecord Tribe, RectangularTerritorySite Site)>> sites = [];
    internal void Rebuild(IEnumerable<TribeRecord> tribes)
    {
        buckets.Clear(); sites.Clear();
        foreach (var tribe in tribes)
        foreach (var site in tribe.Sites)
        for (int x = site.Bounds.MinX >> 5; x <= site.Bounds.MaxX >> 5; x++)
        for (int z = site.Bounds.MinZ >> 5; z <= site.Bounds.MaxZ >> 5; z++)
        {
            var key = (site.Dimension,x,z);
            if (!sites.TryGetValue(key, out var entries)) sites[key] = entries = [];
            entries.Add((tribe,site));
        }
        foreach (var tribe in tribes.Where(t => t.TerritoryVersion < 4))
        foreach (var region in tribe.Regions)
        foreach (var p in region.Chunks.Keys)
        {
            var chunk = (region.Dimension, p.X, p.Y, p.Z);
            if (!buckets.TryGetValue(chunk, out var list)) buckets[chunk] = list = [];
            list.Add((tribe, region));
        }
    }
    internal TribeRecord? At(TerritoryCell cell, int dimension, string? tribeId = null)
    {
        if (sites.TryGetValue((dimension,cell.X >> 5,cell.Z >> 5), out var rectangles))
            foreach (var entry in rectangles)
                if ((tribeId == null || entry.Tribe.Id == tribeId) && entry.Site.Contains(cell,dimension)) return entry.Tribe;
        return buckets.TryGetValue((dimension, cell.X >> 5, cell.Y >> 5, cell.Z >> 5), out var list)
            ? list.FirstOrDefault(r => (tribeId == null || r.Tribe.Id == tribeId) && r.Region.Cells.Contains(cell)).Tribe : null;
    }
    internal IEnumerable<BuildingTerritoryRegion> NearChunk(int x, int internalY, int z)
    {
        var seen = new HashSet<string>();
        int dimension = internalY / (BlockPos.DimensionBoundary / 32), y = internalY % (BlockPos.DimensionBoundary / 32);
        for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
            if (buckets.TryGetValue((dimension, x + dx, y + dy, z + dz), out var list))
                foreach (var entry in list) if (seen.Add(entry.Region.Id)) yield return entry.Region;
    }
}

[ProtoContract]
public sealed class TerritoryRegionView
{
    [ProtoMember(1)] public string Id { get; set; } = "";
    [ProtoMember(2)] public string SiteId { get; set; } = "";
    [ProtoMember(3)] public string Kind { get; set; } = "";
    [ProtoMember(4)] public string Status { get; set; } = "";
    [ProtoMember(5)] public int Area { get; set; }
    [ProtoMember(6)] public bool Root { get; set; }
    [ProtoMember(7)] public int X { get; set; }
    [ProtoMember(8)] public int Y { get; set; }
    [ProtoMember(9)] public int Z { get; set; }
    [ProtoMember(10)] public int Dimension { get; set; }
    [ProtoMember(11)] public long Revision { get; set; }
}

[ProtoContract]
public sealed class TerritoryCandidateView
{
    [ProtoMember(1)] public string Id { get; set; } = "";
    [ProtoMember(2)] public string Purpose { get; set; } = "";
    [ProtoMember(3)] public string Kind { get; set; } = "";
    [ProtoMember(4)] public int Area { get; set; }
    [ProtoMember(5)] public string Message { get; set; } = "";
    [ProtoMember(6)] public int X { get; set; }
    [ProtoMember(7)] public int Y { get; set; }
    [ProtoMember(8)] public int Z { get; set; }
    [ProtoMember(9)] public TerritoryRectangle? Bounds { get; set; }
    [ProtoMember(10)] public int PreviousArea { get; set; }
    [ProtoMember(11)] public long SupportLimit { get; set; }
    [ProtoMember(12)] public long StableSecondsRemaining { get; set; }
    [ProtoMember(13)] public bool Ready { get; set; }
    [ProtoMember(14)] public List<string> ReleasedRegions { get; set; } = [];
}
