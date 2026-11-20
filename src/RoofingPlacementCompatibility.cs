using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace SeraphCivilizationSimulation;

// Optional integration: keep Roofing's placement rules, but remove absolute direction locks.
internal sealed class RoofingPlacementCompatibility : IDisposable
{
    private const string PatchId = "vsscsmod.roofing";
    private const string OrientationAttribute = "vsroofing.orientation";
    private const string PageAttribute = "vsroofing.page";
    private const string ReverseAttribute = "vscncivilizationsimulation.roofingReverse";
    private const int StandardModeCount = 10;
    private static readonly object SyncRoot = new();
    private static int users;
    private bool disposed;

    public RoofingPlacementCompatibility()
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
        try
        {
            var behavior = AccessTools.TypeByName("VSRoofing.CollectibleBehaviorPlaceFrame")
                ?? throw new MissingMemberException("VSRoofing.CollectibleBehaviorPlaceFrame");
            var block = AccessTools.TypeByName("VSRoofing.RoofBlock")
                ?? throw new MissingMemberException("VSRoofing.RoofBlock");
            var getModes = AccessTools.DeclaredMethod(behavior, "GetToolModes",
                [typeof(ItemSlot), typeof(IClientPlayer), typeof(BlockSelection)])
                ?? throw new MissingMethodException(behavior.FullName, "GetToolModes");
            var setMode = AccessTools.DeclaredMethod(behavior, "SetToolMode",
                [typeof(ItemSlot), typeof(IPlayer), typeof(BlockSelection), typeof(int)])
                ?? throw new MissingMethodException(behavior.FullName, "SetToolMode");
            var placeFrame = AccessTools.DeclaredMethod(block, "PlaceFrame",
                [typeof(IWorldAccessor), typeof(BlockSelection), typeof(IPlayer), typeof(ItemStack)])
                ?? throw new MissingMethodException(block.FullName, "PlaceFrame");

            harmony.Patch(getModes,
                prefix: new HarmonyMethod(typeof(RoofingPlacementCompatibility), nameof(BeforeGetToolModes)),
                postfix: new HarmonyMethod(typeof(RoofingPlacementCompatibility), nameof(AfterGetToolModes)));
            harmony.Patch(setMode, prefix: new HarmonyMethod(typeof(RoofingPlacementCompatibility), nameof(BeforeSetToolMode)));
            harmony.Patch(placeFrame,
                prefix: new HarmonyMethod(typeof(RoofingPlacementCompatibility), nameof(BeforePlaceFrame)),
                transpiler: new HarmonyMethod(typeof(RoofingPlacementCompatibility), nameof(ReplacePlacementOrientation)));
            // Like Roofing's own tool settings, this preference must not prevent material stacks from merging.
            if (!GlobalConstants.IgnoredStackAttributes.Contains(ReverseAttribute))
                GlobalConstants.IgnoredStackAttributes = [..GlobalConstants.IgnoredStackAttributes, ReverseAttribute];
        }
        catch (Exception ex)
        {
            harmony.UnpatchAll(PatchId);
            throw new InvalidOperationException("VS Roofing player-facing placement integration failed.", ex);
        }
    }

    private static void ClearOrientation(ItemSlot? slot)
    {
        if (slot?.Itemstack?.Attributes.HasAttribute(OrientationAttribute) != true) return;
        slot.Itemstack.Attributes.RemoveAttribute(OrientationAttribute);
        slot.MarkDirty();
    }

    private static void BeforeGetToolModes(ItemSlot __0) => ClearOrientation(__0);

    private static void AfterGetToolModes(ItemSlot __0, LoadedTexture[] ___orientationTextures, ref SkillItem[]? __result)
    {
        if (__0?.Itemstack?.Attributes.GetAsInt(PageAttribute) != 0 || __result == null
            || __result.Length < 14) return;
        // Filter the returned menu only: Roofing still indexes its original 14-item array when updating icons.
        // Reuse the hidden south arrow and its native inactive/active textures without allocating GPU resources.
        var reverse = __result[12];
        reverse.Name = Lang.Get("vsscsmod:roofing.face-away");
        reverse.Linebreak = true;
        reverse.Texture = ___orientationTextures[__0.Itemstack.Attributes.GetBool(ReverseAttribute) ? 6 : 2];
        __result = __result[..(StandardModeCount + 1)];
        __result[StandardModeCount] = reverse;
    }

    private static bool BeforeSetToolMode(ItemSlot __0, IPlayer __1, int __3)
    {
        ClearOrientation(__0);
        if (__0?.Itemstack == null) return false;
        if (__0.Itemstack.Attributes.GetAsInt(PageAttribute) == 0 && __3 == StandardModeCount)
        {
            bool reverse = !__0.Itemstack.Attributes.GetBool(ReverseAttribute);
            if (reverse) __0.Itemstack.Attributes.SetBool(ReverseAttribute, true);
            else __0.Itemstack.Attributes.RemoveAttribute(ReverseAttribute);
            __0.MarkDirty();
            __1.Entity.World.Api.Event.PushEvent("keepopentoolmodedlg");
            return false;
        }
        // Advanced pages also use indices >= 10 for slope options and their back button.
        return __0.Itemstack.Attributes.GetAsInt(PageAttribute) != 0 || __3 < StandardModeCount;
    }

    private static void BeforePlaceFrame(ItemStack __3)
    {
        // Run on both sides, including for old stacks that have never opened the tool menu.
        // Without this attribute, Roofing uses Block.SuggestedHVOrientation(player, selection).
        __3?.Attributes.RemoveAttribute(OrientationAttribute);
    }

    private static IEnumerable<CodeInstruction> ReplacePlacementOrientation(IEnumerable<CodeInstruction> instructions)
    {
        var original = AccessTools.Method(typeof(Block), nameof(Block.SuggestedHVOrientation),
            [typeof(IPlayer), typeof(BlockSelection)]);
        var replacement = AccessTools.Method(typeof(RoofingPlacementCompatibility), nameof(RelativeOrientation));
        int replaced = 0;
        foreach (var instruction in instructions)
        {
            if (!instruction.Calls(original)) { yield return instruction; continue; }
            var stack = new CodeInstruction(OpCodes.Ldarg_S, (byte)4);
            stack.labels.AddRange(instruction.labels);
            stack.blocks.AddRange(instruction.blocks);
            yield return stack;
            yield return new CodeInstruction(OpCodes.Call, replacement);
            replaced++;
        }
        if (replaced != 1) throw new InvalidOperationException("Roofing placement orientation call was not found exactly once.");
    }

    private static BlockFacing[] RelativeOrientation(IPlayer player, BlockSelection selection, ItemStack stack)
    {
        var facing = Block.SuggestedHVOrientation(player, selection);
        if (stack.Attributes.GetBool(ReverseAttribute)) facing[0] = facing[0].Opposite;
        return facing;
    }

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
