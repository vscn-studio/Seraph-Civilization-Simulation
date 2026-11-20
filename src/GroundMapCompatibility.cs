using System.Collections;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace SeraphCivilizationSimulation;

internal static class GroundMapCompatibility
{
    internal static void Install(Harmony harmony)
    {
        void Patch(string type, string method, string? prefix = null, string? postfix = null)
            => harmony.Patch(AccessTools.Method(AccessTools.TypeByName("ImmersiveMaps." + type), method),
                prefix == null ? null : new HarmonyMethod(typeof(GroundMapCompatibility), prefix),
                postfix == null ? null : new HarmonyMethod(typeof(GroundMapCompatibility), postfix));
        Patch("Items.ItemMap", "OnHeldInteractStart", nameof(MapInteract));
        Patch("Items.ItemMap", "CanRedraw", nameof(CanRedraw));
        Patch("Items.ItemMap", "DrawStroke", nameof(DrawStroke));
        Patch("Items.ItemMap", "GetHeldItemName", postfix: nameof(Name));
        Patch("Items.ItemMap", "GetHeldItemInfo", nameof(MapInfo), nameof(Info));
        Patch("Network.MapRequestHandler", "OnMapRotated", nameof(ServerRotation));
        Patch("ImmersiveMapsModSystem", "RotateHeldMap", nameof(ClientRotation));
        harmony.Patch(AccessTools.Method(typeof(CollectibleObject), nameof(CollectibleObject.OnHeldInteractStart)),
            prefix: new HarmonyMethod(typeof(GroundMapCompatibility), nameof(KnifeInteract)));
        Patch("Rendering.MapRenderSystem", "Render", nameof(Render));
        harmony.Patch(AccessTools.Method(AccessTools.TypeByName("ImmersiveMaps.Rendering.MapPainter"), "PaintWaypointLayer"),
            prefix: new HarmonyMethod(typeof(GroundMapCompatibility), nameof(BeforeWaypoints)),
            finalizer: new HarmonyMethod(typeof(GroundMapCompatibility), nameof(AfterWaypoints)));
        Patch("Rendering.MapPainter", "Replace", nameof(ReplaceBorder));
        // Older versions of the companion fix mod draw distance labels. This integration owns
        // the atlas-only HUD instead, with no distance text and no visibility outside the union.
        var oldHud = AccessTools.TypeByName("VscnServerModFix.ImmersiveMapWaypointRenderer");
        if (oldHud != null) harmony.Patch(AccessTools.Method(oldHud, "OnRenderFrame"), prefix: new HarmonyMethod(typeof(GroundMapCompatibility), nameof(DisableOldHud)));
    }
    private static bool DisableOldHud() => false;
    private static bool CanRedraw(ItemStack map, ref bool __result)
    { if (!GroundMapData.IsAtlas(map) && !map.Attributes.HasAttribute(MapDrawingDocument.Attribute)) return true; __result = false; return false; }
    private static bool DrawStroke(ItemStack map) => !GroundMapData.IsAtlas(map) && !map.Attributes.HasAttribute(MapDrawingDocument.Attribute);
    private static bool ServerRotation(IServerPlayer player)
        => !GroundMapData.IsAtlas(player.InventoryManager.ActiveHotbarSlot?.Itemstack);
    private static bool ClientRotation(object __instance, float delta, ref bool __result)
    {
        var client = (ICoreClientAPI?)AccessTools.Field(__instance.GetType(), "_capi").GetValue(__instance);
        var stack = client?.World.Player.InventoryManager.ActiveHotbarSlot?.Itemstack;
        if (stack == null || !GroundMapData.IsAtlas(stack)) return true;
        __result = GroundAtlasClient.Current?.RotateHeld(stack, delta) == true; return false;
    }
    private static void Name(ItemStack itemStack, ref string __result)
    {
        if (GroundMapData.IsAtlas(itemStack) && string.IsNullOrWhiteSpace(itemStack.Attributes.GetString("mapName")))
            __result = BlockGroundMap.Text(itemStack.Collectible.Code.Path == "map-papyrus" ? "atlas-papyrus" : "atlas-parchment");
    }
    private static void Info(ItemSlot inSlot, StringBuilder dsc)
    {
        if (inSlot.Itemstack is not { } stack || !GroundMapData.IsAtlas(stack)) return;
        // Keep paragraph breaks in their own VTML components. Native AfterWord wrapping
        // can insert empty rows (and lose characters) when truncating a long CJK token
        // that includes a trailing newline. A br does not attach that newline to the text.
        dsc.Append(BlockGroundMap.Text(stack.Collectible.Code.Path == "map-papyrus" ? "made-papyrus" : "made-parchment", stack.Attributes.GetTreeAttribute(GroundMapData.AtlasKey).GetInt("count"))).Append("<br>");
        dsc.Append(BlockGroundMap.Text("fixed")).Append("<br>");
        dsc.Append(BlockGroundMap.Text("atlas-place")).Append("<br>");
        dsc.Append(BlockGroundMap.Text("split")).Append("<br>");
    }
    private static bool MapInfo(ItemSlot inSlot, StringBuilder dsc)
    {
        if (inSlot.Itemstack is not { } stack) return true;
        // Both single sheets and atlases must omit geographic and debug coordinates.
        // Keep player names and lock information; do not alter recorded map data.
        if (!GroundMapData.IsAtlas(stack) && !string.IsNullOrWhiteSpace(stack.Attributes.GetString("mapName")))
            dsc.Append(stack.GetName()).Append("<br>");
        if (ImmersiveMapCompatibility.Locked(stack))
            dsc.Append(Vintagestory.API.Config.Lang.Get("immersivemaps:locked-by", stack.Attributes.GetString("mapLockedByName", ""))).Append("<br>");
        return false;
    }
    private static bool MapInteract(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, bool firstEvent, ref EnumHandHandling handling)
    {
        if (byEntity is not EntityPlayer entity || slot.Itemstack is not { } stack) return true;
        if (RecordedPaperMap.IsPaperMap(stack) && (byEntity.Controls.CtrlKey || GroundMapData.IsAtlas(stack)) && blockSel != null)
        {
            handling = EnumHandHandling.PreventDefault;
            if (GroundMapData.IsAtlas(stack) && blockSel.Face?.IsHorizontal == true)
            {
                // Only the client knows the held reading angle. A single request
                // carries both placement and angle, avoiding a native packet race.
                if (firstEvent && byEntity.World.Side == EnumAppSide.Client)
                    GroundAtlasClient.Current?.PlaceOnWall(stack, blockSel);
            }
            else if (firstEvent) BlockGroundMap.Place(byEntity.World, entity.Player, blockSel, slot);
            return false;
        }
        if (!GroundMapData.IsAtlas(stack)) return true;
        handling = EnumHandHandling.PreventDefault;
        return false;
    }
    private static bool KnifeInteract(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, bool firstEvent, ref EnumHandHandling handling)
    {
        if (slot.Itemstack?.Collectible.Code.ToString() == CivilizationMapInventory.NeedleCode && byEntity is EntityPlayer sewer
            && blockSel != null && byEntity.World.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityGroundMap maps)
        {
            handling = EnumHandHandling.PreventDefault;
            if (firstEvent) maps.Sew(sewer.Player);
            return false;
        }
        if (slot.Itemstack?.Collectible.Tool != EnumTool.Knife || byEntity is not EntityPlayer entity
            || byEntity.LeftHandItemSlot?.Itemstack is not { } atlas || !GroundMapData.IsAtlas(atlas)) return true;
        handling = EnumHandHandling.PreventDefault;
        if (!firstEvent || byEntity.World.Side != EnumAppSide.Server || !byEntity.Alive) return false;
        if (ImmersiveMapCompatibility.Locked(atlas)) { BlockGroundMap.Tell(byEntity.World, entity.Player, "locked"); return false; }
        var sheets = GroundMapData.Split(atlas, byEntity.World);
        if (sheets.Count == 0) return false;
        byEntity.LeftHandItemSlot.TakeOut(1); byEntity.LeftHandItemSlot.MarkDirty();
        slot.Itemstack.Collectible.DamageItem(byEntity.World, byEntity, slot, 1); slot.MarkDirty();
        foreach (var sheet in sheets) BlockGroundMap.Give(byEntity.World, entity.Player, sheet);
        return false;
    }
    internal static object? Property(object value, string name) => AccessTools.Property(value.GetType(), name).GetValue(value);
    internal static object? Call(object value, string name, params object?[] args) => AccessTools.Method(value.GetType(), name).Invoke(value, args);
    private static bool Render(object __instance, ItemStack stack, EnumItemRenderTarget target, ref ItemRenderInfo renderInfo)
    {
        if (!GroundMapData.IsAtlas(stack) || target == EnumItemRenderTarget.Gui) return true;
        var atlas = GroundAtlasClient.Current?.GetAtlas(__instance, stack, renderInfo.InSlot);
        if (atlas != null) { renderInfo.ModelRef = atlas; renderInfo.CullFaces = false; renderInfo.NormalShaded = false; }
        return false;
    }
    internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> BorderlessStates = new();
    [ThreadStatic] private static bool suppressBorder;
    private static void BeforeWaypoints(object state, out bool __state)
    { __state = suppressBorder; suppressBorder = BorderlessStates.TryGetValue(state, out _); }
    private static void AfterWaypoints(bool __state) => suppressBorder = __state;
    private static void ReplaceBorder(ref LoadedTexture source)
    { if (suppressBorder && GroundAtlasClient.Current is { } client) source = client.GetTransparent(source.Width, source.Height); }

    internal static bool Pin(IServerPlayer player, object packet)
    {
        var stack = player.InventoryManager.ActiveHotbarSlot?.Itemstack;
        if (player.Entity?.Alive != true || stack == null || !GroundMapData.IsAtlas(stack) || ImmersiveMapCompatibility.Locked(stack)
            || (int)Property(packet, "HotbarSlot")! != player.InventoryManager.ActiveHotbarSlotNumber) return false;
        var markers = ImmersiveMapCompatibility.Read(stack);
        string id = (string)Property(packet, "Id")!;
        if (id == null || !markers.Contains(id)) return false;
        var marker = markers[id]!;
        if (!GroundMapData.Contains(stack, Convert.ToDouble(Property(marker, "X")), Convert.ToDouble(Property(marker, "Z")))) return false;
        return true;
    }
}
