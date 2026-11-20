using System.Collections;
using System.Globalization;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace SeraphCivilizationSimulation;

// The originals remain real item stacks, including locks, names and third-party attributes.
internal static class GroundMapData
{
    internal const string AtlasKey = "vscncsAtlas";
    // Saved originals must remain readable regardless of the current creation limit.
    internal const int MaxStoredSheets = PaperMapsConfig.MaximumMaxSheets;
    internal static bool IsAtlas(ItemStack? stack) => stack?.Attributes.GetTreeAttribute(AtlasKey)?.GetInt("version") == 1;
    internal static List<ItemStack> Sheets(ItemStack stack, IWorldAccessor world)
        => ReadSheets(stack.Attributes.GetTreeAttribute(AtlasKey), world);

    internal static TreeAttribute Store(IReadOnlyList<ItemStack> sheets)
    {
        var tree = new TreeAttribute(); tree.SetInt("version", 1); tree.SetInt("count", sheets.Count);
        for (int i = 0; i < sheets.Count; i++) tree.SetItemstack(i.ToString(CultureInfo.InvariantCulture), sheets[i].Clone());
        return tree;
    }

    internal static List<ItemStack> ReadSheets(ITreeAttribute? tree, IWorldAccessor world, bool allowAtlas = false)
    {
        var result = new List<ItemStack>();
        if (tree == null || tree.GetInt("version") != 1 || tree.GetInt("count") is < 1 or > MaxStoredSheets) return result;
        for (int i = 0; i < tree.GetInt("count"); i++)
        {
            var sheet = tree.GetItemstack(i.ToString(CultureInfo.InvariantCulture));
            if (sheet == null || !sheet.ResolveBlockOrItem(world) || RecordedPaperMap.Describe(sheet) == null) return [];
            if (IsAtlas(sheet) && (!allowAtlas || tree.GetInt("count") != 1 || !CanPlace(sheet, world))) return [];
            result.Add(sheet);
        }
        return result;
    }

    internal static bool CanAdd(IReadOnlyList<ItemStack> sheets, ItemStack candidate, int maxSheets = PaperMapsConfig.DefaultMaxSheets)
    {
        if (sheets.Count >= maxSheets || sheets.Count >= MaxStoredSheets || sheets.Any(IsAtlas) || IsAtlas(candidate)
            || RecordedPaperMap.Describe(candidate) is not { } shape) return false;
        return sheets.Count == 0 || (candidate.Collectible.Code.Equals(sheets[0].Collectible.Code)
            && sheets.Select(RecordedPaperMap.Describe).Any(s => s != null && s.Dimension == shape.Dimension && MapPolygon.Connected(s, shape)));
    }

    internal static bool CanPlace(ItemStack stack, IWorldAccessor world)
        => IsAtlas(stack) ? Sheets(stack, world) is { Count: >= 2 } originals && Connected(originals) : CanAdd([], stack);

    // Storage owns the stitched item; rendering borrows its original fixed footprints.
    internal static List<ItemStack> DisplaySheets(IReadOnlyList<ItemStack> stored, IWorldAccessor world)
    {
        if (stored.Count != 1 || !IsAtlas(stored[0])) return stored.ToList();
        var originals = Sheets(stored[0], world).Select(s => s.Clone()).ToList();
        foreach (var original in originals) ImmersiveMapCompatibility.Write(original, ImmersiveMapCompatibility.Read(stored[0]));
        return originals;
    }

    internal static bool Contains(ItemStack stack, double x, double z)
    {
        var tree = stack.Attributes.GetTreeAttribute(AtlasKey);
        if (tree == null) return false;
        for (int i = 0; i < Math.Min(MaxStoredSheets, tree.GetInt("count")); i++)
        {
            var original = tree.GetItemstack(i.ToString(CultureInfo.InvariantCulture));
            // Nested stacks arrive unresolved after inventory sync or loading. Sewing
            // requires the same material, so use the atlas's resolved code for matching IDs.
            if (original == null || original.Class != stack.Class || original.Id != stack.Id) continue;
            if (RecordedPaperMap.Describe(original, stack.Collectible?.Code) is { } sheet
                && new AtlasViewport(new(sheet.CenterX, sheet.CenterZ), sheet.MapWidth, sheet.Rotation, sheet.Dimension).Contains(new(x, z))) return true;
        }
        return false;
    }

