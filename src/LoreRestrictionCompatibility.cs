using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace SeraphCivilizationSimulation;

// No compile-time dependency on optional pack mods. Guards use the interacting world's policy.
internal sealed class LoreRestrictionCompatibility : IDisposable
{
    internal const string PatchId = "vsscsmod.lorerestrictions";
    private static readonly object Sync = new();
    private static int users;
    private bool disposed;

    public LoreRestrictionCompatibility()
    {
        lock (Sync)
        {
            if (users == 0) Install();
            users++;
        }
    }

    private static void Install()
    {
        var harmony = new Harmony(PatchId);
        void Patch(Type type, string name, string? prefix = null, string? postfix = null)
        {
            var method = AccessTools.DeclaredMethod(type, name) ?? throw new MissingMethodException(type.FullName, name);
            harmony.Patch(method, prefix: prefix == null ? null : new HarmonyMethod(typeof(LoreRestrictionCompatibility), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(LoreRestrictionCompatibility), postfix));
        }
        void Optional(string type, string name, string? prefix = null, string? postfix = null)
        {
            if (AccessTools.TypeByName(type) is { } found) Patch(found, name, prefix, postfix);
        }
        try
        {
            Patch(typeof(EntityBehaviorHarvestable), "Initialize", postfix: nameof(AfterHarvestInitialized));
            foreach (string type in new[] { "BlockSkullOfTheDead", "BlockHandOfTheDead" })
                Optional("PrimitiveSurvival.ModSystem." + type, "TryPlaceBlock", nameof(BeforeRelicPlace));
            Optional("PrimitiveSurvival.ModSystem.BlockSkullOfTheDead", "OnBlockInteractStart", nameof(BeforeRestrictedBlockInteract));
            Optional("PrimitiveSurvival.ModSystem.EntitySkullOfTheDead", "OnGameTick", nameof(BeforeSkullTick));
            Optional("Jaunt.Behaviors.EntityBehaviorJauntRevivable", "OnInteract", nameof(BeforeRevival));
            Optional("DynamicSeasonalTemperature.BlockTemporalTempForecaster", "OnBlockInteractStart", nameof(BeforeRestrictedBlockInteract));
            Optional("ImmersiveMaps.Items.ItemMap", "OnHeldInteractStart", nameof(BeforeMapInteract));
            Optional("ImmersiveMaps.Items.ItemMap", "OnHeldIdle", nameof(BeforeMapIdle));
        }
        catch (Exception ex)
        {
            harmony.UnpatchAll(PatchId);
            throw new InvalidOperationException("Seraph Civilization Simulation lore restrictions could not patch an installed mod; check mod version compatibility.", ex);
        }
    }

    internal static void FilterPan(CollectibleObject collectible, LoreRestrictionsConfig policy)
    {
        if (!policy.Enabled || collectible is not BlockPan pan) return;
        var field = AccessTools.Field(typeof(BlockPan), "dropsBySourceMat")
            ?? throw new MissingFieldException(typeof(BlockPan).FullName, "dropsBySourceMat");
        if (field.GetValue(pan) is not Dictionary<string, PanningDrop[]> drops) return;
        foreach (string source in drops.Keys.ToArray())
            drops[source] = drops[source].Where(d => !policy.PanDropRestricted(source, d.Code)).ToArray();
    }

    private static void AfterHarvestInitialized(EntityBehaviorHarvestable __instance, ref BlockDropItemStack[] ___jsonDrops)
    {
        var entity = __instance.entity;
        if (entity.Code?.Domain != "primitivesurvival" || !entity.Code.Path.StartsWith("livingdead-", StringComparison.Ordinal)
            || LoreRestrictionSystem.Policy(entity.World) is not { } policy || ___jsonDrops == null) return;
        ___jsonDrops = ___jsonDrops.Where(d => !policy.LivingDeadDropRestricted(d.Code)).ToArray();
    }

    private static bool BeforeRelicPlace(Block __instance, IWorldAccessor __0, IPlayer __1, ref string __4, ref bool __result)
    {
        if (LoreRestrictionSystem.Policy(__0)?.CollectibleRestricted(__instance.Code) != true) return true;
        __4 = "__ignore__";
        if (__0.Api is ICoreClientAPI client)
            client.TriggerIngameError(__1, "vscncs-lore-restricted", Lang.Get("vsscsmod:lore-restricted"));
        __result = false;
        return false;
    }

    private static bool BeforeRestrictedBlockInteract(Block __instance, IWorldAccessor __0, ref bool __result)
    {
        if (LoreRestrictionSystem.Policy(__0)?.CollectibleRestricted(__instance.Code) != true) return true;
        __result = true;
        return false;
    }

    private static bool BeforeSkullTick(Entity __instance) => LoreRestrictionSystem.Policy(__instance.World)?.SkullOfTheDead != true;

    private static bool BeforeMapIdle(ItemSlot __0, EntityAgent __1)
        => LoreRestrictionSystem.Policy(__1?.World)?.CollectibleRestricted(__0?.Itemstack?.Collectible.Code) != true;

    private static bool BeforeMapInteract(ItemSlot __0, EntityAgent __1, ref EnumHandHandling __5)
    {
        if (BeforeMapIdle(__0, __1)) return true;
        __5 = EnumHandHandling.PreventDefaultAction;
        return false;
    }

    // Skip only Jaunt's revival; do not consume the interaction or block riding/feeding behaviors.
    private static bool BeforeRevival(EntityBehavior __instance)
        => __instance.entity.Code?.Domain != "equusferus" || LoreRestrictionSystem.Policy(__instance.entity.World)?.EquusRevival != true;

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
