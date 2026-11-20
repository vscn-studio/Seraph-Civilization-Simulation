using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using SeraphCivilizationSimulation;

internal static class TerritoryRendererChecks
{
    public static void Run(Action<bool, string> check)
    {
        var volume = new TribeBoundaryView { RegionId = "room", Revision = 7, X = 32, Y = 96, Z = 32,
            Cells = [new() { X = 33, Y = 100, Z = 33, Length = 2 }] };
        var building = TerritoryRenderer.BuildBuildingMeshData(volume, true);
        check(building.GetMode() == EnumDrawMode.Lines && building.IndicesCount > 0
            && building.Indices.Take(building.IndicesCount).All(i => i >= 0 && i < building.VerticesCount), "Building outline indices invalid.");
        for (int i = 0; i < building.VerticesCount; i++)
            check(building.xyz[i * 3] is >= 1 and <= 3 && building.xyz[i * 3 + 1] is >= 4 and <= 5
                && building.xyz[i * 3 + 2] is >= 1 and <= 2, "Building outline must preserve actual voxel height and chunk-relative coordinates.");
        using (var stream = new MemoryStream())
        {
            ProtoBuf.Serializer.Serialize(stream, new CivilizationSnapshotPacket { Boundaries = [volume],
                Regions = [new() { Id = "room", Dimension = 1, Revision = 7 }], TerritoryArea = 2, TerritoryBudget = 384, HasMainHall = true });
            stream.Position = 0; var received = ProtoBuf.Serializer.Deserialize<CivilizationSnapshotPacket>(stream);
            check(received.Boundaries.Single().Cells.Single().Length == 2 && received.Regions.Single().Dimension == 1
                && received.HasMainHall && received.TerritoryBudget == 384, "Building territory protocol lost geometry or budget.");
        }
        foreach (int radius in new[] { 1, 50, 1000 })
        {
            var mesh = TerritoryRenderer.BuildBoundaryMeshData(radius, true);
            check(mesh.GetMode() == EnumDrawMode.Lines, "Territory ranges must use independent batched line segments.");
            check(mesh.VerticesCount == mesh.IndicesCount && mesh.VerticesCount % 2 == 0,
                "Every territory boundary segment must have exactly two indexed vertices.");
            check(mesh.Indices.Take(mesh.IndicesCount).All(index => index >= 0 && index < mesh.VerticesCount),
                "Territory boundary indices must stay inside the generated mesh.");
            for (int i = 0; i < mesh.VerticesCount; i++)
            {
                float x = mesh.xyz[i * 3];
                float y = mesh.xyz[i * 3 + 1];
                float z = mesh.xyz[i * 3 + 2];
                double radiusError = Math.Abs((double)x * x + (double)z * z - (double)radius * radius);
                check(float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z)
                    && Math.Abs(x) <= radius + 0.01 && Math.Abs(z) <= radius + 0.01
                    && radiusError <= Math.Max(0.05, radius * 0.002) && y is >= 0 and <= 10,
                    "Territory boundary geometry must be finite, lie on the configured circle and use the expected height.");
            }
        }

        var own = TerritoryRenderer.BuildBoundaryMeshData(50, true);
        var ownColor = BitConverter.ToInt32(own.Rgba, 0);
        check(ColorUtil.ColorA(ownColor) == 180 && ColorUtil.ColorR(ownColor) == 55
            && ColorUtil.ColorG(ownColor) == 220 && ColorUtil.ColorB(ownColor) == 85,
            "Own territory must pack alpha before its visible green color channels.");
        var other = TerritoryRenderer.BuildBoundaryMeshData(50, false);
        var otherColor = BitConverter.ToInt32(other.Rgba, 0);
        check(ColorUtil.ColorA(otherColor) == 180 && ColorUtil.ColorR(otherColor) == 235
            && ColorUtil.ColorG(otherColor) == 70 && ColorUtil.ColorB(otherColor) == 70,
            "Other territory must pack alpha before its visible red color channels.");
        check(TerritoryRenderer.GetBoundarySegmentCount(50) is >= 24 and <= 512
            && TerritoryRenderer.GetBoundarySegmentCount(1000) == 512,
            "Territory boundary geometry must retain a small-radius detail floor and cap large-radius mesh complexity.");
    }
}
