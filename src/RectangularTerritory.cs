using Newtonsoft.Json;
using ProtoBuf;

namespace SeraphCivilizationSimulation;

public sealed class RectangularTerritoryConfig
{
    public int BaseArea { get; set; } = 1024;
    public int AreaPerMember { get; set; } = 256;
    public int InitialPadding { get; set; } = 8;
    public int ExpansionPadding { get; set; } = 4;
    public int MinimumRoomArea { get; set; } = 16;
    public int MinimumRoomSide { get; set; } = 3;
    public double MinimumFloorCoverage { get; set; } = .6;
    public int StableMinutes { get; set; } = 10;
    public int ExpansionCooldownMinutes { get; set; } = 30;
    public int MaximumGap { get; set; } = 8;
    public int MaximumSide { get; set; } = 128;
    public int MaximumAspectRatio { get; set; } = 3;
    public int AreaPerRoomFloor { get; set; } = 16;
    public int BelowSurface { get; set; } = 16;
    public int AboveSurface { get; set; } = 32;
    public void Validate()
    {
        if (BaseArea is < 1 or > 100000 || AreaPerMember is < 0 or > 10000 || InitialPadding is < 1 or > 32
            || ExpansionPadding is < 0 or > 32 || MinimumRoomArea is < 1 or > 196 || MinimumRoomSide is < 1 or > 14
            || !double.IsFinite(MinimumFloorCoverage) || MinimumFloorCoverage is <= 0 or > 1
            || StableMinutes is < 0 or > 1440 || ExpansionCooldownMinutes is < 0 or > 1440 || MaximumGap is < 0 or > 32
            || MaximumSide is < 16 or > 256 || MaximumAspectRatio is < 1 or > 8 || AreaPerRoomFloor is < 1 or > 64
            || BelowSurface is < 0 or > 64 || AboveSurface is < 1 or > 128) throw new ArgumentException("Invalid rectangular territory settings.");
    }
}

[ProtoContract]
public sealed class TerritoryRectangle
{
    [ProtoMember(1)] public int MinX { get; set; }
    [ProtoMember(2)] public int MinZ { get; set; }
    [ProtoMember(3)] public int MaxX { get; set; }
    [ProtoMember(4)] public int MaxZ { get; set; }
    [JsonIgnore] public int Width => checked(MaxX - MinX + 1);
    [JsonIgnore] public int Length => checked(MaxZ - MinZ + 1);
    [JsonIgnore] public int Area => checked(Width * Length);
    public bool Contains(int x, int z) => x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;
    internal bool Same(TerritoryRectangle b) => MinX == b.MinX && MaxX == b.MaxX && MinZ == b.MinZ && MaxZ == b.MaxZ;
    internal TerritoryRectangle Union(TerritoryRectangle b) => new() { MinX = Math.Min(MinX, b.MinX), MaxX = Math.Max(MaxX, b.MaxX), MinZ = Math.Min(MinZ, b.MinZ), MaxZ = Math.Max(MaxZ, b.MaxZ) };
    internal TerritoryRectangle Pad(int p) => new() { MinX = checked(MinX - p), MaxX = checked(MaxX + p), MinZ = checked(MinZ - p), MaxZ = checked(MaxZ + p) };
    internal static TerritoryRectangle Around(IEnumerable<TerritoryCell> cells)
    { var all = cells.ToArray(); return new() { MinX = all.Min(p => p.X), MaxX = all.Max(p => p.X), MinZ = all.Min(p => p.Z), MaxZ = all.Max(p => p.Z) }; }
    internal bool Adjacent(TerritoryRectangle other, int gap)
    {
        bool overlapX = MinX <= other.MaxX && MaxX >= other.MinX, overlapZ = MinZ <= other.MaxZ && MaxZ >= other.MinZ;
        int dx = Math.Max(0, Math.Max(MinX - other.MaxX - 1, other.MinX - MaxX - 1));
        int dz = Math.Max(0, Math.Max(MinZ - other.MaxZ - 1, other.MinZ - MaxZ - 1));
        return overlapX && dz <= gap || overlapZ && dx <= gap;
    }
}

// Heights are row-major Z/X; only one integer per horizontal column, never a filled voxel volume.
public sealed class TerritoryNamedArea
{
    public string Name { get; set; } = "";
    public List<TerritoryRun> Footprint { get; set; } = [];
    [JsonIgnore] private HashSet<TerritoryCell>? cells;
    [JsonIgnore] internal HashSet<TerritoryCell> Cells => cells ??= TerritoryGeometry.Decode(Footprint).ToHashSet();
}