    internal static ItemStack Sew(IReadOnlyList<ItemStack> sheets, int maxSheets = PaperMapsConfig.DefaultMaxSheets)
    {
        if (sheets.Count < 2 || sheets.Count > maxSheets || sheets.Count > MaxStoredSheets
            || sheets.Any(s => IsAtlas(s) || ImmersiveMapCompatibility.Locked(s)))
            throw new InvalidOperationException("Unlock the original maps before sewing.");
        if (!Connected(sheets)) throw new InvalidOperationException("Maps must form one connected material and dimension.");
        var atlas = sheets[0].Clone(); atlas.StackSize = 1;
        atlas.Attributes[AtlasKey] = Store(sheets);
        atlas.Attributes.RemoveAttribute("mapName");
        var merged = ImmersiveMapCompatibility.Read(atlas);
        for (int i = 1; i < sheets.Count; i++)
            foreach (DictionaryEntry entry in ImmersiveMapCompatibility.Read(sheets[i]))
            {
                var marker = entry.Value!;
                string id = (string)entry.Key;
                if (merged.Contains(id))
                {
                    // Equal IDs describe the same copied waypoint. Distinct contents must survive too.
                    if (SameMarker(merged[id]!, marker)) continue;
                    id = Guid.NewGuid().ToString("N");
                    AccessTools.Property(marker.GetType(), "Id").SetValue(marker, id);
                }
                merged[id] = marker;
            }
        ImmersiveMapCompatibility.Write(atlas, merged);
        if ((atlas.Attributes.GetBytes("waypoints")?.Length ?? 0) > short.MaxValue)
            throw new InvalidOperationException("The combined waypoint data exceeds the native map format.");
        return atlas;
    }

    internal static bool Connected(IReadOnlyList<ItemStack> sheets)
    {
        if (sheets.Count == 0 || sheets.Any(s => IsAtlas(s) || RecordedPaperMap.Describe(s) == null
            || !s.Collectible.Code.Equals(sheets[0].Collectible.Code))) return false;
        var shapes = sheets.Select(s => RecordedPaperMap.Describe(s)!).ToList();
        var reached = new HashSet<int> { 0 };
        bool changed;
        do
        {
            changed = false;
            for (int i = 0; i < shapes.Count; i++)
                if (!reached.Contains(i) && reached.Any(j => shapes[j].Dimension == shapes[i].Dimension && MapPolygon.Connected(shapes[j], shapes[i])))
                { reached.Add(i); changed = true; }
        } while (changed);
        return reached.Count == shapes.Count;
    }

    internal static int ThreadCost(IReadOnlyList<ItemStack> sheets)
    {
        var shapes = sheets.Select(RecordedPaperMap.Describe).ToList();
        int cost = 0;
        for (int i = 0; i < shapes.Count; i++)
            for (int j = i + 1; j < shapes.Count; j++)
                if (shapes[i] is { } a && shapes[j] is { } b && a.Dimension == b.Dimension && MapPolygon.Connected(a, b)) cost++;
        return cost;
    }

    private static bool SameMarker(object a, object b)
        => new[] { "X", "Z", "Name", "Icon", "Color", "Notes" }.All(p => Equals(AccessTools.Property(a.GetType(), p).GetValue(a), AccessTools.Property(b.GetType(), p).GetValue(b)));

    internal static List<ItemStack> Split(ItemStack atlas, IWorldAccessor world)
    {
        if (ImmersiveMapCompatibility.Locked(atlas)) return [];
        var sheets = Sheets(atlas, world);
        // Every returned sheet receives the same current waypoint collection. Native rendering clips
        // to the individual sheet; deletions and edits therefore also survive disassembly.
        foreach (var sheet in sheets) ImmersiveMapCompatibility.Write(sheet, ImmersiveMapCompatibility.Read(atlas));
        return sheets;
    }
}

internal readonly record struct MapPoint(double X, double Z)
{
    public static MapPoint operator +(MapPoint a, MapPoint b) => new(a.X + b.X, a.Z + b.Z);
    public static MapPoint operator -(MapPoint a, MapPoint b) => new(a.X - b.X, a.Z - b.Z);
    public static MapPoint operator *(MapPoint a, double n) => new(a.X * n, a.Z * n);
}

