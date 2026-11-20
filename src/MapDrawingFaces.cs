namespace SeraphCivilizationSimulation;

// Extract bounded faces from a set of drawn lines. Coordinates refer to cells in
// the drawing canvas; the outside margin makes open lines distinguishable from
// closed lines even when they touch the edge of the visible map.
internal static class MapDrawingFaces
{
    internal readonly record struct Point(int X, int Z);
    internal const int MaximumSide = 512;

    // Include line cells that border bounded faces, but not unrelated open strokes.
    internal static HashSet<Point> EnclosedWithBoundary(int width, int height, IEnumerable<IReadOnlyList<Point>> source)
    {
        var lines = source.ToArray();
        var inside = Extract(width, height, lines).SelectMany(face => face).ToHashSet();
        var edge = new HashSet<Point>();
        foreach (var line in lines)
            for (int i = 1; i < line.Count; i++)
            {
                var a = line[i - 1]; var b = line[i];
                int x = a.X, z = a.Z, dx = Math.Abs(b.X - x), dz = Math.Abs(b.Z - z);
                int sx = x < b.X ? 1 : -1, sz = z < b.Z ? 1 : -1, error = dx - dz;
                while (true)
                {
                    edge.Add(new(x, z));
                    if (x == b.X && z == b.Z) break;
                    int twice = 2 * error;
                    if (twice > -dz) { error -= dz; x += sx; }
                    if (twice < dx) { error += dx; z += sz; }
                }
            }
        var faces = inside.ToHashSet();
        foreach (var p in edge)
            if (faces.Contains(new(p.X - 1, p.Z)) || faces.Contains(new(p.X + 1, p.Z))
                || faces.Contains(new(p.X, p.Z - 1)) || faces.Contains(new(p.X, p.Z + 1))
                || faces.Contains(new(p.X - 1, p.Z - 1)) || faces.Contains(new(p.X + 1, p.Z + 1))
                || faces.Contains(new(p.X - 1, p.Z + 1)) || faces.Contains(new(p.X + 1, p.Z - 1)))
                inside.Add(p);
        return inside;
    }

    // Keep only the portions of a new stroke that bound a closed face. This
    // accepts crossings even when the mouse starts or ends beyond the boundary.
    internal static List<List<Point>> ClosedStroke(int width, int height, IReadOnlyList<IReadOnlyList<Point>> existing, IReadOnlyList<Point> stroke)
    {
        if (stroke.Count < 2) throw new InvalidDataException("A stroke needs two points.");
        int oldCount = Extract(width, height, existing).Count;
        var combined = existing.Append(stroke).ToArray();
        if (Extract(width, height, combined).Count <= oldCount) throw new InvalidDataException("The stroke does not close an area.");
        // A dangling line inside a face touches the same region on both sides.
        // Retain only cells separating distinct flood-filled regions.
        var enclosed = Extract(width, height, combined);
        var regions = new Dictionary<Point, int>();
        for (int i = 0; i < enclosed.Count; i++)
            foreach (var p in enclosed[i]) regions[p] = i + 1;
        var blocked = new HashSet<Point>();
        foreach (var line in combined)
            for (int i = 1; i < line.Count; i++)
            {
                var a = line[i - 1]; var b = line[i];
                int x = a.X, z = a.Z, dx = Math.Abs(b.X - x), dz = Math.Abs(b.Z - z);
                int sx = x < b.X ? 1 : -1, sz = z < b.Z ? 1 : -1, error = dx - dz;
                while (true)
                {
                    blocked.Add(new(x, z));
                    if (x == b.X && z == b.Z) break;
                    int twice = 2 * error;
                    if (twice > -dz) { error -= dz; x += sx; }
                    if (twice < dx) { error += dx; z += sz; }
                }
            }
        bool Boundary(Point point)
        {
            var adjacent = new HashSet<int>();
            for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
            {
                var p = new Point(point.X + dx, point.Z + dz);
                if (!blocked.Contains(p)) adjacent.Add(regions.GetValueOrDefault(p));
            }
            return adjacent.Count > 1 && adjacent.Any(id => id != 0);
        }
        var result = new List<List<Point>>();
        var run = new List<Point>();
        void Flush()
        {
            if (run.Count > 0) { if (run.Count == 1) run.Add(run[0]); result.Add(run); }
            run = [];
        }
        for (int i = 1; i < stroke.Count; i++)
        {
            var a = stroke[i - 1]; var b = stroke[i];
            int x = a.X, z = a.Z, dx = Math.Abs(b.X - x), dz = Math.Abs(b.Z - z);
            int sx = x < b.X ? 1 : -1, sz = z < b.Z ? 1 : -1, error = dx - dz;
            while (true)
            {
                var point = new Point(x, z);
                if (Boundary(point)) { if (run.Count == 0 || run[^1] != point) run.Add(point); }
                else Flush();
                if (x == b.X && z == b.Z) break;
                int twice = 2 * error;
                if (twice > -dz) { error -= dz; x += sx; }
                if (twice < dx) { error += dx; z += sz; }
            }
        }
        Flush();
        if (Extract(width, height, existing.Concat(result)).Count <= oldCount)
            throw new InvalidDataException("The closed boundary could not be retained.");
        return result;
    }

    internal static List<List<Point>> Extract(int width, int height, IEnumerable<IReadOnlyList<Point>> lines)
    {
        if (width is < 1 or > MaximumSide || height is < 1 or > MaximumSide)
            throw new ArgumentOutOfRangeException(nameof(width));
        int stride = width + 2, rows = height + 2;
        var blocked = new bool[stride * rows];
        var visited = new bool[blocked.Length];
        void Mark(Point p)
        {
            if (p.X < 0 || p.X >= width || p.Z < 0 || p.Z >= height)
                throw new ArgumentOutOfRangeException(nameof(lines));
            blocked[(p.Z + 1) * stride + p.X + 1] = true;
        }
        void Segment(Point a, Point b)
        {
            Mark(a); Mark(b);
            int x = a.X, z = a.Z, dx = Math.Abs(b.X - x), dz = Math.Abs(b.Z - z);
            int sx = x < b.X ? 1 : -1, sz = z < b.Z ? 1 : -1, error = dx - dz;
            while (x != b.X || z != b.Z)
            {
                int twice = 2 * error;
                if (twice > -dz) { error -= dz; x += sx; }
                if (twice < dx) { error += dx; z += sz; }
                Mark(new(x, z));
            }
        }
        foreach (var line in lines)
        {
            if (line.Count == 0) continue;
            Mark(line[0]);
            for (int i = 1; i < line.Count; i++) Segment(line[i - 1], line[i]);
        }

        var result = new List<List<Point>>();
        var queue = new Queue<int>();
        for (int start = 0; start < visited.Length; start++)
        {
            if (blocked[start] || visited[start]) continue;
            var face = new List<Point>();
            bool exterior = false;
            visited[start] = true; queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int index = queue.Dequeue(), x = index % stride, z = index / stride;
                if (x == 0 || z == 0 || x == stride - 1 || z == rows - 1) exterior = true;
                else face.Add(new(x - 1, z - 1));
                void Visit(int nx, int nz)
                {
                    if (nx < 0 || nx >= stride || nz < 0 || nz >= rows) return;
                    int next = nz * stride + nx;
                    if (visited[next] || blocked[next]) return;
                    visited[next] = true; queue.Enqueue(next);
                }
                Visit(x - 1, z); Visit(x + 1, z); Visit(x, z - 1); Visit(x, z + 1);
            }
            if (!exterior && face.Count > 0) result.Add(face);
        }
        return result;
    }
}
