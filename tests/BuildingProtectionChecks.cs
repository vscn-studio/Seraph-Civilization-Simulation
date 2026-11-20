using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using SeraphCivilizationSimulation;

internal static class BuildingProtectionChecks
{
    private sealed class FireEntity : BlockEntity { }
    public static void Run(Action<bool, string> check)
    {
        var config = new TerritoryConfig();
        check(BuildingProtectionRules.RecoveryRate(config, 5) == 1 && BuildingProtectionRules.RecoveryRate(config, 100) == 4, "Population recovery rate/cap failed.");
        var wear = new TerritoryWear { Capacity = 50 };
        check(BuildingProtectionRules.Consume(wear, 50, 50, 100) == 50, "Initial reserve consumption failed.");
        check(BuildingProtectionRules.Remaining(wear, 50, 700, 1, 600) == 0, "Recovery started during cooldown.");
        check(Math.Abs(BuildingProtectionRules.Remaining(wear, 50, 730, 1, 600) - .5) < 1e-8, "Fractional recovery failed.");
        BuildingProtectionRules.Consume(wear, 50, 5, 730);
        check(BuildingProtectionRules.Remaining(wear, 50, 1330, 1, 600) == 0, "Continued attack did not reset cooldown.");
        check(BuildingProtectionRules.Remaining(wear, 60, 1330, 1, 600) == 0, "Population growth instantly refilled a breached block.");
        var restarted = JsonConvert.DeserializeObject<TerritoryWear>(JsonConvert.SerializeObject(wear))!;
        check(BuildingProtectionRules.Remaining(restarted, 60, 1330, 1, 600) == 0, "Restart without elapsed server time refilled reserve.");
        check(BuildingProtectionRules.Remaining(restarted, 60, 1930, 1, 600) == 10, "Recovery did not resume after restart.");
        BuildingProtectionRules.Remaining(restarted, 0, 1930, 0, 600);
        check(BuildingProtectionRules.Remaining(restarted, 60, 1930, 1, 600) == 0, "Restoring population from zero instantly refilled reserve.");
        foreach (Action<VscnConfig> invalid in new Action<VscnConfig>[]
        { c => c.BuildingProtection.ArrowContactSeconds = double.NaN, c => c.BuildingProtection.WoodIgnitionSeconds = 0,
          c => c.Territory.ReinforcementRecoveryDelaySeconds = -1, c => c.Territory.ReinforcementRecoveryMaximumPerMinute = double.PositiveInfinity })
        {
            var bad = new VscnConfig(); invalid(bad);
            try { bad.Validate(); check(false, "Invalid fire/recovery configuration accepted."); }
            catch (ArgumentException) { check(true, "Invalid configuration rejected."); }
        }
        string? mods = Environment.GetEnvironmentVariable("VSCN_BUILDING_MODS");
        if (mods == null) throw new InvalidOperationException("Set VSCN_BUILDING_MODS to the actual Mods directory.");
        string game = Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            foreach (string directory in new[] { game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods") })
            { string file = Path.Combine(directory, name.Name + ".dll"); if (File.Exists(file)) return context.LoadFromAssemblyPath(file); }
            return null;
        }
        AssemblyLoadContext.Default.Resolving += Resolve;
        try
        {
            foreach (var package in new[] { ("CarryOnLib-*.zip", "CarryOnLib.dll"), ("Carry On-*.zip", "CarryOn.dll"),
                ("VSCN Fire Arrows-*.zip", "VscnFireArrows.dll"), ("Pyrogenesis Continued-*.zip", "Pyrogenesis.dll"), ("VS Roofing Mod-*.zip", "vsroofing.dll"), ("Logging Expanded-*.zip", "LoggingMod.dll") })
            {
                using var zip = ZipFile.OpenRead(Directory.GetFiles(mods, package.Item1).Single());
                using var source = zip.Entries.Single(e => e.Name.Equals(package.Item2, StringComparison.OrdinalIgnoreCase)).Open();
                using var memory = new MemoryStream(); source.CopyTo(memory); memory.Position = 0;
                if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name!.Equals(Path.GetFileNameWithoutExtension(package.Item2), StringComparison.OrdinalIgnoreCase)))
                    AssemblyLoadContext.Default.LoadFromStream(memory);
            }
            Verify(check);
        }
        finally { AssemblyLoadContext.Default.Resolving -= Resolve; }
    }

    private static void Verify(Action<bool, string> check)
    {
        var type = typeof(VscnCivilizationModSystem);
        object? Call(string method, object target, params object?[] args) => AccessTools.Method(type, method).Invoke(target, args);
        void Set(object target, string field, object value) => AccessTools.Field(target.GetType(), field).SetValue(target, value);
        var system = new VscnCivilizationModSystem();
        var life = (CivilizationLifeState)AccessTools.Field(type, "lifeState").GetValue(system)!;
        var tribes = (TribeState)AccessTools.Field(type, "tribeState").GetValue(system)!;
        var tribe = new TribeRecord { Id = "home", Name = "Home", X = 100, Y = 100, Z = 100, Members = ["owner"] };
        tribes.Tribes.Add(tribe); life.Players["owner"] = new() { Living = true };
        TerritoryTestWorld.Region(tribe); TerritoryTestWorld.Index(system);
        var pos = new BlockPos(100, 100, 100);
        var block = new Block { BlockId = 10, Code = new("game:planks-oak"), BlockMaterial = EnumBlockMaterial.Wood };
        Block[] catalog = [block, (Block)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(AccessTools.TypeByName("VSRoofing.RoofBlock"))];
        bool loaded = true, granted = true; int removed = 0; long clock = 0;
        var chunkData = new Dictionary<string, byte[]>();
        var chunk = Proxy.Make<IWorldChunk>((m, a) =>
        {
            if (m.Name == "GetModdata") return chunkData.GetValueOrDefault((string)a![0]!);
            if (m.Name == "SetModdata") chunkData[(string)a![0]!] = (byte[])a[1]!;
            return null;
        });
        var blocks = Proxy.Make<IBlockAccessor>((m, a) => m.Name switch
        {
            "GetBlock" => block, "GetChunkAtBlockPos" or "GetChunk" => loaded ? chunk : null,
            "SetBlock" => Remove(), _ => null
        });
        object? Remove() { removed++; block = new Block { BlockId = 0, Code = new("game:air") }; return null; }
        var claims = Proxy.Make<ILandClaimAPI>((m, a) => m.Name switch
        { "get_All" => new List<LandClaim>(), "TestAccess" => granted ? EnumWorldAccessResponse.Granted : EnumWorldAccessResponse.DeniedByMod, _ => null });
        var entity = new EntityPlayer();
        var attacker = Proxy.Make<IPlayer>((m, a) => m.Name switch { "get_PlayerUID" or "get_PlayerName" => "attacker", "get_Entity" => entity, _ => null });
        ICoreServerAPI? api = null;
        var world = Proxy.Make<IServerWorldAccessor>((m, a) => m.Name switch
        { "get_BlockAccessor" => blocks, "get_Claims" => claims, "get_Api" => api, "get_Side" => EnumAppSide.Server,
          "get_Blocks" => catalog,
          "get_ElapsedMilliseconds" => clock, "get_AllOnlinePlayers" => Array.Empty<IPlayer>(), "PlayerByUid" => attacker, _ => null });
        var native = new ModSystemBlockReinforcement();
        var loader = Proxy.Make<IModLoader>((m, a) => m.Name == "GetModSystem" ? native : null);
        var events = Proxy.Make<IServerEventAPI>((m, a) => m.Name == "RegisterGameTickListener" ? 1L : null);
        api = Proxy.Make<ICoreServerAPI>((m, a) => m.Name switch
        { "get_World" => world, "get_Event" => events, "get_ModLoader" => loader, "get_Side" => EnumAppSide.Server, "get_Logger" => Proxy.Make<ILogger>((m, a) => null), _ => null });
        entity.World = world; Set(system, "sapi", api); Set(native, "api", api);
        object? previous = AccessTools.Field(type, "instance").GetValue(null);
        Set(system, "instance", system);
        try
        {
            Call("StartBuildingProtection", system);
            foreach (var pair in new[] { ("Vscn.FireArrows.FireEnvironment", "IgniteBlock"), ("Pyrogenesis.TreeMechanics", "BreakTreeBlock"), ("VSRoofing.RoofBlock", "OnBlockBroken"), ("LoggingMod.FellingListener", "OnBreakBlock") })
                check(Harmony.GetPatchInfo(AccessTools.DeclaredMethod(AccessTools.TypeByName(pair.Item1), pair.Item2))?.Owners.Contains("vsscsmod.buildingprotection") == true, "Installed mod entry was not patched: " + pair);
            var carry = AccessTools.TypeByName("CarryOn.Common.Services.CarryPermissionService").GetMethods().Single(m => m.Name == "HasPermissionAt" && m.GetParameters().Length == 4);
            check(Harmony.GetPatchInfo(carry)?.Owners.Contains("vsscsmod.buildingprotection") == true, "CarryOn permission patch missing.");
            var carryPlacement = AccessTools.TypeByName("CarryOn.Common.Services.CarryPlacementService").GetMethods()
                .Single(m => m.Name == "TryPlaceDown" && m.GetParameters().Any(p => p.ParameterType == typeof(string).MakeByRefType()));
            check(Harmony.GetPatchInfo(carryPlacement)?.Owners.Contains("vsscsmod.buildingprotection") == true, "CarryOn placement attribution patch missing.");
            block.BlockBehaviors = []; block.CollectibleBehaviors = [];
            var carriedType = AccessTools.TypeByName("CarryOn.API.Common.Models.CarriedBlock");
            var slot = Enum.ToObject(AccessTools.TypeByName("CarryOn.API.Common.Models.CarrySlot"), 0);
            var carried = carriedType.GetConstructors().Single(c => c.GetParameters().Length == 3).Invoke([slot, new ItemStack(block), null]);
            var attachedType = AccessTools.TypeByName("CarryOn.API.Common.Models.AttachedCarriedBlock");
            var attached = attachedType.GetConstructors().Single().Invoke([new BlockPos(0, 1, 0), carried, null]);
            var attachedList = Array.CreateInstance(attachedType, 1); attachedList.SetValue(attached, 0);
            var carriedPair = carriedType.GetConstructors().Single(c => c.GetParameters().Length == 4).Invoke([slot, new ItemStack(block), null, attachedList]);
            var placementService = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(carryPlacement.DeclaringType!);
            system.Config.TribeAuthorization.OutsiderBlockQuotaEnabled = true; system.Config.TribeAuthorization.OutsiderBlockQuota = 1;
            object?[] placementArgs = [entity, carriedPair, new BlockSelection { Position = pos, Face = BlockFacing.UP }, "", true, false];
            check(carryPlacement.Invoke(placementService, placementArgs) is false && placementArgs[3]?.ToString() == "vsscsmod:auth-placement-quota",
                "Installed CarryOn attached blocks bypassed placement quota.");
            check(AccessTools.Field(type, "placementContext").GetValue(null) == null, "Denied CarryOn placement leaked player context.");
            system.Config.TribeAuthorization.OutsiderBlockQuotaEnabled = false;
            double Reserve() => ((TribeReinforcementPacket)Call("TerritoryReinforcement", system, null, pos)!).Strength;
            void Expose(double seconds)
            {
                for (int i = 0; i < seconds * 4; i++) { Call("ExposeBuilding", system, pos, "attacker", true, .75); Call("TickBuildingProtection", system, .25f); }
            }
            check(Reserve() == 10, "Offline living member reserve missing.");
            var carryService = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(carry.DeclaringType!);
            object?[] carryArgs = [entity, pos, null, false];
            check(carry.Invoke(carryService, carryArgs) is false && carryArgs[2]?.ToString() == "Reinforced", "CarryOn could move a protected block.");
            EnumHandling blastHandling = EnumHandling.PassThrough;
            new BlockBehaviorReinforcable(block).OnBlockExploded(world, pos, pos, EnumBlastType.OreBlast, ref blastHandling);
            check(blastHandling == EnumHandling.PreventDefault && Reserve() == 8, "Explosion did not debit shared reserve and prevent immediate loss.");
            Call("ClearBuildingState", system, pos);
            Expose(10);
            check(Reserve() == 8 && !life.BuildingFires.Values.Single().Ignited && removed == 0, "Wood short exposure bypassed ignition or shared reserve.");
            Call("ConsumeBuildingReserve", system, pos, attacker, 3d);
            check(Reserve() == 5, "Mining and fire did not share reserve.");
            Expose(40);
            check(Reserve() == 0 && life.BuildingFires.Values.Single().Ignited && life.BuildingFires.Values.Single().DamageSeconds <= 5.5, "Reserve did not absorb fire before structural loss.");
            Expose(10); double damage = life.BuildingFires.Values.Single().DamageSeconds;
            check(damage >= 10 && removed == 0, "Wood failure threshold ignored.");
            for (int i = 0; i < 12; i++) Call("TickBuildingProtection", system, .25f);
            check(!life.BuildingFires.Values.Single().Ignited && life.BuildingFires.Values.Single().DamageSeconds >= damage, "Cooling repaired permanent damage.");
            loaded = false; Call("TickBuildingProtection", system, .25f); loaded = true;
            check(life.BuildingFires.Values.Single().Heat == 0 && life.BuildingFires.Values.Single().DamageSeconds >= damage, "Unloading lost damage or preserved stale heat.");
            life.ProtectionSeconds = life.BlockWear.Values.Single().LastAttackSeconds + 600; check(Reserve() == 0, "Reserve recovered before cooldown.");
            life.ProtectionSeconds += 300; check(Reserve() == 1, "Population-based slow recovery failed.");
            Call("ClearBuildingState", system, pos);
            var arrowType = AccessTools.TypeByName("Vscn.FireArrows.FireEnvironment");
            var arrowConfig = Activator.CreateInstance(AccessTools.TypeByName("Vscn.FireArrows.FireConfig"))!;
            var arrow = new EntityProjectile { World = world, Api = api, ProjectileStack = new ItemStack(new Item { Code = new("game:arrow-flint") }) };
            arrow.ProjectileStack.Attributes.SetBool("vscnFireArrow", true); arrow.ProjectileStack.Attributes.SetInt("vscnFireState", 1);
            var ignite = AccessTools.Method(arrowType, "IgniteBlock");
            block = new Block { BlockId = 11, Code = new("game:stone-granite"), BlockMaterial = EnumBlockMaterial.Stone };
            ignite.Invoke(null, [arrow, pos, arrowConfig]);
            check(life.BlockWear.Count == 0 && life.BuildingFires.Count == 0, "Stone wall was damaged by a fire arrow.");
            block = new Block { BlockId = 12, Code = new("game:planks-oak"), BlockMaterial = EnumBlockMaterial.Wood };
            for (int i = 0; i < 1500; i++) { clock += 20; ignite.Invoke(null, [arrow, pos, arrowConfig]); if (i % 12 == 0) Call("TickBuildingProtection", system, .24f); }
            check(life.BuildingFires.Values.Single().Heat <= 12.1 && !life.BuildingFires.Values.Single().Ignited && Reserve() >= 7, "One arrow supplied unbounded heat or fixed damage.");
            Call("ClearBuildingState", system, pos);
            system.Config.Territory.ReinforcementMaximum = 0;
            block = new Block { BlockId = 13, Code = new("game:hay"), BlockMaterial = EnumBlockMaterial.Plant };
            Expose(16);
            check(removed == 0 && life.BuildingFires.Values.Single().Ignited, "Grass ignition/failure stages were merged.");
            Expose(2); check(removed == 1, $"Sustained grass fire did not destroy fuel: removed={removed}, state={JsonConvert.SerializeObject(life.BuildingFires)}.");
            block = new Block { BlockId = 14, Code = new("game:log-oak"), BlockMaterial = EnumBlockMaterial.Wood };
            Expose(46); check(!life.BuildingFires.Values.Single().Ignited, "Structural timber used plank ignition threshold.");
            granted = false; double heat = life.BuildingFires.Values.Single().Heat; Expose(1);
            check(life.BuildingFires.Values.Single().Heat <= heat, "Fire bypassed native claim permissions.");
            granted = true; Call("ClearBuildingState", system, pos);
            block = new Block { BlockId = 15, Code = new("game:planks-oak"), BlockMaterial = EnumBlockMaterial.Wood };
            var manualBehavior = new BlockBehaviorReinforcable(block);
            block.BlockBehaviors = [manualBehavior]; block.CollectibleBehaviors = [manualBehavior];
            check(!native.TryLock(pos, attacker, "game:padlock") && !native.StrengthenBlock(pos, attacker, 200), "Outsider must not manually lock/reinforce another territory.");
            tribe.Members.Add(attacker.PlayerUID);
            check(native.TryLock(pos, attacker, "game:padlock") && native.StrengthenBlock(pos, attacker, 200), "Manual fire fixture failed.");
            tribe.Members.Remove(attacker.PlayerUID);
            Expose(100);
            var manualState = life.BuildingFires.Values.Single();
            check(manualState.DamageSeconds < .01 && native.GetReinforcment(pos).Strength < 200, "Manual reinforcement did not delay structural loss.");
            double spent = manualState.ManualConsumedFraction;
            for (int i = 0; i < 400; i++) Call("TickBuildingProtection", system, .25f);
            check(life.BuildingFires.Values.Single().ManualConsumedFraction >= spent, "Extinguishing reset manual fire delay.");
            Expose(250);
            check(removed == 2, "Reinforced wood became fireproof or refreshed its delay after extinguishing.");
            Call("ClearBuildingState", system, pos);
            block = new Block { BlockId = 16, Code = new("game:planks-oak"), BlockMaterial = EnumBlockMaterial.Wood };
            var be = new FireEntity { Api = api, Pos = pos.AddCopy(0, 1, 0) };
            AccessTools.Property(typeof(BlockEntity), "Block").SetValue(be, new Block { Code = new("game:fire") });
            var burning = new BEBehaviorBurning(be) { FuelPos = pos, FirePos = be.Pos, IsBurning = true, remainingBurnDuration = 8, AllowFireSpread = true };
            AccessTools.Field(typeof(BlockEntityBehavior), "Api").SetValue(burning, api);
            int deaths = 0; bool consumedFuel = true;
            burning.OnFireDeath = consume => { deaths++; consumedFuel = consume; };
            for (int i = 0; i < 33 && burning.IsBurning; i++)
            { AccessTools.Method(typeof(BEBehaviorBurning), "OnTick").Invoke(burning, [.25f]); Call("TickBuildingProtection", system, .25f); }
            check(deaths == 1 && !consumedFuel && removed == 2 && !life.BuildingFires.Values.Any(f => f.Ignited), "Pilot flame bypassed wood threshold or consumed cold structural fuel.");
            Call("ClearBuildingState", system, pos);
            Expose(46);
            burning.IsBurning = true; burning.remainingBurnDuration = .1f;
            AccessTools.Method(typeof(BEBehaviorBurning), "OnTick").Invoke(burning, [.25f]);
            check(burning.IsBurning && burning.remainingBurnDuration == 8 && deaths == 1, "Established structural fire did not sustain itself.");
            double priorDamage = life.BuildingFires.Values.First(f => f.X == pos.X && f.Y == pos.Y && f.Z == pos.Z).DamageSeconds;
            burning.KillFire(true);
            check(deaths == 2 && !consumedFuel && life.BuildingFires.Values.First(f => f.X == pos.X && f.Y == pos.Y && f.Z == pos.Z).DamageSeconds == priorDamage, "Native fire death consumed fuel or erased burn damage.");
        }
        finally { Call("StopBuildingProtection", system); AccessTools.Field(type, "instance").SetValue(null, previous); }
    }
}
