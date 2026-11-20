using Cairo;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace SeraphCivilizationSimulation;

internal sealed class GuiDialogMapDrawing : GuiDialog
{
    private readonly ItemStack stack;
    private readonly int slot;
    private readonly string fingerprint;
    private readonly RecordedPaperMap map;
    private readonly MapDrawingProjection projection;
    private MapDrawingDocument document;
    private readonly Stack<byte[]> history = new();
    private List<MapDrawingFaces.Point> stroke = [];
    private List<List<MapDrawingFaces.Point>> faces = [];
    private bool drawing, disposed;
    private readonly bool isLeader;
    private MapDrawingCanvas canvas = null!;
    private GuiDialogMapArea? areaDialog;
    private GuiDialogMapAreaList? areaList;
    private GuiDialogMapAreaNotes? notesDialog;
    private string? pending;
    private long sentAt;
    public override string ToggleKeyCombinationCode => null!;
    public override bool UnregisterOnClose => true;
    private string L(string key) => Lang.Get("vsscsmod:ui." + key);

    internal GuiDialogMapDrawing(ICoreClientAPI api, ItemStack held, int hotbarSlot, bool isLeader) : base(api)
    {
        this.isLeader = isLeader; stack = held; slot = hotbarSlot;
        fingerprint = ImmersiveMapCompatibility.Fingerprint(held);
        map = RecordedPaperMap.Describe(held) ?? throw new InvalidDataException("Map position is unavailable.");
        projection = new(map); document = MapDrawingDocument.Read(held, map); faces = projection.Faces(document);
        Compose(); TryOpen();
    }
    private void Compose()
    {
        double scale = Math.Max(.25, RuntimeEnv.GUIScale);
        double side = Math.Max(180, Math.Min(560, Math.Min(capi.Render.FrameWidth / scale - 80, capi.Render.FrameHeight / scale - 210)));
        double width = Math.Max(650, side + 32), height = side + 155;
        canvas = new(capi, ElementBounds.Fixed((width - side) / 2, 65, side, side), stack, projection,
            ctx => MapDrawingRendering.Draw(ctx, document, projection, canvas.Bounds.InnerWidth, canvas.Bounds.InnerHeight, stroke, faces));
        SingleComposer = capi.Gui.CreateCompo("vsscs-map-drawing", ElementBounds.Fixed(0, 0, width, height).WithAlignment(EnumDialogArea.CenterMiddle))
            .AddShadedDialogBG(ElementBounds.Fill)
            .AddDialogTitleBar(L("map-draw-title"), () => TryClose())
            .AddStaticText(L("map-draw-help"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(16, 33, width - 32, 30))
            .AddInteractiveElement(canvas, "canvas")
            .AddSmallButton(L("map-draw-undo"), Undo, ElementBounds.Fixed(16, height - 75, 100, 30), key: "undo")
            .AddSmallButton(L("map-draw-list"), OpenAreaList, ElementBounds.Fixed(125, height - 75, 155, 30))
            .AddSmallButton(L("map-draw-save"), Save, ElementBounds.Fixed(width - 220, height - 75, 95, 30), key: "save")
            .AddSmallButton(L("map-draw-cancel"), () => { TryClose(); return true; }, ElementBounds.Fixed(width - 115, height - 75, 99, 30))
            .AddDynamicText("", CairoFont.WhiteSmallText(), ElementBounds.Fixed(16, height - 36, width - 32, 30), "status")
            .Compose();
    }
    private void Status(string key) => SingleComposer.GetDynamicText("status").SetNewText(L(key));
    private MapDrawingFaces.Point Point(MouseEvent e)
    {
        var b = canvas.Bounds;
        return new(Math.Clamp((int)((e.X - b.renderX) / b.InnerWidth * projection.Size), 0, projection.Size - 1),
            Math.Clamp((int)((e.Y - b.renderY) / b.InnerHeight * projection.Size), 0, projection.Size - 1));
    }
    // Snap to any segment, not just sampled endpoints, so existing edges can close a new region.
    private MapDrawingFaces.Point Snap(MapDrawingFaces.Point p, bool close)
    {
        double best = Math.Pow(7 * projection.Size / Math.Max(1, canvas.Bounds.InnerWidth), 2);
        var answer = p;
        void Consider(double x, double z)
        {
            double d = (x - p.X) * (x - p.X) + (z - p.Z) * (z - p.Z);
            if (d > best) return; best = d; answer = new((int)Math.Round(x), (int)Math.Round(z));
        }
        foreach (var line in document.Lines)
        {
            var cells = line.Points.Select(q => projection.Cell(q.X, q.Z)).ToArray();
            for (int i = 1; i < cells.Length; i++)
            {
                var a = cells[i - 1]; var b = cells[i]; double dx = b.X - a.X, dz = b.Z - a.Z, len = dx * dx + dz * dz;
                double t = len == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Z - a.Z) * dz) / len, 0, 1);
                Consider(a.X + t * dx, a.Z + t * dz);
            }
        }
        if (close && stroke.Count > 3) Consider(stroke[0].X, stroke[0].Z);
        return answer;
    }
    public override bool ShouldReceiveMouseEvents() => IsOpened() && !capi.Input.MouseGrabbed;
    public override void OnRenderGUI(float dt)
    {
        if (pending != null && capi.World.ElapsedMilliseconds - sentAt > 15000)
        { pending = null; SingleComposer.GetButton("save").Enabled = true; Status("map-edit-timeout"); }
        base.OnRenderGUI(dt);
    }
    public override void OnMouseDown(MouseEvent e)
    {
        if (areaDialog?.IsOpened() == true || areaList?.IsOpened() == true || notesDialog?.IsOpened() == true) return;
        if (pending != null && capi.World.ElapsedMilliseconds - sentAt > 15000)
        { pending = null; SingleComposer.GetButton("save").Enabled = true; Status("map-edit-timeout"); }
        if (pending != null) { e.Handled = true; return; }
        if (e.Button != EnumMouseButton.Left || !canvas.Bounds.PointInside(e.X, e.Y)) { base.OnMouseDown(e); return; }
        drawing = true; stroke = [Snap(Point(e), false)]; e.Handled = true;
    }
    public override void OnMouseMove(MouseEvent e)
    {
        if (!drawing) { base.OnMouseMove(e); return; }
        var p = Point(e);
        if (stroke.Count < MapDrawingDocument.MaxPoints && p != stroke[^1]) stroke.Add(p);
        canvas.Dirty = true; e.Handled = true;
    }
    public override void OnMouseUp(MouseEvent e)
    {
        if (!drawing || e.Button != EnumMouseButton.Left) { base.OnMouseUp(e); return; }
        drawing = false; e.Handled = true;
        if (stroke.Count <= 2 && stroke.All(p => Math.Abs(p.X - stroke[0].X) + Math.Abs(p.Z - stroke[0].Z) < 3))
        {
            var p = Point(e); stroke = []; canvas.Dirty = true;
            var face = faces.FirstOrDefault(f => f.Contains(p)); if (face != null) NameFace(face);
            return;
        }
        stroke.Add(Snap(Point(e), true));
        var points = Simplify(stroke); stroke = [];
        byte[] before = document.Encode(map);
        try
        {
            var existing = document.Lines.Select(line => (IReadOnlyList<MapDrawingFaces.Point>)line.Points.Select(p => projection.Cell(p.X, p.Z)).ToArray()).ToArray();
            foreach (var line in MapDrawingFaces.ClosedStroke(projection.Size, projection.Size, existing, points))
                document.Lines.Add(new() { Points = Simplify(line).Select(projection.World).ToList() });
        }
        catch (InvalidDataException)
        { document = MapDrawingDocument.Decode(before, map); canvas.Dirty = true; Status("map-draw-invalid"); return; }
        try
        {
            document.Encode(map); var newFaces = projection.Faces(document);
            if (newFaces.Count <= faces.Count) throw new InvalidDataException("The stroke must close a new area.");
            faces = newFaces;
            if (faces.Count > MapDrawingDocument.MaxAreas) throw new InvalidDataException("Too many enclosed areas.");
            // Splitting a face keeps its old name on the part containing the old seed.
            // If a new stroke crosses that seed, retain the name on the nearest remaining face.
            var used = new HashSet<List<MapDrawingFaces.Point>>();
            foreach (var area in document.Areas.ToArray())
            {
                var seed = projection.Cell(area.Seed.X, area.Seed.Z);
                var face = faces.FirstOrDefault(f => !used.Contains(f) && f.Contains(seed))
                    ?? faces.Where(f => !used.Contains(f)).OrderBy(f => f.Min(p => Math.Abs(p.X - seed.X) + Math.Abs(p.Z - seed.Z))).FirstOrDefault();
                if (face == null) { document.Areas.Remove(area); continue; }
                area.Seed = projection.World(MapDrawingRendering.Center(face)); used.Add(face);
            }
            history.Push(before); canvas.Dirty = true; NameNext();
        }
        catch (InvalidDataException)
        { document = MapDrawingDocument.Decode(before, map); faces = projection.Faces(document); canvas.Dirty = true; Status("map-draw-limit"); }
    }
    internal static List<MapDrawingFaces.Point> Simplify(IReadOnlyList<MapDrawingFaces.Point> points)
    {
        var result = new List<MapDrawingFaces.Point>();
        foreach (var p in points)
        {
            if (result.Count > 0 && result[^1] == p) continue;
            if (result.Count > 1)
            {
                var a = result[^2]; var b = result[^1];
                if ((b.X - a.X) * (p.Z - b.Z) == (b.Z - a.Z) * (p.X - b.X)
                    && (b.X - a.X) * (p.X - b.X) + (b.Z - a.Z) * (p.Z - b.Z) >= 0) result.RemoveAt(result.Count - 1);
            }
            result.Add(p);
        }
        return result;
    }
    private bool Undo()
    {
        if (pending != null || history.Count == 0) return false;
        document = MapDrawingDocument.Decode(history.Pop(), map); faces = projection.Faces(document); canvas.Dirty = true; return true;
    }
    private void NameNext()
    {
        if (!IsOpened() || disposed || areaDialog?.IsOpened() == true) return;
        var used = document.Areas.Select(a => projection.Cell(a.Seed.X, a.Seed.Z)).ToHashSet();
        var face = faces.FirstOrDefault(f => !f.Any(used.Contains));
        if (face != null) NameFace(face);
    }
    private void NameFace(List<MapDrawingFaces.Point> face)
    {
        var area = document.Areas.FirstOrDefault(a => face.Contains(projection.Cell(a.Seed.X, a.Seed.Z)));
        areaDialog?.Dispose();
        areaDialog = new(capi, area?.Name ?? "", area?.Color ?? unchecked((int)0xffd8a44a), isLeader, document.TerritorySelected, (name, color, territory) =>
        {
            history.Push(document.Encode(map));
            if (area == null) document.Areas.Add(new() { Name = name, Color = color, Seed = projection.World(MapDrawingRendering.Center(face)) });
            else { area.Name = name; area.Color = color; }
            if (isLeader) document.TerritorySelected = territory;
            canvas.Dirty = true;
            capi.Event.EnqueueMainThreadTask(NameNext, "map-name-next");
        });
    }
    internal void OpenAreaAction(int index, string action)
    {
        if (index < 0 || index >= document.Areas.Count) return;
        var area = document.Areas[index];
        if (action == "delete") { DeleteArea(area); return; }
        if (action == "edit")
        {
            var face = faces.FirstOrDefault(f => f.Contains(projection.Cell(area.Seed.X, area.Seed.Z)));
            if (face != null) NameFace(face);
            return;
        }
        if (action == "notes")
        {
            notesDialog?.TryClose(); notesDialog?.Dispose();
            notesDialog = new(capi, area.Notes, value => { history.Push(document.Encode(map)); area.Notes = value; canvas.Dirty = true; });
        }
    }
    private bool OpenAreaList()
    {
        if (pending != null) return false;
        areaList?.TryClose(); areaList?.Dispose();
        areaList = new(capi, document.Areas, area =>
        {
            var face = faces.FirstOrDefault(f => f.Contains(projection.Cell(area.Seed.X, area.Seed.Z)));
            if (face != null) NameFace(face);
        }, DeleteArea);
        return true;
    }

    private void DeleteArea(MapDrawingArea area)
    {
        byte[] before = document.Encode(map);
        try
        {
            var target = faces.First(f => f.Contains(projection.Cell(area.Seed.X, area.Seed.Z))).ToHashSet();
            var others = faces.Where(f => !f.Any(target.Contains)).SelectMany(f => f).ToHashSet();
            bool Near(HashSet<MapDrawingFaces.Point> face, MapDrawingFaces.Point p)
            {
                for (int z = -1; z <= 1; z++) for (int x = -1; x <= 1; x++)
                    if (face.Contains(new(p.X + x, p.Z + z))) return true;
                return false;
            }
            var rebuilt = new List<MapDrawingLine>();
            foreach (var line in document.Lines)
            {
                var cells = line.Points.Select(p => projection.Cell(p.X, p.Z)).ToArray();
                var run = new List<MapDrawingFaces.Point>();
                void Flush()
                {
                    if (run.Count > 0)
                    {
                        var simplified = Simplify(run);
                        if (simplified.Count == 1) simplified.Add(simplified[0]);
                        rebuilt.Add(new() { Points = simplified.Select(projection.World).ToList() });
                    }
                    run = [];
                }
                for (int i = 1; i < cells.Length; i++)
                {
                    var a = cells[i - 1]; var b = cells[i]; int x = a.X, z = a.Z, dx = Math.Abs(b.X - x), dz = Math.Abs(b.Z - z);
                    int sx = x < b.X ? 1 : -1, sz = z < b.Z ? 1 : -1, error = dx - dz;
                    while (true)
                    {
                        var point = new MapDrawingFaces.Point(x, z);
                        if (Near(target, point) && !Near(others, point)) Flush();
                        else if (run.Count == 0 || run[^1] != point) run.Add(point);
                        if (x == b.X && z == b.Z) break;
                        int twice = 2 * error;
                        if (twice > -dz) { error -= dz; x += sx; }
                        if (twice < dx) { error += dx; z += sz; }
                    }
                }
                Flush();
            }
            document.Lines = rebuilt; document.Areas.Remove(area);
            document.Encode(map); projection.ValidateAreas(document);
            var remaining = projection.Faces(document);
            if (remaining.Any(f => f.Any(target.Contains))) throw new InvalidDataException("Area boundary cannot be removed without affecting adjacent areas.");
            history.Push(before); faces = remaining; canvas.Dirty = true;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException)
        { document = MapDrawingDocument.Decode(before, map); faces = projection.Faces(document); Status("map-draw-delete-failed"); }
        OpenAreaList();
    }
    private bool Save()
    {
        if (pending != null) return false;
        try
        {
            projection.ValidateAreas(document);
            byte[] bytes = document.Encode(map);
            pending = Guid.NewGuid().ToString("N"); sentAt = capi.World.ElapsedMilliseconds;
            capi.Network.GetChannel(VscnCivilizationModSystem.ChannelName).SendPacket(new MapDrawingSaveRequest
            { RequestId = pending, HotbarSlot = slot, MapFingerprint = fingerprint, Document = bytes, SetTerritory = isLeader && document.TerritorySelected });
            SingleComposer.GetButton("save").Enabled = false; Status("map-edit-saving"); return true;
        }
        catch (InvalidDataException) { Status("map-draw-invalid"); return false; }
    }
    internal void Receive(MapEditResult result)
    {
        if (disposed || result.RequestId != pending) return;
        pending = null; SingleComposer.GetButton("save").Enabled = true;
        if (result.Success) { capi.ShowChatMessage(L(result.MessageKey)); TryClose(); }
        else Status(result.MessageKey);
    }
    public override bool TryClose() { areaDialog?.TryClose(); areaList?.TryClose(); notesDialog?.TryClose(); return base.TryClose(); }
    public override void Dispose() { if (disposed) return; TryClose(); disposed = true; areaDialog?.Dispose(); areaDialog = null; areaList?.Dispose(); areaList = null; notesDialog?.Dispose(); notesDialog = null; base.Dispose(); }
}

