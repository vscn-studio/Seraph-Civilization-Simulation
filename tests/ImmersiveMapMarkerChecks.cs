using System.Collections;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using SeraphCivilizationSimulation;

internal static class ImmersiveMapMarkerChecks
{
    public static void Run(Assembly maps, VscnCivilizationModSystem mod, ICoreAPI common, Action<bool, string> check)
    {
        Type Native(string name) => maps.GetType("ImmersiveMaps." + name, true)!;
        var compat = typeof(VscnCivilizationModSystem).Assembly.GetType("SeraphCivilizationSimulation.ImmersiveMapCompatibility")!;
        object? Integration(string method, params object?[] args) => AccessTools.Method(compat, method).Invoke(null, args);
        var item = (Item)Activator.CreateInstance(Native("Items.ItemMap"))!;
        item.Code = new AssetLocation("immersivemaps:map-papyrus"); item.ItemId = 101;
        AccessTools.Field(item.GetType(), "api").SetValue(item, common);
        IDictionary Markers(string id, bool pinned = false)
        {
            var dictionary = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), Native("Waypoints.Waypoint")))!;
            var marker = Activator.CreateInstance(Native("Waypoints.Waypoint"), 123, 456, 0x123456, "home", "Home")!;
            AccessTools.Property(marker.GetType(), "Id").SetValue(marker, id);
            AccessTools.Property(marker.GetType(), "Notes").SetValue(marker, "Saved notes");
            AccessTools.Property(marker.GetType(), "Pinned").SetValue(marker, pinned);
            dictionary.Add(id, marker); return dictionary;
        }
        ItemStack Sheet(string id)
        { var sheet = new ItemStack(item); Integration("Write", sheet, Markers(id, true)); return sheet; }
        var source = Sheet("source"); var target = Sheet("existing"); var slot = new DummySlot(source);
        var pencil = (Item)Activator.CreateInstance(Native("Items.ItemDrawingTool"))!;
        pencil.Code = new AssetLocation("immersivemaps:charcoalpencil"); pencil.ItemId = 102;
        var offhand = new DummySlot();
        int active = 2, syncs = 0;
        var manager = Proxy.Make<IPlayerInventoryManager>((m, a) => m.Name switch
        { "get_ActiveHotbarSlot" => slot, "get_ActiveHotbarSlotNumber" => active, "get_OffhandHotbarSlot" => offhand, _ => null });
        var player = Proxy.Make<IServerPlayer>((m, a) => m.Name switch
        { "get_PlayerUID" => "owner", "get_LanguageCode" => "en", "get_Entity" => new EntityPlayer(), "get_InventoryManager" => manager, _ => null });
        var channel = Proxy.Make<IServerNetworkChannel>((m, a) => { if (m.Name == "SendPacket") syncs++; return null; });
        var network = Proxy.Make<IServerNetworkAPI>((m, a) => m.Name == "GetChannel" ? channel : null);
        long now = 0;
        var world = Proxy.Make<IServerWorldAccessor>((m, a) => m.Name == "get_ElapsedMilliseconds" ? now += 1000 : null);
        var logger = Proxy.Make<ILogger>((m, a) => null);
        var replies = Proxy.Make<IServerNetworkChannel>((m, a) => null);
        var sapi = Proxy.Make<ICoreServerAPI>((m, a) => m.Name switch { "get_Network" => network, "get_World" => world, "get_Logger" => logger, _ => null });
        AccessTools.Field(mod.GetType(), "channel").SetValue(mod, replies);
        AccessTools.Field(mod.GetType(), "sapi").SetValue(mod, sapi);
        void Request(string action, int? number = null, string? fingerprint = null, IServerPlayer? sender = null)
            => AccessTools.Method(mod.GetType(), "OnMapMarkerClipboard").Invoke(mod, [sender ?? player, new MapMarkerClipboardRequest
            { Action = action, HotbarSlot = number ?? active, MapFingerprint = fingerprint ?? (string)Integration("Fingerprint", slot.Itemstack!)! }]);
        source.Attributes.SetInt("mapPositionX", 100); source.Attributes.SetInt("mapPositionZ", 200);
        source.Attributes.SetFloat("mapRotation", 0);
        var shape = RecordedPaperMap.Describe(source)!;
        var drawing = new MapDrawingDocument { Lines = [new() { Points = [new() { X = 99, Z = 199 }, new() { X = 101, Z = 201 }] }] };
        void SaveDrawing(string? fingerprint = null, byte[]? bytes = null)
            => AccessTools.Method(mod.GetType(), "OnMapDrawingSave").Invoke(mod, [player, new MapDrawingSaveRequest
            { HotbarSlot = active, MapFingerprint = fingerprint ?? (string)Integration("Fingerprint", source)!, Document = bytes ?? drawing.Encode(shape) }]);
        pencil.Code = new AssetLocation("immersivemaps:charcoal-pencil");
        SaveDrawing();
        check(source.Attributes.GetBytes(MapDrawingDocument.Attribute) == null, "Drawing requires a pencil in the offhand.");
        offhand.Itemstack = new ItemStack(pencil); SaveDrawing("stale");
        check(source.Attributes.GetBytes(MapDrawingDocument.Attribute) == null, "A stale drawing must not modify another map.");
        source.Attributes.SetString("mapLockedBy", "owner"); SaveDrawing();
        check(source.Attributes.GetBytes(MapDrawingDocument.Attribute) == null, "A locked map must reject drawing changes.");
        source.Attributes.RemoveAttribute("mapLockedBy"); SaveDrawing(bytes: [0xff]);
        check(source.Attributes.GetBytes(MapDrawingDocument.Attribute) == null, "Invalid drawing data must be rejected.");
        SaveDrawing();
        check(MapDrawingDocument.Read(source, shape).Lines.Count == 1, "The server must save a validated drawing on the held map.");
        source.Attributes.RemoveAttribute(MapDrawingDocument.Attribute); offhand.Itemstack = null;
        IDictionary Read(ItemStack sheet) => (IDictionary)Integration("Read", sheet)!;
        check(!(bool)AccessTools.Property(Native("Waypoints.Waypoint"), "Pinned").GetValue(Read(source)["source"])!, "Saving markers must clear forged pinned values.");
        Request("copy"); slot.Itemstack = target; offhand.Itemstack = new ItemStack(pencil); Request("paste");
        check(syncs == 0 && Read(target).Count == 1, "Copy without an offhand pencil must not populate the clipboard.");
        slot.Itemstack = source; offhand.Itemstack = new ItemStack(new Item { Code = new AssetLocation("game:stick"), ItemId = 103 });
        Request("copy"); slot.Itemstack = target; offhand.Itemstack = new ItemStack(pencil); Request("paste");
        check(syncs == 0 && Read(target).Count == 1, "An unrelated offhand item must not authorize copying.");
        slot.Itemstack = source; Request("copy"); slot.Itemstack = target;
        offhand.Itemstack = null; Request("paste");
        check(syncs == 0 && Read(target).Count == 1, "Removing the pencil after copying must prevent server-side paste.");
        offhand.Itemstack = new ItemStack(source.Item); Request("paste");
        check(syncs == 0 && Read(target).Count == 1, "An offhand map must not substitute for the pencil when pasting.");
        offhand.Itemstack = new ItemStack(pencil); Request("paste");
        var pasted = Read(target);
        check(pasted.Count == 2 && pasted.Contains("existing") && pasted.Contains("source") && syncs == 1,
            "Copy/paste must merge server-owned markers and use the real native map synchronization packet.");
        var copied = pasted["source"]!;
        check((string)AccessTools.Property(copied.GetType(), "Notes").GetValue(copied)! == "Saved notes"
            && (int)AccessTools.Property(copied.GetType(), "X").GetValue(copied)! == 123
            && (string)AccessTools.Property(copied.GetType(), "Icon").GetValue(copied)! == "home",
            "Clipboard must preserve coordinates, notes and icons.");
        AccessTools.Property(copied.GetType(), "Name").SetValue(copied, "Destination edit"); Integration("Write", target, pasted);
        Request("paste");
        check(Read(target).Count == 2 && (string)AccessTools.Property(copied.GetType(), "Name").GetValue(Read(target)["source"])! == "Destination edit",
            "Repeated paste must neither duplicate markers nor overwrite destination edits.");
        int sent = syncs;
        foreach (string owner in new[] { "owner", "other" })
        {
            target.Attributes.SetString("mapLockedBy", owner); Request("paste");
            check(syncs == sent, "A locked destination must reject paste, including the lock owner's request.");
            target.Attributes.RemoveAttribute("mapLockedBy");
            source.Attributes.SetString("mapLockedBy", owner); Request("paste");
            check(syncs == sent, "Locking the copied source must prevent subsequent paste.");
            slot.Itemstack = source; Request("copy"); source.Attributes.RemoveAttribute("mapLockedBy"); slot.Itemstack = target;
        }
        Request("paste", active + 1); Request("paste", fingerprint: "stale");
        check(syncs == sent, "Wrong-slot and stale-map packets must not mutate a map.");
        var other = Proxy.Make<IServerPlayer>((m, a) => m.Name switch
        { "get_PlayerUID" => "other", "get_LanguageCode" => "en", "get_Entity" => new EntityPlayer(), "get_InventoryManager" => manager, _ => null });
        Request("paste", sender: other); check(syncs == sent, "Players must not share a clipboard.");
        // A map locked before copy must never enter the clipboard.
        source.Attributes.SetString("mapLockedBy", "owner"); slot.Itemstack = source; Request("copy", sender: other);
        source.Attributes.RemoveAttribute("mapLockedBy"); slot.Itemstack = target; Request("paste", sender: other);
        check(syncs == sent, "A failed locked-source copy must leave the player's clipboard empty.");
        var nativeHandler = Activator.CreateInstance(Native("Network.MapRequestHandler"), sapi, channel, null)!;
        foreach (var endpoint in new[] { ("OnPinWaypoint", "PinWaypointPacket"), ("OnCopyWaypoints", "CopyWaypointsPacket") })
        {
            var packet = Activator.CreateInstance(Native("Network." + endpoint.Item2));
            AccessTools.Method(nativeHandler.GetType(), endpoint.Item1).Invoke(nativeHandler, [player, packet]);
        }
        check(syncs == sent, "Old pin and offhand-copy network endpoints must be disabled.");
        var pinned = Markers("old", true);
        var collection = Activator.CreateInstance(Native("Waypoints.WaypointCollection"))!;
        AccessTools.Property(collection.GetType(), "Waypoints").SetValue(collection, pinned);
        using var bytes = new MemoryStream(); ProtoBuf.Serializer.NonGeneric.Serialize(bytes, collection);
        var state = Activator.CreateInstance(Native("Rendering.MapRenderState"), item)!;
        AccessTools.Method(state.GetType(), "SetWaypointData").Invoke(state, [bytes.ToArray()]);
        var visible = (IDictionary)AccessTools.Property(state.GetType(), "Waypoints").GetValue(state)!;
        check(!(bool)AccessTools.Property(copied.GetType(), "Pinned").GetValue(visible["old"])!, "Rendering legacy pinned markers must restore native map-boundary clipping.");
        AccessTools.Method(state.GetType(), "SetWaypointData").Invoke(state, [bytes.ToArray()]);
        AccessTools.Field(mod.GetType(), "sapi").SetValue(mod, null);
    }
}
