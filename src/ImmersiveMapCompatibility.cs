using System.Collections;
using System.Reflection;
using HarmonyLib;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace SeraphCivilizationSimulation;

[ProtoContract]
public sealed class MapMarkerClipboardRequest
{
    [ProtoMember(1)] public string Action { get; set; } = "";
    [ProtoMember(2)] public int HotbarSlot { get; set; }
    [ProtoMember(3)] public string MapFingerprint { get; set; } = "";
}

// Optional integration with Immersive Maps 1.2.1; its assembly is never packaged here.
internal sealed partial class ImmersiveMapCompatibility : IDisposable
{
    internal static Action<object>? OpenDrawingDialog;
    internal static Action<object, int, string>? OpenAreaAction;
    internal static Action<object>? OpenSettingsDialog;
    internal static Action<object, ItemStack, int, string>? OpenMarkerScaleDialog;
    private const string PatchId = "vsscsmod.immersivemaps";
    private static readonly object Sync = new();
    private static int users;
    private bool disposed;
    internal static Type MapType => Type("Items.ItemMap");
    private static Type Type(string name) => AccessTools.TypeByName("ImmersiveMaps." + name)
        ?? throw new TypeLoadException("ImmersiveMaps." + name);
    private static object? Field(object owner, string name) => AccessTools.Field(owner.GetType(), name).GetValue(owner);
    private static object? Call(object owner, string name, params object?[] args)
        => AccessTools.Method(owner.GetType(), name).Invoke(owner, args);
    private static object? Static(string type, string name, params object?[] args)
        => AccessTools.Method(Type(type), name).Invoke(null, args);
    internal static bool Locked(ItemStack stack) => !string.IsNullOrEmpty(stack.Attributes.GetString("mapLockedBy"));
    internal static bool CanDraw(ItemStack map, ItemSlot? offhand) => Call(map.Item, "CanDraw", offhand) is true;
    internal static bool TryGetDrawingMap(object dialog, out ItemStack? stack, out int slot)
    {
        stack = null; slot = -1;
        var api = Client(dialog); var manager = api.World.Player.InventoryManager;
        if (Field(dialog, "_hotbarSlot") is not int selected || manager.ActiveHotbarSlotNumber != selected
            || manager.ActiveHotbarSlot?.Itemstack is not { } held || !RecordedPaperMap.IsPaperMap(held)
            || GroundMapData.IsAtlas(held) || Locked(held) || !CanDraw(held, manager.OffhandHotbarSlot)) return false;
        stack = held; slot = selected; return true;
    }
    internal static string Fingerprint(ItemStack stack)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true)) stack.Attributes.ToBytes(writer);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.ToArray()));
    }
    internal static IDictionary Read(ItemStack stack) => (IDictionary)Static("Waypoints.MapWaypoints", "Read", stack)!;
    internal static IDictionary Deserialize(byte[] data) => (IDictionary)Static("Waypoints.MapWaypoints", "Deserialize", data)!;
    internal static void Write(ItemStack stack, IDictionary markers) => Static("Waypoints.MapWaypoints", "Write", stack, markers);

    public ImmersiveMapCompatibility()
    {
        lock (Sync)
        {
            if (users == 0)
            {
                var harmony = new Harmony(PatchId);
                try
                {
                    void Patch(string type, string method, string? prefix = null, string? postfix = null)
                        => harmony.Patch(AccessTools.Method(Type(type), method) ?? throw new MissingMethodException(type, method),
                            prefix == null ? null : new HarmonyMethod(typeof(ImmersiveMapCompatibility), prefix),
                            postfix == null ? null : new HarmonyMethod(typeof(ImmersiveMapCompatibility), postfix));
                    Patch("Gui.GuiDialogMapWaypoints", "Compose", nameof(Compose));
                    Patch("Gui.GuiDialogMapWaypoints", "AddRows", nameof(AddRows));
                    Patch("Gui.GuiDialogMapWaypoints", "UpdateDistances", nameof(Disabled));
                    Patch("Gui.GuiDialogEditWaypoint", "Compose", nameof(ComposeMarkerEditor));
                    Patch("Network.MapRequestHandler", "OnPinWaypoint", nameof(PinWaypoint));
                    // The old offhand transfer never checked the source lock. Use the server clipboard instead.
                    Patch("Network.MapRequestHandler", "OnCopyWaypoints", nameof(Disabled));
                    Patch("Waypoints.MapWaypoints", "Write", nameof(BeforeWrite));
                    Patch("Rendering.MapRenderState", "SetWaypointData", postfix: nameof(RenderWaypoints));
                    GroundMapCompatibility.Install(harmony);
                    MapDrawingRendering.Install(harmony);
                }
                catch { harmony.UnpatchAll(PatchId); throw; }
            }
            users++;
        }
    }

    private static bool Disabled() => false;
    private static void ClearPins(IDictionary markers)
    {
        foreach (var marker in markers.Values)
            AccessTools.Property(marker!.GetType(), "Pinned").SetValue(marker, false);
    }
    private static bool PinWaypoint(IServerPlayer player, object packet) => GroundMapCompatibility.Pin(player, packet);
    private static void BeforeWrite(ItemStack stack, object waypoints)
    {
        if (!GroundMapData.IsAtlas(stack)) { ClearPins((IDictionary)waypoints); return; }
        foreach (var marker in ((IDictionary)waypoints).Values)
            if (!GroundMapData.Contains(stack, Convert.ToDouble(GroundMapCompatibility.Property(marker!, "X")), Convert.ToDouble(GroundMapCompatibility.Property(marker!, "Z"))))
                AccessTools.Property(marker!.GetType(), "Pinned").SetValue(marker, false);
    }
    private static void RenderWaypoints(object __instance)
        => ClearPins((IDictionary)AccessTools.Property(__instance.GetType(), "Waypoints").GetValue(__instance)!);

    private static string L(string key, params object[] args) => Lang.Get("vsscsmod:ui." + key, args);
    private static ICoreClientAPI Client(object dialog) => (ICoreClientAPI)Field(dialog, "capi")!;
    private static bool Request(object dialog, string action)
    {
        var api = Client(dialog);
        var manager = api.World.Player.InventoryManager;
        int hotbarSlot = (int)Field(dialog, "_hotbarSlot")!;
        if (manager.ActiveHotbarSlotNumber != hotbarSlot || manager.ActiveHotbarSlot?.Itemstack is not { } stack
            || !MapType.IsInstanceOfType(stack.Item) || Locked(stack) || !CanDraw(stack, manager.OffhandHotbarSlot)) return false;
        api.Network.GetChannel(VscnCivilizationModSystem.ChannelName).SendPacket(new MapMarkerClipboardRequest
        { Action = action, HotbarSlot = hotbarSlot, MapFingerprint = Fingerprint(stack) });
        return true;
    }

    // Keep native editing, naming and lock dialogs. Add a toolbar above the marker list.
    private static bool Compose(object __instance, bool keepScroll)
    {
        var dialog = (GuiDialog)__instance;
        var api = Client(dialog);
        if (Field(dialog, "_mapSlot") is not ItemSlot { Itemstack: { } stack } || !MapType.IsInstanceOfType(stack.Item))
        { dialog.TryClose(); return false; }
        float scroll = keepScroll ? dialog.SingleComposer?.GetScrollbar("scrollbar")?.CurrentYPosition ?? 0 : 0;
        bool locked = Locked(stack), owner = stack.Attributes.GetString("mapLockedBy") == api.World.Player.PlayerUID;
        AccessTools.Field(dialog.GetType(), "_locked").SetValue(dialog, locked);
        var offhand = api.World.Player.InventoryManager.OffhandHotbarSlot;
        bool canDraw = !locked && CanDraw(stack, offhand);
        string? reason = locked ? L("map-marker-locked") : null;
        var list = ElementBounds.Fixed(0, GuiStyle.TitleBarHeight + 42, 650, 400);
        var clip = list.ForkContainingChild(GuiStyle.HalfPadding, GuiStyle.HalfPadding, GuiStyle.HalfPadding, GuiStyle.HalfPadding);
        var content = list.ForkContainingChild(GuiStyle.HalfPadding, GuiStyle.HalfPadding, GuiStyle.HalfPadding, GuiStyle.HalfPadding);
        var scrollbar = list.RightCopy().WithFixedWidth(20);
        var copy = ElementBounds.Fixed(0, GuiStyle.TitleBarHeight + 4, 145, 30);
        var paste = ElementBounds.Fixed(150, GuiStyle.TitleBarHeight + 4, 145, 30);
        var draw = ElementBounds.Fixed(300, GuiStyle.TitleBarHeight + 4, 145, 30);
        var settings = ElementBounds.Fixed(450, GuiStyle.TitleBarHeight + 4, 100, 30);
        string lockLabel = Lang.Get("immersivemaps:" + (!locked ? "waypoints-lock" : owner ? "waypoints-unlock" : "locked-by"), stack.Attributes.GetString("mapLockedByName", ""));
        double lockWidth = Math.Clamp(CairoFont.SmallButtonText().GetTextExtents(lockLabel).Width / Math.Max(.25, RuntimeEnv.GUIScale) + 40, 140, 320);
        var add = ElementBounds.Fixed(0, list.fixedY + 410, 640 - lockWidth, 30);
        var toggleLock = ElementBounds.Fixed(650 - lockWidth, add.fixedY, lockWidth, 30);
        var background = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding).WithSizing(ElementSizing.FitToChildren)
            .WithChildren(copy, paste, draw, settings, list, scrollbar, add, toggleLock);
        var previous = dialog.SingleComposer;
        var composer = api.Gui.CreateCompo("mapwaypointsgui", ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.RightMiddle))
            .AddShadedDialogBG(background).AddDialogTitleBar(stack.GetName(), () => dialog.TryClose())
            .BeginChildElements()
            .AddSmallButton(L("map-marker-copy"), () => Request(dialog, "copy"), copy, key: "vscn-copy-markers")
            .AddSmallButton(L("map-marker-paste"), () => Request(dialog, "paste"), paste, key: "vscn-paste-markers")
            .AddSmallButton(L("map-draw-range"), () => { OpenDrawingDialog?.Invoke(dialog); return true; }, draw, key: "vscn-draw-range")
            .AddSmallButton(L("map-settings"), () => { OpenSettingsDialog?.Invoke(dialog); return true; }, settings, key: "vscn-map-settings")
            .AddInset(list, 3).BeginClip(clip).AddContainer(content, "scroll-content").EndClip()
            .AddVerticalScrollbar(value => Call(dialog, "OnScroll", value), scrollbar, "scrollbar")
            .AddSmallButton(Lang.Get("immersivemaps:waypoints-add"), () => (bool)Call(dialog, "OnAddWaypoint")!, add, key: "addButton")
            .AddSmallButton(lockLabel, () => (bool)Call(dialog, "OnToggleLock")!, toggleLock, key: "lockButton")
            .EndChildElements();
        dialog.SingleComposer = composer;
        Call(dialog, "AddRenameButton", composer, stack.GetName(), canDraw ? null : reason ?? Lang.Get("immersivemaps:tooltip-needs-tool"));
        double rowsHeight = (double)Call(dialog, "AddRows", composer.GetContainer("scroll-content"), stack, reason)!;
        composer.Compose();
        composer.GetScrollbar("scrollbar").SetHeights((float)clip.fixedHeight, (float)rowsHeight);
        composer.GetScrollbar("scrollbar").CurrentYPosition = Math.Clamp(scroll, 0, (float)Math.Max(0, rowsHeight - clip.fixedHeight));
        composer.GetScrollbar("scrollbar").TriggerChanged();
        composer.GetButton("vscn-copy-markers").Enabled = canDraw;
        composer.GetButton("vscn-paste-markers").Enabled = canDraw;
        composer.GetButton("vscn-draw-range").Enabled = canDraw && !GroundMapData.IsAtlas(stack) && RecordedPaperMap.Describe(stack) != null;
        composer.GetButton("vscn-map-settings").Enabled = true;
        composer.GetButton("addButton").Enabled = canDraw;
        composer.GetButton("lockButton").Enabled = !locked || owner;
        previous?.Dispose();
        return false;
    }

    private static bool AddRows(object __instance, GuiElementContainer container, ItemStack stack, string? lockReason, ref double __result)
    {
        var api = Client(__instance);
        double y = 0;
        foreach (object marker in Read(stack).Values.Cast<object>().OrderBy(m => (string)AccessTools.Property(m.GetType(), "Name").GetValue(m)!))
        {
            object? Value(string name) => AccessTools.Property(marker.GetType(), name).GetValue(marker);
            Call(__instance, "AddIcon", container, marker, y);
            bool atlas = GroundMapData.IsAtlas(stack);
            container.Add(new GuiElementDynamicText(api, (string)Value("Name")!, CairoFont.WhiteSmallText(), ElementBounds.Fixed(50, y + 15, atlas ? 400 : 440, 30)));
            if (atlas)
            {
                bool inside = GroundMapData.Contains(stack, Convert.ToDouble(Value("X")), Convert.ToDouble(Value("Z")));
                var toggle = new GuiElementSwitch(api, pinned =>
                {
                    var manager = api.World.Player.InventoryManager;
                    if (manager.ActiveHotbarSlotNumber != (int)Field(__instance, "_hotbarSlot")!
                        || manager.ActiveHotbarSlot?.Itemstack is not { } held || !ReferenceEquals(held, stack) || !GroundMapData.IsAtlas(held) || Locked(held)) return;
                    var current = Read(held);
                    if (current[Value("Id")!] is not { } waypoint
                        || !GroundMapData.Contains(held, Convert.ToDouble(GroundMapCompatibility.Property(waypoint, "X")), Convert.ToDouble(GroundMapCompatibility.Property(waypoint, "Z")))) return;
                    Call(Field(__instance, "_requests")!, "SetPinned", (int)Field(__instance, "_hotbarSlot")!, (string)Value("Id")!, pinned);
                }, ElementBounds.Fixed(460, y + 10, 30, 30), 30, 4);
                toggle.SetValue(inside && (bool)Value("Pinned")!);
                toggle.Enabled = inside && !Locked(stack);
                container.Add(toggle);
            }
            var tooltips = new List<GuiElement>();
            void Button(double x, string icon, string method, bool enabled, string tooltip)
            {
                var bounds = ElementBounds.Fixed(x, y + 10, 30, 30);
                Static("Gui.GuiElementButtonIcon", "AddIconButton", container, api, icon,
                    (ActionConsumable)(() => (bool)Call(__instance, method, marker)!), bounds, enabled, 18d);
                tooltips.Add((GuiElement)Static("Gui.Tooltips", "Create", api, tooltip, bounds)!);
            }
            var scaleBounds = ElementBounds.Fixed(500, y + 10, 30, 30);
            Static("Gui.GuiElementButtonIcon", "AddIconButton", container, api, "immersivemaps-pencil",
                (ActionConsumable)(() => { OpenMarkerScaleDialog?.Invoke(__instance, stack, (int)Field(__instance, "_hotbarSlot")!, (string)Value("Id")!); return true; }), scaleBounds, lockReason == null, 14d);
            tooltips.Add((GuiElement)Static("Gui.Tooltips", "Create", api, L("map-marker-scale"), scaleBounds)!);
            string notes = (string)Value("Notes")!;
            Button(535, notes.Length > 0 ? "immersivemaps-notes" : "immersivemaps-notes-empty", "OnOpenNotes", notes.Length > 0 || lockReason == null,
                notes.Length > 0 ? (string)Static("Gui.GuiDialogMapWaypoints", "NotesPreview", notes)! : Lang.Get("immersivemaps:tooltip-notes-add"));
            Button(570, "immersivemaps-pencil", "OnEditWaypoint", lockReason == null, Lang.Get("immersivemaps:tooltip-edit"));
            Button(605, "immersivemaps-remove", "OnRemoveWaypoint", lockReason == null, Lang.Get("immersivemaps:tooltip-remove"));
            foreach (var tooltip in tooltips) container.Add(tooltip);
            y += 40;
        }
        if (!GroundMapData.IsAtlas(stack) && RecordedPaperMap.Describe(stack) is { } map)
        {
            try
            {
                var areas = MapDrawingDocument.Read(stack, map).Areas;
                foreach (var entry in areas.Select((area, index) => (area, index)).OrderBy(e => e.area.Name))
                {
                    var areaMarker = Activator.CreateInstance(Type("Waypoints.Waypoint"),
                        (int)Math.Round(entry.area.Seed.X), (int)Math.Round(entry.area.Seed.Z), entry.area.Color, "home", entry.area.Name)!;
                    Call(__instance, "AddIcon", container, areaMarker, y);
                    container.Add(new GuiElementDynamicText(api, L("map-draw-list") + ": " + entry.area.Name,
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(50, y + 15, 440, 30)));
                    bool enabled = lockReason == null && CanDraw(stack, api.World.Player.InventoryManager.OffhandHotbarSlot);
                    void AreaButton(double x, string icon, string action, string tip)
                    {
                        var bounds = ElementBounds.Fixed(x, y + 10, 30, 30);
                        Static("Gui.GuiElementButtonIcon", "AddIconButton", container, api, icon,
                            (ActionConsumable)(() => { OpenAreaAction?.Invoke(__instance, entry.index, action); return true; }), bounds, enabled, 18d);
                        container.Add((GuiElement)Static("Gui.Tooltips", "Create", api, tip, bounds)!);
                    }
                    AreaButton(535, entry.area.Notes.Length > 0 ? "immersivemaps-notes" : "immersivemaps-notes-empty", "notes", Lang.Get("immersivemaps:tooltip-notes-add"));
                    AreaButton(570, "immersivemaps-pencil", "edit", Lang.Get("immersivemaps:tooltip-edit"));
                    AreaButton(605, "immersivemaps-remove", "delete", Lang.Get("immersivemaps:tooltip-remove"));
                    y += 40;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException)
            { api.Logger.Warning("Invalid map areas ignored: {0}", ex.Message); }
        }
        __result = y;
        return false;
    }

    internal static void Commit(ICoreServerAPI api, IServerPlayer player, IDictionary markers)
    {
        Write(player.InventoryManager.ActiveHotbarSlot.Itemstack!, markers);
        // The native handler is held only by network delegates, so use its stateless commit path.
        var handler = Activator.CreateInstance(Type("Network.MapRequestHandler"), api, api.Network.GetChannel("immersivemaps"), null)!;
        var heldType = Type("Network.MapRequestHandler").GetNestedType("HeldMap", BindingFlags.NonPublic)!;
        var stack = player.InventoryManager.ActiveHotbarSlot.Itemstack!;
        var held = Activator.CreateInstance(heldType, player.InventoryManager.ActiveHotbarSlot, stack, stack.Item, player.InventoryManager.ActiveHotbarSlotNumber);
        Call(handler, "Commit", player, held, markers);
    }

    public void Dispose()
    {
        lock (Sync)
        {
            if (disposed) return;
            disposed = true;
            if (--users == 0) new Harmony(PatchId).UnpatchAll(PatchId);
        }
    }
}