internal sealed class GuiDialogMapAreaNotes : GuiDialog
{
    public override string ToggleKeyCombinationCode => null!;
    public override bool UnregisterOnClose => true;
    internal GuiDialogMapAreaNotes(ICoreClientAPI api, string notes, Action<string> save) : base(api)
    {
        var c = api.Gui.CreateCompo("vsscs-map-area-notes", ElementBounds.Fixed(0, 0, 450, 300).WithAlignment(EnumDialogArea.CenterMiddle))
            .AddShadedDialogBG(ElementBounds.Fill).AddDialogTitleBar(Lang.Get("vsscsmod:ui.map-draw-notes"), () => TryClose())
            .AddTextArea(ElementBounds.Fixed(20, 48, 410, 190), _ => { }, CairoFont.TextInput(), "notes")
            .AddSmallButton(Lang.Get("vsscsmod:ui.map-draw-cancel"), () => { TryClose(); return true; }, ElementBounds.Fixed(20, 255, 100, 30))
            .AddSmallButton(Lang.Get("vsscsmod:ui.map-draw-save"), () => { save(SingleComposer.GetTextArea("notes").GetText()); TryClose(); return true; }, ElementBounds.Fixed(330, 255, 100, 30));
        SingleComposer = c.Compose(); SingleComposer.GetTextArea("notes").SetMaxLength(500); SingleComposer.GetTextArea("notes").SetValue(notes); TryOpen();
    }
}