// A square view of recorded data; its center can stop at an edge while the holder keeps moving.
internal readonly record struct AtlasViewport(MapPoint Center, int Width, float Rotation, int Dimension, MapPoint? Holder = null)
{
    internal bool Contains(MapPoint point)
    {
        var projected = Project(point);
        return Math.Abs(projected.X) <= 1 + 1e-8 && Math.Abs(projected.Z) <= 1 + 1e-8;
    }
    internal MapPoint Project(MapPoint point)
    {
        double dx = point.X - Center.X, dz = point.Z - Center.Z, c = Math.Cos(Rotation), s = Math.Sin(Rotation);
        return new((c * dx + s * dz) * 2 / Width, (-s * dx + c * dz) * 2 / Width);
    }
    internal MapPoint Unproject(MapPoint point)
    {
        double x = point.X * Width / 2, z = point.Z * Width / 2, c = Math.Cos(Rotation), s = Math.Sin(Rotation);
        return new(Center.X + c * x - s * z, Center.Z + s * x + c * z);
    }
    internal List<List<List<MapPoint>>> Clip(GroundMapLayout layout)
    {
        var center = Center; int dimension = Dimension;
        double c = Math.Cos(Rotation), s = Math.Sin(Rotation), h = Width / 2d;
        var window = new[] { new MapPoint(-h, -h), new MapPoint(h, -h), new MapPoint(h, h), new MapPoint(-h, h) }
            .Select(p => new MapPoint(center.X + c * p.X - s * p.Z, center.Z + s * p.X + c * p.Z)).ToList();
        return layout.Pieces.Select((pieces, i) => layout.Shapes[i].Dimension != dimension ? new List<List<MapPoint>>() : pieces
            .Select(p => MapPolygon.Intersect(p, window)).Where(p => MapPolygon.Area(p) > .0001).ToList()).ToList();
    }
}

internal static class MapPolygon
{
    internal static List<MapPoint> Corners(RecordedPaperMap map)
    {
        double h = map.MapWidth / 2d, c = Math.Cos(map.Rotation), s = Math.Sin(map.Rotation);
        return new[] { new MapPoint(-h, -h), new MapPoint(h, -h), new MapPoint(h, h), new MapPoint(-h, h) }
            .Select(p => new MapPoint(map.CenterX + c * p.X - s * p.Z, map.CenterZ + s * p.X + c * p.Z)).ToList();
    }
    private static double Cross(MapPoint a, MapPoint b) => a.X * b.Z - a.Z * b.X;
    internal static double Area(IReadOnlyList<MapPoint> p)
    {
        if (p.Count < 3) return 0;
        double area = 0;
        for (int i = 1; i < p.Count - 1; i++) area += Cross(p[i] - p[0], p[i + 1] - p[0]);
        return Math.Abs(area) / 2;
    }
    private static List<MapPoint> Clip(IReadOnlyList<MapPoint> poly, MapPoint a, MapPoint b, bool inside)
    {
        var output = new List<MapPoint>();
        if (poly.Count == 0) return output;
        double D(MapPoint p) => Cross(b - a, p - a) * (inside ? 1 : -1);
        MapPoint previous = poly[^1]; double pd = D(previous);
        foreach (var current in poly)
        {
            double cd = D(current);
            if ((cd >= 0) != (pd >= 0)) output.Add(previous + (current - previous) * (pd / (pd - cd)));
            if (cd >= 0) output.Add(current);
            previous = current; pd = cd;
        }
        return output;
    }
    internal static List<MapPoint> Intersect(IReadOnlyList<MapPoint> polygon, IReadOnlyList<MapPoint> clip)
    {
        var result = polygon.ToList();
        for (int i = 0; i < clip.Count && result.Count > 0; i++) result = Clip(result, clip[i], clip[(i + 1) % clip.Count], true);
        return result;
    }
    internal static bool Connected(RecordedPaperMap a, RecordedPaperMap b)
    {
        var pa = Corners(a); var pb = Corners(b); var intersection = pa;
        for (int i = 0; i < 4; i++) intersection = Clip(intersection, pb[i], pb[(i + 1) % 4], true);
        if (Area(intersection) > .001) return true;
        // A shared edge is continuous; touching at only one corner is not.
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++)
        {
            var d = pa[(i + 1) % 4] - pa[i]; double length = Math.Sqrt(d.X * d.X + d.Z * d.Z);
            if (Math.Abs(Cross(d, pb[j] - pa[i])) > length * .001 || Math.Abs(Cross(d, pb[(j + 1) % 4] - pa[i])) > length * .001) continue;
            double T(MapPoint p) => ((p.X - pa[i].X) * d.X + (p.Z - pa[i].Z) * d.Z) / (length * length);
            if (Math.Min(1, Math.Max(T(pb[j]), T(pb[(j + 1) % 4]))) - Math.Max(0, Math.Min(T(pb[j]), T(pb[(j + 1) % 4]))) > .0001) return true;
        }
        return false;
    }
    // Subtract earlier sheets from later ones. All output pieces are convex, so they can be
    // triangulated without overlaps, z-fighting, internal paper borders or leaking outside the union.
    internal static List<List<MapPoint>> Subtract(List<MapPoint> polygon, IReadOnlyList<MapPoint> cutter)
    {
        var pieces = new List<List<MapPoint>>(); var remaining = polygon;
        for (int i = 0; i < cutter.Count && remaining.Count > 0; i++)
        {
            var outside = Clip(remaining, cutter[i], cutter[(i + 1) % cutter.Count], false);
            if (Area(outside) > .0001) pieces.Add(outside);
            remaining = Clip(remaining, cutter[i], cutter[(i + 1) % cutter.Count], true);
        }
        return pieces;
    }
}

