using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using SeraphCivilizationSimulation;

// Exercise the installed Roofing DLL and real Harmony patches, with world/GPU services replaced.
internal static class RoofingPlacementChecks
{
    public static void Run(string roofingZip, Action<bool, string> check)
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
        string oldLocale = Lang.CurrentLocale;
        bool hadEnglish = Lang.AvailableLanguages.TryGetValue("en", out var oldEnglish);
        var translations = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(
            File.ReadAllText("assets/vsscsmod/lang/zh-cn.json"))!;
        Lang.AvailableLanguages["en"] = Proxy.Make<ITranslationService>((m, a) => m.Name.StartsWith("Get") && a?[0] is string key
            ? translations.GetValueOrDefault(key.Replace("vsscsmod:", ""), key) : null);
        Lang.ChangeLanguage("en");
        try
        {
            using var zip = ZipFile.OpenRead(roofingZip);
            using var source = (zip.GetEntry("vsroofing.dll") ?? throw new InvalidDataException("Roofing DLL is missing.")).Open();
            using var memory = new MemoryStream();
            source.CopyTo(memory);
            memory.Position = 0;
            CheckPlacement(AssemblyLoadContext.Default.LoadFromStream(memory), check);
        }
        finally
        {
            AssemblyLoadContext.Default.Resolving -= Resolve;
            if (hadEnglish) Lang.AvailableLanguages["en"] = oldEnglish!;
            else Lang.AvailableLanguages.Remove("en");
            Lang.ChangeLanguage(oldLocale);
        }
    }

    private static void CheckPlacement(Assembly roofing, Action<bool, string> check)
    {
        const string patchId = "vsscsmod.roofing";
        const string orientation = "vsroofing.orientation";
        const string reverse = "vsscsmod.roofingReverse";
        Type Type(string name) => roofing.GetType("VSRoofing." + name, throwOnError: true)!;
        var behaviorType = Type("CollectibleBehaviorPlaceFrame");
        var blockType = Type("RoofBlock");
        var compatibilityType = typeof(VscnCivilizationModSystem).Assembly.GetType("SeraphCivilizationSimulation.RoofingPlacementCompatibility")!;
        IDisposable Lease() => (IDisposable)Activator.CreateInstance(compatibilityType)!;
        void Set(object target, string field, object? value) => AccessTools.Field(target.GetType(), field).SetValue(target, value);
        object Create(string name) => Activator.CreateInstance(Type(name))!;
        var getModes = AccessTools.Method(behaviorType, "GetToolModes");
        var setMode = AccessTools.Method(behaviorType, "SetToolMode");
        var placeFrame = AccessTools.Method(blockType, "PlaceFrame");
        using var serverLease = Lease();
        using var clientLease = Lease();
        check(GlobalConstants.IgnoredStackAttributes.Count(a => a == reverse) == 1,
            "Relative direction preference can prevent material stacking or was registered twice.");
        foreach (var method in new[] { getModes, setMode, placeFrame })
            check(Harmony.GetPatchInfo(method)?.Prefixes.Count(p => p.owner == patchId) == 1,
                "Integrated server/client installed duplicate Roofing patches or missed " + method.Name);

        EnumAppSide side = EnumAppSide.Client;
        int pushedEvents = 0, placedBlocks = 0, lastBlockId = 0, playedSounds = 0;
        AssetLocation? requestedCode = null;
        var mainItem = new Item { Code = new AssetLocation("game:stick") };
        var offhandItem = new Item { Code = new AssetLocation("game:hammer-copper"), Tool = EnumTool.Hammer };
        var slot = new DummySlot(new ItemStack(mainItem, 16));
        var offhand = new DummySlot(new ItemStack(offhandItem));
        var inventory = Proxy.Make<IPlayerInventoryManager>((m, a) => m.Name == "get_OffhandHotbarSlot" ? offhand : null);
        var events = Proxy.Make<IClientEventAPI>((m, a) => { if (m.Name == "PushEvent") pushedEvents++; return null; });
        var entity = new EntityPlayer { LocalEyePos = new Vec3d(0, 1.62, 0) };
        var player = Proxy.Make<IClientPlayer>((m, a) => m.Name switch
        { "get_Entity" => entity, "get_InventoryManager" => inventory, _ => null });
        var variants = BlockFacing.HORIZONTALS.Select((f, i) => new Block
            { BlockId = i + 1, Code = new AssetLocation("vsroofing:roof-bare-" + f.Code) }).ToArray();
        var blocks = Proxy.Make<IBlockAccessor>((m, a) =>
        {
            if (m.Name == "GetBlock" && a?[0] is AssetLocation code)
            {
                requestedCode = code;
                return variants.SingleOrDefault(b => b.Code.Equals(code));
            }
            if (m.Name == "SetBlock") { placedBlocks++; lastBlockId = (int)a![0]!; }
            return null;
        });
        ICoreClientAPI? api = null;
        var world = Proxy.Make<IWorldAccessor>((m, a) =>
        {
            if (m.Name == "PlaySoundAt") playedSounds++;
            return m.Name switch { "get_Side" => side, "get_Api" => api, "get_BlockAccessor" => blocks, _ => null };
        });
        api = Proxy.Make<ICoreClientAPI>((m, a) => m.Name switch
        { "get_Side" => side, "get_World" => world, "get_Event" => events, _ => null });
        entity.World = world;
        var behavior = Activator.CreateInstance(behaviorType, mainItem)!;
        Set(behavior, "Api", api);
        SkillItem[] Menu(int count, string prefix) => Enumerable.Range(0, count).Select(i => new SkillItem { Name = prefix + i }).ToArray();
        var standard = Menu(14, "standard-");
        var advanced = Menu(16, "advanced-");
        var eaves = Menu(6, "eaves-");
        var ridge = Menu(6, "ridge-");
        Set(behavior, "toolModes", standard);
        Set(behavior, "advancedToolModes", advanced);
        Set(behavior, "advancedEaveToolModes", eaves);
        Set(behavior, "advancedRidgeToolModes", ridge);
        Set(behavior, "standardTextures", new LoadedTexture[6]);
        Set(behavior, "eaveTextures", new LoadedTexture[6]);
        Set(behavior, "ridgetextures", new LoadedTexture[2]);
        var directionTextures = Enumerable.Range(0, 8).Select(_ =>
            (LoadedTexture)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LoadedTexture))).ToArray();
        Set(behavior, "orientationTextures", directionTextures);
        var frame = Create("AutoRoofFrame");
        var material = Create("AutoRoofMaterialInfo");
        Set(material, "Code", new[] { mainItem.Code });
        Set(frame, "Material", material);
        var frameOffhand = Create("AutoRoofOffhandInfo");
        Set(frameOffhand, "Tool", EnumTool.Hammer);
        Set(frame, "Offhand", frameOffhand);
        var frames = (IDictionary)AccessTools.Field(blockType, "Frames").GetValue(null)!;
        const string frameKey = "vscncs-test-frame";
        frames.Add(frameKey, frame);
        try
        {
            var selection = new BlockSelection
            { Position = new BlockPos(100, 110, 100), Face = BlockFacing.UP, HitPosition = new Vec3d(.5, .5, .5) };
            SkillItem[]? GetModes() => (SkillItem[]?)getModes.Invoke(behavior, [slot, player, selection]);
            void Select(int mode) => setMode.Invoke(behavior, [slot, player, selection, mode]);
            slot.Itemstack!.Attributes.SetInt(orientation, 2);
            var menu = GetModes();
            check(menu?.Length == 11 && menu.Take(10).SequenceEqual(standard.Take(10)), "Standard menu lost roof options or the relative direction toggle.");
            check(ReferenceEquals(menu![10], standard[12]) && ReferenceEquals(menu[10].Texture, directionTextures[2])
                && menu[10].Linebreak && menu[10].Name == "背对玩家方向", "Relative direction toggle did not reuse the hidden south arrow or show its label.");
            check(standard.Length == 14, "Filtering damaged Roofing's internal icon-update array.");
            check(!slot.Itemstack.Attributes.HasAttribute(orientation), "Opening the menu retained an old direction lock.");
            offhand.Itemstack = null;
            check(GetModes() == null, "An invalid material/tool combination gained a Roofing menu.");
            offhand.Itemstack = new ItemStack(offhandItem);
            foreach (int mode in new[] { 0, 1, 2, 4, 5, 6, 8 })
            {
                Select(mode);
                check(slot.Itemstack.Attributes.GetInt("vsroofing.roofMode") == mode, "A normal roof mode no longer works.");
            }
            Select(10);
            check(slot.Itemstack.Attributes.GetBool(reverse) && slot.Itemstack.Attributes.GetInt("vsroofing.roofMode") == 8,
                "Relative direction toggle changed the selected roof shape.");
            check(ReferenceEquals(GetModes()![10].Texture, directionTextures[6]) && slot.Itemstack.Attributes.GetBool(reverse),
                "Reopening the menu reset relative direction or lost the active icon.");
            foreach (int mode in new[] { 11, 12, 13, 99 })
            {
                slot.Itemstack.Attributes.SetInt(orientation, 1);
                Select(mode);
                check(!slot.Itemstack.Attributes.HasAttribute(orientation)
                    && slot.Itemstack.Attributes.GetInt("vsroofing.roofMode") == 8,
                    "An old fixed-direction selection changed the roof mode or restored a lock.");
            }
            Select(10);
            check(!slot.Itemstack.Attributes.HasAttribute(reverse) && ReferenceEquals(GetModes()![10].Texture, directionTextures[2]),
                "Relative direction toggle could not return to normal placement.");
            Select(10);
            Select(3);
            check(ReferenceEquals(GetModes(), advanced), "Advanced roof menu was truncated.");
            Select(14);
            check(slot.Itemstack.Attributes.GetInt("vsroofing.slope") == 5, "A high-index advanced slope was blocked.");
            Select(10);
            check(slot.Itemstack.Attributes.GetBool(reverse), "An advanced slope changed relative direction.");
            Select(15);
            check(slot.Itemstack.Attributes.GetAsInt("vsroofing.page") == 0, "Advanced roof back button was blocked.");
            foreach (int mode in new[] { 7, 9 })
            {
                Select(mode);
                check(ReferenceEquals(GetModes(), mode == 7 ? advanced : ridge), "An advanced eave/ridge menu changed.");
                // Roofing opens the full slope page for eaves; also verify its dedicated slopes page.
                slot.Itemstack.Attributes.SetInt("vsroofing.page", 2);
                check(ReferenceEquals(GetModes(), mode == 7 ? eaves : ridge), "Dedicated slope menu changed.");
                Select(4);
                check(slot.Itemstack.Attributes.GetInt("vsroofing.slope") == 5, "Eave/ridge slope selection was blocked.");
                Select(5);
                check(slot.Itemstack.Attributes.GetAsInt("vsroofing.page") == 0, "Slopes back button was blocked.");
            }
            check(pushedEvents > 0, "Advanced navigation lost its keep-open event.");
            check(slot.Itemstack.Attributes.GetBool(reverse), "Advanced navigation reset relative direction.");
            Select(10);

            var roof = (Block)Activator.CreateInstance(blockType)!;
            roof.Code = new AssetLocation("vsroofing:roof-bare-north");
            roof.Sounds = new BlockSounds { Place = new SoundAttributes { Location = new AssetLocation("game:sounds/block/planks") } };
            bool Place(ItemStack stack) => (bool)placeFrame.Invoke(roof, [world, selection, player, stack])!;
            foreach (var testSide in new[] { EnumAppSide.Client, EnumAppSide.Server })
            {
                side = testSide;
                foreach (var offset in new[] { (-3, 0), (3, 0), (0, -3), (0, 3) })
                {
                    entity.Pos.SetPos(100.5 + offset.Item1, 110, 100.5 + offset.Item2);
                    foreach (bool reversed in new[] { false, true })
                    {
                        var facing = Block.SuggestedHVOrientation(player, selection)[0];
                        string expected = "roof-bare-" + (reversed ? facing.Opposite : facing).Code;
                        foreach (int oldLock in new[] { -1, 0, 1, 2, 3, 999 })
                        {
                            var stack = new ItemStack(mainItem, 16);
                            if (reversed) stack.Attributes.SetBool(reverse, true);
                            if (oldLock != -1) stack.Attributes.SetInt(orientation, oldLock);
                            int before = placedBlocks;
                            check(Place(stack) && placedBlocks == before + 1 && requestedCode?.Path == expected,
                                "Placement did not follow the player on " + side + " with old lock " + oldLock);
                            check(!stack.Attributes.HasAttribute(orientation) && lastBlockId > 0 && stack.StackSize == 16,
                                "Placement retained the direction lock or damaged material accounting.");
                            check(stack.Attributes.GetBool(reverse) == reversed, "Placement lost the relative direction setting.");
                        }
                    }
                }
            }
            check(playedSounds == placedBlocks, "Roofing placement sounds were lost.");
            var invalidSlope = new ItemStack(mainItem);
            invalidSlope.Attributes.SetInt("vsroofing.page", 1);
            check(!Place(invalidSlope), "Placement bypassed Roofing's missing-slope check.");
            var blockStack = new ItemStack(variants[0]);
            check(!Place(blockStack), "Placement bypassed Roofing's material-item check.");
            check(roof.Code.Path == "roof-bare-north", "Placement modified an existing block definition.");

            clientLease.Dispose();
            clientLease.Dispose();
            check(Harmony.GetPatchInfo(placeFrame)?.Owners.Contains(patchId) == true,
                "Closing one side removed patches still needed by the other side.");
            serverLease.Dispose();
            foreach (var method in new[] { getModes, setMode, placeFrame })
                check(Harmony.GetPatchInfo(method)?.Owners.Contains(patchId) != true, "Roofing patches were not removed on final disposal.");
            using var nextWorld = Lease();
            check(GetModes()?.Length == 11, "Roofing patches did not reinstall for the next world.");
        }
        finally { frames.Remove(frameKey); }

        bool roofingEnabled = false;
        INetworkChannel? networkChannel = null;
        networkChannel = Proxy.Make<INetworkChannel>((m, a) => m.Name == "RegisterMessageType" ? networkChannel : null);
        var network = Proxy.Make<INetworkAPI>((m, a) => m.Name == "RegisterChannel" ? networkChannel : null);
        var loader = Proxy.Make<IModLoader>((m, a) => m.Name == "IsModEnabled" ? roofingEnabled && a?[0] as string == "vsroofing" : null);
        var logger = Proxy.Make<ILogger>((m, a) => null);
        var core = Proxy.Make<ICoreAPI>((m, a) => m.Name switch
        { "get_Network" => network, "get_ModLoader" => loader, "get_Logger" => logger, _ => null });
        var withoutRoofing = new VscnCivilizationModSystem();
        try
        {
            withoutRoofing.Start(core);
            check(Harmony.GetPatchInfo(placeFrame)?.Owners.Contains(patchId) != true,
                "A disabled Roofing mod still installed compatibility patches.");
        }
        finally { withoutRoofing.Dispose(); }
        roofingEnabled = true;
        var server = new VscnCivilizationModSystem();
        var client = new VscnCivilizationModSystem();
        try
        {
            server.Start(core);
            client.Start(core);
            check(Harmony.GetPatchInfo(placeFrame)?.Owners.Contains(patchId) == true,
                "The universal mod startup did not enable Roofing compatibility.");
            client.Dispose();
            check(Harmony.GetPatchInfo(placeFrame)?.Owners.Contains(patchId) == true,
                "Universal client disposal removed server placement enforcement.");
        }
        finally { client.Dispose(); server.Dispose(); }
        check(Harmony.GetPatchInfo(placeFrame)?.Owners.Contains(patchId) != true,
            "Universal mod disposal left Roofing patches installed.");
        check(!typeof(VscnCivilizationModSystem).Assembly.GetReferencedAssemblies().Any(a => a.Name == "vsroofing"),
            "Optional Roofing compatibility added a hard assembly dependency.");
    }
}
