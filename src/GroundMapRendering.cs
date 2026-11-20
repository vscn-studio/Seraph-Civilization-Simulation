using System.Collections;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace SeraphCivilizationSimulation;

// Borrow native map textures; own only the meshes combining them. No second terrain cache.
internal sealed class MapCollage : IDisposable
{
    private readonly ICoreClientAPI api;
    private readonly List<ItemStack> sheets;
    private readonly object[] states;
    private readonly object native;
    private readonly IDictionary markers;
    private readonly Dictionary<string, double> markerScales = [];
    private readonly bool framed;
    internal GroundMapSurface? Surface { get; set; }
    internal GroundMapLayout Layout { get; }
    private MultiTextureMeshRef? model;
    private AtlasViewport? builtViewport;
    private GroundMapSurface? builtSurface;
    private int builtGeometry;
    private long built, refreshed, lastRequested;
    internal MapCollage(ICoreClientAPI api, object native, IReadOnlyList<ItemStack> source, bool framed = false)
    {
        this.api = api; this.native = native; this.framed = framed;
        sheets = source.Select(s => s.Clone()).ToList(); Layout = new(sheets);
        markers = ImmersiveMapCompatibility.Read(sheets[0]);
        foreach (var sheet in sheets.Skip(1))
            foreach (DictionaryEntry marker in ImmersiveMapCompatibility.Read(sheet))
                if (!markers.Contains(marker.Key)) markers.Add(marker.Key, marker.Value);
        // Native sheet layers retain the transparent background; the assembled layer
        // below owns all icons and names, including across overlap and rotation seams.
        foreach (var sheet in sheets)
        {
            foreach (var scale in MapMarkerScales.Read(sheet)) markerScales.TryAdd(scale.Key, scale.Value);
            sheet.Attributes.RemoveAttribute("waypoints");
        }
        states = sheets.Select(sheet =>
        {
            sheet.TempAttributes.RemoveAttribute("immersivemaps-renderid");
            var state = GroundMapCompatibility.Call(native, "GetOrCreateState", sheet.Item, sheet)!;
            GroundMapCompatibility.BorderlessStates.Add(state, new object()); return state;
        }).ToArray();
    }
    internal MultiTextureMeshRef? GetModel(AtlasViewport? viewport = null)
    {
        long now = api.World.ElapsedMilliseconds;
        if (framed && Surface == null)
        {
            var first = Layout.Shapes[0];
            viewport ??= new AtlasViewport(new(first.CenterX, first.CenterZ), first.MapWidth, first.Rotation, first.Dimension);
        }
        var pieces = viewport?.Clip(Layout) ?? Layout.Pieces;
        // Native map states expire while this surface is out of render range.
        if (now - lastRequested > 15000)
        {
            for (int i = 0; i < states.Length; i++)
            {
                var state = GroundMapCompatibility.Call(native, "GetOrCreateState", sheets[i].Item, sheets[i])!;
                if (ReferenceEquals(state, states[i])) continue;
                states[i] = state;
                GroundMapCompatibility.BorderlessStates.GetValue(state, _ => new object());
                model?.Dispose(); model = null;
            }
        }
        lastRequested = now;
        for (int i = 0; i < states.Length; i++)
        {
            var shape = Layout.Shapes[i]; var state = states[i];
            AccessTools.Property(state.GetType(), "LastUsedMs").SetValue(state, now);
            GroundMapCompatibility.Call(state, "Request", shape.CenterX, shape.CenterZ, shape.Rotation, sheets[i].Attributes.GetBytes("waypoints"), 0f, false);
            if (now - refreshed >= 2000) GroundMapCompatibility.Call(state, "RequestTerrainRepaint");
        }
        if (now - refreshed >= 2000) refreshed = now;
        if (model != null && now - built < 250 && viewport == builtViewport && ReferenceEquals(Surface, builtSurface)) return model;
        if (states.Any(s => GroundMapCompatibility.Property(s, "Model") == null)) return model;
        var firstShape = Layout.Shapes[0];
        var markerFrame = viewport ?? Surface?.MarkerFrame ?? new AtlasViewport(new(Layout.CenterX, Layout.CenterZ), (int)Math.Ceiling(Layout.Span), 0, firstShape.Dimension);
        var glyphCache = AccessTools.Field(native.GetType(), "_glyphs").GetValue(native);
        int markerPixels = (int)(((LoadedTexture)GroundMapCompatibility.Property(states[0], "WaypointTexture")!).Width * markerFrame.Width / firstShape.MapWidth);
        var glyphs = glyphCache == null ? [] : MapMarkerLayer.Glyphs(markers, glyphCache, Layout, markerFrame, Math.Max(1, markerPixels), now, markerScales);
        var geometry = new HashCode();
        foreach (var glyph in glyphs) geometry.Add(glyph);
        foreach (var state in states)
        {
            geometry.Add(((LoadedTexture)GroundMapCompatibility.Property(state, "TerrainTexture")!).TextureId);
            geometry.Add(((LoadedTexture)GroundMapCompatibility.Property(state, "WaypointTexture")!).TextureId);
            foreach (float uv in ((MeshData)GroundMapCompatibility.Property(state, "TerrainMesh")!).Uv) geometry.Add(uv);
            if (GroundMapCompatibility.Property(state, "MarkerMesh") is MeshData marker)
                foreach (float uv in marker.Uv) geometry.Add(uv);
        }
        int geometryHash = geometry.ToHashCode();
        // Painting updates existing textures in place. It does not require disposing and
        // reuploading the same mesh every quarter-second.
        if (model != null && viewport == builtViewport && ReferenceEquals(Surface, builtSurface) && geometryHash == builtGeometry) return model;
        var meshes = new List<MeshRef>(); var textures = new List<int>();
        try
        {
            if (framed)
            {
                var template = GroundMapCompatibility.Call(native, "GetTemplate", sheets[0].Item)!;
                AddFrame((LoadedTexture)GroundMapCompatibility.Property(template, "BlankTerrainTexture")!, -.001f, true);
                AddFrame((LoadedTexture)GroundMapCompatibility.Property(template, "BackTexture")!, -.015f, false);
            }
            for (int i = 0; i < states.Length; i++)
            {
                var state = states[i]; var shape = Layout.Shapes[i];
                var terrain = (MeshData)GroundMapCompatibility.Property(state, "TerrainMesh")!;
                var terrainTexture = (LoadedTexture)GroundMapCompatibility.Property(state, "TerrainTexture")!;
                var waypointTexture = (LoadedTexture)GroundMapCompatibility.Property(state, "WaypointTexture")!;
                if (pieces[i].Count == 0) continue;
                AddLayer(terrainTexture.TextureId, terrain.Uv, 0);
                AddLayer(waypointTexture.TextureId, [0, 0, 1, 0, 1, 1, 0, 1], .002f);
                if (viewport == null && GroundMapCompatibility.Property(state, "MarkerMesh") is MeshData marker && GroundMapCompatibility.Property(state, "Marker") is { } symbol)
                    AddLayer(((LoadedTexture)GroundMapCompatibility.Property(symbol, "Texture")!).TextureId, marker.Uv, .004f);
                void AddLayer(int textureId, float[] uv, float depth)
                {
                    var polygons = Surface?.Patches[i] ?? pieces[i].Select(p => p.Select(point =>
                    {
                        var projected = viewport?.Project(point) ?? new MapPoint((point.X - Layout.CenterX) / Layout.Span * 2, (point.Z - Layout.CenterZ) / Layout.Span * 2);
                        return new GroundMapVertex(point, projected.X, projected.Z, 0);
                    }).ToList()).ToList();
                    int count = polygons.Sum(p => (p.Count - 2) * 3);
                    if (count == 0) return;
                    var mesh = new MeshData(count, count, false, true, true, true);
                    foreach (var polygon in polygons)
                    {
                        var offset = new Vec3d(0, 0, depth);
                        if (Surface != null)
                        {
                            offset.Set(0, depth, 0);
                            for (int n = 1; n < polygon.Count - 1; n++)
                            {
                                var normal = (polygon[n].Position - polygon[0].Position).Cross(polygon[n + 1].Position - polygon[0].Position);
                                if (normal.LengthSq() > 1e-16) { offset = normal.Normalize() * (normal.Dot(Surface.Outward) < 0 ? -depth : depth); break; }
                            }
                        }
                        for (int j = 1; j < polygon.Count - 1; j++)
                            foreach (var vertex in new[] { polygon[0], polygon[j], polygon[j + 1] })
                            {
                                var point = vertex.Map;
                                double dx = point.X - shape.CenterX, dz = point.Z - shape.CenterZ;
                                double c = Math.Cos(shape.Rotation), s = Math.Sin(shape.Rotation);
                                float u = (float)((c * dx + s * dz) / shape.MapWidth + .5), v = (float)((-s * dx + c * dz) / shape.MapWidth + .5);
                                float tu = uv[0] + (uv[2] - uv[0]) * u + (uv[6] - uv[0]) * v;
                                float tv = uv[1] + (uv[3] - uv[1]) * u + (uv[7] - uv[1]) * v;
                                mesh.AddIndex(mesh.VerticesCount);
                                mesh.AddVertexWithFlags((float)(vertex.X + offset.X), (float)(vertex.Y + offset.Y),
                                    (float)(vertex.Z + offset.Z), tu, tv, -1, 0);
                            }
                    }
                    meshes.Add(api.Render.UploadMesh(mesh)); textures.Add(textureId);
                }
            }
            if (Surface != null)
            {
                var template = GroundMapCompatibility.Call(native, "GetTemplate", sheets[0].Item)!;
                var texture = (LoadedTexture)GroundMapCompatibility.Property(template, "BackTexture")!;
                // Blend the grain after terrain, with labels above it. Drawing it first
                // would write depth before the opaque map underneath has been drawn.
                foreach (var mesh in GroundMapPaper.Meshes(Surface, Layout))
                    if (mesh.VerticesCount > 0)
                    { meshes.Add(api.Render.UploadMesh(mesh)); textures.Add(texture.TextureId); }
            }
            var paper = Surface?.Patches.SelectMany(p => p).ToList() ?? pieces.SelectMany(p => p).Select(p => p.Select(point =>
            {
                var projected = viewport?.Project(point) ?? new MapPoint((point.X - Layout.CenterX) / Layout.Span * 2, (point.Z - Layout.CenterZ) / Layout.Span * 2);
                return new GroundMapVertex(point, projected.X, projected.Z, 0);
            }).ToList()).ToList();
            foreach (var glyph in glyphs)
                if (MapMarkerLayer.Mesh(glyph, markerFrame, paper, Surface?.Outward ?? new Vec3d(0, 0, 1)) is { } mesh)
                { meshes.Add(api.Render.UploadMesh(mesh)); textures.Add(glyph.Texture.TextureId); }
            if (framed)
            {
                var template = GroundMapCompatibility.Call(native, "GetTemplate", sheets[0].Item)!;
                AddFrame((LoadedTexture)GroundMapCompatibility.Property(template, "BorderTexture")!, .006f, true);
                if (viewport is { } view && view.Dimension == Layout.Shapes[0].Dimension
                    && Layout.Contains(view.Holder ?? view.Center, view.Dimension)
                    && view.Project(view.Holder ?? view.Center) is var position
                    && GroundMapCompatibility.Property(states[0], "Marker") is { } marker)
                {
                    var texture = (LoadedTexture)GroundMapCompatibility.Property(marker, "Texture")!;
                    // Keep the complete icon on the paper and above its border. The native
                    // full-paper UV projection clips the icon at an edge, under the frame.
                    var quad = CreateQuad(texture.TextureId, .008f, true);
                    double width = ((LoadedTexture)GroundMapCompatibility.Property(states[0], "WaypointTexture")!).Width;
                    int markWidth = (int)GroundMapCompatibility.Property(marker, "MarkWidth")!;
                    int markHeight = (int)GroundMapCompatibility.Property(marker, "MarkHeight")!;
                    double halfWidth = markWidth / width, halfHeight = markHeight / width;
                    double x = Math.Clamp(position.X, -1 + halfWidth, 1 - halfWidth);
                    double y = Math.Clamp(position.Z, -1 + halfHeight, 1 - halfHeight);
                    for (int i = 0; i < quad.VerticesCount; i++)
                    {
                        quad.xyz[3 * i] = (float)(quad.xyz[3 * i] * halfWidth + x);
                        quad.xyz[3 * i + 1] = (float)(quad.xyz[3 * i + 1] * halfHeight + y);
                        quad.Uv[2 * i] = (float)((quad.Uv[2 * i] * markWidth + (int)GroundMapCompatibility.Property(marker, "MarkX")!) / (double)texture.Width);
                        quad.Uv[2 * i + 1] = (float)((quad.Uv[2 * i + 1] * markHeight + (int)GroundMapCompatibility.Property(marker, "MarkY")!) / (double)texture.Height);
                    }
                    meshes.Add(api.Render.UploadMesh(quad)); textures.Add(texture.TextureId);
                }
            }
            void AddFrame(LoadedTexture texture, float depth, bool front)
            {
                var quad = CreateQuad(texture.TextureId, depth, front);
                meshes.Add(api.Render.UploadMesh(quad)); textures.Add(texture.TextureId);
            }
            MeshData CreateQuad(int texture, float depth, bool front)
            {
                var quad = (MeshData)AccessTools.Method(AccessTools.TypeByName("ImmersiveMaps.Rendering.MapPainter"), "CreateLayerQuad").Invoke(null, [texture, depth, front])!;
                // Native paper has extra-weak wind on two corners. Our clipped terrain does not:
                // a waving paper/blank layer crosses it and intermittently hides the map.
                for (int i = 0; i < quad.VerticesCount; i++) quad.Flags[i] &= ~VertexFlags.WindBitsMask;
                return quad;
            }
            var next = new MultiTextureMeshRef(meshes.ToArray(), textures.ToArray());
            model?.Dispose(); model = next; built = now; builtViewport = viewport; builtSurface = Surface; builtGeometry = geometryHash;
        }
        catch { foreach (var mesh in meshes) mesh.Dispose(); throw; }
        return model;
    }
    public void Dispose() { model?.Dispose(); model = null; }
}

