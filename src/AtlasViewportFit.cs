namespace SeraphCivilizationSimulation;

// Compute the centers where a native-size paper window fits entirely in the stitched union.
// An exposed union edge cannot cross the paper interior. Its Minkowski sum with a
// half-width square excludes those centers; the center must also belong to the union.
// Cache the remaining boundary segments, including zero-width corridors for a single row of maps.
internal sealed class AtlasViewportFit
{
    private const double Epsilon = 1e-8;
    private readonly RecordedPaperMap first;
    internal float Rotation { get; }
    internal int Width { get; }
    private readonly double c, s, minX, maxX, minZ, maxZ;
    private readonly List<List<MapPoint>> sheets;
    private readonly List<List<MapPoint>> obstacles = [];
    private readonly List<(MapPoint A, MapPoint B)> boundaries = [];
    internal bool HasFilledView => Valid(new(0, 0)) || boundaries.Count > 0;

    internal AtlasViewportFit(GroundMapLayout layout, float? rotation = null, int? width = null)
    {
        first = layout.Shapes[0]; Rotation = rotation ?? first.Rotation; Width = width ?? first.MapWidth;
        c = Math.Cos(Rotation); s = Math.Sin(Rotation);
        sheets = layout.Shapes.Where(p => p.Dimension == first.Dimension)
            .Select(p => MapPolygon.Corners(p).Select(Local).ToList()).ToList();
        var all = sheets.SelectMany(p => p).ToList(); double h = Width / 2d;
        double left = all.Min(p => p.X), right = all.Max(p => p.X), top = all.Min(p => p.Z), bottom = all.Max(p => p.Z);
        minX = left + h; maxX = Math.Max(minX, right - h);
        minZ = top + h; maxZ = Math.Max(minZ, bottom - h);
        var offsets = Rectangle(-h, -h, h, h);
        var seen = new HashSet<string>();
        foreach (var sheet in sheets)
            for (int i = 0; i < sheet.Count; i++)
            {
                var a = sheet[i]; var direction = sheet[(i + 1) % sheet.Count] - a;
                double length = Math.Sqrt(direction.X * direction.X + direction.Z * direction.Z);
                var cuts = new List<double> { 0, 1 };
                foreach (var other in sheets)
                    for (int j = 0; j < other.Count; j++)
                    {
                        var edge = other[(j + 1) % other.Count] - other[j];
                        double cross = Cross(direction, edge);
                        if (Math.Abs(cross) > Epsilon)
                        {
                            double t = Cross(other[j] - a, edge) / cross;
                            if (t > 0 && t < 1) cuts.Add(t);
                        }
                        else if (Math.Abs(Cross(direction, other[j] - a)) < Epsilon * length)
                        {
                            double t = ((other[j].X - a.X) * direction.X + (other[j].Z - a.Z) * direction.Z) / (length * length);
                            if (t > 0 && t < 1) cuts.Add(t);
                        }
                    }
                cuts = cuts.Distinct().Order().ToList();
                for (int j = 1; j < cuts.Count; j++)
                {
                    if ((cuts[j] - cuts[j - 1]) * length < Epsilon) continue;
                    var outward = new MapPoint(direction.Z / length, -direction.X / length);
                    var probe = a + direction * ((cuts[j] + cuts[j - 1]) / 2) + outward * .00001;
                    if (InCoverage(probe)) continue;
                    var edgePoints = new[] { a + direction * cuts[j - 1], a + direction * cuts[j] };
                    var obstacle = Hull(edgePoints.SelectMany(p => offsets.Select(d => p + d)));
                    if (obstacle.Max(p => p.X) <= minX + Epsilon || obstacle.Min(p => p.X) >= maxX - Epsilon
                        || obstacle.Max(p => p.Z) <= minZ + Epsilon || obstacle.Min(p => p.Z) >= maxZ - Epsilon) continue;
                    string key = string.Join(";", obstacle.Select(p => FormattableString.Invariant($"{p.X:F7},{p.Z:F7}")));
                    if (seen.Add(key)) obstacles.Add(obstacle);
                }
            }
        var domain = Rectangle(minX, minZ, maxX, maxZ);
        foreach (var polygon in obstacles.Prepend(domain))
            for (int i = 0; i < polygon.Count; i++) AddBoundary(polygon[i], polygon[(i + 1) % polygon.Count]);
    }

    internal AtlasViewport For(MapPoint holder, int dimension)
    {
        if (dimension != first.Dimension) return new(holder, Width, Rotation, dimension, holder);
        var desired = Local(holder);
        var closest = new MapPoint(Math.Clamp(desired.X, minX, maxX), Math.Clamp(desired.Z, minZ, maxZ));
        if (!InCoverage(closest) || obstacles.Any(p => Inside(p, closest)))
        {
            // Layout selects a smaller inscribed view if this angle cannot fit the native width.
            closest = new(0, 0);
            double distance = Valid(closest) ? DistanceSquared(closest, desired) : double.PositiveInfinity;
            foreach (var (a, b) in boundaries)
            {
                var d = b - a; double length = DistanceSquared(a, b);
                double t = length <= Epsilon * Epsilon ? 0 : Math.Clamp(((desired.X - a.X) * d.X + (desired.Z - a.Z) * d.Z) / length, 0, 1);
                var candidate = a + d * t; double next = DistanceSquared(candidate, desired);
                if (next < distance) { closest = candidate; distance = next; }
            }
        }
        return new(World(closest), Width, Rotation, dimension, holder);
    }

