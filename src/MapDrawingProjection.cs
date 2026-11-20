namespace SeraphCivilizationSimulation;

// The paper rotates around its centre. Use the same transform as Contains so
// that one canvas cell represents one block even on a rotated sheet.
internal sealed class MapDrawingProjection
{
    private readonly RecordedPaperMap map;
    private readonly double cos, sin, half;
    internal int Size => map.MapWidth;

    internal MapDrawingProjection(RecordedPaperMap map)
    {
        if (map.MapWidth is < 1 or > MapDrawingFaces.MaximumSide || !float.IsFinite(map.Rotation))
            throw new ArgumentOutOfRangeException(nameof(map));
        this.map = map;
        cos = Math.Cos(map.Rotation); sin = Math.Sin(map.Rotation); half = map.MapWidth / 2.0;
    }

    internal MapDrawingFaces.Point Cell(double x, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z) || !map.Contains(x, z))
            throw new ArgumentOutOfRangeException(nameof(x));
        double dx = x - map.CenterX, dz = z - map.CenterZ;
        return new(Math.Clamp((int)Math.Floor(cos * dx + sin * dz + half), 0, Size - 1),
            Math.Clamp((int)Math.Floor(-sin * dx + cos * dz + half), 0, Size - 1));
    }

    internal MapDrawingPoint World(MapDrawingFaces.Point cell)
    {
        if (cell.X < 0 || cell.X >= Size || cell.Z < 0 || cell.Z >= Size)
            throw new ArgumentOutOfRangeException(nameof(cell));
        double x = cell.X + .5 - half, z = cell.Z + .5 - half;
        return new() { X = map.CenterX + cos * x - sin * z, Z = map.CenterZ + sin * x + cos * z };
    }

    internal List<List<MapDrawingFaces.Point>> Faces(MapDrawingDocument document)
    {
        document.Validate(map);
        var lines = document.Lines.Select(line => (IReadOnlyList<MapDrawingFaces.Point>)line.Points
            .Select(point => Cell(point.X, point.Z)).ToArray());
        return MapDrawingFaces.Extract(Size, Size, lines);
    }

    internal RectangularTerritorySite Territory(MapDrawingDocument document, TribeRecord tribe)
    {
        if (map.Dimension != tribe.Dimension) throw new InvalidDataException("Map is in another dimension.");
        document.Validate(map);
        var lines = document.Lines.Select(line => (IReadOnlyList<MapDrawingFaces.Point>)line.Points.Select(p => Cell(p.X, p.Z)).ToArray());
        var enclosed = MapDrawingFaces.EnclosedWithBoundary(Size, Size, lines);
        var world = new HashSet<TerritoryCell>();
        for (int z = (int)map.Top; z < map.Top + map.Size; z++)
            for (int x = (int)map.Left; x < map.Left + map.Size; x++)
                if (map.Contains(x + .5, z + .5) && enclosed.Contains(Cell(x + .5, z + .5))) world.Add(new(x, 0, z));
        var core = FixedTerritoryRules.Create(tribe);
        for (int z = core.Bounds.MinZ; z <= core.Bounds.MaxZ; z++)
            for (int x = core.Bounds.MinX; x <= core.Bounds.MaxX; x++)
                if (!world.Contains(new(x, 0, z))) throw new InvalidDataException("The complete tribe core must be inside the drawn range.");
        var connected = new HashSet<TerritoryCell>();
        var queue = new Queue<TerritoryCell>();
        var first = new TerritoryCell(tribe.X, 0, tribe.Z); connected.Add(first); queue.Enqueue(first);
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            foreach (var n in new[] { new TerritoryCell(p.X - 1, 0, p.Z), new TerritoryCell(p.X + 1, 0, p.Z), new TerritoryCell(p.X, 0, p.Z - 1), new TerritoryCell(p.X, 0, p.Z + 1) })
                if (world.Contains(n) && connected.Add(n)) queue.Enqueue(n);
        }
        if (connected.Count != world.Count) throw new InvalidDataException("Territory has disconnected enclosed areas.");
        for (int z = core.Bounds.MinZ; z <= core.Bounds.MaxZ; z++)
            for (int x = core.Bounds.MinX; x <= core.Bounds.MaxX; x++)
                if (!connected.Contains(new(x, 0, z))) throw new InvalidDataException("The tribe core must be connected.");
        if (connected.Count > 262144) throw new InvalidDataException("Drawn territory is too large.");
        var bounds = TerritoryRectangle.Around(connected);
        var faceMap = new Dictionary<MapDrawingFaces.Point, int>();
        var faces = Faces(document);
        var names = new List<string>();
        foreach (var area in document.Areas)
        {
            var seed = Cell(area.Seed.X, area.Seed.Z);
            var face = faces.First(f => f.Contains(seed));
            int index = names.Count; names.Add(area.Name);
            foreach (var point in face) faceMap[point] = index;
        }
        var areaCells = names.Select(_ => new List<TerritoryCell>()).ToArray();
        foreach (var point in connected)
        {
            var cell = Cell(point.X + .5, point.Z + .5);
            if (faceMap.TryGetValue(cell, out int index)) areaCells[index].Add(point);
        }
        return new() { Id = "main", Dimension = tribe.Dimension, Bounds = bounds, Heights = Enumerable.Repeat(tribe.Y, bounds.Area).ToArray(),
            NamedAreas = names.Select((name, i) => new TerritoryNamedArea { Name = name, Footprint = TerritoryGeometry.Encode(areaCells[i]) }).ToList(),
            Footprint = TerritoryGeometry.Encode(connected), Below = FixedTerritoryRules.Depth, Above = -1, InitialArea = FixedTerritoryRules.Area,
            Revision = (tribe.Sites.FirstOrDefault()?.Revision ?? 0) + 1 };
    }

    // Names must refer to distinct enclosed faces, not arbitrary map positions.
    internal void ValidateAreas(MapDrawingDocument document)
    {
        var faces = Faces(document);
        var remaining = faces.Select(face => face.ToHashSet()).ToList();
        foreach (var area in document.Areas)
        {
            var point = Cell(area.Seed.X, area.Seed.Z);
            int index = remaining.FindIndex(face => face.Contains(point));
            if (index < 0) throw new InvalidDataException("Area does not identify a unique enclosed face.");
            remaining.RemoveAt(index);
        }
    }
}
