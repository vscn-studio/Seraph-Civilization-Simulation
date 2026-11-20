using Cairo;
using HarmonyLib;
using System.Collections;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace SeraphCivilizationSimulation;

// State belongs to the native renderer. Its normal expiry also releases our GPU overlay.
internal static class MapDrawingRendering
{
    private sealed class DrawingState
    {
        internal ItemStack Stack = null!;
        internal string Signature = "";
    }
    private static readonly ConditionalWeakTable<object, DrawingState> States = new();
    internal static void Install(Harmony harmony)
    {
        void Patch(string type, string method, string patch, bool prefix = false)
        {
            var target = AccessTools.Method(AccessTools.TypeByName("ImmersiveMaps.Rendering." + type), method);
            var hook = new HarmonyMethod(typeof(MapDrawingRendering), patch);
            harmony.Patch(target, prefix: prefix ? hook : null, postfix: prefix ? null : hook);
        }
        Patch("MapRenderSystem", "GetOrCreateState", nameof(Attach));
        Patch("MapRenderState", "SetWaypointData", nameof(Changed));
        Patch("MapPainter", "PaintWaypointLayer", nameof(Paint), true);
    }
    private static void Attach(ItemStack stack, object __result) => States.GetOrCreateValue(__result).Stack = stack;
    private static void Changed(object __instance, ref bool __result)
    {
        if (!States.TryGetValue(__instance, out var info)) return;
        string signature = Convert.ToBase64String(info.Stack.Attributes.GetBytes(MapDrawingDocument.Attribute) ?? [])
            + info.Stack.Attributes.GetString(MapMarkerScales.Attribute, "")
            + GuiDialogMapSettings.Current.IconScale + ":" + GuiDialogMapSettings.Current.FontScale;
        if (signature == info.Signature) return;
        info.Signature = signature; __result = true;
    }
    private static bool Paint(object __instance, object state, object template, int mapSize, int mapX, int mapZ, float rotation, long nowMs)
    {
        if (!States.TryGetValue(state, out var info)) return true;
        var api = (ICoreClientAPI)__instance.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single(f => typeof(ICoreClientAPI).IsAssignableFrom(f.FieldType)).GetValue(__instance)!;
        var glyphs = __instance.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single(f => f.FieldType.Name == "WaypointGlyphCache").GetValue(__instance);
        var target = (FrameBufferRef)GroundMapCompatibility.Property(state, "WaypointFramebuffer")!;
        var texture = (LoadedTexture)GroundMapCompatibility.Property(state, "WaypointTexture")!;
        bool collage = GroundMapCompatibility.BorderlessStates.TryGetValue(state, out _);
        var border = (LoadedTexture)GroundMapCompatibility.Property(template, "BorderTexture")!;
        if (collage && GroundAtlasClient.Current is { } client) border = client.GetTransparent(border.Width, border.Height);
        api.Render.RenderTextureIntoFrameBuffer(0, border, 0, 0, border.Width, border.Height, target, 0, 0, -1);
        api.Render.GlToggleBlend(true);
        double blocks = 96 * (mapSize - 2), ratio = texture.Width / blocks;
        if (!collage)
        {
            var scales = MapMarkerScales.Read(info.Stack);
            foreach (object marker in ((IDictionary)GroundMapCompatibility.Property(state, "Waypoints")!).Values)
            {
                object V(string n) => GroundMapCompatibility.Property(marker, n)!;
                double dx = Convert.ToDouble(V("X")) - mapX, dz = Convert.ToDouble(V("Z")) - mapZ;
                double x = Math.Cos(rotation) * dx + Math.Sin(rotation) * dz;
                double z = -Math.Sin(rotation) * dx + Math.Cos(rotation) * dz;
                if (Math.Abs(x) > blocks / 2 || Math.Abs(z) > blocks / 2) continue;
                if (glyphs == null) continue;
                var icon = (LoadedTexture?)AccessTools.Method(glyphs.GetType(), "GetIcon", [typeof(string), typeof(int)]).Invoke(glyphs, [V("Icon"), V("Color")]);
                if (icon == null) continue;
                double scale = GuiDialogMapSettings.Current.IconScale * scales.GetValueOrDefault((string)V("Id"), 1);
                double width = icon.Width * scale, height = icon.Height * scale;
                double px = texture.Width / 2d + x * ratio, py = texture.Height / 2d + z * ratio;
                Blit(api, icon, target, px - width / 2, py - height / 2, scale);
                if (!string.IsNullOrWhiteSpace((string)V("Name")))
                {
                    var label = (LoadedTexture)GroundMapCompatibility.Call(glyphs, "GetLabel", V("Name"), nowMs)!;
                    double font = GuiDialogMapSettings.Current.FontScale;
                    Blit(api, label, target, px - label.Width * font / 2, py + height / 6, font);
                }
            }
        }
        if (RecordedPaperMap.Describe(info.Stack) is { } map && info.Stack.Attributes.HasAttribute(MapDrawingDocument.Attribute))
        {
            try
            {
                var doc = MapDrawingDocument.Read(info.Stack, map);
                using var surface = new ImageSurface(Format.Argb32, texture.Width, texture.Height);
                using (var ctx = new Context(surface))
                {
                    double dx = map.CenterX - mapX, dz = map.CenterZ - mapZ;
                    ctx.Translate(texture.Width / 2d + (Math.Cos(rotation) * dx + Math.Sin(rotation) * dz) * ratio,
                        texture.Height / 2d + (-Math.Sin(rotation) * dx + Math.Cos(rotation) * dz) * ratio);
                    ctx.Rotate(map.Rotation - rotation); ctx.Scale(ratio, ratio); ctx.Translate(-map.MapWidth / 2d, -map.MapWidth / 2d);
                    Draw(ctx, doc, new(map), map.MapWidth, map.MapWidth);
                }
                var overlay = new LoadedTexture(api);
                api.Gui.LoadOrUpdateCairoTexture(surface, false, ref overlay);
                overlay.Width = texture.Width; overlay.Height = texture.Height;
                Blit(api, overlay, target, 0, 0, 1);
                overlay.Dispose();
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException)
            { api.Logger.Warning("Invalid paper drawing ignored: {0}", ex.Message); }
        }
        api.Render.BindTexture2d(texture.TextureId); api.Render.GlGenerateTex2DMipmaps();
        return false;
    }
    internal static void Blit(ICoreClientAPI api, LoadedTexture source, FrameBufferRef target, double x, double y, double scale)
    {
        // The native blitter uses texture dimensions for both UVs and destination size.
        // Temporarily changing only metadata scales the complete texture, without copying GPU data.
        int w = source.Width, h = source.Height;
        try
        {
            source.Width = Math.Max(1, (int)Math.Round(w * scale)); source.Height = Math.Max(1, (int)Math.Round(h * scale));
            api.Render.RenderTextureIntoFrameBuffer(0, source, 0, 0, source.Width, source.Height, target, (float)x, (float)y);
        }
        finally { source.Width = w; source.Height = h; }
    }
    internal static MapDrawingFaces.Point Center(IReadOnlyList<MapDrawingFaces.Point> face)
    {
        double x = face.Average(p => p.X), z = face.Average(p => p.Z);
        return face.MinBy(p => (p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z));
    }
    internal static void Draw(Context ctx, MapDrawingDocument doc, MapDrawingProjection projection, double width, double height,
        IReadOnlyList<MapDrawingFaces.Point>? stroke = null, List<List<MapDrawingFaces.Point>>? knownFaces = null)
    {
        double sx = width / projection.Size, sy = height / projection.Size;
        var faces = knownFaces ?? projection.Faces(doc);
        var named = doc.Areas.GroupBy(a => projection.Cell(a.Seed.X, a.Seed.Z)).ToDictionary(g => g.Key, g => g.First());
        void Line(IReadOnlyList<MapDrawingFaces.Point> pts, bool live, int color = unchecked((int)0xffd8a44a))
        {
            if (pts.Count < 2) return;
            uint rgb = (uint)color; ctx.SetSourceRGBA(live ? 1 : ((rgb >> 16) & 255) / 255d, live ? 1 : ((rgb >> 8) & 255) / 255d, live ? 1 : (rgb & 255) / 255d, 1); ctx.LineWidth = Math.Max(1.3, width / projection.Size);
            ctx.MoveTo((pts[0].X + .5) * sx, (pts[0].Z + .5) * sy);
            foreach (var p in pts.Skip(1)) ctx.LineTo((p.X + .5) * sx, (p.Z + .5) * sy);
            ctx.Stroke();
        }
        // Colour each segment according to an adjacent named face. Shared borders use the first adjacent area.
        var cellColors = new Dictionary<MapDrawingFaces.Point, int>();
        foreach (var face in faces)
        {
            var area = face.Select(p => named.GetValueOrDefault(p)).FirstOrDefault(a => a != null);
            if (area == null) continue;
            foreach (var p in face)
                for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
                    cellColors.TryAdd(new(p.X + dx, p.Z + dz), area.Color);
        }
        foreach (var line in doc.Lines)
        {
            var pts = line.Points.Select(p => projection.Cell(p.X, p.Z)).ToArray();
            for (int i = 1; i < pts.Length; i++)
            {
                var a = pts[i - 1]; var b = pts[i]; int steps = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Z - a.Z));
                if (steps == 0) continue;
                var previous = a;
                for (int n = 1; n <= steps; n++)
                {
                    var next = new MapDrawingFaces.Point(a.X + (b.X - a.X) * n / steps, a.Z + (b.Z - a.Z) * n / steps);
                    Line([previous, next], false, cellColors.GetValueOrDefault(next, unchecked((int)0xffd8a44a))); previous = next;
                }
            }
        }
        if (stroke != null) Line(stroke, true);
        foreach (var area in doc.Areas)
        {
            var seed = projection.Cell(area.Seed.X, area.Seed.Z);
            var face = faces.FirstOrDefault(f => f.Contains(seed)); if (face == null) continue;
            var center = Center(face);
            CairoFont.WhiteSmallText().SetupContext(ctx);
            ctx.SetFontSize(14 * GuiDialogMapSettings.Current.FontScale * width / 410d);
            var ext = ctx.TextExtents(area.Name);
            double x = Math.Clamp((center.X + .5) * sx - ext.Width / 2 - ext.XBearing, 0, Math.Max(0, width - ext.Width));
            double y = Math.Clamp((center.Z + .5) * sy - ext.Height / 2 - ext.YBearing, ext.Height, height);
            ctx.MoveTo(x, y); ctx.TextPath(area.Name); ctx.SetSourceRGBA(0, 0, 0, .9); ctx.LineWidth = 3; ctx.StrokePreserve();
            uint rgb = (uint)area.Color; ctx.SetSourceRGB(((rgb >> 16) & 255) / 255d, ((rgb >> 8) & 255) / 255d, (rgb & 255) / 255d); ctx.Fill();
        }
    }
}
