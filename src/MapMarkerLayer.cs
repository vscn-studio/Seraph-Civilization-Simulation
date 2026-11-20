using System.Collections;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace SeraphCivilizationSimulation;

// Project each waypoint once in a common paper frame. Individual sheet rotations
// must not rotate or duplicate labels. Clip glyphs to the actual paper.
internal static class MapMarkerLayer
{
    internal readonly record struct Glyph(LoadedTexture Texture, double Left, double Top, double Width, double Height);

    internal static List<Glyph> Glyphs(IDictionary markers, object glyphs, GroundMapLayout layout, AtlasViewport frame, int pixels, long now, IReadOnlyDictionary<string, double>? markerScales = null)
    {
        var result = new List<Glyph>();
        foreach (object marker in markers.Values)
        {
            object? Value(string name) => GroundMapCompatibility.Property(marker, name);
            var point = new MapPoint(Convert.ToDouble(Value("X")), Convert.ToDouble(Value("Z")));
            if (!layout.Contains(point, frame.Dimension)) continue;
            var position = frame.Project(point);
            bool edge = false;
            if (frame.Holder is { } holder && layout.Contains(holder, frame.Dimension)
                && GroundAtlasClient.TryGetHudTarget(layout, holder, frame.Dimension, frame.Rotation, point, out var target, out edge))
                position = frame.Project(target);
            else if (Math.Abs(position.X) > 1 || Math.Abs(position.Z) > 1) continue;
            var icon = (LoadedTexture?)AccessTools.Method(glyphs.GetType(), "GetIcon", [typeof(string), typeof(int)])
                .Invoke(glyphs, [Value("Icon"), Value("Color")]);
            if (icon == null) continue;
            double scale = 2d / pixels, iconScale = scale * GuiDialogMapSettings.Current.IconScale * (markerScales?.GetValueOrDefault((string)Value("Id")!, 1) ?? 1);
            double labelScale = scale * GuiDialogMapSettings.Current.FontScale;
            double width = icon.Width * iconScale, height = icon.Height * iconScale;
            string name = (string)Value("Name")!;
            LoadedTexture? label = string.IsNullOrWhiteSpace(name) ? null
                : (LoadedTexture)GroundMapCompatibility.Call(glyphs, "GetLabel", name, now)!;
            if (edge)
            {
                double horizontalMargin = Math.Max(width, label?.Width * labelScale ?? 0) / 2 + .035;
                double topMargin = height / 2 + .035;
                double bottomMargin = height / 6 + (label?.Height * labelScale ?? 0) + .035;
                horizontalMargin = Math.Min(horizontalMargin, .99); topMargin = Math.Min(topMargin, .99); bottomMargin = Math.Min(bottomMargin, .99);
                position = new(Math.Clamp(position.X, -1 + horizontalMargin, 1 - horizontalMargin),
                    Math.Clamp(position.Z, -1 + topMargin, 1 - bottomMargin));
            }
            double left = position.X - width / 2, top = position.Z - height / 2;
            result.Add(new(icon, left, top, width, height));
            if (label != null)
                result.Add(new(label, position.X - label.Width * labelScale / 2, top + height / 1.5, label.Width * labelScale, label.Height * labelScale));
        }
        return result;
    }

    internal static MeshData? Mesh(Glyph glyph, AtlasViewport frame, IEnumerable<List<GroundMapVertex>> paper, Vec3d outward)
    {
        var rectangle = new List<MapPoint>
        {
            new(glyph.Left, glyph.Top), new(glyph.Left + glyph.Width, glyph.Top),
            new(glyph.Left + glyph.Width, glyph.Top + glyph.Height), new(glyph.Left, glyph.Top + glyph.Height)
        };
        var triangles = new List<(Vec3d Position, float U, float V)>();
        foreach (var patch in paper)
        {
            // Folded paper is piecewise planar; interpolate within each triangle.
            for (int i = 1; i < patch.Count - 1; i++)
            {
                var a = patch[0]; var b = patch[i]; var c = patch[i + 1];
                var pa = frame.Project(a.Map); var pb = frame.Project(b.Map); var pc = frame.Project(c.Map);
                double determinant = Cross(pb - pa, pc - pa);
                if (Math.Abs(determinant) < 1e-12) continue;
                var triangle = determinant > 0 ? new List<MapPoint> { pa, pb, pc } : [pa, pc, pb];
                var clipped = MapPolygon.Intersect(rectangle, triangle);
                if (MapPolygon.Area(clipped) < 1e-12) continue;
                var normal = (b.Position - a.Position).Cross(c.Position - a.Position).Normalize();
                var offset = normal * (normal.Dot(outward) < 0 ? -.012 : .012);
                for (int j = 1; j < clipped.Count - 1; j++)
                    foreach (var p in new[] { clipped[0], clipped[j], clipped[j + 1] })
                    {
                        double u = Cross(p - pa, pc - pa) / determinant, v = Cross(pb - pa, p - pa) / determinant;
                        var position = a.Position + (b.Position - a.Position) * u + (c.Position - a.Position) * v + offset;
                        triangles.Add((position, (float)Math.Clamp((p.X - glyph.Left) / glyph.Width, 0, 1), (float)Math.Clamp((p.Z - glyph.Top) / glyph.Height, 0, 1)));
                    }
            }
        }
        if (triangles.Count == 0) return null;
        var mesh = new MeshData(triangles.Count, triangles.Count, false, true, true, true);
        foreach (var vertex in triangles)
        {
            mesh.AddIndex(mesh.VerticesCount);
            mesh.AddVertexWithFlags((float)vertex.Position.X, (float)vertex.Position.Y, (float)vertex.Position.Z, vertex.U, vertex.V, -1, 0);
        }
        return mesh;
    }

    private static double Cross(MapPoint a, MapPoint b) => a.X * b.Z - a.Z * b.X;
}