public sealed partial class VscnCivilizationModSystem
{
    private sealed record MarkerClipboard(ItemStack Source, byte[] Data);
    private readonly Dictionary<string, MarkerClipboard> markerClipboards = [];

    private void OnMapMarkerClipboard(IServerPlayer player, MapMarkerClipboardRequest request)
    {
        if (disposed || immersiveMapCompatibility == null || player.Entity?.Alive != true
            || player.InventoryManager.ActiveHotbarSlotNumber != request.HotbarSlot
            || player.InventoryManager.ActiveHotbarSlot?.Itemstack is not { } stack
            || !ImmersiveMapCompatibility.MapType.IsInstanceOfType(stack.Item)
            || ImmersiveMapCompatibility.Fingerprint(stack) != request.MapFingerprint) return;
        void Tell(string key, params object[] args) => player.SendMessage(GlobalConstants.GeneralChatGroup,
            Lang.GetL(player.LanguageCode, "vsscsmod:ui." + key, args), EnumChatType.Notification);
        if (ImmersiveMapCompatibility.Locked(stack))
        { Tell("map-marker-locked"); return; }
        if (!ImmersiveMapCompatibility.CanDraw(stack, player.InventoryManager.OffhandHotbarSlot))
        { Tell("map-marker-needs-tool"); return; }
        if (request.Action == "copy")
        {
            byte[] data = stack.Attributes.GetBytes("waypoints") ?? [];
            if (data.Length > short.MaxValue) return;
            markerClipboards[player.PlayerUID] = new(stack, data.ToArray());
            Tell("map-marker-copied", ImmersiveMapCompatibility.Deserialize(data).Count);
        }
        else if (request.Action == "paste")
        {
            if (!markerClipboards.TryGetValue(player.PlayerUID, out var clipboard))
            { Tell("map-marker-empty"); return; }
            if (ImmersiveMapCompatibility.Locked(clipboard.Source))
            { Tell("map-marker-locked"); return; }
            var markers = ImmersiveMapCompatibility.Read(stack);
            var copied = ImmersiveMapCompatibility.Deserialize(clipboard.Data);
            foreach (DictionaryEntry marker in copied)
                if (!markers.Contains(marker.Key)) markers[marker.Key] = marker.Value;
            // Serialise on a clone first; an oversized clipboard merge must not alter the real sheet.
            var preview = stack.Clone();
            ImmersiveMapCompatibility.Write(preview, markers);
            if (preview.Attributes.GetBytes("waypoints").Length > short.MaxValue) return;
            ImmersiveMapCompatibility.Commit(sapi!, player, markers);
            Tell("map-marker-pasted", copied.Count);
        }
    }
}
