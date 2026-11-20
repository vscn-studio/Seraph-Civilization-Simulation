namespace SeraphCivilizationSimulation;

internal static class FixedTerritoryRules
{
    internal const int Version = 5;
    internal const int Side = 16;
    internal const int Area = Side * Side;
    internal const int Depth = 10;
    internal static int Members(TribeRecord tribe) => tribe.Members.Append(tribe.LeaderUid).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).Count();
    internal static RectangularTerritorySite Create(TribeRecord tribe) => new()
    {
        Id = "main", Dimension = tribe.Dimension,
        Bounds = new() { MinX = checked(tribe.X - Side / 2), MaxX = checked(tribe.X + Side / 2 - 1),
            MinZ = checked(tribe.Z - Side / 2), MaxZ = checked(tribe.Z + Side / 2 - 1) },
        Heights = Enumerable.Repeat(tribe.Y, Area).ToArray(), Below = Depth, Above = -1, InitialArea = Area,
        Revision = (tribe.Sites?.Select(s => s.Revision).DefaultIfEmpty(0).Max() ?? 0) + 1
    };
    internal static void Apply(TribeRecord tribe)
    {
        tribe.Sites = [Create(tribe)];
        tribe.Regions = []; tribe.RoomObservations = []; tribe.Outposts = [];
        tribe.PopulationTerritory = null; tribe.LandExpandedAt = 0; tribe.TerritoryVersion = Version;
    }
    internal static void Validate(TribeRecord tribe)
    {
        var expected = Create(tribe);
        if (tribe.Sites.Count != 1 || tribe.Sites[0] is not { } site || site.Id != "main"
            || site.Dimension != tribe.Dimension || site.Bounds.Width is < 16 or > 1024 || site.Bounds.Length is < 16 or > 1024
            || site.Heights.Length != site.Bounds.Area || site.Heights.Any(y => y != tribe.Y)
            || site.Below != Depth || site.Above != -1)
            throw new InvalidDataException("Invalid fixed tribe territory.");
        if (site.Footprint == null && !site.Bounds.Same(expected.Bounds)) throw new InvalidDataException("Invalid fixed tribe territory.");
        if (site.Footprint != null)
        {
            long count = 0;
            foreach (var run in site.Footprint)
            {
                if (run == null || run.Y != 0 || run.Length is < 1 or > 32
                    || run.X > int.MaxValue - run.Length || (run.X >> 5) != ((run.X + run.Length - 1) >> 5)
                    || !site.Bounds.Contains(run.X, run.Z) || !site.Bounds.Contains(run.X + run.Length - 1, run.Z)
                    || (count += run.Length) > 262144)
                    throw new InvalidDataException("Invalid drawn tribe territory runs.");
            }
            var cells = site.FootprintCells!;
            if (cells.Count == 0 || cells.Count != count)
                throw new InvalidDataException("Invalid drawn tribe territory.");
            for (int x = expected.Bounds.MinX; x <= expected.Bounds.MaxX; x++)
                for (int z = expected.Bounds.MinZ; z <= expected.Bounds.MaxZ; z++)
                    if (!site.ContainsColumn(x, z)) throw new InvalidDataException("Drawn territory excludes the core.");
            var reached = new HashSet<TerritoryCell>();
            var queue = new Queue<TerritoryCell>();
            var start = new TerritoryCell(tribe.X, 0, tribe.Z);
            reached.Add(start); queue.Enqueue(start);
            while (queue.TryDequeue(out var p))
                foreach (var n in new[] { new TerritoryCell(p.X - 1, 0, p.Z), new TerritoryCell(p.X + 1, 0, p.Z),
                    new TerritoryCell(p.X, 0, p.Z - 1), new TerritoryCell(p.X, 0, p.Z + 1) })
                    if (cells.Contains(n) && reached.Add(n)) queue.Enqueue(n);
            if (reached.Count != cells.Count) throw new InvalidDataException("Drawn territory is disconnected.");
        }
    }
}