    private bool Valid(MapPoint point) => point.X >= minX - Epsilon && point.X <= maxX + Epsilon
        && point.Z >= minZ - Epsilon && point.Z <= maxZ + Epsilon
        && InCoverage(point) && !obstacles.Any(p => Inside(p, point));

    private MapPoint Local(MapPoint p)
    {
        double x = p.X - first.CenterX, z = p.Z - first.CenterZ;
        return new(c * x + s * z, -s * x + c * z);
    }
    private MapPoint World(MapPoint p) => new(first.CenterX + c * p.X - s * p.Z, first.CenterZ + s * p.X + c * p.Z);
    private static List<MapPoint> Rectangle(double x0, double z0, double x1, double z1)
        => [new(x0, z0), new(x1, z0), new(x1, z1), new(x0, z1)];
    private static double Cross(MapPoint a, MapPoint b) => a.X * b.Z - a.Z * b.X;
    private static double DistanceSquared(MapPoint a, MapPoint b) => (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z);
    private bool InCoverage(MapPoint point) => sheets.Any(p => Enumerable.Range(0, p.Count)
        .All(i => Cross(p[(i + 1) % p.Count] - p[i], point - p[i]) >= -Epsilon));
    private static bool Inside(List<MapPoint> polygon, MapPoint p)
        => Enumerable.Range(0, polygon.Count).All(i =>
        {
            var edge = polygon[(i + 1) % polygon.Count] - polygon[i];
            return Cross(edge, p - polygon[i]) > Epsilon * Math.Sqrt(edge.X * edge.X + edge.Z * edge.Z);
        });

    private static List<MapPoint> Hull(IEnumerable<MapPoint> points)
    {
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Z).ToList();
        var hull = new List<MapPoint>();
        foreach (var p in sorted)
        {
            while (hull.Count >= 2 && Cross(hull[^1] - hull[^2], p - hull[^1]) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        int lower = hull.Count;
        for (int i = sorted.Count - 2; i >= 0; i--)
        {
            var p = sorted[i];
            while (hull.Count > lower && Cross(hull[^1] - hull[^2], p - hull[^1]) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        hull.RemoveAt(hull.Count - 1); return hull;
    }

    private void AddBoundary(MapPoint a, MapPoint b)
    {
        var direction = b - a; double lo = 0, hi = 1;
        // Clip each candidate edge to the bounding rectangle of all valid centers.
        bool Limit(double start, double delta, double minimum, double maximum)
        {
            if (Math.Abs(delta) < Epsilon) return start >= minimum - Epsilon && start <= maximum + Epsilon;
            double t0 = (minimum - start) / delta, t1 = (maximum - start) / delta;
            lo = Math.Max(lo, Math.Min(t0, t1)); hi = Math.Min(hi, Math.Max(t0, t1));
            return lo <= hi;
        }
        if (!Limit(a.X, direction.X, minX, maxX) || !Limit(a.Z, direction.Z, minZ, maxZ)) return;
        var intervals = new List<(double Lo, double Hi)> { (lo, hi) };
        foreach (var obstacle in obstacles)
        {
            double enter = lo, exit = hi; bool crossesInterior = true;
            for (int i = 0; i < obstacle.Count; i++)
            {
                var edge = obstacle[(i + 1) % obstacle.Count] - obstacle[i];
                double length = Math.Sqrt(edge.X * edge.X + edge.Z * edge.Z);
                double start = Cross(edge, a - obstacle[i]) / length, delta = Cross(edge, direction) / length;
                if (Math.Abs(delta) < Epsilon)
                { if (start <= Epsilon) { crossesInterior = false; break; } }
                else
                {
                    double t = (Epsilon - start) / delta;
                    if (delta > 0) enter = Math.Max(enter, t); else exit = Math.Min(exit, t);
                }
            }
            // A single tangency point excludes no interval. Splitting it would duplicate
            // zero-length segments repeatedly where many original edges meet.
            if (!crossesInterior || enter >= exit) continue;
            var remaining = new List<(double Lo, double Hi)>();
            foreach (var interval in intervals)
            {
                if (exit < interval.Lo || enter > interval.Hi) { remaining.Add(interval); continue; }
                if (enter >= interval.Lo) remaining.Add((interval.Lo, Math.Min(interval.Hi, enter)));
                if (exit <= interval.Hi) remaining.Add((Math.Max(interval.Lo, exit), interval.Hi));
            }
            intervals = remaining;
            if (intervals.Count == 0) break;
        }
        foreach (var interval in intervals)
        {
            var start = a + direction * interval.Lo; var end = a + direction * interval.Hi;
            // Strictly interior endpoints can arise for a degenerate interval contained in a hole.
            var middle = (start + end) * .5;
            if (InCoverage(middle) && !obstacles.Any(p => Inside(p, middle))) boundaries.Add((start, end));
        }
    }
}
