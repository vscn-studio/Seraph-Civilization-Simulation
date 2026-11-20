using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using SeraphCivilizationSimulation;

internal static class LoreRestrictionChecks
{
    public static void Run(Action<bool, string> check, string? dllDirectory = null)
    {
        var c = new LoreRestrictionsConfig();
        check(c.Active(false) && !c.Active(true), "Lore world gate failed.");
        c.OnlyWhenLoreDisabled = false;
        check(c.Active(true), "Forced mode failed.");
        c.Enabled = false;
        check(!c.Active(false) && !c.Active(true), "Master switch failed.");
        check(!LoreRestrictionSystem.ParseConfig(JObject.Parse("{LivingDeadSpawns:false}")).LivingDeadSpawns,
            "Existing per-content false was lost.");
        check(LoreRestrictionSystem.ParseConfig(new JObject()).SkullOfTheDead, "Missing new switches must use defaults.");
        foreach (string bad in new[] { "{LivingDeadSpawns:0}", "{LivingDeadSpawns:'false'}", "{LivingDeadSpawn:false}" })
        {
            bool rejected = false;
            try { LoreRestrictionSystem.ParseConfig(JObject.Parse(bad)); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "Invalid config was accepted: " + bad);
        }
        VerifyWorldConfigSerialization(check);
        var cases = new (string Switch, string Code, System.Func<LoreRestrictionsConfig, AssetLocation, bool> Match)[]
        {
            ("LivingDeadSpawns", "primitivesurvival:livingdead-normal", (p,a) => p.EntityRestricted(a)),
            ("GreenWillowispSpawns", "primitivesurvival:willowisp-green", (p,a) => p.EntityRestricted(a)),
            ("WhiteWillowispSpawns", "primitivesurvival:willowisp-white", (p,a) => p.EntityRestricted(a)),
            ("YellowWillowispSpawns", "primitivesurvival:willowisp-yellow", (p,a) => p.EntityRestricted(a)),
            ("LivingDeadTemporalGearDrops", "game:gear-temporal", (p,a) => p.LivingDeadDropRestricted(a)),
            ("SkullOfTheDead", "primitivesurvival:skullofthedead-normal", (p,a) => p.CollectibleRestricted(a)),
            ("HandOfTheDead", "primitivesurvival:handofthedead-empty-north", (p,a) => p.CollectibleRestricted(a)),
            ("BoneSoilTemporalGearDrops", "game:gear-temporal", (p,a) => p.PanDropRestricted("@(bonysoil|bonysoil-.*)", a)),
            ("BoneSoilLoreBookDrops", "game:lore-book-flint", (p,a) => p.PanDropRestricted("bonysoil", a)),
            ("BoneSoilLoreCylinderDrops", "game:tuningcylinder-5", (p,a) => p.PanDropRestricted("bonysoil", a)),
            ("TemporalAmuletTeleport", "game:clothes-neck-gear-amulet-temporal", (p,a) => p.CollectibleRestricted(a)),
            ("ImmersiveTemporalMaps", "immersivemaps:map-papyrus-temporal", (p,a) => p.CollectibleRestricted(a)),
            ("TemporalTemperatureForecaster", "dynamicseasonaltemperature:temporaltempforecaster", (p,a) => p.CollectibleRestricted(a)),
            ("ScribeClockmakerNotebookRecipe", "scribe:scribeclockmakernotebook", (p,a) => p.CollectibleRestricted(a)),
            ("VinconomyCatalogRecipe", "vinconomy:catalog", (p,a) => p.CollectibleRestricted(a)),
            ("CaravanTemporalGearSign", "cartwrightscaravan:cartsign-single-oak-wood-temporalgear", (p,a) => p.CollectibleRestricted(a)),
            ("ExpandedMatterTemporalMaterials", "em:crushed-metal-temporal", (p,a) => p.CollectibleRestricted(a)),
            ("ShipwrightLoreFigureheads", "shipwright:agedfigurehead-eidolon", (p,a) => p.CollectibleRestricted(a)),
            ("HeraldryDrifterPattern", "game:drifter", (p,a) => p.PatternRestricted(a.Path)),
            ("HeraldryEidolonPattern", "game:eidolon", (p,a) => p.PatternRestricted(a.Path))
        };
        foreach (var entry in cases)
        {
            c = new();
            var prop = typeof(LoreRestrictionsConfig).GetProperty(entry.Switch)!;
            var code = new AssetLocation(entry.Code);
            check(entry.Match(c, code), "Missing restriction: " + entry.Switch);
            prop.SetValue(c, false);
            check(!entry.Match(c, code), "Switch cannot restore content: " + entry.Switch);
            check(cases.Where(other => other.Switch != entry.Switch).All(other => other.Match(c, new AssetLocation(other.Code))),
                "Switch affects unrelated content: " + entry.Switch);
        }
        c = new();
        foreach (string code in new[] { "game:gear-rusty", "primitivesurvival:nightcrawler", "equusferus:ferus-adult-male-bay",
                     "shipwright:agedfigurehead-bear", "immersivemaps:map-papyrus", "em:crushed-metal-copper" })
            check(!c.EntityRestricted(new(code)) && !c.CollectibleRestricted(new(code)), "Ordinary content was restricted: " + code);
        check(!c.PanDropRestricted("sand-*", new("game:gear-temporal")) && !c.PanDropRestricted("bonysoil", new("game:tuningcylinder-1")),
            "Bone soil filter touched another drop source or cylinder.");

        // The runtime integration is also valid without any optional mods installed.
        using (var absent = new LoreRestrictionCompatibility())
            check(Harmony.GetPatchInfo(AccessTools.Method(typeof(EntityBehaviorHarvestable), "Initialize"))!.Owners.Contains(LoreRestrictionCompatibility.PatchId), "Native harvest guard absent.");
        VerifyWorldAndAssets(check);
        if (dllDirectory == null) return;
        string game = Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? "E:/vintagestory/Vintagestory";
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            foreach (string dir in new[] { dllDirectory, game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods") })
            {
                string path = Path.GetFullPath(Path.Combine(dir, name.Name + ".dll"));
                if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
            }
            return null;
        }
        AssemblyLoadContext.Default.Resolving += Resolve;
        try
        {
            foreach (string dll in new[] { "primitivesurvival.dll", "Jaunt.dll", "DynamicSeasonalTemperature.dll", "ImmersiveMaps.dll" })
                AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(Path.Combine(dllDirectory, dll)));
            VerifyInstalledMods(check);
        }
        finally { AssemblyLoadContext.Default.Resolving -= Resolve; }
    }