internal sealed class GroundAtlasClient : IRenderer
{
    private const float RotationStep = MathF.PI / 36f;
    internal static GroundAtlasClient? Current { get; private set; }
    private readonly Dictionary<(int, int), LoadedTexture> transparent = [];
    private readonly ICoreClientAPI api;
    private readonly Dictionary<string, AtlasEntry> atlases = [];
    private readonly Dictionary<string, float> orientations = [];
    private readonly Dictionary<(string Name, float Scale), (LoadedTexture Texture, long LastUsed)> hudLabels = [];
    private sealed record AtlasEntry(MapCollage View, string OrientationKey) { internal long LastUsed; }
    internal object? Native => api.ModLoader.GetModSystem("ImmersiveMaps.Rendering.MapRenderSystem");
    internal GroundAtlasClient(ICoreClientAPI api)
    {
        this.api = api; Current = this;
        api.Event.RegisterRenderer(this, EnumRenderStage.Ortho, "vscn-atlas-markers");
    }
    internal LoadedTexture GetTransparent(int width, int height)
    {
        if (transparent.TryGetValue((width, height), out var texture)) return texture;
        texture = new LoadedTexture(api, 0, width, height);
        api.Render.LoadOrUpdateTextureFromBgra(new int[width * height], false, 0, ref texture);
        transparent[(width, height)] = texture; return texture;
    }
    internal MultiTextureMeshRef? GetAtlas(object native, ItemStack stack, ItemSlot? slot)
    {
        string hash = stack.Collectible.Code + ":" + ImmersiveMapCompatibility.Fingerprint(stack);
        if (!atlases.TryGetValue(hash, out var entry))
        {
            var originals = GroundMapData.Sheets(stack, api.World).Select(s => s.Clone()).ToList();
            if (originals.Count == 0) return null;
            foreach (var original in originals) ImmersiveMapCompatibility.Write(original, ImmersiveMapCompatibility.Read(stack));
            atlases[hash] = entry = new(new(api, native, originals, true), OrientationKey(stack));
        }
        var holder = (Vintagestory.API.Common.Entities.Entity?)GroundMapCompatibility.Call(native, "GetAnchor", slot) ?? api.World.Player?.Entity;
        var shape = entry.View.Layout.Shapes[0];
        var center = holder == null ? new MapPoint(shape.CenterX, shape.CenterZ) : new MapPoint(holder.Pos.X, holder.Pos.Z);
        var viewport = entry.View.Layout.ViewportFor(center, holder?.Pos.Dimension ?? shape.Dimension, Orientation(entry.OrientationKey, holder));
        entry.LastUsed = api.World.ElapsedMilliseconds; return entry.View.GetModel(viewport);
    }
    private static string OrientationKey(ItemStack stack)
    {
        // The stored originals identify a stitched map across clones, marker edits and locks.
        // Viewing angles remain client state; never rewrite the originals' coverage rotation.
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true))
            stack.Attributes.GetTreeAttribute(GroundMapData.AtlasKey).ToBytes(writer);
        return stack.Collectible.Code + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.ToArray()));
    }
    private float Orientation(string key, Vintagestory.API.Common.Entities.Entity? holder)
    {
        if (!orientations.TryGetValue(key, out float angle))
        {
            var forward = holder?.Pos.GetViewVector();
            angle = forward == null ? 0 : (float)Math.Atan2(forward.X, -forward.Z);
            orientations[key] = angle;
        }
        return angle;
    }
    internal static bool TryGetHudTarget(GroundMapLayout layout, MapPoint player, int dimension, float rotation,
        MapPoint marker, out MapPoint target, out bool edge)
    {
        target = marker; edge = false;
        // Pins are useful only while the player is on recorded terrain. Do not
        // use the layout's bounding box: gaps and holes are not map data.
        if (!layout.Contains(player, dimension) || !layout.Contains(marker, dimension)) return false;
        var view = layout.ViewportFor(player, dimension, rotation);
        var projected = view.Project(marker);
        if (view.Contains(marker)) return true;
        double extent = Math.Max(Math.Abs(projected.X), Math.Abs(projected.Z));
        if (!double.IsFinite(extent) || extent < 1e-12) return false;
        target = view.Unproject(new(projected.X / extent, projected.Z / extent));
        edge = true;
        return true;
    }
    internal static (double X, double Y) HudEdgePosition(MapPoint player, MapPoint marker, double forwardX, double forwardZ)
    {
        double dx = marker.X - player.X, dz = marker.Z - player.Z;
        double length = Math.Sqrt(dx * dx + dz * dz), forwardLength = Math.Sqrt(forwardX * forwardX + forwardZ * forwardZ);
        if (length < 1e-9 || forwardLength < 1e-9) return (0, -1);
        dx /= length; dz /= length; forwardX /= forwardLength; forwardZ /= forwardLength;
        double right = dx * -forwardZ + dz * forwardX;
        double ahead = dx * forwardX + dz * forwardZ;
        double scale = Math.Max(Math.Abs(right), Math.Abs(ahead));
        return (right / scale, -ahead / scale);
    }
    internal bool RotateHeld(ItemStack stack, float delta)
    {
        if (!GroundMapData.IsAtlas(stack) || !float.IsFinite(delta)) return false;
        if (delta == 0) return true;
        string key = OrientationKey(stack);
        float angle = Orientation(key, api.World.Player?.Entity);
        orientations[key] = (angle + MathF.CopySign(RotationStep, delta)) % MathF.Tau;
        return true;
    }
    internal void PlaceOnWall(ItemStack stack, BlockSelection selection)
    {
        api.Network.GetChannel(VscnCivilizationModSystem.ChannelName).SendPacket(new GroundMapPlaceRequest
        {
            X = selection.Position.X, Y = selection.Position.Y, Z = selection.Position.Z, Dimension = selection.Position.dimension,
            Face = selection.Face.Index, DidOffset = selection.DidOffset,
            HitX = selection.HitPosition.X, HitY = selection.HitPosition.Y, HitZ = selection.HitPosition.Z,
            HotbarSlot = api.World.Player.InventoryManager.ActiveHotbarSlotNumber,
            Fingerprint = ImmersiveMapCompatibility.Fingerprint(stack),
            ViewRotation = Orientation(OrientationKey(stack), api.World.Player.Entity)
        });
    }
    internal LoadedTexture HudLabel(string name, long now)
    {
        var key = (name, RuntimeEnv.GUIScale);
        if (!hudLabels.TryGetValue(key, out var entry))
        {
            // Paper labels use the native 28-point glyph cache. The screen HUD has
            // always used a separate 14-point, unscaled label with a dark outline.
            var font = CairoFont.WhiteSmallText().WithFontSize(14).WithWeight(Cairo.FontWeight.Bold)
                .WithStroke([0, 0, 0, 1], 3);
            var background = new TextBackground { Padding = 2, FillColor = new double[4], BorderColor = new double[4], BorderWidth = 0, Radius = 0 };
            entry = (api.Gui.TextTexture.GenUnscaledTextTexture(name, font, background), now);
        }
        hudLabels[key] = (entry.Texture, now);
        return entry.Texture;
    }
    public double RenderOrder => .94;
    public int RenderRange => 0;
    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        long now = api.World.ElapsedMilliseconds;
        foreach (var key in atlases.Where(e => now - e.Value.LastUsed > 15000).Select(e => e.Key).ToArray())
        { atlases[key].View.Dispose(); atlases.Remove(key); }
        foreach (var key in hudLabels.Where(e => now - e.Value.LastUsed > 60000).Select(e => e.Key).ToArray())
        { hudLabels[key].Texture.Dispose(); hudLabels.Remove(key); }
        if (api.HideGuis || api.World.Player?.Entity is not { } player || Native is not { } native) return;
        var stack = player.RightHandItemSlot?.Itemstack;
        if (!GroundMapData.IsAtlas(stack)) stack = player.LeftHandItemSlot?.Itemstack;
        if (stack == null || !GroundMapData.IsAtlas(stack)) return;
        var atlasSheets = GroundMapData.Sheets(stack, api.World);
        if (atlasSheets.Count == 0) return;
        var layout = new GroundMapLayout(atlasSheets);
        var playerPoint = new MapPoint(player.Pos.X, player.Pos.Z);
        if (!layout.Contains(playerPoint, player.Pos.Dimension)) return;
        float rotation = Orientation(OrientationKey(stack), player);
        var glyphs = AccessTools.Field(native.GetType(), "_glyphs").GetValue(native);
        if (glyphs == null) return;
        foreach (object marker in ImmersiveMapCompatibility.Read(stack).Values)
        {
            if (GroundMapCompatibility.Property(marker, "Pinned") is not true) continue;
            double x = Convert.ToDouble(GroundMapCompatibility.Property(marker, "X")), z = Convert.ToDouble(GroundMapCompatibility.Property(marker, "Z"));
            if (!GroundMapData.Contains(stack, x, z)) continue;
            if (!TryGetHudTarget(layout, playerPoint, player.Pos.Dimension, rotation, new(x, z), out var target, out _)) continue;
            var render = api.Render;
            var direction = HudEdgePosition(playerPoint, new(x, z), player.Pos.GetViewVector().X, player.Pos.GetViewVector().Z);
            double screenX = render.FrameWidth / 2d + direction.X * (render.FrameWidth / 2d - 20);
            double screenY = render.FrameHeight / 2d + direction.Y * (render.FrameHeight / 2d - 20);
            var icon = (LoadedTexture?)AccessTools.Method(glyphs.GetType(), "GetIcon", [typeof(string), typeof(int)]).Invoke(glyphs,
                [GroundMapCompatibility.Property(marker, "Icon"), GroundMapCompatibility.Property(marker, "Color")]);
            if (icon != null) render.Render2DTexturePremultipliedAlpha(icon.TextureId, (float)screenX - 12, (float)screenY - 12, 24, 24, 970);
            string name = (string)GroundMapCompatibility.Property(marker, "Name")!;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var label = HudLabel(name, now);
            float width = Math.Min(180, label.Width);
            double labelY = screenY < render.FrameHeight / 2d ? screenY + 14 : screenY - label.Height * width / label.Width - 14;
            render.Render2DTexturePremultipliedAlpha(label.TextureId, (float)screenX - width / 2, (float)labelY, width, label.Height * width / label.Width, 972);
        }
    }
    public void Dispose()
    {
        api.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);
        foreach (var entry in atlases.Values) entry.View.Dispose(); atlases.Clear(); orientations.Clear();
        foreach (var texture in transparent.Values) texture.Dispose(); transparent.Clear();
        foreach (var entry in hudLabels.Values) entry.Texture.Dispose(); hudLabels.Clear();
        if (ReferenceEquals(Current, this)) Current = null;
    }
}

