namespace SeraphCivilizationSimulation;

internal static class TerritoryPatches
{
    internal static int Area(IEnumerable<RectangularTerritorySite> sites)
    {
        int total=0;
        foreach (var dimension in sites.GroupBy(s => s.Dimension))
        {
            var rectangles=dimension.Select(s => s.Bounds).ToArray();
            var edges=rectangles.SelectMany(b => new[] { b.MinX,b.MaxX+1 }).Distinct().Order().ToArray();
            for (int i=0;i<edges.Length-1;i++)
            {
                int end=int.MinValue, length=0;
                foreach (var b in rectangles.Where(b => b.MinX<=edges[i] && b.MaxX>=edges[i]).OrderBy(b => b.MinZ))
                { int start=Math.Max(end,b.MinZ); if (b.MaxZ+1>start) length+=b.MaxZ+1-start; end=Math.Max(end,b.MaxZ+1); }
                total=checked(total+(edges[i+1]-edges[i])*length);
            }
        }
        return total;
    }
    internal static RectangularTerritorySite[] Component(IEnumerable<RectangularTerritorySite> sites,string id)
    {
        var remaining=sites.ToList(); var first=remaining.FirstOrDefault(s => s.Id==id);
        if (first==null) return [];
        List<RectangularTerritorySite> result=[first]; remaining.Remove(first);
        for (int i=0;i<result.Count;i++)
        foreach (var next in remaining.Where(s => s.Dimension==first.Dimension && result[i].Bounds.Adjacent(s.Bounds,0)).ToArray())
        { result.Add(next); remaining.Remove(next); }
        return result.ToArray();
    }
    internal static bool Covers(IEnumerable<RectangularTerritorySite> sites,BuildingTerritoryRegion room)
    {
        var same=sites.Where(s => s.Dimension==room.Dimension).ToArray();
        return room.Cells.All(p => same.Any(s => s.Bounds.Contains(p.X,p.Z)));
    }
    internal static bool InComponent(BuildingTerritoryRegion room,IEnumerable<RectangularTerritorySite> sites)
        => sites.Any(s => s.Dimension==room.Dimension && (s.Id==room.SiteId || room.FloorCells.Any(p => s.Bounds.Contains(p.X,p.Z))));
    internal static long Support(IEnumerable<RectangularTerritorySite> sites,IEnumerable<BuildingTerritoryRegion> rooms,RectangularTerritoryConfig config)
        => Math.Max(sites.Sum(s => (long)s.InitialArea),(long)RectangularTerritoryRules.Footprint(rooms.Where(r => r.Status=="valid" && r.Kind=="room"))*config.AreaPerRoomFloor);
}