    private static void VerifyWorldConfigSerialization(Action<bool, string> check)
    {
        var policy = new LoreRestrictionsConfig { LivingDeadSpawns = false, HeraldryEidolonPattern = false };
        var legacy = new TreeAttribute();
        legacy.SetString(LoreRestrictionSystem.WorldKey, JsonConvert.SerializeObject(policy));
        bool reproduced = false;
        try { JToken.Parse(legacy.ToJsonToken()); }
        catch (JsonReaderException error)
        {
            reproduced = error.Path == LoreRestrictionSystem.WorldKey && error.Message.Contains("encountered: E");
        }
        check(reproduced, "Legacy raw JSON must reproduce the reported settings crash at Enabled.");
        var oldServer = Proxy.Make<IWorldAccessor>((m,a) => m.Name == "get_Config" ? legacy : null);
        check(LoreRestrictionSystem.Policy(oldServer) is { LivingDeadSpawns: false, HeraldryEidolonPattern: false },
            "Client no longer reads policies from older servers.");

        var native = new TreeAttribute();
        native[LoreRestrictionSystem.WorldKey] = new JsonObject(JObject.FromObject(policy)).ToAttribute();
        native.SetString("worldClimate", "patchy");
        native.SetInt("daysPerMonth", 12);
        var saved = new TreeAttribute();
        saved.FromBytes(native.ToBytes());
        var parsed = JObject.Parse(saved.ToJsonToken());
        check(parsed[LoreRestrictionSystem.WorldKey] is JObject
            && parsed[LoreRestrictionSystem.WorldKey]!["LivingDeadSpawns"]!.Value<bool>() == false
            && parsed["worldClimate"]!.Value<string>() == "patchy" && parsed["daysPerMonth"]!.Value<int>() == 12,
            "Native binary world config must remain parseable by the settings screen with other values intact.");
        var menu = new Vintagestory.Common.WorldConfig([]);
        var savegame = new SaveGame { WorldConfiguration = saved, PlayStyle = "default", Seed = 123, MapSizeY = 256 };
        AccessTools.Method(typeof(Vintagestory.Common.WorldConfig), "loadFromSavegame").Invoke(menu, [savegame]);
        check(menu.Seed == "123" && menu.MapsizeY == 256, "Real singleplayer world settings loader rejected the repaired configuration.");
        var reloaded = Proxy.Make<IWorldAccessor>((m,a) => m.Name == "get_Config" ? saved : null);
        check(LoreRestrictionSystem.Policy(reloaded) is { LivingDeadSpawns: false, HeraldryEidolonPattern: false, SkullOfTheDead: true },
            "Native server policy lost independent switches after binary save and reload.");
        saved.GetTreeAttribute(LoreRestrictionSystem.WorldKey).SetBool("Enabled", false);
        var disabled = Proxy.Make<IWorldAccessor>((m,a) => m.Name == "get_Config" ? saved : null);
        check(LoreRestrictionSystem.Policy(disabled) == null, "Native policy ignored the disabled master switch.");
    }

