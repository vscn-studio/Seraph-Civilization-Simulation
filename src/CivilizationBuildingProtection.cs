using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnCivilizationModSystem
{
    private Harmony? buildingHarmony;
    private long buildingListener;
    private readonly Dictionary<string, FireExposure> fireExposures = [];
    private readonly HashSet<string> activeBuildingFires = [];
    private readonly Dictionary<string, double> arrowHeat = [];
    private sealed record FireExposure(double Until, string Owner, bool Strong);
    private static readonly FieldInfo FireOwner = AccessTools.Field(typeof(BEBehaviorBurning), "startedByPlayerUid");
    private static MethodInfo? arrowMaterial;
    private static MethodInfo? arrowBurning;

    private static bool WearMatches(TerritoryWear wear, Block block) => wear.BlockCode.Length > 0
        ? wear.BlockCode == block.Code?.ToString() : wear.BlockId == block.Id;

    private static VscnCivilizationModSystem? ProtectionSystem(IWorldAccessor world) => instance is { disposed: false } system
        && ReferenceEquals(system.sapi?.World, world) && system.config.BuildingProtection.Enabled ? system : null;

    private void ClearBuildingState(BlockPos pos) => ClearBuildingDamage(pos, true);
    private void ClearBuildingDamage(BlockPos pos, bool forgetOwnership)
    {
        string key = WearKey(pos);
        lifeState.BlockWear.Remove(key); lifeState.BuildingFires.Remove(key);
        if (forgetOwnership) lifeState.PlacedBuildings.Remove(key);
        activeBuildingFires.Remove(key); fireExposures.Remove(key); arrowHeat.Remove(key);
    }

    private double ConsumeBuildingReserve(BlockPos pos, IPlayer? player, double amount)
    {
        var extra = TerritoryReinforcement(player, pos);
        if (extra.TribeId.Length == 0 || amount <= 0) return 0;
        var tribe = tribeState.Find(extra.TribeId)!;
        int capacity = (int)Math.Min(config.Territory.ReinforcementMaximum, (long)LivingMembers(tribe) * config.Territory.ReinforcementPerLivingMember);
        string key = WearKey(pos);
        var block = sapi!.World.BlockAccessor.GetBlock(pos);
        if (!lifeState.BlockWear.TryGetValue(key, out var wear) || !WearMatches(wear, block))
            lifeState.BlockWear[key] = wear = new() { BlockId = block.Id, BlockCode = block.Code?.ToString() ?? "", TribeId = tribe.Id, Capacity = capacity };
        wear.TribeId = tribe.Id;
        // Even after exhaustion, continued effective attacks postpone recovery.
        return BuildingProtectionRules.Consume(wear, capacity, amount, lifeState.ProtectionSeconds);
    }

    private void StartBuildingProtection()
    {
        var patch = buildingHarmony = new Harmony("vsscsmod.buildingprotection");
        void Patch(Type type, string method, string prefix, Type[]? args = null) => patch.Patch(
            (args == null ? AccessTools.DeclaredMethod(type, method) : AccessTools.Method(type, method, args)) ?? throw new MissingMethodException(type.FullName, method),
            prefix: new HarmonyMethod(typeof(VscnCivilizationModSystem), prefix) { priority = Priority.First });
        try
        {
            Patch(typeof(ModSystemBlockReinforcement), "StrengthenBlock", nameof(AuthorizationReinforce));
            Patch(typeof(ModSystemBlockReinforcement), "TryLock", nameof(AuthorizationLock));
            StartPlacementTracking(patch);
            Patch(typeof(BEBehaviorBurning), "canBurn", nameof(BuildingCanBurn));
            Patch(typeof(BEBehaviorBurning), "getBurnDuration", nameof(BuildingBurnDuration));
            Patch(typeof(BEBehaviorBurning), "OnTick", nameof(BuildingFireTick));
            Patch(typeof(BEBehaviorBurning), "KillFire", nameof(BuildingFireDeath));
            Patch(typeof(BEBehaviorBurning), "TrySpreadTo", nameof(BuildingSpreadAccess));
            Patch(typeof(BlockBehaviorReinforcable), "OnBlockExploded", nameof(BuildingExplosion));
            Patch(typeof(BlockBehaviorReinforcable), "OnBlockBroken", nameof(BuildingMiningAttack));
            var fire = AccessTools.TypeByName("Vscn.FireArrows.FireEnvironment");
            var state = AccessTools.TypeByName("Vscn.FireArrows.FireState");
            var materials = AccessTools.TypeByName("Vscn.FireArrows.FireMaterials");
            arrowMaterial = materials == null ? null : AccessTools.Method(materials, "At");
            arrowBurning = state == null ? null : AccessTools.Method(state, "Burning");
            if (fire != null) Patch(fire, "IgniteBlock", nameof(BuildingArrowContact));
            var pruning = AccessTools.TypeByName("Pyrogenesis.TreeMechanics");
            if (pruning != null) Patch(pruning, "BreakTreeBlock", nameof(BuildingTreePruning));
            var logging = AccessTools.TypeByName("LoggingMod.FellingListener");
            if (logging != null) Patch(logging, "OnBreakBlock", nameof(BuildingFellingAccess));
            var carry = AccessTools.TypeByName("CarryOn.Common.Services.CarryPermissionService");
            if (carry != null)
            {
                var permission = carry.GetMethods().Single(m => m.Name == "HasPermissionAt" && m.GetParameters().Length == 4);
                patch.Patch(permission, prefix: new HarmonyMethod(typeof(VscnCivilizationModSystem), nameof(BuildingCarryAccess)));
            }
            foreach (string method in new[] { "OnHeldAttackStart", "OnHeldInteractStart" })
                Patch(typeof(ItemChisel), method, nameof(BuildingChiselAccess));
            var roofing = AccessTools.TypeByName("VSRoofing.RoofBlock");
            if (roofing != null)
            {
                Patch(roofing, "OnBlockBroken", nameof(BuildingRoofBreak));
                Patch(roofing, "OnBlockInteractStart", nameof(BuildingRoofInteract));
            }
            buildingListener = sapi!.Event.RegisterGameTickListener(TickBuildingProtection, 250);
            sapi.Logger.Notification("Seraph Civilization Simulation: shared building protection, recovery and material fire enabled; Fire Arrows={0}, Pyrogenesis={1}.", fire != null, pruning != null);
        }
        catch { StopBuildingProtection(); throw; }
    }

    private void StopBuildingProtection()
    {
        if (sapi != null)
        {
            if (buildingListener != 0) sapi.Event.UnregisterGameTickListener(buildingListener);
        }
        buildingHarmony?.UnpatchAll(buildingHarmony.Id); buildingHarmony = null;
        activeBuildingFires.Clear(); fireExposures.Clear(); arrowHeat.Clear();
    }

    internal static bool BuildingToolAllowed(IWorldAccessor world, IPlayer? player, BlockPos pos)
    {
        var system = ProtectionSystem(world);
        return system == null || system.TerritoryReinforcement(player, pos).Strength <= 0;
    }

    private static bool BuildingFellingAccess(IServerPlayer byPlayer, BlockSelection blockSel)
    {
        var world = byPlayer.Entity.World;
        return ProtectionSystem(world) == null || (BuildingToolAllowed(world, byPlayer, blockSel.Position)
            && world.Api.ModLoader.GetModSystem<ModSystemBlockReinforcement>()?.GetReinforcment(blockSel.Position)?.Strength is not > 0);
    }

    private static bool BuildingCarryAccess(Entity entity, BlockPos pos, object[] __args, System.Reflection.MethodBase __originalMethod, ref bool __result)
    {
        if (BuildingToolAllowed(entity.World, (entity as EntityPlayer)?.Player, pos)) return true;
        __args[2] = Enum.Parse(__originalMethod.GetParameters()[2].ParameterType.GetElementType()!, "Reinforced");
        __result = false; return false;
    }

    private static bool BuildingChiselAccess(EntityAgent byEntity, BlockSelection blockSel, ref EnumHandHandling handling)
    {
        if (blockSel?.Position == null || BuildingToolAllowed(byEntity.World, (byEntity as EntityPlayer)?.Player, blockSel.Position)) return true;
        handling = EnumHandHandling.PreventDefaultAction; return false;
    }

    private static bool BuildingRoofInteract(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref bool __result)
    {
        if (BuildingToolAllowed(world, byPlayer, blockSel.Position)) return true;
        __result = false; return false;
    }

    private static bool BuildingRoofBreak(Block __instance, IWorldAccessor world, BlockPos pos, IPlayer byPlayer)
    {
        var system = ProtectionSystem(world);
        if (system == null || byPlayer == null) return true;
        int extra = system.TerritoryReinforcement(byPlayer, pos).Strength;
        var native = world.Api.ModLoader.GetModSystem<ModSystemBlockReinforcement>();
        if (extra <= 0 && native?.GetReinforcment(pos)?.Strength is not > 0) return true;
        EnumHandling handling = EnumHandling.PassThrough;
        new BlockBehaviorReinforcable(__instance).OnBlockBroken(world, pos, byPlayer, 1, ref handling);
        return false;
    }

    private static bool StructuralFire(BEBehaviorBurning fire) => fire.Blockentity.Block?.Code?.Path == "fire";

    private static void BuildingMiningAttack(IWorldAccessor world, BlockPos pos, IPlayer byPlayer)
    {
        var system = ProtectionSystem(world);
        if (system == null || byPlayer == null || byPlayer.HasPrivilege("denybreakreinforced")) return;
        if (system.TerritoryReinforcement(byPlayer, pos).Strength == 0) system.ConsumeBuildingReserve(pos, byPlayer, 1);
    }

    internal static void FinalizeBuildingAssets(ICoreAPI api)
    {
        if (api.Side == EnumAppSide.Server && instance?.buildingHarmony is { } placementPatch)
            instance.StartPlacementTracking(placementPatch);
        foreach (var block in api.World.Blocks)
        {
            if (block.Id == 0 || block.IsLiquid() || block.Replaceable >= 6000
                || block.BlockMaterial is EnumBlockMaterial.Fire or EnumBlockMaterial.Lava
                || block.HasBehavior<BlockBehaviorReinforcable>()) continue;
            var behavior = new BlockBehaviorReinforcable(block);
            block.BlockBehaviors = [behavior, .. block.BlockBehaviors];
            block.CollectibleBehaviors = [behavior, .. block.CollectibleBehaviors];
        }
    }

    private static bool BuildingExplosion(IWorldAccessor world, BlockPos pos, ref EnumHandling handling)
    {
        var system = ProtectionSystem(world);
        if (system == null) return true;
        bool protectedBlock = system.TerritoryReinforcement(null, pos).Strength > 0;
        system.ConsumeBuildingReserve(pos, null, 2);
        if (!protectedBlock) return true;
        handling = EnumHandling.PreventDefault; world.BlockAccessor.MarkBlockDirty(pos); return false;
    }

    private static bool BuildingTreePruning(BlockPos pos)
    {
        // Pyrogenesis can prune a whole tree through SetBlock, including placed logs.
        // In tribal territory all such loss must come from sustained exposure.
        var system = instance;
        if (system?.sapi == null || ProtectionSystem(system.sapi.World) == null) return true;
        return !system.tribeState.Tribes.Any(t => system.WithinTerritory(t, pos.X, pos.Y, pos.Z, pos.dimension));
    }

    private bool FireAccess(string uid, BlockPos pos)
    {
        var world = sapi!.World;
        var player = string.IsNullOrEmpty(uid) ? null : world.PlayerByUid(uid);
        if (player != null) return !player.HasPrivilege("denybreakreinforced")
            && world.Claims.TestAccess(player, pos, EnumBlockAccessFlags.BuildOrBreak) == EnumWorldAccessResponse.Granted;
        // Offline fires continue on unclaimed tribal land; native claims stay closed.
        return !world.Claims.All.Any(c => c.PositionInside(pos));
    }

    private static bool BuildingCanBurn(BEBehaviorBurning __instance, BlockPos pos, ref bool __result)
    {
        var system = ProtectionSystem(__instance.Api.World);
        if (system == null || !StructuralFire(__instance)) return true;
        __result = system.FireProfile(pos) != null && system.FireAccess((string?)FireOwner.GetValue(__instance) ?? "", pos);
        return false;
    }

    private static bool BuildingBurnDuration(BEBehaviorBurning __instance, BlockPos pos, ref float __result)
    {
        var system = ProtectionSystem(__instance.Api.World);
        if (system == null || !StructuralFire(__instance)) return true;
        __result = system.FireProfile(pos) == null ? 0 : 8;
        return false;
    }

    private static bool BuildingSpreadAccess(BEBehaviorBurning __instance, BlockPos pos, ref bool __result)
    {
        var system = ProtectionSystem(__instance.Api.World);
        if (system == null || !StructuralFire(__instance)) return true;
        // Material exposure below owns spread. Native spread would create a new
        // pilot flame on cold wood and repeatedly restart its heating period.
        __result = false; return false;
    }

    private bool IsIgnited(BlockPos pos) => lifeState.BuildingFires.TryGetValue(WearKey(pos), out var fire) && fire.Ignited;

    private static bool BuildingFireTick(BEBehaviorBurning __instance, float dt)
    {
        var system = ProtectionSystem(__instance.Api.World);
        if (system == null || !StructuralFire(__instance) || __instance.Api.Side != EnumAppSide.Server) return true;
        var pos = __instance.FuelPos;
        string uid = (string?)FireOwner.GetValue(__instance) ?? "";
        if (pos == null || system.FireProfile(pos) == null || !system.FireAccess(uid, pos))
        { __instance.KillFire(false); return false; }
        system.ExposeBuilding(pos, uid, true, .75);
        bool ignited = system.IsIgnited(pos);
        if (ignited) __instance.remainingBurnDuration = 8;
        else if ((__instance.remainingBurnDuration -= dt) <= 0) { __instance.KillFire(false); return false; }
        __instance.OnFireTick?.Invoke(dt);
        // An established fire supplies heat to adjacent material. A pilot flame
        // expires after eight seconds unless another fire keeps heating the fuel.
        if (ignited && __instance.AllowFireSpread)
            foreach (var face in BlockFacing.ALLFACES)
            {
                var adjacent = pos.AddCopy(face);
                if (system.FireProfile(adjacent) != null && system.FireAccess(uid, adjacent))
                    system.ExposeBuilding(adjacent, uid, true, .75);
            }
        // Native slow ticks retain rain/water extinguishing and entity damage.
        return false;
    }

    private static void BuildingFireDeath(BEBehaviorBurning __instance, ref bool consumeFuel)
    {
        var system = ProtectionSystem(__instance.Api.World);
        if (system == null || !StructuralFire(__instance) || __instance.FuelPos == null) return;
        // The simulation is the sole authority for removing structural fuel.
        consumeFuel = false;
        string key = WearKey(__instance.FuelPos);
        system.fireExposures.Remove(key);
        system.arrowHeat.Remove(key);
        // Extinguishing removes transient heat, never permanent burn damage.
        // Other live sources may register a fresh exposure on their next tick.
        if (system.lifeState.BuildingFires.TryGetValue(key, out var state)) { state.Ignited = false; state.Heat = 0; }
    }

    private static bool BuildingArrowContact(EntityProjectileBase arrow, BlockPos fuel, object config)
    {
        var system = ProtectionSystem(arrow.World);
        if (system == null) return true;
        if (arrow.Api.Side != EnumAppSide.Server || arrowBurning?.Invoke(null, [arrow.ProjectileStack]) is not true
            || AccessTools.Property(config.GetType(), "IgniteBlocks")?.GetValue(config) is not true) return false;
        string uid = (arrow.FiredBy as EntityPlayer)?.PlayerUID ?? arrow.WatchedAttributes.GetString("vscnFireOwner", "");
        if (!system.FireAccess(uid, fuel) || system.FireProfile(fuel) == null) return false;
        // Use elapsed server time, not callback count. One arrow has at most a
        // fixed contact budget even after chunk unload or repeated collisions.
        string key = "vscncsHeat:" + WearKey(fuel);
        double used = arrow.Attributes.GetDouble(key);
        long now = arrow.World.ElapsedMilliseconds;
        long last = arrow.Attributes.GetLong(key + ":last", now - 20);
        double elapsed = Math.Clamp((now - last) / 1000.0, 0, .1);
        arrow.Attributes.SetLong(key + ":last", now);
        if (used >= system.config.BuildingProtection.ArrowContactSeconds || elapsed <= 0) return false;
        arrow.Attributes.SetDouble(key, used + elapsed);
        system.ExposeBuilding(fuel, uid, false, .5);
        string heatKey = WearKey(fuel);
        system.arrowHeat[heatKey] = Math.Min(.5, system.arrowHeat.GetValueOrDefault(heatKey) + elapsed);
        return false;
    }

    private void ExposeBuilding(BlockPos pos, string uid, bool strong, double duration)
    {
        if (FireProfile(pos) == null) return;
        string key = WearKey(pos);
        var block = sapi!.World.BlockAccessor.GetBlock(pos);
        if (!lifeState.BuildingFires.TryGetValue(key, out var state) || state.BlockCode != block.Code?.ToString())
            lifeState.BuildingFires[key] = state = new() { X = pos.X, Y = pos.Y, Z = pos.Z, Dimension = pos.dimension, BlockCode = block.Code?.ToString() ?? "", OwnerUid = uid };
        if (!fireExposures.TryGetValue(key, out var existing) || existing.Until < lifeState.ProtectionSeconds || strong || !existing.Strong)
            fireExposures[key] = new(lifeState.ProtectionSeconds + duration, uid, strong);
        activeBuildingFires.Add(key);
    }

    private sealed record MaterialFire(double Ignition, double Failure);
    private MaterialFire? FireProfile(BlockPos pos)
    {
        var world = sapi!.World;
        var block = world.BlockAccessor.GetBlock(pos);
        if (block.Id == 0 || block.IsLiquid() || block.Attributes?["smothersFire"].AsBool() == true) return null;
        var material = arrowMaterial?.Invoke(null, [world, pos]) is EnumBlockMaterial inferred ? inferred : block.GetBlockMaterial(world.BlockAccessor, pos);
        string code = block.Code?.Path ?? "";
        // Roofing overrides GetBlockMaterial using its covering and frame data.
        // Item ingredients such as metal sheets must not be guessed as grass.
        if (block.Code?.Domain == "vsroofing") material = block.GetBlockMaterial(world.BlockAccessor, pos);
        var settings = config.BuildingProtection;
        bool grass = material is EnumBlockMaterial.Plant or EnumBlockMaterial.Leaves
            || code.Contains("thatch") || code.Contains("drygrass") || code.Contains("straw");
        if (grass) return new(settings.GrassIgnitionSeconds, settings.GrassFailureSeconds);
        if (material == EnumBlockMaterial.Cloth) return new(settings.ClothIgnitionSeconds, settings.ClothFailureSeconds);
        if (material != EnumBlockMaterial.Wood) return null;
        bool timber = code.StartsWith("log-") || code.Contains("beam") || code.StartsWith("trunk");
        return timber ? new(settings.TimberIgnitionSeconds, settings.TimberFailureSeconds) : new(settings.WoodIgnitionSeconds, settings.WoodFailureSeconds);
    }

    private void TickBuildingProtection(float dt)
    {
        if (disposed) return;
        TickBuildingEnrollment();
        double elapsed = Math.Clamp(dt, 0, .5);
        lifeState.ProtectionSeconds += elapsed;
        if (!config.BuildingProtection.Enabled) return;
        // Only loaded, actively exposed/cooling positions are visited. Damage-only
        // records remain saved without scanning all territory blocks.
        foreach (string key in activeBuildingFires.ToArray())
        {
            if (!lifeState.BuildingFires.TryGetValue(key, out var state)) { activeBuildingFires.Remove(key); continue; }
            var pos = new BlockPos(state.X, state.Y, state.Z, state.Dimension);
            if (sapi!.World.BlockAccessor.GetChunkAtBlockPos(pos) == null)
            { state.Heat = 0; state.Ignited = false; activeBuildingFires.Remove(key); fireExposures.Remove(key); arrowHeat.Remove(key); continue; }
            var block = sapi.World.BlockAccessor.GetBlock(pos);
            if (block.Code?.ToString() != state.BlockCode) { ClearBuildingDamage(pos, block.Id == 0); continue; }
            var profile = FireProfile(pos);
            if (profile == null) { activeBuildingFires.Remove(key); continue; }
            bool exposed = fireExposures.TryGetValue(key, out var source) && source.Until >= lifeState.ProtectionSeconds
                && FireAccess(source.Owner, pos);
            if (!exposed)
            {
                state.Heat = Math.Max(0, state.Heat - elapsed * config.BuildingProtection.CoolingPerSecond);
                state.Ignited = false;
                if (state.Heat <= 0)
                { activeBuildingFires.Remove(key); fireExposures.Remove(key); arrowHeat.Remove(key); if (state.DamageSeconds <= 0 && state.ManualConsumedFraction <= 0) lifeState.BuildingFires.Remove(key); }
                continue;
            }
            state.OwnerUid = source!.Owner;
            double exposureTime = source.Strong ? elapsed : Math.Min(elapsed, arrowHeat.GetValueOrDefault(key));
            arrowHeat[key] = Math.Max(0, arrowHeat.GetValueOrDefault(key) - elapsed);
            state.Heat = Math.Min(profile.Ignition, Math.Max(0, state.Heat + exposureTime
                - (elapsed - exposureTime) * config.BuildingProtection.CoolingPerSecond));
            if (state.Heat >= profile.Ignition) state.Ignited = true;
            double demand = exposureTime * config.BuildingProtection.FireProtectionDamagePerSecond;
            double absorbed = ConsumeBuildingReserve(pos, null, demand);
            if (state.Ignited) EnsureBuildingFlame(pos, state.OwnerUid);
            if (!state.Ignited || demand <= 0 || absorbed >= demand - 1e-8) continue;
            double damageTime = exposureTime * (1 - absorbed / demand);
            var native = sapi.ModLoader.GetModSystem<ModSystemBlockReinforcement>();
            var manual = native?.GetReinforcment(pos);
            if (manual?.Strength > 0 && state.ManualConsumedFraction < 1)
            {
                double totalPoints = state.ManualConsumedFraction * config.BuildingProtection.ManualFireStrengthForMaximum;
                double availablePoints = Math.Max(0, manual.Strength - (totalPoints - Math.Floor(totalPoints + 1e-8)));
                double proportion = Math.Min(Math.Min(1 - state.ManualConsumedFraction,
                    availablePoints / config.BuildingProtection.ManualFireStrengthForMaximum),
                    damageTime / config.BuildingProtection.ManualFireDelayMaximumSeconds);
                double points = proportion * config.BuildingProtection.ManualFireStrengthForMaximum;
                state.ManualConsumedFraction += proportion;
                // Preserve fractional consumption across ticks. Native strength is
                // integral; debit only when crossing a full point.
                double previous = (state.ManualConsumedFraction - proportion) * config.BuildingProtection.ManualFireStrengthForMaximum;
                int debit = (int)Math.Floor(previous + points + 1e-8) - (int)Math.Floor(previous + 1e-8);
                if (debit > 0) native!.ConsumeStrength(pos, debit);
                damageTime = Math.Max(0, damageTime - proportion * config.BuildingProtection.ManualFireDelayMaximumSeconds);
            }
            state.DamageSeconds += damageTime;
            if (state.DamageSeconds + 1e-8 < profile.Failure) continue;
            // Recheck permission immediately before permanent loss. Do not drop
            // intact building resources from combustion.
            if (!FireAccess(state.OwnerUid, pos) || TerritoryReinforcement(null, pos).Strength > 0) continue;
            sapi.World.BlockAccessor.GetBlockEntity(pos)?.OnBlockBroken(null);
            native?.ClearReinforcement(pos);
            sapi.World.BlockAccessor.SetBlock(0, pos);
            sapi.World.BlockAccessor.TriggerNeighbourBlockUpdate(pos);
            ClearBuildingState(pos);
        }
    }

    private void EnsureBuildingFlame(BlockPos fuel, string uid)
    {
        var world = sapi!.World;
        foreach (var facing in BlockFacing.ALLFACES)
        {
            var pos = fuel.AddCopy(facing);
            if (world.BlockAccessor.GetBlockEntity(pos)?.GetBehavior<BEBehaviorBurning>() is { IsBurning: true } burning
                && burning.FuelPos?.Equals(fuel) == true) return;
        }
        foreach (var facing in BlockFacing.ALLFACES)
        {
            var pos = fuel.AddCopy(facing);
            if (world.BlockAccessor.GetBlock(pos).Id != 0 || world.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid).IsLiquid() || !FireAccess(uid, pos)) continue;
            var fire = world.GetBlock(new AssetLocation("game:fire"));
            if (fire == null) return;
            world.BlockAccessor.SetBlock(fire.Id, pos);
            world.BlockAccessor.GetBlockEntity(pos)?.GetBehavior<BEBehaviorBurning>()?.OnFirePlaced(pos, fuel, uid);
            return;
        }
    }
}
