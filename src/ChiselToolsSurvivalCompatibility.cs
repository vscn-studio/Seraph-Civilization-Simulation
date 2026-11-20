using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace SeraphCivilizationSimulation;

// Optional QP integration. Keep registered assets so old worlds retain their blocks and inventories.
internal sealed class ChiselToolsSurvivalCompatibility : IDisposable
{
    private const string PatchId = "vsscsmod.chiseltools";
    private static readonly object SyncRoot = new();
    private static PropertyInfo availableModes = null!;
    private static int users;
    private bool disposed;

    private sealed record OperationContext(IWorldAccessor World, BlockPos Position, ItemSlot? Slot);

    public ChiselToolsSurvivalCompatibility()
    {
        lock (SyncRoot)
        {
            if (users == 0) Install();
            users++;
        }
    }

    private static void Install()
    {
        var harmony = new Harmony(PatchId);
        void Patch(string typeName, string methodName, string prefix)
        {
            var type = AccessTools.TypeByName(typeName) ?? throw new TypeLoadException(typeName);
            var method = AccessTools.DeclaredMethod(type, methodName) ?? throw new MissingMethodException(typeName, methodName);
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(ChiselToolsSurvivalCompatibility), prefix));
        }
        try
        {
            var pantograph = AccessTools.TypeByName("chisel.src.ItemPantograph") ?? throw new TypeLoadException("chisel.src.ItemPantograph");
            availableModes = AccessTools.Property(pantograph, "AvailableModes") ?? throw new MissingMemberException("AvailableModes");
            Patch("chisel.src.ItemPantograph", "OnHeldInteractStart", nameof(BeforePantographInteract));
            Patch("chisel.src.ItemPantograph", "SetToolMode", nameof(BeforeSetPantographMode));
            var pantographInteract = AccessTools.DeclaredMethod(pantograph, "OnHeldInteractStart");
            harmony.Patch(pantographInteract, postfix: new HarmonyMethod(typeof(ChiselToolsSurvivalCompatibility), nameof(AfterPantographInteract)));
            var pantographAttack = AccessTools.DeclaredMethod(pantograph, "OnHeldAttackStart");
            harmony.Patch(pantographAttack,
                prefix: new HarmonyMethod(typeof(ChiselToolsSurvivalCompatibility), nameof(BeforePantographInteract)),
                postfix: new HarmonyMethod(typeof(ChiselToolsSurvivalCompatibility), nameof(AfterPantographInteract)));
            harmony.Patch(AccessTools.DeclaredMethod(pantograph, "MakeUndoCopy"),
                postfix: new HarmonyMethod(typeof(ChiselToolsSurvivalCompatibility), nameof(AfterMakeUndoCopy)));
            Patch("chiseltools.src.ItemPalette", "OnHeldInteractStart", nameof(BeforePaletteInteract));
            Patch("chisel.src.ItemPaintBrush", "OnHeldInteractStart", nameof(BeforePaintBrushInteract));
            Patch("chiseltools.src.ItemPalette", "GetPaletteInk", nameof(BeforePaletteInk));
            Patch("chiseltools.src.ItemPalette", "GetPaletteBlockIds", nameof(BeforePaletteMaterials));
            Patch("chisel.src.ItemLadderMaker", "OnHeldInteractStart", nameof(BeforeDisabledTool));
            Patch("chiseltools.src.BlockDecoWaterSource", "OnBlockInteractStart", nameof(BeforeWaterSource));
            // TrueChisel inherits the native menu, which also serves ordinary game chisels.
            harmony.Patch(AccessTools.DeclaredMethod(typeof(ItemChisel), "GetToolModes"),
                postfix: new HarmonyMethod(typeof(ChiselToolsSurvivalCompatibility), nameof(AfterTrueChiselModes)));
            foreach (string name in new[] { "PantographDataToBlockEntity", "PasteNetHandler" })
            {
                var handler = AccessTools.DeclaredMethod(AccessTools.TypeByName("chisel.src.ChiselToolLoader"), name);
                harmony.Patch(handler,
                    prefix: new HarmonyMethod(typeof(ChiselToolsSurvivalCompatibility), nameof(BeforeVoxelPacket)),
                    postfix: new HarmonyMethod(typeof(ChiselToolsSurvivalCompatibility), nameof(AfterPantographInteract)));
            }
            Patch("chisel.src.BEFunctionChiseled", "AddState", nameof(BeforeDoorAddState));
        }
        catch (Exception ex)
        {
            harmony.UnpatchAll(PatchId);
            throw new InvalidOperationException("QP Chisel Tools survival integration failed.", ex);
        }
    }

    private static bool IsDisabled(AssetLocation? code) => code?.Domain == "chiseltools" &&
        (code.Path == "chiseledwatersource" || code.Path.StartsWith("laddermaker-", StringComparison.Ordinal)
         || code.Path.StartsWith("jonaspaintbrush-", StringComparison.Ordinal));

    internal static void FinalizeAssets(ICoreAPI api)
    {
        int removed = api.World.GridRecipes.RemoveAll(r => IsDisabled(r.Output?.ResolvedItemStack?.Collectible.Code ?? r.Output?.Code));
        removed += api.GetSmithingRecipes().RemoveAll(r => IsDisabled(r.Output?.ResolvedItemstack?.Collectible.Code ?? r.Output?.Code));
        foreach (var collectible in api.World.Collectibles)
        {
            var code = collectible.Code;
            if (code?.Domain == "chiseltools" && code.Path.StartsWith("halfcreativelight-", StringComparison.Ordinal))
            {
                byte[] light = collectible.LightHsv;
                if (light.Length >= 3 && light[2] > 5)
                {
                    light[2] = 5;
                    collectible.LightHsv = light;
                }
            }
            if (!IsDisabled(code)) continue;
            collectible.CreativeInventoryTabs = [];
            collectible.CreativeInventoryStacks = [];
            var attributes = collectible.Attributes?.Token?.DeepClone() as JObject ?? new JObject();
            var handbook = attributes["handbook"] as JObject ?? new JObject();
            handbook["exclude"] = true;
            attributes["handbook"] = handbook;
            collectible.Attributes = new JsonObject(attributes);
            if (code!.Path.StartsWith("jonaspaintbrush-", StringComparison.Ordinal))
                collectible.LightHsv = default;
        }
        var road = api.World.GetBlock(new AssetLocation("chiseltools:pathedchiseledblock"));
        if (road != null) road.WalkSpeedMultiplier = 1.3f;
        var ladder = api.World.GetBlock(new AssetLocation("chiseltools:climbablechiseledblock"));
        if (ladder != null) ladder.Climbable = false;
        api.Logger.Notification("Seraph Civilization Simulation: QP survival restrictions applied; removed {0} recipes.", removed);
    }

    private static void Notify(EntityAgent? entity, string reason)
    {
        if (entity?.Api is ICoreClientAPI capi)
            capi.TriggerIngameError(entity, "vscncs-qp-" + reason, Lang.Get("vsscsmod:qp." + reason));
    }

    private static bool Deny(EntityAgent? entity, string reason, ref EnumHandHandling handling)
    {
        handling = EnumHandHandling.PreventDefaultAction;
        Notify(entity, reason);
        return false;
    }

    private static bool BeforeDisabledTool(EntityAgent __1, ref EnumHandHandling __5) => Deny(__1, "disabled", ref __5);

    private static bool BeforeWaterSource(IPlayer __1, ref bool __result)
    {
        Notify(__1?.Entity, "disabled");
        __result = true; // Consume interaction, including attempts with filled containers.
        return false;
    }

    private static bool BeforePaletteInteract(ItemSlot __0, EntityAgent __1, ref EnumHandHandling __5)
    {
        if (IsDisabled(__0?.Itemstack?.Collectible.Code)) return Deny(__1, "disabled", ref __5);
        return true;
    }

    private static bool BeforePaintBrushInteract(ItemSlot __0, EntityAgent __1, ref EnumHandHandling __5)
    {
        if (!BeforePaletteInteract(__0, __1, ref __5)) return false;
        if (__1?.Controls.ShiftKey == true) return Deny(__1, "material-add", ref __5);
        return true;
    }

    private static bool BeforePaletteInk(ItemSlot __1, ref int __result)
    {
        if (!IsDisabled(__1?.Itemstack?.Collectible.Code)) return true;
        __result = -1;
        return false;
    }

    private static bool BeforePaletteMaterials(ItemSlot __1, ref int[] __result)
    {
        if (!IsDisabled(__1?.Itemstack?.Collectible.Code)) return true;
        __result = [];
        return false;
    }

    private static void AfterTrueChiselModes(CollectibleObject __instance, SkillItem[]? __result)
    {
        if (__instance.GetType().FullName != "chiseltools.src.TrueChisel" || __result == null) return;
        for (int i = 7; i <= 10 && i < __result.Length; i++) __result[i].Enabled = true;
    }

    private static string? Mode(object tool, ItemSlot? slot, int? selected = null)
    {
        var modes = (string[])availableModes.GetValue(tool)!;
        int index = selected ?? slot?.Itemstack?.Attributes.GetInt("toolMode") ?? 0;
        return index >= 0 && index < modes.Length ? modes[index] : null;
    }

    private static bool BeforeSetPantographMode(object __instance, ItemSlot __0, IPlayer __1, BlockSelection __2, int __3)
    {
        string? mode = Mode(__instance, __0, __3);
        if (mode == null) return false;
        if (mode != "UNDO" || __1?.Entity?.World is not { Side: EnumAppSide.Server }) return true;

        TryRestoreUndo(__0, __1);
        int lastMode = Math.Clamp(__0.Itemstack?.Attributes.GetInt("lastToolMode", 0) ?? 0,
            0, ((string[])availableModes.GetValue(__instance)!).Length - 1);
        if (__0.Itemstack != null) __0.Itemstack.Attributes.SetInt("toolMode", lastMode);
        __0.MarkDirty();
        return false;
    }

    private static bool CanModify(IWorldAccessor world, IPlayer? player, BlockPos position) => player != null
        && VscnCivilizationModSystem.BuildingToolAllowed(world, player, position)
        && world.Claims.TryAccess(player, position, EnumBlockAccessFlags.BuildOrBreak)
        && world.Api.ModLoader.GetModSystem<ModSystemBlockReinforcement>()?.IsReinforced(position) != true;

    // Observe native operations only to refresh selection boxes and remove empty blocks.
    // QP owns all modes, material use and voxel changes; no volume budget or rollback is applied.
    private static bool BeforePantographInteract(ItemSlot __0, EntityAgent __1, BlockSelection __2,
        out OperationContext? __state)
    {
        __state = __1?.World is { } world && __2?.Position != null && CanModify(world, (__1 as EntityPlayer)?.Player, __2.Position)
            ? new OperationContext(world, __2.Position.Copy(), __0) : null;
        return __2?.Position == null || __1?.World == null
            || VscnCivilizationModSystem.BuildingToolAllowed(__1.World, (__1 as EntityPlayer)?.Player, __2.Position);
    }

    private static void AfterPantographInteract(OperationContext? __state)
    {
        if (__state == null) return;
        var target = __state.World.BlockAccessor.GetBlockEntity(__state.Position) as BlockEntityMicroBlock;
        if (target is BlockEntityChisel && target.VoxelCuboids is { Count: 0 })
        {
            if (__state.World.Side == EnumAppSide.Server) MarkUndoEmptyAllowed(__state.Slot, __state.Position);
            __state.World.BlockAccessor.SetBlock(0, __state.Position);
        }
        else if (target != null)
        {
            target.RegenSelectionBoxes(__state.World, null);
        }
        else if (__state.World.Side == EnumAppSide.Server
            && __state.World.BlockAccessor.GetBlock(__state.Position).Id == 0)
        {
            MarkUndoEmptyAllowed(__state.Slot, __state.Position);
        }
    }

    private static void AfterMakeUndoCopy(ICoreAPI __0, ItemSlot __1, BlockPos __2)
    {
        if (__0.Side != EnumAppSide.Server || __1.Itemstack == null) return;
        __1.Itemstack.Attributes.SetInt("vscncsundoblockid", __0.World.BlockAccessor.GetBlock(__2).BlockId);
        __1.Itemstack.Attributes.SetBool("vscncsundoemptyallowed", false);
        __1.MarkDirty();
    }

    private static void MarkUndoEmptyAllowed(ItemSlot? slot, BlockPos position)
    {
        if (slot?.Itemstack == null) return;
        var undoPosition = TreeAttributeUtil.GetBlockPos(slot.Itemstack.Attributes, "undoposition", null);
        if (undoPosition == null || !undoPosition.Equals(position)) return;
        slot.Itemstack.Attributes.SetBool("vscncsundoemptyallowed", true);
        slot.MarkDirty();
    }

    private static void TryRestoreUndo(ItemSlot slot, IPlayer player)
    {
        if (player?.Entity?.World is not { Side: EnumAppSide.Server } world || slot.Itemstack == null) return;
        List<uint>? voxels;
        List<int>? materials;
        BlockPos? position;
        try
        {
            voxels = SerializerUtil.Deserialize<List<uint>>(slot.Itemstack.Attributes.GetBytes("undovoxels"));
            materials = SerializerUtil.Deserialize<List<int>>(slot.Itemstack.Attributes.GetBytes("undomats"));
            position = TreeAttributeUtil.GetBlockPos(slot.Itemstack.Attributes, "undoposition", null);
        }
        catch { return; }
        if (voxels == null || voxels.Count == 0 || materials == null || materials.Count == 0 || position == null)
            return;
        if (!CanModify(world, player, position)) { Notify(player.Entity, "no-access"); return; }

        var blockEntity = world.BlockAccessor.GetBlockEntity(position);
        if (blockEntity is not BlockEntityMicroBlock target)
        {
            int previousBlockId = slot.Itemstack.Attributes.GetInt("vscncsundoblockid", 0);
            if (!slot.Itemstack.Attributes.GetBool("vscncsundoemptyallowed")
                || world.BlockAccessor.GetBlock(position).Id != 0 || previousBlockId <= 0
                || world.GetBlock(previousBlockId) is not BlockChisel)
                return;
            world.BlockAccessor.SetBlock(previousBlockId, position);
            if (world.BlockAccessor.GetBlockEntity(position) is not BlockEntityChisel restoredTarget) return;
            target = restoredTarget;
        }
        target.BlockIds = materials.ToArray();
        target.VoxelCuboids = voxels.ToList();
        target.RegenSelectionBoxes(world, null);
        target.MarkDirty(true);
        slot.Itemstack.Attributes.SetBool("vscncsundoemptyallowed", false);
        slot.MarkDirty();
    }

    private static bool BeforeVoxelPacket(IPlayer __0, object __1, out OperationContext? __state)
    {
        __state = null;
        if (__0?.Entity?.World is not { } world || __1 == null) return false;
        var position = AccessTools.Field(__1.GetType(), "pos")?.GetValue(__1) as BlockPos;
        if (position == null || !CanModify(world, __0, position)) return false;
        __state = new OperationContext(world, position.Copy(), __0.InventoryManager?.ActiveHotbarSlot);
        return true;
    }

    // Protobuf omits empty repeated fields; QP expects a usable list for empty door shapes.
    private static void BeforeDoorAddState(ref List<uint> __1) => __1 ??= [];

    public void Dispose()
    {
        lock (SyncRoot)
        {
            if (disposed) return;
            disposed = true;
            if (--users == 0) new Harmony(PatchId).UnpatchAll(PatchId);
        }
    }
}
