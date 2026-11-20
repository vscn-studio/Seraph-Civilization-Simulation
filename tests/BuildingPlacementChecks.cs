using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using SeraphCivilizationSimulation;

internal static class BuildingPlacementChecks
{
    private sealed class PairBlock : Block
    {
        public bool ThrowAfterWrite;
        public bool FluidOnly;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public override bool CanPlaceBlock(IWorldAccessor w, IPlayer p, BlockSelection s, ref string error) => true;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public override bool TryPlaceBlock(IWorldAccessor w, IPlayer p, ItemStack stack, BlockSelection s, ref string error)
        {
            if (!CanPlaceBlock(w, p, s, ref error) || !CanPlaceBlock(w, p, s.AddPosCopy(0, 1, 0), ref error)) return false;
            if (FluidOnly) { w.BlockAccessor.SetBlock(2, s.Position, BlockLayersAccess.Fluid); return true; }
            w.BlockAccessor.SetBlock(1, s.Position);
            w.BlockAccessor.SetBlock(1, s.Position.UpCopy());
            if (ThrowAfterWrite) throw new InvalidOperationException("placement failure");
            return true;
        }
    }

    public static void Run(Action<bool, string> check)
    {
        var type = typeof(VscnCivilizationModSystem);
        var system = new VscnCivilizationModSystem();
        var previous = AccessTools.Field(type, "instance").GetValue(null);
        var patch = new Harmony("vscn.tests.building-placement");
        var life = (CivilizationLifeState)AccessTools.Field(type, "lifeState").GetValue(system)!;
        var tribes = (TribeState)AccessTools.Field(type, "tribeState").GetValue(system)!;
        var tribe = new TribeRecord { Id = "host", LeaderUid = "chief", Members = ["chief"], X = 100, Y = 100, Z = 100 };
        tribes.Tribes.Add(tribe); life.Players["chief"] = new() { Living = true };
        TerritoryTestWorld.Region(tribe, 32, 8); TerritoryTestWorld.Index(system);
        var pair = new PairBlock { BlockId = 1, Code = new("test:pair"), BlockBehaviors = [], CollectibleBehaviors = [] };
        Block[] available = [new Block { BlockId = 0 }, pair, new Block { BlockId = 2 }];
        var solids = new Dictionary<BlockPos, int>();
        var accessor = Proxy.Make<IBlockAccessor>((method, args) =>
        {
            if (method.Name == "GetBlock") return available[solids.GetValueOrDefault((BlockPos)args![0]!)];
            if (method.Name == "SetBlock" && !(args!.Length == 3 && args[2] is int layer && layer == BlockLayersAccess.Fluid))
                solids[((BlockPos)args![1]!).Copy()] = (int)args[0]!;
            return null;
        });
        var world = Proxy.Make<IServerWorldAccessor>((method, args) => method.Name switch
        { "get_Blocks" => available, "get_BlockAccessor" => accessor, "get_Side" => EnumAppSide.Server, "get_AllOnlinePlayers" => Array.Empty<IPlayer>(), _ => null });
        var api = Proxy.Make<ICoreServerAPI>((method, args) => method.Name == "get_World" ? world : null);
        var outsider = Proxy.Make<IPlayer>((method, args) => method.Name == "get_PlayerUID" ? "visitor" : null);
        var settings = system.Config.TribeAuthorization;
        var selected = new BlockSelection { Position = new BlockPos(110, 100, 100) };
        bool Place() { string failure = ""; return pair.TryPlaceBlock(world, outsider, null!, selected, ref failure); }
        void Clear() { life.PlacedBuildings.Clear(); solids.Clear(); }
        try
        {
            AccessTools.Field(type, "instance").SetValue(null, system);
            AccessTools.Field(type, "sapi").SetValue(system, api);
            AccessTools.Method(type, "StartPlacementTracking").Invoke(system, [patch]);
            AccessTools.Method(type, "StartPlacementTracking").Invoke(system, [patch]);
            check(Place() && life.PlacedBuildings.Count == 2 && life.PlacedBuildings.Values.All(p => p.TribeId == ""), "Multi-block outsider placement attribution failed.");
            tribe.Authorizations.Add(new() { Uid = "visitor", ExpiresAt = -1, Construction = true });
            pair.FluidOnly = true;
            check(Place() && life.PlacedBuildings.Values.All(p => p.TribeId == ""), "Fluid layer update reassigned existing buildings.");
            pair.FluidOnly = false; Clear();
            check(Place() && life.PlacedBuildings.Count == 2 && life.PlacedBuildings.Values.All(p => p.TribeId == tribe.Id), "Authorized multi-block attribution failed.");
            tribe.Authorizations.Clear(); Clear();
            settings.OutsiderBlockQuotaEnabled = true; settings.OutsiderBlockQuota = 1;
            check(!Place() && solids.Count == 0 && life.PlacedBuildings.Count == 0, "Multi-block placement exceeded quota or left a partial building.");
            settings.OutsiderBlockQuota = 2;
            check(Place() && life.PlacedBuildings.Count == 2, "Exact multi-block quota was rejected.");
            accessor.SetBlock(0, selected.Position);
            check(life.PlacedBuildings.Count == 1, "Direct block removal retained ownership/quota.");
            settings.OutsiderBlockQuotaEnabled = false; settings.ProtectCriticalAreas = true;
            selected.Position.Set(100, 98, 100); Clear();
            check(!Place() && solids.Count == 0, "Multi-block child entered protected core area.");
            settings.ProtectCriticalAreas = false; selected.Position.Set(110, 100, 100);
            pair.ThrowAfterWrite = true;
            try { Place(); check(false, "Placement exception fixture did not throw."); }
            catch (InvalidOperationException) { check(true, "Placement exception propagated."); }
            accessor.SetBlock(1, new BlockPos(111, 100, 100));
            check(life.PlacedBuildings.Count == 2, "Placement exception leaked player context to later world writes.");
            pair.ThrowAfterWrite = false;
            settings.OutsiderPlacementCooldownEnabled = true;
            selected.Position.Set(112, 100, 100);
            check(!Place(), "Real multi-block placement did not apply outsider cooldown.");
            settings.OutsiderPlacementCooldownEnabled = false;
            check(Place(), "Denied attempt leaked its context to the next placement.");
        }
        finally
        {
            patch.UnpatchAll(patch.Id);
            AccessTools.Field(type, "instance").SetValue(null, previous);
        }
    }
}