internal sealed class GroundMapRenderer : IRenderer
{
    private static readonly HashSet<GroundMapRenderer> active = [];
    private readonly ICoreClientAPI api;
    private readonly BlockEntityGroundMap maps;
    private MapCollage? view;
    private int selected = -1;
    private bool ctrl;
    private string revision = "";
    private bool disposed;
    private long projected;
    public double RenderOrder => .5;
    public int RenderRange => 32;
    internal GroundMapRenderer(ICoreClientAPI api, BlockEntityGroundMap maps)
    {
        this.api = api; this.maps = maps;
        active.Add(this);
        api.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "vscn-ground-map"); api.Event.MouseDown += MouseDown;
    }
    internal void Invalidate() { view?.Dispose(); view = null; selected = -1; }
    private (int Index, Vec3d Point, double Distance)? Hit()
    {
        if (disposed || view?.Surface == null || maps.Pos.dimension != api.World.Player.Entity.Pos.Dimension) return null;
        var player = api.World.Player.Entity;
        var origin = player.Pos.XYZ.Add(player.LocalEyePos).Sub(maps.Pos.X, maps.Pos.InternalY, maps.Pos.Z);
        var hit = view.Surface.Hit(origin, player.Pos.GetViewVector().ToVec3d());
        if (hit == null) return null;
        if (!GroundMapSurface.Visible(api.World, maps.Pos, origin, hit.Value.Point)) return null;
        return hit;
    }
    private void MouseDown(MouseEvent e)
    {
        if (e.Handled || e.Button is not (EnumMouseButton.Left or EnumMouseButton.Right) || Hit() is not { } hit) return;
        if (active.Any(r => !ReferenceEquals(r, this) && r.Hit() is { } other && other.Distance < hit.Distance)) return;
        bool held = api.World.Player.Entity.Controls.CtrlKey;
        string action;
        if (e.Button == EnumMouseButton.Left) { if (!held) { e.Handled = true; return; } action = "take"; }
        else if (held && RecordedPaperMap.IsPaperMap(api.World.Player.InventoryManager.ActiveHotbarSlot.Itemstack)) action = "add";
        else if (api.World.Player.InventoryManager.ActiveHotbarSlot.Itemstack?.Collectible.Code.ToString() == CivilizationMapInventory.NeedleCode) action = "sew";
        else return;
        e.Handled = true;
        int index = action == "take" && ctrl && selected >= 0 ? selected : hit.Index;
        api.Network.GetChannel(VscnCivilizationModSystem.ChannelName).SendPacket(new GroundMapTakeRequest
        { X = maps.Pos.X, Y = maps.Pos.Y, Z = maps.Pos.Z, Dimension = maps.Pos.dimension, Index = index, Revision = maps.Revision,
            Action = action, HitX = hit.Point.X, HitY = hit.Point.Y, HitZ = hit.Point.Z });
    }
    public void OnRenderFrame(float dt, EnumRenderStage stage)
    {
        if (disposed || maps.Sheets.Count == 0 || maps.Pos.dimension != api.World.Player.Entity.Pos.Dimension
            || api.World.Player.Entity.Pos.SquareDistanceTo(maps.Pos.X + .5, maps.Pos.InternalY, maps.Pos.Z + .5) > RenderRange * RenderRange) return;
        if (revision != maps.Revision) { Invalidate(); revision = maps.Revision; projected = 0; }
        if (view == null && GroundAtlasClient.Current?.Native is { } native) view = new(api, native, maps.DisplaySheets);
        if (view != null && (view.Surface == null || api.World.ElapsedMilliseconds - projected > 1000))
        { view.Surface = maps.Surface(); projected = api.World.ElapsedMilliseconds; }
        var model = view?.GetModel(); if (model == null) return;
        var render = api.Render; var previous = render.CurrentActiveShader; previous?.Stop();
        try
        {
            var camera = api.World.Player.Entity.CameraPos;
            var shader = render.PreparedStandardShader(maps.Pos.X, maps.Pos.InternalY, maps.Pos.Z);
            shader.NormalShaded = 0; shader.AlphaTest = .005f;
            shader.ProjectionMatrix = render.CurrentProjectionMatrix; shader.ViewMatrix = render.CameraMatrixOriginf;
            shader.ModelMatrix = new Matrixf().Translate(maps.Pos.X - camera.X, maps.Pos.InternalY - camera.Y, maps.Pos.Z - camera.Z).Values;
            render.GlDisableCullFace(); render.GlToggleBlend(true); render.RenderMultiTextureMesh(model, "tex"); shader.Stop();
            bool held = api.World.Player.Entity.Controls.CtrlKey;
            if (!held) selected = -1;
            else if ((!ctrl || selected < 0) && Hit() is { } hit) selected = hit.Index;
            ctrl = held;
            if (ctrl && selected >= 0 && view != null)
            {
                var n = view.Surface!.Outward * .008;
                foreach (var (a, b) in view.Surface.Outline(selected, maps.IsAtlas)) render.RenderLine(maps.Pos,
                    (float)(a.X + n.X), (float)(a.Y + n.Y), (float)(a.Z + n.Z), (float)(b.X + n.X), (float)(b.Y + n.Y), (float)(b.Z + n.Z), ColorUtil.ToRgba(255, 255, 215, 80));
            }
        }
        finally { render.GlEnableCullFace(); previous?.Use(); }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        active.Remove(this);
        api.Event.MouseDown -= MouseDown; api.Event.UnregisterRenderer(this, EnumRenderStage.Opaque); Invalidate();
    }
}
