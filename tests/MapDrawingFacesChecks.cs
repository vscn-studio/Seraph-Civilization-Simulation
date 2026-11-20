using SeraphCivilizationSimulation;
using Vintagestory.API.Common;
using Point = SeraphCivilizationSimulation.MapDrawingFaces.Point;

internal static class MapDrawingFacesChecks
{
    internal static void Run(Action<bool, string> check)
    {
        Point[] square = [new(2, 2), new(8, 2), new(8, 8), new(2, 8), new(2, 2)];
        var faces = MapDrawingFaces.Extract(12, 12, [square]);
        check(faces.Count == 1 && faces[0].Count == 25 && faces[0].Contains(new(5, 5)), "A closed line must produce its enclosed face.");
        check(MapDrawingFaces.Extract(12, 12, [square[..^1]]).Count == 0, "An open line must not produce a face.");
        Point[] divider = [new(5, 2), new(5, 8)];
        faces = MapDrawingFaces.Extract(12, 12, [square, divider]);
        check(faces.Count == 2 && faces.All(f => f.Count == 10), "A line joining an existing boundary must split the face.");
        Point[] inner = [new(4, 4), new(6, 4), new(6, 6), new(4, 6), new(4, 4)];
        faces = MapDrawingFaces.Extract(12, 12, [square, inner]);
        check(faces.Count == 2 && faces.Any(f => f.Count == 1 && f[0] == new Point(5, 5)), "Nested rings must produce separately selectable faces.");
        Point[] crossing = [new(2, 5), new(8, 5)];
        faces = MapDrawingFaces.Extract(12, 12, [square, divider, crossing]);
        check(faces.Count == 4 && faces.All(f => f.Count == 4), "Crossing lines must produce each enclosed face.");
        Point[] extendedCrossing = [new(0, 5), new(11, 5)];
        var closedStroke = MapDrawingFaces.ClosedStroke(12, 12, [square], extendedCrossing);
        check(MapDrawingFaces.Extract(12, 12, new IReadOnlyList<Point>[] { square }.Concat(closedStroke)).Count == 2
            && closedStroke.SelectMany(line => line).All(p => p.X >= 2 && p.X <= 8),
            "A stroke crossing a closed boundary must split it and discard open tails.");
        Point[] bow = [new(2, 2), new(8, 8), new(2, 8), new(8, 2), new(2, 2)];
        var bowStroke = MapDrawingFaces.ClosedStroke(12, 12, [], bow);
        check(MapDrawingFaces.Extract(12, 12, bowStroke).Count == 2, "A self-crossing closed stroke must produce both faces.");
        Point[] spur = [new(2, 2), new(8, 2), new(8, 8), new(2, 8), new(2, 2), new(5, 5)];
        var spurStroke = MapDrawingFaces.ClosedStroke(12, 12, [], spur);
        check(MapDrawingFaces.Extract(12, 12, spurStroke).Count == 1 && !spurStroke.SelectMany(line => line).Contains(new Point(5, 5)),
            "A closed stroke must discard a dangling line inside its face.");
        bool rejected = false;
        try { MapDrawingFaces.Extract(513, 12, [square]); } catch (ArgumentOutOfRangeException) { rejected = true; }
        check(rejected, "Unbounded canvases must be rejected.");

        var map = new RecordedPaperMap { CenterX = 100, CenterZ = 200, MapWidth = 288, Pixels = [] };
        var document = new MapDrawingDocument
        {
            TerritorySelected = true,
            Lines = [new() { Points = [new() { X = 99, Z = 199 }, new() { X = 102, Z = 202 }] }],
            Areas = [new() { Name = "营地", Color = 0x123456, Seed = new() { X = 100, Z = 200 } }]
        };
        var item = new ItemStack(new Item());
        item.Attributes.SetBytes(MapDrawingDocument.Attribute, document.Encode(map));
        var restored = MapDrawingDocument.Read(item, map);
        check(restored.TerritorySelected && restored.Areas.Single().Name == "营地" && restored.Lines.Single().Points.Count == 2,
            "A drawing and its area names must survive item attribute serialization.");
        document.Lines[0].Points[0].X = double.NaN;
        rejected = false;
        try { document.Encode(map); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "Non-finite coordinates must not be saved.");
        document.Lines[0].Points[0].X = 1000;
        rejected = false;
        try { document.Encode(map); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "Coordinates outside the sheet must not be saved.");

        foreach (float rotation in new[] { 0f, MathF.PI / 4, MathF.PI / 2, -1.3f })
        {
            var rotated = new RecordedPaperMap { CenterX = 100, CenterZ = -200, MapWidth = 288, Rotation = rotation, Pixels = [] };
            var projection = new MapDrawingProjection(rotated);
            foreach (var cell in new[] { new Point(0, 0), new Point(143, 144), new Point(287, 287) })
            {
                var world = projection.World(cell);
                check(rotated.Contains(world.X, world.Z) && projection.Cell(world.X, world.Z) == cell,
                    "Rotated paper coordinates must round-trip through the drawing canvas.");
            }
            var ring = new[] { new Point(100, 100), new Point(120, 100), new Point(120, 120), new Point(100, 120), new Point(100, 100) };
            var rotatedDocument = new MapDrawingDocument { Lines = [new() { Points = ring.Select(projection.World).ToList() }] };
            var extracted = projection.Faces(rotatedDocument);
            check(extracted.Count == 1 && extracted[0].Contains(new Point(110, 110)),
                "Rotating a paper map must preserve its enclosed drawing face.");
            rotatedDocument.Areas.Add(new() { Name = "Inside", Seed = projection.World(new Point(110, 110)) });
            projection.ValidateAreas(rotatedDocument);
            rotatedDocument.Areas.Add(new() { Name = "Duplicate", Seed = projection.World(new Point(111, 111)) });
            bool duplicateRejected = false;
            try { projection.ValidateAreas(rotatedDocument); } catch (InvalidDataException) { duplicateRejected = true; }
            check(duplicateRejected, "Two names may not claim the same enclosed face.");
            rotatedDocument.Areas[1].Seed = projection.World(new Point(50, 50));
            bool outsideRejected = false;
            try { projection.ValidateAreas(rotatedDocument); } catch (InvalidDataException) { outsideRejected = true; }
            check(outsideRejected, "An area name must lie inside a closed face.");
        }
        var tribe = new TribeRecord { X = 0, Y = 100, Z = 0, Dimension = 0 };
        var site = FixedTerritoryRules.Create(tribe);
        site.Bounds.MaxX = 8;
        site.Heights = Enumerable.Repeat(100, site.Bounds.Area).ToArray();
        var columns = Enumerable.Range(-8, 16).SelectMany(z => Enumerable.Range(-8, 16).Select(x => new TerritoryCell(x, 0, z))).ToList();
        columns.Add(new(8, 0, 0));
        site.Footprint = TerritoryGeometry.Encode(columns);
        tribe.Sites = [site];
        FixedTerritoryRules.Validate(tribe);
        check(site.Contains(new(8, 90, 0), 0) && !site.Contains(new(8, 90, 1), 0), "Protection must follow drawn columns and exclude gaps.");
        check(!site.Contains(new(8, 89, 0), 0) && site.Contains(new(8, 500, 0), 0), "Drawn territory retains the core's vertical rules.");
        var edges = VscnCivilizationModSystem.FixedTerritoryEdges(tribe, 1024);
        check(edges.Any(e => e.X1 == 9 && e.X2 == 9 && e.Z1 == 0 && e.Z2 == 1), "Boundary follows the protruding column.");
        columns.Remove(new(-8, 0, -8));
        site.Footprint = TerritoryGeometry.Encode(columns);
        // Rehydrate to clear cached decoded cells, as when loading a save.
        tribe = Newtonsoft.Json.JsonConvert.DeserializeObject<TribeRecord>(Newtonsoft.Json.JsonConvert.SerializeObject(tribe))!;
        bool missingCoreRejected = false;
        try { FixedTerritoryRules.Validate(tribe); } catch (InvalidDataException) { missingCoreRejected = true; }
        check(missingCoreRejected, "The complete fixed core is required after loading a drawn territory.");
        var drawingMap = new RecordedPaperMap { CenterX = 0, CenterZ = 0, MapWidth = 288, Pixels = [] };
        var drawingProjection = new MapDrawingProjection(drawingMap);
        var drawingRing = new[] { new Point(130, 130), new Point(158, 130), new Point(158, 158), new Point(130, 158), new Point(130, 130) };
        var drawing = new MapDrawingDocument { Lines = [new() { Points = drawingRing.Select(drawingProjection.World).ToList() }] };
        var owner = new TribeRecord { X = 0, Y = 100, Z = 0, Dimension = 0 };
        var drawnSite = drawingProjection.Territory(drawing, owner);
        owner.Sites = [drawnSite]; FixedTerritoryRules.Validate(owner);
        check(drawnSite.ContainsColumn(0, 0) && drawnSite.ContainsColumn(-14, 0) && !drawnSite.ContainsColumn(20, 0), "A closed map range creates its enclosed protection footprint.");
        var smallRing = new[] { new Point(140, 140), new Point(148, 140), new Point(148, 148), new Point(140, 148), new Point(140, 140) };
        drawing.Lines = [new() { Points = smallRing.Select(drawingProjection.World).ToList() }];
        bool smallRejected = false;
        try { drawingProjection.Territory(drawing, owner); } catch (InvalidDataException) { smallRejected = true; }
        check(smallRejected, "A drawn range that excludes the fixed core must be rejected.");
    }
}
