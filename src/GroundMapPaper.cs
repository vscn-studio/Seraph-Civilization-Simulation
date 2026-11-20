using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace SeraphCivilizationSimulation;

// A thin paper shell follows the same clipped/folded surface used for picking.
// All material UVs follow the printed sheet rather than the world's axes.
internal static class GroundMapPaper
{
    internal const double Thickness = .003;
    internal const double GrainDepth = .0005;
    internal static MeshData[] Meshes(GroundMapSurface surface, GroundMapLayout layout)
    {
        var back = new MeshData(4, 6, false, true, true, true);
        var rim = new MeshData(4, 6, false, true, true, true);
        var grain = new MeshData(4, 6, false, true, true, true);
        var edges = new Dictionary<((long, long, long), (long, long, long)), (GroundMapVertex A, GroundMapVertex B, Vec3d Normal, int Sheet, int Count)>();
        (long, long, long) Key(GroundMapVertex p) => ((long)Math.Round(p.X * 1000000), (long)Math.Round(p.Y * 1000000), (long)Math.Round(p.Z * 1000000));
        for (int sheet = 0; sheet < surface.Patches.Count; sheet++)
            foreach (var polygon in surface.Patches[sheet])
            {
                var normal = new Vec3d();
                for (int j = 1; j < polygon.Count - 1; j++)
                {
                    normal = (polygon[j].Position - polygon[0].Position).Cross(polygon[j + 1].Position - polygon[0].Position);
                    if (normal.LengthSq() > 1e-16) break;
                }
                if (normal.LengthSq() < 1e-16) continue;
                normal.Normalize(); if (normal.Dot(surface.Outward) < 0) normal *= -1;
                for (int j = 1; j < polygon.Count - 1; j++)
                    foreach (var vertex in new[] { polygon[0], polygon[j], polygon[j + 1] })
                    {
                        Add(back, vertex, normal * -Thickness, sheet, -1);
                        Add(grain, vertex, normal * GrainDepth, sheet, ColorUtil.ToRgba(30, 255, 255, 255));
                    }
                for (int j = 0; j < polygon.Count; j++)
                {
                    var a = polygon[j]; var b = polygon[(j + 1) % polygon.Count];
                    var ak = Key(a); var bk = Key(b); var key = ak.CompareTo(bk) < 0 ? (ak, bk) : (bk, ak);
                    edges[key] = edges.TryGetValue(key, out var old) ? old with { Count = old.Count + 1 } : (a, b, normal, sheet, 1);
                }
            }
        foreach (var edge in edges.Values.Where(e => e.Count == 1))
        {
            var inset = edge.Normal * -Thickness;
            int color = ColorUtil.ToRgba(255, 207, 188, 147);
            Add(rim, edge.A, new(), edge.Sheet, color); Add(rim, edge.B, new(), edge.Sheet, color); Add(rim, edge.B, inset, edge.Sheet, color);
            Add(rim, edge.A, new(), edge.Sheet, color); Add(rim, edge.B, inset, edge.Sheet, color); Add(rim, edge.A, inset, edge.Sheet, color);
        }
        return [back, rim, grain];

        void Add(MeshData mesh, GroundMapVertex vertex, Vec3d offset, int sheet, int color)
        {
            var shape = layout.Shapes[sheet];
            double x = vertex.Map.X - shape.CenterX, z = vertex.Map.Z - shape.CenterZ;
            double c = Math.Cos(shape.Rotation), s = Math.Sin(shape.Rotation);
            mesh.AddIndex(mesh.VerticesCount);
            mesh.AddVertexWithFlags((float)(vertex.X + offset.X), (float)(vertex.Y + offset.Y), (float)(vertex.Z + offset.Z),
                (float)((c * x + s * z) / shape.MapWidth + .5), (float)((-s * x + c * z) / shape.MapWidth + .5), color, 0);
        }
    }
}