public sealed class RectangularTerritorySite
{
    public List<TerritoryNamedArea> NamedAreas { get; set; } = [];
    public string Id { get; set; } = "main";
    public int Dimension { get; set; }
    public TerritoryRectangle Bounds { get; set; } = new();
    public int[] Heights { get; set; } = [];
    // Null preserves the original rectangular footprint. Runs encode the
    // horizontal footprint at Y=0 for drawn territories.
    private List<TerritoryRun>? footprint;
    public List<TerritoryRun>? Footprint { get => footprint; set { footprint = value; footprintCells = null; } }
    [JsonIgnore] private HashSet<TerritoryCell>? footprintCells;
    [JsonIgnore] internal HashSet<TerritoryCell>? FootprintCells => Footprint == null ? null : footprintCells ??= TerritoryGeometry.Decode(Footprint).ToHashSet();
    internal bool ContainsColumn(int x, int z) => Bounds.Contains(x, z) && (FootprintCells == null || FootprintCells.Contains(new(x, 0, z)));
    internal int ColumnCount => FootprintCells?.Count ?? Bounds.Area;
    public int Below { get; set; } = 16;
    public int Above { get; set; } = 32;
    public int InitialArea { get; set; }
    public long ExpandedAt { get; set; }
    public long Revision { get; set; } = 1;
    public int Height(int x, int z) => Heights[(z - Bounds.MinZ) * Bounds.Width + x - Bounds.MinX];
    public bool Contains(TerritoryCell p, int dimension) => dimension == Dimension && ContainsColumn(p.X, p.Z)
        && p.Y >= Math.Max(0, Height(p.X, p.Z) - Below) && (Above < 0 || p.Y <= Height(p.X, p.Z) + Above);
    internal IEnumerable<TerritoryCell> Surface()
    {
        for (int z = Bounds.MinZ; z <= Bounds.MaxZ; z++) for (int x = Bounds.MinX; x <= Bounds.MaxX; x++) if (ContainsColumn(x,z)) yield return new(x, Height(x,z), z);
    }
    internal IEnumerable<TerritoryCell> Outline()
    {
        if (FootprintCells is { } cells)
        {
            foreach (var p in cells)
                if (!ContainsColumn(p.X - 1, p.Z) || !ContainsColumn(p.X + 1, p.Z)
                    || !ContainsColumn(p.X, p.Z - 1) || !ContainsColumn(p.X, p.Z + 1))
                    yield return new(p.X, Height(p.X, p.Z), p.Z);
            yield break;
        }
        for (int x=Bounds.MinX; x<=Bounds.MaxX; x++)
        {
            yield return new(x,Height(x,Bounds.MinZ),Bounds.MinZ);
            if (Bounds.MaxZ!=Bounds.MinZ) yield return new(x,Height(x,Bounds.MaxZ),Bounds.MaxZ);
        }
        for (int z=Bounds.MinZ+1; z<Bounds.MaxZ; z++)
        {
            yield return new(Bounds.MinX,Height(Bounds.MinX,z),z);
            if (Bounds.MaxX!=Bounds.MinX) yield return new(Bounds.MaxX,Height(Bounds.MaxX,z),z);
        }
    }
}

public sealed class TerritoryRoomObservation
{
    public BuildingTerritoryRegion Room { get; set; } = new();
    public long StableSince { get; set; }
    public long LastChecked { get; set; }
}

[ProtoContract]
public sealed class TerritorySiteView
{
    [ProtoMember(1)] public string Id { get; set; } = "";
    [ProtoMember(2)] public int Dimension { get; set; }
    [ProtoMember(3)] public TerritoryRectangle Bounds { get; set; } = new();
    [ProtoMember(4)] public int Area { get; set; }
    [ProtoMember(5)] public long SupportLimit { get; set; }
    [ProtoMember(6)] public long CooldownSeconds { get; set; }
    [ProtoMember(7)] public string Status { get; set; } = "";
}

internal static class RectangularTerritoryRules
{
    internal static int Footprint(IEnumerable<BuildingTerritoryRegion> rooms) => rooms.SelectMany(r => r.FloorCells.Select(p => (r.Dimension,p.X,p.Z))).Distinct().Count();
    internal static long Support(RectangularTerritorySite site, IEnumerable<BuildingTerritoryRegion> rooms, RectangularTerritoryConfig config)
        => Math.Max(site.InitialArea, (long)Footprint(rooms.Where(r => r.SiteId == site.Id && r.Status == "valid" && r.Kind == "room")) * config.AreaPerRoomFloor);
    internal static bool Qualifies(BuildingTerritoryRegion r, RectangularTerritoryConfig c)
    {
        if (r.Kind != "room" || r.FloorCells.Count == 0) return false;
        var bounds = TerritoryRectangle.Around(r.FloorCells); int area = Footprint([r]);
        return area >= c.MinimumRoomArea && bounds.Width >= c.MinimumRoomSide && bounds.Length >= c.MinimumRoomSide && area >= bounds.Area * c.MinimumFloorCoverage;
    }
}