internal sealed class GroundMapLayout
{
    internal readonly List<RecordedPaperMap> Shapes;
    internal readonly List<List<List<MapPoint>>> Pieces = [];
    internal readonly double CenterX, CenterZ, Span;
    private AtlasViewportFit? viewportFit;
    internal bool Contains(MapPoint point, int dimension) => Shapes.Any(s => s.Dimension == dimension
        && new AtlasViewport(new(s.CenterX, s.CenterZ), s.MapWidth, s.Rotation, dimension).Contains(point));
    internal AtlasViewport ViewportFor(MapPoint holder, int dimension, float? rotation = null)
    {
        float angle = rotation ?? Shapes[0].Rotation;
        if (viewportFit == null || viewportFit.Rotation != angle)
        {
            viewportFit = new(this, angle);
            if (!viewportFit.HasFilledView)
            {
                // A square turned inside a narrow recorded sheet needs its inscribed crop.
                // Keep every paper pixel covered without exploring or rewriting saved bounds.
                double delta = angle - Shapes[0].Rotation;
                int width = Math.Max(1, (int)Math.Floor(Shapes[0].MapWidth / (Math.Abs(Math.Cos(delta)) + Math.Abs(Math.Sin(delta)))));
                viewportFit = new(this, angle, width);
                int lower = width + 1, upper = Shapes[0].MapWidth - 1;
                while (lower <= upper)
                {
                    int candidateWidth = lower + (upper - lower) / 2;
                    var candidate = new AtlasViewportFit(this, angle, candidateWidth);
                    if (candidate.HasFilledView) { viewportFit = candidate; lower = candidateWidth + 1; }
                    else upper = candidateWidth - 1;
                }
            }
        }
        return viewportFit.For(holder, dimension);
    }
    internal GroundMapLayout(IReadOnlyList<ItemStack> sheets)
    {
        Shapes = sheets.Select(s => RecordedPaperMap.Describe(s) ?? throw new InvalidDataException("Uninitialized map.")).ToList();
        var corners = Shapes.Select(MapPolygon.Corners).ToList(); var all = corners.SelectMany(c => c).ToList();
        CenterX = (all.Min(p => p.X) + all.Max(p => p.X)) / 2; CenterZ = (all.Min(p => p.Z) + all.Max(p => p.Z)) / 2;
        Span = Math.Max(all.Max(p => p.X) - all.Min(p => p.X), all.Max(p => p.Z) - all.Min(p => p.Z));
        for (int i = 0; i < corners.Count; i++)
        {
            var pieces = new List<List<MapPoint>> { corners[i] };
            for (int j = 0; j < i; j++) pieces = pieces.SelectMany(p => MapPolygon.Subtract(p, corners[j])).ToList();
            Pieces.Add(pieces);
        }
    }
    internal MapPoint Ground(MapPoint world) => new(.5 + (world.X - CenterX) / Span * .94, .5 + (world.Z - CenterZ) / Span * .94);
    internal int Hit(double x, double z)
    {
        double wx = CenterX + (x - .5) / .94 * Span, wz = CenterZ + (z - .5) / .94 * Span;
        for (int i = 0; i < Shapes.Count; i++) if (Shapes[i].Contains(wx, wz)) return i;
        return -1;
    }
}
