using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using SeraphCivilizationSimulation;

internal static class ChiselToolsSurvivalChecks
{
    public static void Run(string package, Action<bool, string> check)
    {
        string game = Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? "E:/vintagestory/Vintagestory";
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            foreach (string directory in new[] { game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods") })
            {
                string file = Path.GetFullPath(Path.Combine(directory, name.Name + ".dll"));
                if (File.Exists(file)) return context.LoadFromAssemblyPath(file);
            }
            return null;
        }
        AssemblyLoadContext.Default.Resolving += Resolve;
        try
        {
            using var zip = ZipFile.OpenRead(package);
            using var input = zip.GetEntry("chiseltools.dll")!.Open();
            using var dll = new MemoryStream();
            input.CopyTo(dll); dll.Position = 0;
            Verify(AssemblyLoadContext.Default.LoadFromStream(dll), zip, check);
        }
        finally { AssemblyLoadContext.Default.Resolving -= Resolve; }
    }

    private static void Verify(Assembly qp, ZipArchive zip, Action<bool, string> check)
    {
        static bool HasJsonPath(JToken root, string pointer)
        {
            JToken? token = root;
            foreach (string part in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                string key = part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                token = token switch
                {
                    JObject obj => obj[key],
                    JArray array when int.TryParse(key, out int index) && index >= 0 && index < array.Count => array[index],
                    _ => null
                };
                if (token == null) return false;
            }
            return true;
        }

        Lang.AvailableLanguages["en"] = Proxy.Make<ITranslationService>((m, a) => m.Name.StartsWith("Get") ? a?[0] : null);
        Lang.ChangeLanguage("en");
        const string patchId = "vsscsmod.chiseltools";
        Type Qp(string name) => qp.GetType(name, true)!;
        var compat = typeof(VscnCivilizationModSystem).Assembly.GetType("SeraphCivilizationSimulation.ChiselToolsSurvivalCompatibility", true)!;
        IDisposable Lease() => (IDisposable)Activator.CreateInstance(compat)!;
        using var serverLease = Lease();
        using var clientLease = Lease();
        var pantoType = Qp("chisel.src.ItemPantograph");
        var interaction = AccessTools.Method(pantoType, "OnHeldInteractStart");
        check(Harmony.GetPatchInfo(interaction)?.Prefixes.Count(p => p.owner == patchId) == 1, "Duplicate/missing QP patch with integrated server/client.");
        bool access = true;
        EnumAppSide side = EnumAppSide.Client;
        int queries = 0, errors = 0;
        var target = new TestChisel { Pos = new BlockPos(10, 100, 10), BlockIds = [1], VoxelCuboids = [Cuboid(16)] };
        int currentBlockId = 1, setBlockCalls = 0;
        BlockEntityMicroBlock? currentTarget = target;
        var entity = new EntityPlayer();
        entity.WatchedAttributes.SetString("playerUID", "owner");
        EnumGameMode gameMode = EnumGameMode.Survival;
        var playerData = Proxy.Make<IWorldPlayerData>((m, a) => m.Name == "get_CurrentGameMode" ? gameMode : null);
        var player = Proxy.Make<IClientPlayer>((m, a) => m.Name switch
        { "get_PlayerUID" => "owner", "get_Entity" => entity, "get_WorldData" => playerData, _ => null });
        var blocks = new[] { new Block(), new BlockChisel { BlockId = 1, Code = new AssetLocation("game:stone-granite") },
            new Block { BlockId = 2, Code = new AssetLocation("game:stone-basalt") } };
        var claims = Proxy.Make<ILandClaimAPI>((m, a) => m.Name == "TryAccess" ? access : null);
        var accessor = Proxy.Make<IBlockAccessor>((m, a) =>
        {
            if (m.Name == "GetBlockEntity") { queries++; return currentTarget; }
            if (m.Name == "GetBlock" && a?[0] is BlockPos) return blocks[currentBlockId];
            if (m.Name == "SetBlock")
            {
                currentBlockId = (int)a![0]!;
                if (currentBlockId == 0) currentTarget = null;
                else if (currentTarget == null)
                {
                    target = new TestChisel { Pos = target.Pos.Copy(), Api = target.Api, Block = blocks[currentBlockId] };
                    currentTarget = target;
                }
                setBlockCalls++;
                return null;
            }
            return null;
        });
        var registry = new RecipeRegistrySystem();
        bool qpEnabled = false;
        var loader = Proxy.Make<IModLoader>((m, a) => m.Name switch
        {
            "GetModSystem" when m.IsGenericMethod && m.GetGenericArguments()[0] == typeof(RecipeRegistrySystem) => registry,
            "IsModEnabled" => qpEnabled && a?[0] as string == "chiseltools", _ => null
        });
        var grid = new List<GridRecipe>();
        var collectibles = new List<CollectibleObject>();
        var road = new Block { Code = new AssetLocation("chiseltools:pathedchiseledblock"), WalkSpeedMultiplier = 1.5f };
        var ladder = new Block { Code = new AssetLocation("chiseltools:climbablechiseledblock"), Climbable = true };
        ICoreClientAPI? api = null;
        var world = Proxy.Make<IServerWorldAccessor>((m, a) => m.Name switch
        {
            "get_Rand" => new Random(1), "get_Api" => api, "get_Side" => side, "get_Claims" => claims, "get_BlockAccessor" => accessor,
            "PlayerByUid" => player, "get_Blocks" => blocks, "get_GridRecipes" => grid, "get_Collectibles" => collectibles,
            "GetBlock" when a?[0] is int blockId => blockId >= 0 && blockId < blocks.Length ? blocks[blockId] : null,
            "GetBlock" when a?[0] is AssetLocation code => code.Path == "pathedchiseledblock" ? road : ladder, _ => null
        });
        var logger = Proxy.Make<ILogger>((m, a) => null);
        Action? texturesLoaded = null;
        var events = Proxy.Make<IClientEventAPI>((m, a) =>
        {
            if (m.Name == "add_BlockTexturesLoaded") texturesLoaded += (Action)a![0]!;
            if (m.Name == "remove_BlockTexturesLoaded") texturesLoaded -= (Action)a![0]!;
            return null;
        });
        INetworkChannel? channel = null;
        channel = Proxy.Make<INetworkChannel>((m, a) => m.Name == "RegisterMessageType" ? channel : null);
        var network = Proxy.Make<INetworkAPI>((m, a) => m.Name == "RegisterChannel" ? channel : null);
        api = Proxy.Make<ICoreClientAPI>((m, a) =>
        {
            if (m.Name == "TriggerIngameError") errors++;
            return m.Name switch
            { "get_World" => world, "get_ModLoader" => loader, "get_Side" => side, "get_Logger" => logger,
              "get_Event" => events, "get_Network" => network, _ => null };
        });
        entity.World = world; entity.Api = api;
        target.Api = api; target.Block = blocks[1];
        var serverApi = Proxy.Make<ICoreServerAPI>((m, a) => m.Name switch
        { "get_World" => world, "get_ModLoader" => loader, "get_Side" => EnumAppSide.Server, "get_Logger" => logger, _ => null });
        var selection = new BlockSelection { Position = target.Pos, Face = BlockFacing.UP, HitPosition = new Vec3d(.5, .5, .5) };
        void Interact(Item tool, DummySlot slot)
        {
            object?[] args = [slot, entity, selection, null, true, EnumHandHandling.NotHandled];
            AccessTools.Method(tool.GetType(), "OnHeldInteractStart").Invoke(tool, args);
            check((EnumHandHandling)args[5]! == EnumHandHandling.PreventDefaultAction, "Restricted interaction fell through: " + tool.Code);
        }
        Item Tool(string type, string code)
        {
            var tool = (Item)Activator.CreateInstance(Qp(type))!;
            tool.Code = new AssetLocation("chiseltools:" + code);
            AccessTools.Field(typeof(CollectibleObject), "api").SetValue(tool, api);
            return tool;
        }
        foreach (var entry in new[] { ("chisel.src.ItemLadderMaker", "laddermaker-steel"),
            ("chiseltools.src.ItemJonasPaintBrush", "jonaspaintbrush-steel") })
        {
            var tool = Tool(entry.Item1, entry.Item2);
            Interact(tool, new DummySlot(new ItemStack(tool)));
        }
        var palette = Tool("chiseltools.src.ItemPalette", "palette-light");
        var paletteSlot = new DummySlot(new ItemStack(palette));
        var water = Activator.CreateInstance(Qp("chiseltools.src.BlockDecoWaterSource"))!;
        check((bool)AccessTools.Method(water.GetType(), "OnBlockInteractStart").Invoke(water, [world, player, selection])!, "Water interaction was not consumed.");
        entity.Controls.ShiftKey = true;
        var brush = Tool("chisel.src.ItemPaintBrush", "paintbrush-steel");
        Interact(brush, new DummySlot(new ItemStack(brush)));
        entity.Controls.ShiftKey = false;
        var chisel = Tool("chiseltools.src.TrueChisel", "truechisel-steel");
        var nativeModes = Enumerable.Range(0, 12).Select(i => new SkillItem()).ToArray();
        AccessTools.Field(typeof(ItemChisel), "ToolModes").SetValue(chisel, nativeModes);
        AccessTools.Field(typeof(ItemChisel), "addMatItem").SetValue(chisel, new SkillItem());
        int menuQueries = queries;
        AccessTools.Method(typeof(ItemChisel), "GetToolModes").Invoke(chisel, [new DummySlot(new ItemStack(chisel)), player, selection]);
        check(nativeModes.All(m => m.Enabled), "True Chisel paint modes were not enabled.");
        var nativeChisel = new ItemChisel();
        var ordinaryModes = Enumerable.Range(0, 12).Select(i => new SkillItem()).ToArray();
        AccessTools.Field(typeof(ItemChisel), "ToolModes").SetValue(nativeChisel, ordinaryModes);
        AccessTools.Field(typeof(ItemChisel), "addMatItem").SetValue(nativeChisel, new SkillItem());
        nativeChisel.GetToolModes(new DummySlot(), player, selection);
        check(ordinaryModes.All(m => m.Enabled), "QP restriction affected the original game's chisel menu.");
        queries = menuQueries;
        check(queries == 0 && errors > 0, "Restricted old tools reached their target or omitted feedback.");

        var panto = Tool("chisel.src.ItemPantograph", "megapantograph-steel");
        var modes = new[] { "SHAPEPASTE", "PASTEALL", "ADDPASTE", "ADDPASTEALL", "MIRRORPASTE", "CHANGEMAT", "UNDO", "PLANEALL", "WEDGEROTATE", "PLANEMATERIAL", "PLANEISLAND", "WEDGEFLIP" };
        AccessTools.Field(pantoType, "localmodes").SetValue(panto, modes);
        var menu = modes.Select(m => new SkillItem { Code = new AssetLocation(m) }).ToArray();
        AccessTools.Field(pantoType, "toolModes").SetValue(panto, menu);
        var slot = new DummySlot(new ItemStack(panto));
        check(ReferenceEquals(menu, AccessTools.Method(pantoType, "GetToolModes").Invoke(panto, [slot, player, selection])), "QP menu indices changed.");
        AccessTools.Method(pantoType, "Undo").Invoke(panto, [slot]);
        check(queries == 0 && menu.All(m => m.Enabled), "A QP mode was disabled or reached the world before use.");
        // Palette checks only observe dispatch; voxel behavior is exercised below with the actual QP methods.
        var interactionProbe = new Harmony("vscncs.tests.qp-restored-interactions");
        var observeInteraction = new HarmonyMethod(typeof(ChiselToolsSurvivalChecks), nameof(PermittedInteraction)) { priority = Priority.Last };
        interactionProbe.Patch(AccessTools.Method(palette.GetType(), "OnHeldInteractStart"), prefix: observeInteraction);
        try
        {
            foreach (var testSide in new[] { EnumAppSide.Client, EnumAppSide.Server })
            {
                side = testSide;
                foreach (string code in new[] { "palette-standard", "palette-glow", "palette-light" })
                {
                    var restoredPalette = Tool("chiseltools.src.ItemPalette", code);
                    foreach (bool shift in new[] { false, true })
                    {
                        entity.Controls.ShiftKey = shift; PermittedPackets = 0;
                        Interact(restoredPalette, new DummySlot(new ItemStack(restoredPalette)));
                        check(PermittedPackets == 1, $"Palette {code}, shift={shift} blocked on {testSide}.");
                    }
                }
                entity.Controls.ShiftKey = false;
                foreach (int mode in new[] { 1, 2, 3, 4, 5, 7, 8, 9, 10, 11 })
                {
                    slot.Itemstack!.Attributes.SetInt("toolMode", 0);
                    AccessTools.Method(pantoType, "SetToolMode").Invoke(panto, [slot, player, selection, mode]);
                    check(menu[mode].Enabled && slot.Itemstack.Attributes.GetInt("toolMode") == mode,
                        $"Restored mode {modes[mode]} could not be selected on {testSide}.");
                }
                slot.Itemstack!.Attributes.SetInt("toolMode", 0);
            }
        }
        finally { entity.Controls.ShiftKey = false; side = EnumAppSide.Client; interactionProbe.UnpatchAll(interactionProbe.Id); }
        slot.Itemstack!.Attributes.SetBytes("copiedmateriallist", SerializerUtil.Serialize(new List<int> { 1 }));
        foreach (var testSide in new[] { EnumAppSide.Client, EnumAppSide.Server })
        {
            side = testSide;
            foreach (int mode in new[] { 0, 2 }) // Native SHAPEPASTE and ADDPASTE must be able to fill a partial block.
            {
                currentBlockId = 1; currentTarget = target;
                target.BlockIds = [1]; target.VoxelCuboids = [Cuboid(1)];
                target.AvailMaterialQuantities = [1];
                slot.Itemstack.Attributes.SetInt("toolMode", mode);
                slot.Itemstack.Attributes.SetBytes("copiedblockvoxels", SerializerUtil.Serialize(new List<uint> { Cuboid(8) }));
                Interact(panto, slot);
                check(target.VoxelCuboids.SequenceEqual(new[] { Cuboid(8) }) && target.VolumeRel == .125f,
                    $"Native {modes[mode]} was limited or rolled back on {testSide}.");
            }
        }

        side = EnumAppSide.Server;
        currentBlockId = 1; currentTarget = target;
        target.BlockIds = [1]; target.VoxelCuboids = [Cuboid(8)];
        slot.Itemstack.Attributes.SetInt("toolMode", 0);
        slot.Itemstack.Attributes.SetBytes("copiedblockvoxels", SerializerUtil.Serialize(new List<uint>()));
        int setBlocksBeforeClear = setBlockCalls;
        Interact(panto, slot);
        check(currentBlockId == 0 && currentTarget == null && setBlockCalls == setBlocksBeforeClear + 1,
            "Clearing all copied voxels left an empty chiseled block instead of air.");
        var clearedTarget = target;
        int previousToolMode = slot.Itemstack.Attributes.GetInt("lastToolMode", 0);
        AccessTools.Method(pantoType, "SetToolMode").Invoke(panto, [slot, player, selection, 6]);
        check(currentBlockId == 1 && currentTarget != null && !ReferenceEquals(currentTarget, clearedTarget)
            && currentTarget.VoxelCuboids.SequenceEqual(new[] { Cuboid(8) })
            && slot.Itemstack.Attributes.GetInt("toolMode") == previousToolMode,
            "Undo did not restore the cleared chiseled block and previous tool mode.");

        // Execute each real TrueChisel paint mode on the server, without an interaction probe.
        chisel.Attributes = new JsonObject(JObject.Parse("{\"needshammer\":false}"));
        AccessTools.Field(typeof(CollectibleObject), "api").SetValue(chisel, serverApi);
        entity.Api = serverApi;
        target.BlockName = "Test chiseled block";
        selection.HitPosition = new Vec3d(.01, 1.0 / 16, .01);
        for (int mode = 7; mode <= 10; mode++)
        {
            target.BlockIds = [1, 2]; target.VoxelCuboids = [Cuboid(1)];
            target.AvailMaterialQuantities = [1, 0];
            var paintSlot = new DummySlot(new ItemStack(chisel));
            paintSlot.Itemstack!.Attributes.SetInt("toolMode", mode);
            paintSlot.Itemstack.Attributes.SetInt("materialId", 2);
            object?[] paintArgs = [paintSlot, entity, selection, null, true, EnumHandHandling.NotHandled];
            AccessTools.Method(chisel.GetType(), "OnHeldInteractStart").Invoke(chisel, paintArgs);
            check(target.VoxelCuboids.SequenceEqual(new[] { Cuboid(1 << (mode - 7), 1) }),
                $"TrueChisel paint mode {mode} did not execute native volume/material changes.");
        }
        entity.Api = api;
        selection.HitPosition = new Vec3d(.5, .5, .5);

        // Exercise actual packet handlers with increased volume and a material not on the target.
        var qpLoader = Activator.CreateInstance(Qp("chisel.src.ChiselToolLoader"))!;
        AccessTools.Field(qpLoader.GetType(), "sapi").SetValue(qpLoader, serverApi);
        var packetType = Qp("chisel.src.PantographData");
        object Packet(List<uint> shape, int[] materials)
        {
            var data = Activator.CreateInstance(packetType)!;
            AccessTools.Field(packetType, "pos").SetValue(data, target.Pos);
            AccessTools.Field(packetType, "voxeldata").SetValue(data, SerializerUtil.Serialize(shape));
            AccessTools.Field(packetType, "matdata").SetValue(data, SerializerUtil.Serialize(materials));
            return data;
        }
        foreach (string name in new[] { "PantographDataToBlockEntity", "PasteNetHandler" })
        {
            currentBlockId = 1; currentTarget = target;
            target.BlockIds = name == "PasteNetHandler" ? [1, 2] : [1];
            target.VoxelCuboids = [Cuboid(1)];
            AccessTools.Method(qpLoader.GetType(), name).Invoke(qpLoader, [player, Packet([Cuboid(8)], [2])]);
            check(target.VoxelCuboids.SequenceEqual(new[] { Cuboid(8) }) && target.VolumeRel == .125f
                && (name != "PantographDataToBlockEntity" || target.BlockIds.SequenceEqual(new[] { 2 })),
                $"Native {name} still limited added volume/materials or left a stale selection cache.");
            access = false;
            AccessTools.Method(qpLoader.GetType(), name).Invoke(qpLoader, [player, Packet([Cuboid(16)], [2])]);
            check(target.VoxelCuboids.SequenceEqual(new[] { Cuboid(8) }), $"Native {name} bypassed land claims.");
            access = true;
            AccessTools.Method(qpLoader.GetType(), name).Invoke(qpLoader, [player, Packet([], [2])]);
            check(currentTarget == null && currentBlockId == 0, $"Native {name} left an empty block.");
        }
        currentBlockId = 1; currentTarget = target;
        target.BlockIds = [1]; target.VoxelCuboids = [Cuboid(8)];

        var doorType = Qp("chisel.src.BEFunctionChiseled");
        var door = (BlockEntityMicroBlock)Activator.CreateInstance(doorType)!;
        door.Pos = target.Pos; door.Api = serverApi;
        AccessTools.Field(doorType, "sapi").SetValue(door, serverApi);
        var addState = AccessTools.Method(doorType, "AddState");
        addState.Invoke(door, ["OPEN", new List<uint> { Cuboid(16) }, new List<int> { 2 }, true, false]);
        var stateVoxels = (Dictionary<string, List<uint>>)AccessTools.Field(doorType, "statevoxels").GetValue(door)!;
        var stateMaterials = (Dictionary<string, List<int>>)AccessTools.Field(doorType, "statematerials").GetValue(door)!;
        check(stateVoxels["OPEN"].SequenceEqual(new[] { Cuboid(16) }) && stateMaterials["OPEN"].SequenceEqual(new[] { 2 }),
            "Native door states still limited volume/materials.");
        addState.Invoke(door, ["EMPTY", null, new List<int> { 2 }, true, false]);
        check(stateVoxels["EMPTY"] is { Count: 0 }, "Empty door shape was not normalized into a list.");
        check(Harmony.GetPatchInfo(AccessTools.Method(qpLoader.GetType(), "DoorDataHandler"))?.Owners.Contains(patchId) != true
            && Harmony.GetPatchInfo(AccessTools.Method(doorType, "OnReceivedClientPacket"))?.Owners.Contains(patchId) != true,
            "Door creation/state packets still have compatibility restrictions.");

        string[] disabled = ["chiseledwatersource", "laddermaker-iron", "laddermaker-meteoriciron", "laddermaker-steel", "jonaspaintbrush-steel"];
        string[] enabled = ["palette-light", "palette-standard", "halfcreativelight-32"];
        void Seed()
        {
            grid.Clear(); registry.SmithingRecipes.Clear(); collectibles.Clear();
            foreach (string code in disabled.Concat(enabled))
            {
                var item = new Item { Code = new AssetLocation("chiseltools:" + code), CreativeInventoryTabs = ["general"], LightHsv = new byte[] { 0, 0, 18 } };
                collectibles.Add(item);
                grid.Add(new GridRecipe { Output = new CraftingRecipeIngredient { Code = item.Code } });
                registry.SmithingRecipes.Add(new SmithingRecipe { RecipeId = 99, Output = new JsonItemStack { Code = item.Code } });
            }
            // Deserialized grid recipe outputs may retain only the resolved stack.
            grid.Add(new GridRecipe { Output = new CraftingRecipeIngredient { ResolvedItemStack = new ItemStack(collectibles[0]) } });
            registry.SmithingRecipes.Add(new SmithingRecipe { Output = new JsonItemStack { ResolvedItemstack = new ItemStack(collectibles[0]) } });
        }
        foreach (var testSide in new[] { EnumAppSide.Client, EnumAppSide.Server })
        {
            Seed(); side = testSide;
            compat.GetMethod("FinalizeAssets", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [api]);
            check(grid.Count == enabled.Length && registry.SmithingRecipes.Count == enabled.Length
                && registry.SmithingRecipes.All(recipe => recipe.RecipeId == 99), "Recipe removal affected unrelated recipes/IDs or retained restricted outputs.");
            foreach (var item in collectibles.Take(disabled.Length))
                check(item.CreativeInventoryTabs.Length == 0 && item.GetHandBookStacks(null!) == null, "Restricted item visible: " + item.Code);
            check(road.WalkSpeedMultiplier == 1.3f && !ladder.Climbable
                && collectibles.Skip(disabled.Length).All(item => item.CreativeInventoryTabs.Length == 1), "Road/ladder restriction failed or an enabled palette/light was hidden.");
            check(collectibles.Where(c => c.Code.Path.StartsWith("jonas", StringComparison.Ordinal)).All(c => c.LightHsv[2] == 0)
                && collectibles.Single(c => c.Code.Path == "halfcreativelight-32").LightHsv[2] == 5,
                "Legacy Jonas light emitted light or fluorescent light exceeded its cap.");
        }
        JArray assetPatches = JArray.Parse(File.ReadAllText("assets/vsscsmod/patches/chiseltools-survival.json"));
        JObject glowLevel = assetPatches.Single(p => p!["path"]!.Value<string>() == "/vertexFlags/glowLevel") as JObject ?? throw new InvalidDataException();
        check(glowLevel["value"]!.Value<int>() == 48, "Fluorescent block still has an excessive glow level.");
        JObject lightLevels = assetPatches.Single(p => p!["path"]!.Value<string>() == "/lightHsvByType"
            && p["file"]!.Value<string>() == "chiseltools:blocktypes/halfcreativelight.json")["value"] as JObject ?? throw new InvalidDataException();
        check(lightLevels.Properties().All(property => property.Value[2]!.Value<int>() == 5), "Fluorescent block light emission is not consistently reduced.");
        foreach (JObject patch in assetPatches)
        {
            string path = patch["file"]!.Value<string>()!.Replace("chiseltools:", "assets/chiseltools/");
            using var reader = new StreamReader(zip.GetEntry(path)!.Open());
            check(HasJsonPath(JObject.Parse(reader.ReadToEnd()), patch["path"]!.Value<string>()!), "QP patch property is missing from actual asset: " + path);
            check(patch["dependsOn"]![0]!["modid"]!.Value<string>() == "chiseltools", "QP asset patch lacks optional dependency.");
        }
        var withoutQp = new VscnCivilizationModSystem();
        try
        {
            withoutQp.Start(api);
            check(texturesLoaded == null, "Disabled QP registered a client asset hook.");
        }
        finally { withoutQp.Dispose(); }
        qpEnabled = true;
        var withQp = new VscnCivilizationModSystem();
        try
        {
            withQp.Start(api); Seed(); side = EnumAppSide.Client;
            check(texturesLoaded != null, "Enabled QP missed client asset synchronization hook.");
            texturesLoaded!(); check(grid.Count == enabled.Length, "Client asset synchronization did not remove restricted recipes or removed enabled recipes.");
        }
        finally { withQp.Dispose(); }
        check(texturesLoaded == null, "Client QP asset hook leaked on disposal.");
        clientLease.Dispose(); clientLease.Dispose();
        check(Harmony.GetPatchInfo(interaction)?.Owners.Contains(patchId) == true, "Client disposal removed server QP patch.");
        serverLease.Dispose();
        check(Harmony.GetPatchInfo(interaction)?.Owners.Contains(patchId) != true, "QP patch leaked on final disposal.");
        using (var nextWorld = Lease()) check(Harmony.GetPatchInfo(interaction)?.Owners.Contains(patchId) == true, "QP patch failed to reinstall.");
        check(!typeof(VscnCivilizationModSystem).Assembly.GetReferencedAssemblies().Any(a => a.Name == "chiseltools"), "QP became a hard assembly dependency.");
    }

    private static uint Cuboid(int size, byte material = 0) => BlockEntityMicroBlock.ToUint(0, 0, 0, size, size, size, material);
    private static int PermittedPackets;
    private static bool PermittedInteraction(ref EnumHandHandling __5)
    {
        PermittedPackets++; __5 = EnumHandHandling.PreventDefaultAction; return false;
    }
    private sealed class TestChisel : BlockEntityChisel
    {
        public int Dirty;
        public override void MarkDirty(bool redrawOnClient = false, IPlayer? skipPlayer = null) => Dirty++;
    }
}