    private static void VerifyWorldAndAssets(Action<bool, string> check)
    {
        using var lease = new LoreRestrictionCompatibility();
        var settings = new TreeAttribute();
        settings.SetString("loreContent", "false"); // This setting is a StringAttribute in real worlds.
        var recipes = new List<GridRecipe>();
        var collectibles = new List<CollectibleObject>();
        var types = new List<EntityProperties>();
        var loaded = new System.Collections.Concurrent.ConcurrentDictionary<long, Entity>();
        int despawns = 0;
        ICoreServerAPI? api = null;
        var world = Proxy.Make<IServerWorldAccessor>((m,a) => m.Name switch
        {
            "get_Api" => api, "get_Side" => EnumAppSide.Server,
            "get_Config" => settings, "get_GridRecipes" => recipes, "get_Collectibles" => collectibles,
            "get_EntityTypes" => types, "get_LoadedEntities" => loaded,
            "DespawnEntity" => Despawn((EntityDespawnData)a![1]!), _ => null
        });
        object? Despawn(EntityDespawnData data) { check(data.Reason == EnumDespawnReason.Removed, "Cleanup generated a death/loot event."); despawns++; return null; }
        var registry = new RecipeRegistrySystem();
        var loader = Proxy.Make<IModLoader>((m,a) => m.Name == "GetModSystem" && m.IsGenericMethod && m.GetGenericArguments()[0] == typeof(RecipeRegistrySystem) ? registry : null);
        var logger = Proxy.Make<ILogger>((m,a) => null);
        var classRegistry = Proxy.Make<IClassRegistryAPI>((m,a) => null);
        TrySpawnEntityDelegate? trySpawn = null;
        EntityDelegate? onSpawn = null, onLoad = null;
        var pending = new Queue<Action>();
        var events = Proxy.Make<IServerEventAPI>((m,a) =>
        {
            switch (m.Name)
            {
                case "add_OnTrySpawnEntity": trySpawn += (TrySpawnEntityDelegate)a![0]!; break;
                case "remove_OnTrySpawnEntity": trySpawn -= (TrySpawnEntityDelegate)a![0]!; break;
                case "add_OnEntitySpawn": onSpawn += (EntityDelegate)a![0]!; break;
                case "remove_OnEntitySpawn": onSpawn -= (EntityDelegate)a![0]!; break;
                case "add_OnEntityLoaded": onLoad += (EntityDelegate)a![0]!; break;
                case "remove_OnEntityLoaded": onLoad -= (EntityDelegate)a![0]!; break;
                case "EnqueueMainThreadTask": pending.Enqueue((Action)a![0]!); break;
            }
            return null;
        });
        api = Proxy.Make<ICoreServerAPI>((m,a) => m.Name switch
        {
            "get_World" => world, "get_ModLoader" => loader, "get_Logger" => logger, "get_ClassRegistry" => classRegistry,
            "get_Side" => EnumAppSide.Server, "get_Event" => events, _ => null
        });
        var system = new LoreRestrictionSystem();
        // Old saves keep a valid binary string, even though its JSON view is invalid.
        settings.SetString(LoreRestrictionSystem.WorldKey, JsonConvert.SerializeObject(new LoreRestrictionsConfig()));
        system.AssetsFinalize(api);
        check(settings[LoreRestrictionSystem.WorldKey] is ITreeAttribute
            && JObject.Parse(settings.ToJsonToken())["loreContent"]!.Value<string>() == "false",
            "Entering an old world must replace legacy JSON strings without altering loreContent.");
        system.StartServerSide(api);
        check(LoreRestrictionSystem.Policy(world) != null, "String false world setting did not enable policy.");
        var clientWorld = Proxy.Make<IWorldAccessor>((m,a) => m.Name == "get_Config" ? settings : null);
        check(LoreRestrictionSystem.Policy(clientWorld)?.TemporalAmuletTeleport == true, "Server policy failed to reach an independent client world.");
        var properties = new EntityProperties { Code = new("primitivesurvival:livingdead-normal") };
        check(!trySpawn!(null!, ref properties, new Vec3d(), 0), "Natural spawn bypassed restriction.");
        properties.Code = new("primitivesurvival:nightcrawler");
        check(trySpawn!(null!, ref properties, new Vec3d(), 0), "Ordinary animal spawn blocked.");
        var entity = new EntityAgent { Code = new("primitivesurvival:willowisp-green"), World = world, EntityId = 123 };
        loaded[123] = entity;
        onSpawn!(entity);
        check(despawns == 0 && pending.Count == 1, "Cleanup ran inside unfinished SpawnEntity.");
        pending.Dequeue()();
        check(despawns == 1, "Direct SpawnEntity bypassed cleanup.");
        LoreRestrictionSystem.Bind(world, new() { RemoveExistingRestrictedEntities = false });
        onLoad!(entity);
        check(pending.Count == 0, "Existing entity cleanup switch ignored.");
        LoreRestrictionSystem.Bind(world, new());
        onLoad!(entity); pending.Dequeue()();
        check(despawns == 2, "Existing entity cleanup absent.");
        var undead = new EntityAgent { Code = new("primitivesurvival:livingdead-normal"), World = world, Api = api, EntityId = 456 };
        var harvest = new EntityBehaviorHarvestable(undead);
        var dropConfig = new JsonObject(JObject.Parse("{drops:[{type:'item',code:'game:gear-temporal'},{type:'block',code:'primitivesurvival:skullofthedead-normal'},{type:'block',code:'primitivesurvival:handofthedead-empty-north'},{type:'item',code:'primitivesurvival:clothes-lowerbody-clean-pants'}]}"));
        harvest.Initialize(new EntityProperties(), dropConfig);
        var actualDrops = (BlockDropItemStack[])AccessTools.Field(typeof(EntityBehaviorHarvestable), "jsonDrops").GetValue(harvest)!;
        check(actualDrops.Length == 1 && actualDrops[0].Code!.Path == "clothes-lowerbody-clean-pants", "Harvest behavior retained restricted loot.");
        LoreRestrictionSystem.Bind(world, new() { LivingDeadTemporalGearDrops = false, SkullOfTheDead = false });
        harvest.Initialize(new EntityProperties(), dropConfig);
        actualDrops = (BlockDropItemStack[])AccessTools.Field(typeof(EntityBehaviorHarvestable), "jsonDrops").GetValue(harvest)!;
        check(actualDrops.Length == 3 && actualDrops.Any(d => d.Code!.Path == "gear-temporal"), "Harvest switches were not independent.");

        Item Item(string code) => new() { Code = new(code) };
        GridRecipe Recipe(string output, string ingredient = "game:stick") => new()
        {
            Output = new CraftingRecipeIngredient { Code = new(output) },
            Ingredients = new() { ["A"] = new() { Code = new(ingredient) } }
        };
        recipes.AddRange([Recipe("vinconomy:catalog"), Recipe("game:plank-oak"), Recipe("em:powdered-metal-temporal")]);
        var catalog = Item("vinconomy:catalog");
        var cape = Item("capes:cape");
        cape.Attributes = new JsonObject(JObject.Parse("{HeraldryProperties:{AvailablePatterns:['drifter','eidolon','bear']}}"));
        var skull = new Block { Code = new("primitivesurvival:skullofthedead-normal"), LightHsv = new ThreeBytes(new byte[] { 20, 10, 8 }) };
        var hand = new Block { Code = new("primitivesurvival:handofthedead-empty-north"), LightHsv = new ThreeBytes(new byte[] { 30, 12, 6 }) };
        var ordinary = Item("primitivesurvival:nightcrawler");
        ordinary.LightHsv = new ThreeBytes(new byte[] { 40, 15, 4 });
        collectibles.AddRange([catalog, cape, skull, hand, ordinary]);
        LoreRestrictionSystem.ApplyAssets(api, new() { SkullOfTheDead = false, HandOfTheDead = false, HeraldryDrifterPattern = false, HeraldryEidolonPattern = false });
        check(skull.LightHsv[2] == 8 && hand.LightHsv[2] == 6, "Disabled relic switches changed original lighting.");
        LoreRestrictionSystem.ApplyAssets(api, new() { HeraldryEidolonPattern = false });
        check(((byte[])skull.LightHsv).SequenceEqual(new byte[3]) && ((byte[])hand.LightHsv).SequenceEqual(new byte[3]),
            "Restricted relic asset processing must finish with zero light.");
        check(skull.Attributes["handbook"]["exclude"].AsBool() && hand.Attributes["handbook"]["exclude"].AsBool()
            && skull.CreativeInventoryTabs.Length == 0 && hand.CreativeInventoryTabs.Length == 0,
            "Restricted relics remain in handbook or creative inventory.");
        check(((byte[])ordinary.LightHsv).SequenceEqual(new byte[] { 40, 15, 4 }), "Ordinary Primitive Survival item lighting was changed.");
        check(recipes.Count == 1 && recipes[0].Output!.Code!.ToString() == "game:plank-oak", "Recipe filtering missed a target or removed ordinary recipe.");
        check(catalog.Attributes["handbook"]["exclude"].AsBool(), "Restricted item remains in handbook.");
        check(cape.Attributes["HeraldryProperties"]["AvailablePatterns"].AsObject<string[]>().SequenceEqual(new[] { "eidolon", "bear" }), "Independent pattern switches failed.");
        var pan = new BlockPan();
        var panDrops = new Dictionary<string,PanningDrop[]>
        {
            ["bonysoil"] = new[] { "gear-temporal", "lore-book-story", "tuningcylinder-8", "gear-rusty" }.Select(s => new PanningDrop { Code = new(s) }).ToArray(),
            ["sand-*"] = [new PanningDrop { Code = new("gear-temporal") }]
        };
        AccessTools.Field(typeof(BlockPan), "dropsBySourceMat").SetValue(pan, panDrops);
        LoreRestrictionCompatibility.FilterPan(pan, new() { BoneSoilLoreBookDrops = false });
        check(panDrops["bonysoil"].Select(d => d.Code.Path).SequenceEqual(new[] { "lore-book-story", "gear-rusty" }), "Actual panning cache is not filtered by switches.");
        check(panDrops["sand-*"].Length == 1, "Panning changed unrelated source.");
        system.Dispose();
        check(trySpawn == null && onSpawn == null && onLoad == null, "Server handlers leaked after dispose.");
        settings.SetString("loreContent", "true");
        system = new(); system.AssetsFinalize(api);
        check(LoreRestrictionSystem.Policy(world) == null, "Restrictions leaked into lore-enabled world.");
    }