internal sealed class GuiDialogMapAreaList : GuiDialog
{
    private readonly IReadOnlyList<MapDrawingArea> areas;
    private readonly Action<MapDrawingArea> edit, delete;
    private int page;
    public override string ToggleKeyCombinationCode => null!;
    public override bool UnregisterOnClose => true;
    private string L(string key) => Lang.Get("vsscsmod:ui." + key);
    internal GuiDialogMapAreaList(ICoreClientAPI api, IReadOnlyList<MapDrawingArea> areas, Action<MapDrawingArea> edit, Action<MapDrawingArea> delete) : base(api)
    { this.areas = areas; this.edit = edit; this.delete = delete; Compose(); TryOpen(); }
    private void Compose()
    {
        var old = SingleComposer;
        var c = capi.Gui.CreateCompo("vsscs-map-area-list", ElementBounds.Fixed(0, 0, 500, 370).WithAlignment(EnumDialogArea.CenterMiddle))
            .AddShadedDialogBG(ElementBounds.Fill).AddDialogTitleBar(L("map-draw-list"), () => TryClose());
        if (areas.Count == 0) c.AddStaticText(L("map-draw-list-empty"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(20, 52, 440, 30));
        foreach (var (area, i) in areas.Skip(page * 6).Take(6).Select((a, i) => (a, i)))
        {
            double y = 48 + i * 43;
            var icons = GroundMapCompatibility.Property(capi.ModLoader.GetModSystem("ImmersiveMaps.Rendering.MapRenderSystem"), "Icons")!;
            var rgba = ColorUtil.ToRGBADoubles(area.Color);
            c.AddStaticElement(new GuiElementCustomDraw(capi, ElementBounds.Fixed(20, y, 30, 30), (ctx, _, bounds) =>
                GroundMapCompatibility.Call(icons, "Draw", capi, ctx, "home", bounds.drawX + 2, bounds.drawY + 2,
                    bounds.InnerWidth - 4, bounds.InnerHeight - 4, rgba), false));
            c.AddStaticText(area.Name, CairoFont.WhiteSmallText(), ElementBounds.Fixed(55, y + 4, 245, 30));
            c.AddSmallButton(L("map-draw-edit"), () => { TryClose(); edit(area); return true; }, ElementBounds.Fixed(305, y, 80, 30));
            c.AddSmallButton(L("map-draw-delete"), () => { TryClose(); delete(area); return true; }, ElementBounds.Fixed(395, y, 80, 30));
        }
        c.AddSmallButton(L("map-draw-prev"), () => { if (page > 0) { page--; Compose(); } return true; }, ElementBounds.Fixed(20, 325, 100, 30));
        c.AddStaticText($"{page + 1}/{Math.Max(1, (areas.Count + 5) / 6)}", CairoFont.WhiteSmallText(), ElementBounds.Fixed(220, 330, 80, 25));
        c.AddSmallButton(L("map-draw-next"), () => { if ((page + 1) * 6 < areas.Count) { page++; Compose(); } return true; }, ElementBounds.Fixed(380, 325, 100, 30));
        SingleComposer = c.Compose(); old?.Dispose();
    }
}

internal sealed class GuiDialogMapArea : GuiDialog
{
    public override string ToggleKeyCombinationCode => null!;
    public override bool UnregisterOnClose => true;
    internal GuiDialogMapArea(ICoreClientAPI api, string name, int color, bool leader, bool territory, Action<string, int, bool> save) : base(api)
    {
        string L(string key) => Lang.Get("vsscsmod:ui." + key);
        int[] palette = ((IEnumerable<int>?)AccessTools.Method(AccessTools.TypeByName("ImmersiveMaps.Waypoints.WaypointPalette"), "Colors")?.Invoke(null, [api.World]))?.ToArray() ?? [unchecked((int)0xffd8a44a)];
        int selected = color;
        // Native picker wraps after nine 30px swatches (35px step, maxLineWidth 285).
        int rows = Math.Max(1, (palette.Length + 8) / 9);
        double checkboxY = 125 + rows * 35 + 15;
        double footer = checkboxY + (leader ? 45 : 0);
        double dialogHeight = footer + 55;
        var c = api.Gui.CreateCompo("vsscs-map-area", ElementBounds.Fixed(0, 0, 450, dialogHeight).WithAlignment(EnumDialogArea.CenterMiddle))
            .AddShadedDialogBG(ElementBounds.Fill).AddDialogTitleBar(L("map-draw-name-face"), () => TryClose())
            .AddStaticText(L("map-draw-name"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(16, 45, 90, 30))
            .AddTextInput(ElementBounds.Fixed(110, 45, 290, 30), _ => { }, CairoFont.TextInput(), "name")
            .AddStaticText(L("map-draw-color"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(16, 85, 90, 30))
            .AddTextInput(ElementBounds.Fixed(110, 85, 140, 30), _ => { }, CairoFont.TextInput(), "color")
            .AddColorListPicker(palette, i => { selected = palette[i]; SingleComposer.GetTextInput("color").SetValue(((uint)selected & 0xffffff).ToString("X6")); }, ElementBounds.Fixed(110, 125, 30, 30), 285, "palette");
        if (leader)
        {
            c.AddInteractiveElement(new GuiElementSwitch(api, value => territory = value, ElementBounds.Fixed(16, checkboxY, 24, 24), 24) { On = territory });
            c.AddStaticText(L("map-draw-territory"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(50, checkboxY, 375, 30));
        }
        c.AddSmallButton(L("map-draw-cancel"), () => { TryClose(); return true; }, ElementBounds.Fixed(16, footer, 100, 30))
            .AddSmallButton(L("map-draw-save"), () =>
            {
                string text = SingleComposer.GetTextInput("name").GetText().Trim();
                if (text.Length is < 1 or > 64 || text.Any(char.IsControl)
                    || !uint.TryParse(SingleComposer.GetTextInput("color").GetText().Trim().TrimStart('#'), System.Globalization.NumberStyles.HexNumber, null, out uint rgb) || rgb > 0xffffff)
                { api.ShowChatMessage(L("map-draw-invalid")); return false; }
                TryClose(); save(text, unchecked((int)(0xff000000 | rgb)), territory); return true;
            }, ElementBounds.Fixed(300, footer, 100, 30));
        SingleComposer = c.Compose(); SingleComposer.GetTextInput("name").SetMaxLength(64); SingleComposer.GetTextInput("name").SetValue(name);
        SingleComposer.GetTextInput("color").SetValue(((uint)color & 0xffffff).ToString("X6")); TryOpen();
    }
}

internal sealed class MapDrawingCanvas : GuiElement
{
    private readonly object? native;
    private readonly ItemStack paper;
    private readonly RecordedPaperMap map;
    private readonly Action<Context> draw;
    private object? state;
    private LoadedTexture? overlay;
    private MeshRef? terrain;
    private float[] uv = [];
    private long lastDraw;
    private bool disposed;
    internal bool Dirty = true;
    internal MapDrawingCanvas(ICoreClientAPI api, ElementBounds bounds, ItemStack stack, MapDrawingProjection projection, Action<Context> draw) : base(api, bounds)
    {
        this.draw = draw; paper = stack.Clone(); paper.TempAttributes.RemoveAttribute("immersivemaps-renderid");
        paper.Attributes.RemoveAttribute(MapDrawingDocument.Attribute); map = RecordedPaperMap.Describe(paper)!;
        native = api.ModLoader.GetModSystem("ImmersiveMaps.Rendering.MapRenderSystem");
    }
    public override void ComposeElements(Context ctx, ImageSurface surface)
    { Bounds.CalcWorldBounds(); ctx.SetSourceRGB(.18, .17, .13); ctx.Rectangle(Bounds.drawX, Bounds.drawY, Bounds.InnerWidth, Bounds.InnerHeight); ctx.Fill(); }
    public override void RenderInteractiveElements(float deltaTime)
    {
        if (disposed) return;
        if (native != null)
        {
            state = GroundMapCompatibility.Call(native, "GetOrCreateState", paper.Item, paper);
            AccessTools.Property(state!.GetType(), "LastUsedMs").SetValue(state, api.World.ElapsedMilliseconds);
            GroundMapCompatibility.Call(state, "Request", map.CenterX, map.CenterZ, map.Rotation, paper.Attributes.GetBytes("waypoints"), 0f, false);
            if (GroundMapCompatibility.Property(state, "Model") != null)
            {
                var mesh = (MeshData)GroundMapCompatibility.Property(state, "TerrainMesh")!;
                if (!uv.SequenceEqual(mesh.Uv))
                {
                    uv = mesh.Uv.ToArray(); var quad = QuadMeshUtil.GetQuad(); quad.Uv = uv;
                    terrain?.Dispose(); terrain = api.Render.UploadMesh(quad);
                }
                var tex = (LoadedTexture)GroundMapCompatibility.Property(state, "TerrainTexture")!;
                if (terrain != null) api.Render.Render2DTexture(terrain, tex.TextureId, (float)Bounds.renderX, (float)Bounds.renderY, (float)Bounds.InnerWidth, (float)Bounds.InnerHeight, 50);
                var marks = (LoadedTexture)GroundMapCompatibility.Property(state, "WaypointTexture")!;
                api.Render.Render2DTexture(marks.TextureId, Bounds, 51);
            }
        }
        if (Dirty && (overlay == null || api.World.ElapsedMilliseconds - lastDraw >= 33))
        {
            using var surface = new ImageSurface(Format.Argb32, Math.Max(1, (int)Bounds.InnerWidth), Math.Max(1, (int)Bounds.InnerHeight));
            using (var ctx = new Context(surface)) draw(ctx);
            overlay ??= new LoadedTexture(api);
            api.Gui.LoadOrUpdateCairoTexture(surface, false, ref overlay); Dirty = false; lastDraw = api.World.ElapsedMilliseconds;
        }
        if (overlay != null) api.Render.Render2DTexturePremultipliedAlpha(overlay.TextureId, Bounds, 52);
    }
    public override void Dispose() { if (disposed) return; disposed = true; overlay?.Dispose(); overlay = null; terrain?.Dispose(); terrain = null; base.Dispose(); }
}

internal sealed class GuiDialogMapSettings : GuiDialog
{
    internal sealed class Settings
    {
        public double IconScale { get; set; } = 1;
        public double FontScale { get; set; } = 1;
    }
    internal static Settings Current { get; private set; } = new();
    private const string ConfigFile = "vsscs-map-client.json";
    internal static void Load(ICoreClientAPI api)
    {
        try { Current = api.LoadModConfig<Settings>(ConfigFile) ?? new(); } catch { Current = new(); }
        Current.IconScale = double.IsFinite(Current.IconScale) ? Math.Clamp(Current.IconScale, .5, 3) : 1;
        Current.FontScale = double.IsFinite(Current.FontScale) ? Math.Clamp(Current.FontScale, .5, 3) : 1;
    }
    public override string ToggleKeyCombinationCode => null!;
    public override bool UnregisterOnClose => true;
    private readonly string? marker;
    private string? pending;
    private long sentAt;
    public override void OnRenderGUI(float dt)
    {
        if (pending != null && capi.World.ElapsedMilliseconds - sentAt > 15000)
        {
            pending = null; SingleComposer.GetButton("save").Enabled = true;
            capi.ShowChatMessage(Lang.Get("vsscsmod:ui.map-edit-timeout"));
        }
        base.OnRenderGUI(dt);
    }
    internal void Receive(MapEditResult result)
    {
        if (result.RequestId != pending) return;
        pending = null; SingleComposer.GetButton("save").Enabled = true;
        capi.ShowChatMessage(Lang.Get("vsscsmod:ui." + result.MessageKey)); if (result.Success) TryClose();
    }
    public GuiDialogMapSettings(ICoreClientAPI api, ItemStack? stack = null, int slot = -1, string? markerId = null) : base(api)
    {
        marker = markerId;
        string L(string key) => Lang.Get("vsscsmod:ui." + key);
        string fingerprint = stack == null ? "" : ImmersiveMapCompatibility.Fingerprint(stack);
        var c = api.Gui.CreateCompo("vsscs-map-settings", ElementBounds.Fixed(0, 0, 390, 220).WithAlignment(EnumDialogArea.CenterMiddle))
            .AddShadedDialogBG(ElementBounds.Fill).AddDialogTitleBar(L(marker == null ? "map-settings" : "map-marker-scale"), () => TryClose())
            .AddStaticText(L("map-scale"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(15, 48, 200, 30))
            .AddTextInput(ElementBounds.Fixed(235, 48, 130, 30), _ => { }, CairoFont.TextInput(), "scale");
        if (marker == null) c.AddStaticText(L("map-font-scale"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(15, 88, 200, 30))
            .AddTextInput(ElementBounds.Fixed(235, 88, 130, 30), _ => { }, CairoFont.TextInput(), "font");
        c.AddStaticText(L("map-scale-help"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(15, 125, 360, 30))
            .AddSmallButton(L("map-draw-cancel"), () => { TryClose(); return true; }, ElementBounds.Fixed(15, 170, 100, 30))
            .AddSmallButton(L("map-draw-save"), () =>
            {
                if (pending != null) return false;
                if (!double.TryParse(SingleComposer.GetTextInput("scale").GetText(), out var icon) || !double.IsFinite(icon) || icon is < .5 or > 3)
                { api.ShowChatMessage(L("map-scale-help")); return false; }
                if (marker != null)
                {
                    pending = Guid.NewGuid().ToString("N"); sentAt = api.World.ElapsedMilliseconds;
                    api.Network.GetChannel(VscnCivilizationModSystem.ChannelName).SendPacket(new MapMarkerScaleRequest
                    { HotbarSlot = slot, MapFingerprint = fingerprint, MarkerId = marker, Scale = icon, RequestId = pending });
                    SingleComposer.GetButton("save").Enabled = false; return true;
                }
                if (!double.TryParse(SingleComposer.GetTextInput("font").GetText(), out var font) || !double.IsFinite(font) || font is < .5 or > 3)
                { api.ShowChatMessage(L("map-scale-help")); return false; }
                Current = new() { IconScale = icon, FontScale = font }; api.StoreModConfig(Current, ConfigFile); TryClose(); return true;
            }, ElementBounds.Fixed(270, 170, 100, 30), key: "save");
        SingleComposer = c.Compose();
        SingleComposer.GetTextInput("scale").SetValue((marker == null ? Current.IconScale : MapMarkerScales.Read(stack!).GetValueOrDefault(marker, 1)).ToString("0.##"));
        if (marker == null) SingleComposer.GetTextInput("font").SetValue(Current.FontScale.ToString("0.##")); TryOpen();
    }
}
