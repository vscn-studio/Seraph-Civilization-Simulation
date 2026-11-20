using HarmonyLib;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using SeraphCivilizationSimulation;

internal static class PlacementTrackingChecks
{
    private class InheritedBlock : Block { }
    private class OverrideBlock : InheritedBlock
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public override bool TryPlaceBlock(IWorldAccessor world, IPlayer player, ItemStack stack, BlockSelection selection, ref string failureCode) => true;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public override bool CanPlaceBlock(IWorldAccessor world, IPlayer player, BlockSelection selection, ref string failureCode) => true;
    }
    private sealed class InheritedOverrideBlock : OverrideBlock { }

    public static void Run(Action<bool, string> check)
    {
        var type = typeof(VscnCivilizationModSystem);
        var system = new VscnCivilizationModSystem();
        Block[] catalog = [new Block(), new InheritedBlock(), new InheritedBlock(), new OverrideBlock(), new InheritedOverrideBlock()];
        var blocks = Proxy.Make<IBlockAccessor>((m, a) => m.Name == "GetBlock" ? catalog[0] : null);
        var world = Proxy.Make<IServerWorldAccessor>((m, a) => m.Name switch
        { "get_Blocks" => catalog, "get_BlockAccessor" => blocks, "get_Side" => EnumAppSide.Server,
            "get_AllOnlinePlayers" => Array.Empty<IPlayer>(), _ => null });
        var api = Proxy.Make<ICoreServerAPI>((m, a) => m.Name == "get_World" ? world : null);
        AccessTools.Field(type, "sapi").SetValue(system, api);
        var instance = AccessTools.Field(type, "instance");
        object? previous = instance.GetValue(null);
        instance.SetValue(null, system);
        var patch = new Harmony("vscncs.tests.placement");
        try
        {
            void Install() => AccessTools.Method(type, "StartPlacementTracking").Invoke(system, [patch]);
            Install(); Install();
            foreach (var owner in new[] { typeof(Block), typeof(OverrideBlock) })
            foreach (string name in new[] { nameof(Block.TryPlaceBlock), nameof(Block.CanPlaceBlock) })
            {
                var info = Harmony.GetPatchInfo(AccessTools.DeclaredMethod(owner, name));
                check(info?.Prefixes.Count(p => p.owner == patch.Id) == 1,
                    "Inherited block implementations must be patched once and startup must be repeatable: " + owner + "." + name);
            }
            var tribes = (TribeState)AccessTools.Field(type, "tribeState").GetValue(system)!;
            tribes.Tribes.Add(new() { Id = "home", X = 100, Y = 100, Z = 100, Members = ["owner"] });
            TerritoryTestWorld.Region(tribes.Tribes[0], 32, 8); TerritoryTestWorld.Index(system);
            system.Config.TribeAuthorization.ProtectCriticalAreas = true;
            var player = Proxy.Make<IPlayer>((m, a) => m.Name == "get_PlayerUID" ? "outsider" : null);
            var selection = new BlockSelection { Position = new BlockPos(100, 100, 100) };
            foreach (var block in catalog)
            {
                string failure = "";
                check(!block.TryPlaceBlock(world, player, null!, selection, ref failure) && failure.Length > 0,
                    "Inherited or overridden placement must enforce outsider restrictions: " + block.GetType());
                check(AccessTools.Field(type, "placementContext").GetValue(null) == null,
                    "Denied placement must release its tracking context.");
            }
        }
        finally { patch.UnpatchAll(patch.Id); instance.SetValue(null, previous); }
        check(Harmony.GetPatchInfo(AccessTools.DeclaredMethod(typeof(Block), nameof(Block.TryPlaceBlock)))?.Owners.Contains(patch.Id) != true,
            "Stopping placement tracking must remove its native patches.");
    }
}
