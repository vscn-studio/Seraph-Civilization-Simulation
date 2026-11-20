using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnCivilizationModSystem
{
    [ThreadStatic] private static PlacementContext? placementContext;
    private sealed class PlacementContext
    {
        public required IPlayer Player;
        public required VscnCivilizationModSystem System;
        public HashSet<string> Written = [];
        public Dictionary<string, BlockPos> Planned = [];
    }

    private void StartPlacementTracking(Harmony patch)
    {
        // Reflection can return inherited methods with ReflectedType set to the child.
        // Harmony requires the method as declared on its actual implementation type.
        static MethodInfo DeclaredImplementation(MethodInfo method) => AccessTools.DeclaredMethod(
            method.DeclaringType, method.Name, method.GetParameters().Select(p => p.ParameterType).ToArray())
            ?? throw new MissingMethodException(method.DeclaringType?.FullName, method.Name);
        var carry = AccessTools.TypeByName("CarryOn.Common.Services.CarryPlacementService");
        var carryPlacement = carry?.GetMethods().SingleOrDefault(m => m.Name == "TryPlaceDown" && m.GetParameters().Any(p => p.ParameterType == typeof(string).MakeByRefType()));
        if (carryPlacement != null && Harmony.GetPatchInfo(carryPlacement)?.Owners.Contains(patch.Id) != true)
            patch.Patch(carryPlacement, prefix: new HarmonyMethod(typeof(VscnCivilizationModSystem), nameof(BeforeCarriedPlacement)),
                finalizer: new HarmonyMethod(typeof(VscnCivilizationModSystem), nameof(AfterBuildingPlacement)));
        // Patch concrete overrides too: doors, beds and mod blocks need not call the base implementation.
        if (sapi!.World.Blocks == null) return;
        var methods = sapi.World.Blocks.Where(b => b != null).Select(b => b.GetType()).Append(typeof(Block))
            .Select(t => AccessTools.Method(t, nameof(Block.TryPlaceBlock), [typeof(IWorldAccessor), typeof(IPlayer), typeof(ItemStack), typeof(BlockSelection), typeof(string).MakeByRefType()]))
            .OfType<MethodInfo>().Select(DeclaredImplementation).Distinct();
        foreach (var method in methods.Where(m => Harmony.GetPatchInfo(m)?.Owners.Contains(patch.Id) != true)) patch.Patch(method,
            prefix: new HarmonyMethod(typeof(VscnCivilizationModSystem), nameof(BeforeBuildingPlacement)),
            finalizer: new HarmonyMethod(typeof(VscnCivilizationModSystem), nameof(AfterBuildingPlacement)));
        var checks = sapi.World.Blocks.Where(b => b != null).Select(b => b.GetType()).Append(typeof(Block))
            .Select(t => AccessTools.Method(t, nameof(Block.CanPlaceBlock), [typeof(IWorldAccessor), typeof(IPlayer), typeof(BlockSelection), typeof(string).MakeByRefType()]))
            .OfType<MethodInfo>().Select(DeclaredImplementation).Distinct();
        foreach (var method in checks.Where(m => Harmony.GetPatchInfo(m)?.Owners.Contains(patch.Id) != true))
            patch.Patch(method, prefix: new HarmonyMethod(typeof(VscnCivilizationModSystem), nameof(BeforeBuildingCanPlace)));
        foreach (var method in sapi.World.BlockAccessor.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => (m.Name == "SetBlock" || m.Name == "ExchangeBlock") && m.GetParameters().Any(p => p.ParameterType == typeof(BlockPos)))
            .Select(DeclaredImplementation).Distinct().Where(m => Harmony.GetPatchInfo(m)?.Owners.Contains(patch.Id) != true))
            patch.Patch(method, prefix: new HarmonyMethod(typeof(VscnCivilizationModSystem), nameof(BeforeBuildingWrite)),
                postfix: new HarmonyMethod(typeof(VscnCivilizationModSystem), nameof(AfterBuildingWrite)));
    }

    private static bool BeforeCarriedPlacement(Entity entity, object carriedBlock, BlockSelection selection, bool dropped, ref string failureCode,
        ref bool __result, out PlacementContext? __state)
    {
        __state = placementContext;
        var player = (entity as EntityPlayer)?.Player;
        if (!BeginBuildingPlacement(entity.World, player!, selection, ref failureCode, ref __result)) return false;
        if (placementContext is not { } context || ReferenceEquals(context, __state)) return true;
        var footprint = AccessTools.Method(AccessTools.TypeByName("CarryOn.Common.Logic.CarryFootprint"), "GetPlacementFootprint");
        if (footprint?.Invoke(null, [entity.World, entity, carriedBlock, selection, dropped]) is IEnumerable<BlockPos> positions)
            foreach (var pos in positions) context.Planned[WearKey(pos)] = pos.Copy();
        return CheckPlacementFootprint(context, ref failureCode, ref __result);
    }

    private static bool BeforeBuildingPlacement(Block __instance, IWorldAccessor __0, IPlayer __1, BlockSelection __3,
        ref string __4, ref bool __result, out PlacementContext? __state)
    {
        __state = placementContext;
        if (!BeginBuildingPlacement(__0, __1, __3, ref __4, ref __result)) return false;
        if (placementContext is not { } context || ReferenceEquals(context, __state)) return true;
        __instance.GetBehavior<BlockBehaviorMultiblock>()?.IterateOverEach(__3.Position, pos =>
        { context.Planned[WearKey(pos)] = pos.Copy(); return true; });
        return CheckPlacementFootprint(context, ref __4, ref __result);
    }

    private static bool BeginBuildingPlacement(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel,
        ref string failureCode, ref bool result)
    {
        var system = instance;
        if (system == null || system.disposed || byPlayer == null || blockSel == null || !ReferenceEquals(system.sapi?.World, world)) return true;
        if (placementContext != null) return true;
        string? failure = system.OutsiderPlacementFailure(byPlayer, blockSel.Position);
        if (failure != null) { failureCode = failure; result = false; return false; }
        placementContext = new() { Player = byPlayer, System = system };
        placementContext.Planned[WearKey(blockSel.Position)] = blockSel.Position.Copy();
        return true;
    }

    private static bool CheckPlacementFootprint(PlacementContext context, ref string failureCode, ref bool result)
    {
        string? failure = context.System.OutsiderFootprintFailure(context.Player, context.Planned.Values);
        if (failure == null) return true;
        failureCode = failure; result = false; return false;
    }

    private static bool BeforeBuildingCanPlace(IWorldAccessor __0, IPlayer __1, BlockSelection __2,
        ref string __3, ref bool __result)
    {
        if (placementContext is not { } context || context.Player != __1 || !ReferenceEquals(context.System.sapi?.World, __0)) return true;
        context.Planned[WearKey(__2.Position)] = __2.Position.Copy();
        return CheckPlacementFootprint(context, ref __3, ref __result);
    }

    private static void AfterBuildingPlacement(PlacementContext? __state)
    {
        var current = placementContext;
        placementContext = __state;
        if (current == null || ReferenceEquals(current, __state)) return;
        // Charge cooldown only after a real write; denied or failed attempts do not consume it.
        foreach (string key in current.Written)
            if (current.System.lifeState.PlacedBuildings.TryGetValue(key, out var placed) && placed.TerritoryId.Length > 0 && placed.TribeId.Length == 0)
                current.System.outsiderPlacementTimes[placed.TerritoryId + ":" + current.Player.PlayerUID] = AuthorizationNow;
    }

    private static void BeforeBuildingWrite(object[] __args, out (BlockPos Pos, int BlockId)? __state)
    {
        __state = null;
        var pos = __args.OfType<BlockPos>().FirstOrDefault();
        var system = instance;
        if (pos == null || system == null || system.disposed) return;
        if (placementContext == null && !system.lifeState.PlacedBuildings.ContainsKey(WearKey(pos))
            && !system.lifeState.BlockWear.ContainsKey(WearKey(pos)) && !system.lifeState.BuildingFires.ContainsKey(WearKey(pos))) return;
        __state = (pos.Copy(), system.sapi!.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Solid).Id);
    }

    private static void AfterBuildingWrite((BlockPos Pos, int BlockId)? __state)
    {
        if (__state is not { } before || instance is not { disposed: false } system) return;
        var pos = before.Pos;
        var block = system.sapi!.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Solid);
        if (block.Id == before.BlockId) return; // Fluid updates and no-op writes cannot change building ownership.
        if (block.Id == 0) { system.ClearBuildingState(pos); return; }
        if (placementContext is not { } context || context.System != system) return;
        string key = WearKey(pos);
        if (context.Written.Add(key)) system.ClearBuildingState(pos);
        system.RecordBuilding(context.Player, pos);
    }
}
