using System.Collections;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using SeraphCivilizationSimulation;

internal static class ImmersiveMapGuiChecks
{
    internal static void Run(Assembly maps, ICoreClientAPI original, Action<bool, string> check)
    {
        Type Native(string name) => maps.GetType("ImmersiveMaps." + name, true)!;
        var compat = typeof(VscnCivilizationModSystem).Assembly.GetType("SeraphCivilizationSimulation.ImmersiveMapCompatibility")!;
        using var patch = (IDisposable)Activator.CreateInstance(compat)!;
        var renderSystem = Activator.CreateInstance(Native("Rendering.MapRenderSystem"))!;
        var iconSet = AccessTools.Property(renderSystem.GetType(), "Icons").GetValue(renderSystem)!;
        ((List<string>)AccessTools.Field(iconSet.GetType(), "_names").GetValue(iconSet)!).AddRange(["circle", "star"]);
        var loader = Proxy.Make<IModLoader>((m, a) => m.Name == "GetModSystem" ? renderSystem : null);
        var item = (Item)Activator.CreateInstance(Native("Items.ItemMap"))!;
        item.Code = new AssetLocation("immersivemaps:map-papyrus"); item.ItemId = 101;
        var stack = new ItemStack(item); var slot = new DummySlot(stack);
        var markers = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), Native("Waypoints.Waypoint")))!;
        var marker = Activator.CreateInstance(Native("Waypoints.Waypoint"), 123, 456, 0x123456, "circle", "Home")!;
        AccessTools.Property(marker.GetType(), "Id").SetValue(marker, "home");
        markers.Add("home", marker);
        AccessTools.Method(compat, "Write").Invoke(null, [stack, markers]);
        var pencil = (Item)Activator.CreateInstance(Native("Items.ItemDrawingTool"))!;
        pencil.Code = new AssetLocation("immersivemaps:charcoalpencil"); pencil.ItemId = 102;
        var offhand = new DummySlot(new ItemStack(pencil));
        var inventory = Proxy.Make<IPlayerInventoryManager>((m, a) => m.Name switch
        { "get_ActiveHotbarSlot" => slot, "get_ActiveHotbarSlotNumber" => 2, "get_OffhandHotbarSlot" => offhand, _ => null });
        var player = Proxy.Make<IClientPlayer>((m, a) => m.Name switch
        { "get_PlayerUID" => "owner", "get_InventoryManager" => inventory, _ => null });
        var config = new Vintagestory.API.Datastructures.TreeAttribute(); config.SetString("worldClimate", "patchy");
        var blocks = Proxy.Make<IBlockAccessor>((m, a) => m.Name is "get_MapSizeX" or "get_MapSizeZ" ? 1000000 : null);
        var world = Proxy.Make<IClientWorldAccessor>((m, a) => m.Name switch
        { "get_Player" => player, "get_Config" => config, "get_DefaultSpawnPosition" => new EntityPos(),
            "get_BlockAccessor" => blocks,
            "GetItem" when a?[0] is int id && id == item.Id => item, _ => m.Invoke(original.World, a) });
        var sent = new List<MapMarkerClipboardRequest>(); var pins = new List<object>(); var saves = new List<object>();
        var channel = Proxy.Make<IClientNetworkChannel>((m, a) =>
        {
            if (m.Name == "SendPacket" && a?[0] is MapMarkerClipboardRequest request) sent.Add(request);
            if (m.Name == "SendPacket" && a?[0]?.GetType() == Native("Network.PinWaypointPacket")) pins.Add(a[0]!);
            if (m.Name == "SendPacket" && a?[0]?.GetType() == Native("Network.SaveWaypointPacket")) saves.Add(a[0]!);
            return null;
        });
        var network = Proxy.Make<IClientNetworkAPI>((m, a) => m.Name == "GetChannel" ? channel : null);
        ICoreClientAPI? api = null;
        var gui = Proxy.Make<IGuiAPI>((m, a) =>
        {
            var result = m.Invoke(original.Gui, a);
            if (m.Name == "CreateCompo" && result is GuiComposer composer) composer.Api = api!;
            return result;
        });
        api = Proxy.Make<ICoreClientAPI>((m, a) => m.Name switch
        { "get_ModLoader" => loader, "get_World" => world, "get_Network" => network, "get_Gui" => gui, _ => m.Invoke(original, a) });
        AccessTools.Field(item.GetType(), "api").SetValue(item, api);
        var requests = Activator.CreateInstance(Native("Network.WaypointRequests"), channel)!;
        using var dialog = (GuiDialog)Activator.CreateInstance(Native("Gui.GuiDialogMapWaypoints"), api, requests)!;
        AccessTools.Field(dialog.GetType(), "_mapSlot").SetValue(dialog, slot);
        AccessTools.Field(dialog.GetType(), "_hotbarSlot").SetValue(dialog, 2);
        void Compose() => AccessTools.Method(dialog.GetType(), "Compose").Invoke(dialog, [false]);
        Compose();
        var copy = dialog.SingleComposer.GetButton("vscn-copy-markers");
        var paste = dialog.SingleComposer.GetButton("vscn-paste-markers");
        var container = dialog.SingleComposer.GetContainer("scroll-content");
        check(copy.Enabled && paste.Enabled && copy.Bounds.renderY < container.Bounds.renderY && paste.Bounds.renderY == copy.Bounds.renderY,
            "Copy and paste must compose as two enabled buttons above the native marker list.");
        check(!container.Elements.OfType<GuiElementSwitch>().Any() && container.Elements.OfType<GuiElementDynamicText>().Count() == 1,
            "The native marker list must retain descriptions while removing pin switches.");
        var text = container.Elements.OfType<GuiElementDynamicText>().Single();
        string description = text.GetText();
        check(description == "Home", "The actual row must contain exactly one name, without coordinates, latitude, longitude or distance.");
        foreach (var button in new[] { copy, paste })
        {
            var click = new MouseEvent((int)(button.Bounds.renderX + 5), (int)(button.Bounds.renderY + 5), EnumMouseButton.Left, 0);
            button.OnMouseDownOnElement(api, click); button.OnMouseUpOnElement(api, click);
        }
        check(sent.Count == 2 && sent[0].Action == "copy" && sent[1].Action == "paste" && sent.All(r => r.HotbarSlot == 2 && r.MapFingerprint.Length == 64),
            "Both toolbar buttons must send their real clipboard action with held-map identity.");
        offhand.Itemstack = null;
        foreach (string action in new[] { "copy", "paste" })
            AccessTools.Method(compat, "Request").Invoke(null, [dialog, action]);
        check(sent.Count == 2, "Removing the pencil while the dialog is open must reject stale enabled-button actions.");
        Compose();
        check(!dialog.SingleComposer.GetButton("vscn-copy-markers").Enabled && !dialog.SingleComposer.GetButton("vscn-paste-markers").Enabled,
            "Both clipboard buttons must be disabled without an offhand pencil.");
        offhand.Itemstack = new ItemStack(item); Compose();
        check(!dialog.SingleComposer.GetButton("vscn-copy-markers").Enabled && !dialog.SingleComposer.GetButton("vscn-paste-markers").Enabled,
            "An unrelated offhand item must leave both clipboard buttons disabled.");
        offhand.Itemstack = new ItemStack(pencil); Compose();
        check(dialog.SingleComposer.GetButton("vscn-copy-markers").Enabled && dialog.SingleComposer.GetButton("vscn-paste-markers").Enabled,
            "Returning the pencil must restore clipboard controls on an unlocked map.");
        foreach (string owner in new[] { "owner", "other" })
        {
            stack.Attributes.SetString("mapLockedBy", owner); Compose();
            check(!dialog.SingleComposer.GetButton("vscn-copy-markers").Enabled && !dialog.SingleComposer.GetButton("vscn-paste-markers").Enabled,
                "The actual GUI must disable both clipboard buttons on locked maps, including the owner's map.");
        }
        stack.Attributes.RemoveAttribute("mapLockedBy");
        stack.Attributes.SetBlockPos("mapPosition", new BlockPos(123, 100, 456)); stack.Attributes.SetFloat("mapRotation", 0);
        foreach (bool debug in new[] { false, true })
        {
            var sheetInfo = new System.Text.StringBuilder("existing text<br>");
            item.GetHeldItemInfo(slot, sheetInfo, world, debug);
            check(sheetInfo.ToString() == "existing text<br>",
                "Single paper sheets must not append location, angular seconds or debug positions to existing tooltip text.");
        }
        stack.Attributes.SetString("mapName", "Survey");
        stack.Attributes.SetString("mapLockedBy", "owner");
        stack.Attributes.SetString("mapLockedByName", "Owner");
        var lockedInfo = new System.Text.StringBuilder();
        item.GetHeldItemInfo(slot, lockedInfo, world, true);
        check(lockedInfo.ToString().Contains("Survey") && lockedInfo.ToString().Contains("immersivemaps:locked-by"),
            "Hiding coordinates must preserve the map name and lock notice.");
        stack.Attributes.RemoveAttribute("mapName"); stack.Attributes.RemoveAttribute("mapLockedBy");
        foreach (bool isNew in new[] { false, true })
        {
            using var editor = (GuiDialog)Activator.CreateInstance(Native("Gui.GuiDialogEditWaypoint"), api, requests)!;
            AccessTools.Method(editor.GetType(), "Open").Invoke(editor, [slot, 2, marker, isNew]);
            check(editor.SingleComposer.GetTextInput("firstCoordinateInput") == null
                && editor.SingleComposer.GetTextInput("secondCoordinateInput") == null
                && editor.SingleComposer.GetTextInput("nameInput") != null,
                "Native new/edit marker dialogs must omit coordinate inputs while retaining name editing.");
            check(editor.SingleComposer.GetButton("saveButton").Enabled,
                "A marker with a recorded position must remain savable without visible coordinate fields.");
            AccessTools.Method(editor.GetType(), "OnNameChanged").Invoke(editor, ["Renamed"]);
            AccessTools.Method(editor.GetType(), "OnIconSelected").Invoke(editor, [1]);
            AccessTools.Method(editor.GetType(), "OnColorSelected").Invoke(editor, [0]);
            AccessTools.Method(editor.GetType(), "OnSave").Invoke(editor, null);
            var saved = saves.Last(); var savedMarker = GroundMapCompatibility.Property(saved, "Waypoint")!;
            check((bool)GroundMapCompatibility.Property(saved, "IsNew")! == isNew
                && (int)GroundMapCompatibility.Property(savedMarker, "X")! == 123
                && (int)GroundMapCompatibility.Property(savedMarker, "Z")! == 456
                && (string)GroundMapCompatibility.Property(savedMarker, "Name")! == "Renamed"
                && (string)GroundMapCompatibility.Property(savedMarker, "Icon")! == "star",
                "Saving a new or edited marker must retain its location and apply name/icon changes.");
        }
        var otherSheet = stack.Clone(); otherSheet.Attributes.SetInt("mapPositionX", 267);
        var atlas = new ItemStack(GroundMapData.Sew([stack, otherSheet]).ToBytes());
        atlas.ResolveBlockOrItem(world); slot.Itemstack = atlas; Compose();
        check(dialog.SingleComposer.GetContainer("scroll-content").Elements.OfType<GuiElementDynamicText>().Single().GetText() == "Home",
            "Atlas marker rows must apply the same name-only restriction as small maps.");
        var info = new System.Text.StringBuilder();
        item.GetHeldItemInfo(slot, info, world, true);
        check(!info.ToString().Contains("123") && !info.ToString().Contains("456") && !info.ToString().Contains("Position:") && !info.ToString().Contains("Rotation:"),
            "Atlas tooltips must not expose first-sheet coordinates or native debug marker positions.");
        var pin = dialog.SingleComposer.GetContainer("scroll-content").Elements.OfType<GuiElementSwitch>().Single();
        check(pin.Enabled, "A deserialized atlas must expose an enabled pin switch before rendering resolves its nested sheets.");
        int pinX = (int)(pin.Bounds.renderX + pin.Bounds.OuterWidth / 2), pinY = (int)(pin.Bounds.renderY + pin.Bounds.OuterHeight / 2);
        dialog.OnMouseDown(new MouseEvent(pinX, pinY, EnumMouseButton.Left, 0));
        dialog.OnMouseUp(new MouseEvent(pinX, pinY, EnumMouseButton.Left, 0));
        check(pins.Count == 1 && (int)GroundMapCompatibility.Property(pins[0], "HotbarSlot")! == 2
            && (bool)GroundMapCompatibility.Property(pins[0], "Pinned")!,
            "A full dialog mouse click must send the native pin-on request for the deserialized held atlas.");
        var synchronized = ImmersiveMapCompatibility.Read(atlas);
        AccessTools.Property(marker.GetType(), "Pinned").SetValue(synchronized["home"], true);
        ImmersiveMapCompatibility.Write(atlas, synchronized); Compose();
        pin = dialog.SingleComposer.GetContainer("scroll-content").Elements.OfType<GuiElementSwitch>().Single();
        check(pin.On, "Server-synchronized pinned state must survive writing an atlas whose nested sheets remain unresolved.");
        dialog.OnMouseDown(new MouseEvent(pinX, pinY, EnumMouseButton.Left, 0));
        dialog.OnMouseUp(new MouseEvent(pinX, pinY, EnumMouseButton.Left, 0));
        check(pins.Count == 2 && !(bool)GroundMapCompatibility.Property(pins[1], "Pinned")!,
            "The same full dialog mouse path must also allow unpinning after synchronization.");
        foreach (string owner in new[] { "owner", "other" })
        {
            atlas.Attributes.SetString("mapLockedBy", owner); Compose();
            check(!dialog.SingleComposer.GetContainer("scroll-content").Elements.OfType<GuiElementSwitch>().Single().Enabled,
                "A locked atlas must disable pin changes even for its owner.");
        }
        atlas.Attributes.RemoveAttribute("mapLockedBy");
        var outside = ImmersiveMapCompatibility.Read(atlas);
        AccessTools.Property(marker.GetType(), "X").SetValue(outside.Values.Cast<object>().Single(), 99999);
        ImmersiveMapCompatibility.Write(atlas, outside); Compose();
        check(!dialog.SingleComposer.GetContainer("scroll-content").Elements.OfType<GuiElementSwitch>().Single().Enabled,
            "Out-of-range atlas rows must retain a visible disabled pin switch.");
        AccessTools.Property(marker.GetType(), "X").SetValue(outside.Values.Cast<object>().Single(), 411);
        ImmersiveMapCompatibility.Write(atlas, outside); Compose();
        check(dialog.SingleComposer.GetContainer("scroll-content").Elements.OfType<GuiElementSwitch>().Single().Enabled,
            "A marker exactly on the outer recorded edge must still have an enabled pin switch.");
    }
}