    private static void VerifyInstalledMods(Action<bool, string> check)
    {
        Vintagestory.API.Config.Lang.AvailableLanguages["en"] = Proxy.Make<Vintagestory.API.Config.ITranslationService>((m,a) => m.Name.StartsWith("Get") ? a?[0] : null);
        Vintagestory.API.Config.Lang.ChangeLanguage("en");
        using var serverLease = new LoreRestrictionCompatibility();
        var clientLease = new LoreRestrictionCompatibility();
        var world = Proxy.Make<IWorldAccessor>((m,a) => null);
        var c = new LoreRestrictionsConfig();
        LoreRestrictionSystem.Bind(world, c);
        var playerEntity = new EntityPlayer { World = world };
        var player = Proxy.Make<IServerPlayer>((m,a) => m.Name == "get_Entity" ? playerEntity : null);
        MethodInfo Method(string type, string method) => AccessTools.DeclaredMethod(AccessTools.TypeByName(type), method)!;
        foreach (var target in new[]
        {
            ("PrimitiveSurvival.ModSystem.BlockSkullOfTheDead", "TryPlaceBlock"),
            ("PrimitiveSurvival.ModSystem.BlockHandOfTheDead", "TryPlaceBlock"),
            ("PrimitiveSurvival.ModSystem.EntitySkullOfTheDead", "OnGameTick"),
            ("Jaunt.Behaviors.EntityBehaviorJauntRevivable", "OnInteract"),
            ("DynamicSeasonalTemperature.BlockTemporalTempForecaster", "OnBlockInteractStart"),
            ("ImmersiveMaps.Items.ItemMap", "OnHeldIdle"), ("ImmersiveMaps.Items.ItemMap", "OnHeldInteractStart")
        })
        {
            var info = Harmony.GetPatchInfo(Method(target.Item1, target.Item2));
            check(info != null && info.Prefixes.Concat(info.Postfixes).Count(p => p.owner == LoreRestrictionCompatibility.PatchId) == 1,
                "Installed method missing/duplicate patch: " + target);
        }
        foreach (var entry in new[] { ("BlockSkullOfTheDead", "skullofthedead-normal"), ("BlockHandOfTheDead", "handofthedead-empty-north") })
        {
            var blockType = AccessTools.TypeByName("PrimitiveSurvival.ModSystem." + entry.Item1);
            var block = (Block)Activator.CreateInstance(blockType)!;
            block.Code = new("primitivesurvival:" + entry.Item2);
            object?[] placement = [world, null, null, null, null];
            check(!(bool)AccessTools.DeclaredMethod(blockType, "TryPlaceBlock").Invoke(block, placement)! && placement[4] is string,
                "Relic placement not blocked: " + entry.Item1);
        }
        var forecasterType = AccessTools.TypeByName("DynamicSeasonalTemperature.BlockTemporalTempForecaster");
        var forecaster = (Block)Activator.CreateInstance(forecasterType)!;
        forecaster.Code = new("dynamicseasonaltemperature:temporaltempforecaster");
        check((bool)AccessTools.DeclaredMethod(forecasterType, "OnBlockInteractStart").Invoke(forecaster, [world, null, null])!, "Forecaster interaction not consumed.");
        var horse = new EntityAgent { Code = new("equusferus:ferus-adult-male-bay"), World = world };
        var reviveType = AccessTools.TypeByName("Jaunt.Behaviors.EntityBehaviorJauntRevivable");
        var revive = Activator.CreateInstance(reviveType, horse)!;
        object?[] reviveArgs = [playerEntity, null, null, EnumInteractMode.Interact, EnumHandling.PassThrough];
        AccessTools.DeclaredMethod(reviveType, "OnInteract").Invoke(revive, reviveArgs);
        check((EnumHandling)reviveArgs[4]! == EnumHandling.PassThrough, "Revival restriction blocked ordinary horse interactions.");
        c.EquusRevival = false;
        AccessTools.DeclaredMethod(reviveType, "OnInteract").Invoke(revive, reviveArgs);
        check((EnumHandling)reviveArgs[4]! == EnumHandling.PassThrough, "Disabled revival restriction consumed an interaction.");
        clientLease.Dispose();
        check(Harmony.GetPatchInfo(Method("PrimitiveSurvival.ModSystem.BlockSkullOfTheDead", "TryPlaceBlock"))!.Owners.Contains(LoreRestrictionCompatibility.PatchId), "Client dispose removed server patches.");
    }
}
