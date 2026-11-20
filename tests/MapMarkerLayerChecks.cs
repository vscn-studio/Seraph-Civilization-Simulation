using System.Collections;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using SeraphCivilizationSimulation;

internal static class MapMarkerLayerChecks
{
    internal static void Run(Assembly maps, ICoreClientAPI api, object native, ItemStack source, object template, Action<bool, string> check)
    {
        Type Native(string name) => maps.GetType("ImmersiveMaps." + name, true)!;
        object? Call(object target, string name, params object?[] args) => GroundMapCompatibility.Call(target, name, args);
        var a = source.Clone(); a.Attributes.SetBlockPos("mapPosition", new BlockPos(0, 100, 0));
        var b = a.Clone(); b.Attributes.SetBlockPos("mapPosition", new BlockPos(288, 100, 0)); b.Attributes.SetFloat("mapRotation", .7f);
        var waypoints = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), Native("Waypoints.Waypoint")))!;
        var waypoint = Activator.CreateInstance(Native("Waypoints.Waypoint"), 144, 0, 0x123456, "circle", "Only one name")!;
        AccessTools.Property(waypoint.GetType(), "Id").SetValue(waypoint, "shared"); waypoints.Add("shared", waypoint);
        ImmersiveMapCompatibility.Write(a, waypoints); ImmersiveMapCompatibility.Write(b, waypoints);
        var glyphCache = Activator.CreateInstance(Native("Rendering.WaypointGlyphCache"), api, GroundMapCompatibility.Property(native, "Icons"))!;
        var icon = new LoadedTexture(api, 90001, 30, 30); var label = new LoadedTexture(api, 90002, 80, 20);
        var icons = (IDictionary)AccessTools.Field(glyphCache.GetType(), "_icons").GetValue(glyphCache)!;
        icons.Add(("circle", 0x123456, (int)Math.Ceiling(30f * Vintagestory.API.Config.RuntimeEnv.GUIScale)), icon);
        var labels = (IDictionary)AccessTools.Field(glyphCache.GetType(), "_labels").GetValue(glyphCache)!;
        labels.Add("Only one name", Activator.CreateInstance(labels.GetType().GetGenericArguments()[1], label));
        AccessTools.Field(native.GetType(), "_glyphs").SetValue(native, glyphCache);
        check(MapMarkerLayer.Glyphs(waypoints, glyphCache, new([a, b]), new(new(144, 0), 100, 0, 0), 256, 10000).Count == 2,
            "A named waypoint at a joined sheet edge must produce one icon and one label.");
        AccessTools.Property(waypoint.GetType(), "X").SetValue(waypoint, 400);
        var edgeFrame = new AtlasViewport(new(144, 0), 288, 0, 0, new(144, 0));
        var edgeGlyphs = MapMarkerLayer.Glyphs(waypoints, glyphCache, new([a, b]), edgeFrame, 256, 10000);
        check(edgeGlyphs.Count == 2 && edgeGlyphs.All(g => g.Left >= -1 && g.Top >= -1
            && g.Left + g.Width <= 1 && g.Top + g.Height <= 1),
            "Off-view markers must keep the full icon and name inside the paper's visible edge margin.");
        AccessTools.Property(waypoint.GetType(), "X").SetValue(waypoint, 144);
        var nativeStates = (IDictionary)AccessTools.Field(native.GetType(), "_states").GetValue(native)!;
        var previous = nativeStates.Values.Cast<object>().ToArray();
        using var collage = new MapCollage(api, native, [a, b], true);
        var states = nativeStates.Values.Cast<object>().Except(previous).ToArray();
        foreach (var state in states)
        {
            Call(state, "CreateResources", api, template, (System.Func<int, int[]>)(size => new int[size * size]), null);
            Call(state, "SetView", 0, 0, 0f, 10000L); Call(state, "OnTerrainPainted", 0, 0, 10000L, true);
        }
        foreach (float angle in new[] { 0f, .7f, -1.2f, MathF.PI / 2 })
        {
            var frame = new AtlasViewport(new(144, 0), 100, angle, 0);
            var model = collage.GetModel(frame)!;
            check(model.textureids.Count(id => id == icon.TextureId) == 1 && model.textureids.Count(id => id == label.TextureId) == 1,
                "Overlapping rotated original sheets must render one shared icon and one shared name. Textures: " + string.Join(",", model.textureids));
            check(states.All(state => GroundMapCompatibility.Property(state, "RequestedWaypoints") == null),
                "The original sheet painters must not also draw copies of the unified waypoints.");
            var glyphs = MapMarkerLayer.Glyphs(waypoints, glyphCache, collage.Layout, frame, 256, 10000);
            var paper = frame.Clip(collage.Layout).SelectMany(p => p).Select(p => p.Select(point =>
            {
                var position = frame.Project(point); return new GroundMapVertex(point, position.X, position.Z, 0);
            }).ToList()).ToList();
            foreach (var glyph in glyphs)
            {
                var mesh = MapMarkerLayer.Mesh(glyph, frame, paper, new Vec3d(0, 0, 1))!;
                double area = 0;
                for (int i = 0; i < mesh.VerticesCount; i += 3)
                    area += Math.Abs((mesh.Uv[2 * i + 2] - mesh.Uv[2 * i]) * (mesh.Uv[2 * i + 5] - mesh.Uv[2 * i + 1])
                        - (mesh.Uv[2 * i + 4] - mesh.Uv[2 * i]) * (mesh.Uv[2 * i + 3] - mesh.Uv[2 * i + 1])) / 2;
                check(Math.Abs(area - 1) < .00001 && mesh.Uv.Take(mesh.VerticesCount * 2).All(v => v >= 0 && v <= 1),
                    "A name spanning a rotated seam must cover its full texture exactly once, with no repeated or missing letters.");
            }
            var edgeGlyph = glyphs[1] with { Left = .95, Top = .95 };
            var clipped = MapMarkerLayer.Mesh(edgeGlyph, frame, paper, new Vec3d(0, 0, 1));
            check(clipped == null || clipped.xyz.Take(clipped.VerticesCount * 3).Where((_, i) => i % 3 < 2).All(v => v >= -1.00001 && v <= 1.00001),
                "Waypoint labels near a paper edge must clip without wrapping onto another edge.");
        }
        check(MapMarkerLayer.Glyphs(waypoints, glyphCache, collage.Layout, new(new(144, 0), 100, 0, 1), 256, 10000).Count == 0
            && MapMarkerLayer.Glyphs(waypoints, glyphCache, collage.Layout, new(new(10000, 0), 100, 0, 0), 256, 10000).Count == 0,
            "Other dimensions and views outside the recorded map must never expose waypoint names.");
        check(ImmersiveMapCompatibility.Read(a).Count == 1 && ImmersiveMapCompatibility.Read(b).Count == 1,
            "Unified rendering must preserve the stored original waypoint data.");
        var placedReference = a.Clone(); placedReference.Attributes.SetFloat("mapRotation", .71f);
        var placement = new GroundMapPlacement(); placement.SetReference(placedReference);
        foreach (double angle in new[] { .19, 1.11, 2.76 })
        {
            placement.FacePlayer(new(Math.Sin(angle), 0, -Math.Cos(angle)));
            var surface = new GroundMapSurface(new([placedReference]), placement, (x, z) => 0);
            var glyph = new MapMarkerLayer.Glyph(label, -.1, -.1, .2, .2);
            var mesh = MapMarkerLayer.Mesh(glyph, surface.MarkerFrame, surface.Patches[0], surface.Outward)!;
            // Derive the physical text baseline from a textured triangle. It must turn
            // with the paper, regardless of the original sheet's saved map rotation.
            double u1 = mesh.Uv[2] - mesh.Uv[0], v1 = mesh.Uv[3] - mesh.Uv[1];
            double u2 = mesh.Uv[4] - mesh.Uv[0], v2 = mesh.Uv[5] - mesh.Uv[1];
            var d1 = new Vec3d(mesh.xyz[3] - mesh.xyz[0], mesh.xyz[4] - mesh.xyz[1], mesh.xyz[5] - mesh.xyz[2]);
            var d2 = new Vec3d(mesh.xyz[6] - mesh.xyz[0], mesh.xyz[7] - mesh.xyz[1], mesh.xyz[8] - mesh.xyz[2]);
            var baseline = ((d1 * v2 - d2 * v1) / (u1 * v2 - u2 * v1)).Normalize();
            check(baseline.DistanceTo(new Vec3d(Math.Cos(angle), 0, Math.Sin(angle))) < 1e-5,
                "Placed label text must rotate with the physical paper rather than reveal a fixed world direction.");
        }
        collage.Surface = new(collage.Layout, placement, (x, z) => 0);
        var placedModel = collage.GetModel()!;
        check(placedModel.textureids.Count(id => id == icon.TextureId) == 1 && placedModel.textureids.Count(id => id == label.TextureId) == 1
            && Array.LastIndexOf(placedModel.textureids, 1) < Array.IndexOf(placedModel.textureids, label.TextureId),
            "Placed models must use the paper's label frame once and draw names after the paper grain.");
        AccessTools.Field(native.GetType(), "_glyphs").SetValue(native, null);
        Call(glyphCache, "Dispose");
    }
}
