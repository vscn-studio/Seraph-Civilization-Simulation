using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Server;
using SeraphCivilizationSimulation;
using Newtonsoft.Json;
using Vintagestory.API.Config;

internal static class SleepInteractionChecks
{
    public static void Run(Action<bool, string> check)
    {
        string game = Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? "E:/vintagestory/Vintagestory";
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            foreach (string directory in new[] { game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods") })
            {
                string file = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(file)) return context.LoadFromAssemblyPath(Path.GetFullPath(file));
            }
            return null;
        }
        AssemblyLoadContext.Default.Resolving += Resolve;
        string oldLocale = Lang.CurrentLocale;
        var oldLanguages = new[] { "en", "zh-cn" }.ToDictionary(code => code,
            code => Lang.AvailableLanguages.GetValueOrDefault(code));
        try
        {
            foreach (string language in oldLanguages.Keys)
            {
                var translations = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                    File.ReadAllText($"assets/game/lang/{language}.json"))!;
                Lang.AvailableLanguages[language] = Proxy.Make<ITranslationService>((m, a) =>
                {
                    if (a?.FirstOrDefault() is not string key) return null;
                    string translationKey = key.Replace("game:", "");
                    if (m.Name == "HasTranslation") return translations.ContainsKey(translationKey);
                    return m.Name.StartsWith("Get") ? translations.GetValueOrDefault(translationKey, key) : null;
                });
            }
            foreach (string language in oldLanguages.Keys)
            {
                Lang.ChangeLanguage(language);
                CheckRestrictions(game, language, check);
            }
        }
        finally
        {
            foreach (var (language, service) in oldLanguages)
                if (service != null) Lang.AvailableLanguages[language] = service;
                else Lang.AvailableLanguages.Remove(language);
            Lang.ChangeLanguage(oldLocale);
            AssemblyLoadContext.Default.Resolving -= Resolve;
        }
    }

    private static void CheckRestrictions(string game, string language, Action<bool, string> check)
    {
        const string enabled = "vscncsSleepInteractionRestricted";
        var entity = new EntityPlayer();
        var tiredness = new TreeAttribute();
        entity.WatchedAttributes.SetAttribute("tiredness", tiredness);
        var data = new ServerWorldPlayerData { EntityPlayer = entity };
        AccessTools.Field(typeof(ServerWorldPlayerData), "PlayerUID").SetValue(data, "sleeper");
        var player = (ServerPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ServerPlayer));
        player.LanguageCode = language;
        AccessTools.Field(typeof(ServerPlayer), "worlddata").SetValue(player, data);
        Block worldBlock = new() { Code = new AssetLocation("bed-wood-head-north") };
        var blocks = Proxy.Make<IBlockAccessor>((m, a) => m.Name switch
        { "GetBlock" => worldBlock, _ => null });
        var world = Proxy.Make<IServerWorldAccessor>((m, a) => m.Name switch
        { "get_Side" => EnumAppSide.Server, "get_AllOnlinePlayers" => new IPlayer[] { player }, "get_BlockAccessor" => blocks, _ => null });
        var subscriptions = new Dictionary<string, Delegate?>();
        var events = Proxy.Make<IServerEventAPI>((m, a) =>
        {
            if (m.Name.StartsWith("add_") || m.Name.StartsWith("remove_"))
            {
                string name = m.Name[(m.Name.IndexOf('_') + 1)..];
                subscriptions.TryGetValue(name, out var current);
                subscriptions[name] = m.Name.StartsWith("add_")
                    ? Delegate.Combine(current, (Delegate)a![0]!)
                    : Delegate.Remove(current, (Delegate)a![0]!);
            }
            return null;
        });
        var sapi = Proxy.Make<ICoreServerAPI>((m, a) => m.Name switch
        { "get_World" => world, "get_Side" => EnumAppSide.Server, "get_Event" => events, _ => null });
        entity.World = world;
        entity.Api = sapi;
        var server = new VscnCivilizationModSystem();
        AccessTools.Field(server.GetType(), "sapi").SetValue(server, sapi);
        var client = new VscnClientModSystem();
        var access = AccessTools.Method(client.GetType(), "OnSleepingBlockAccess");
        var canUse = AccessTools.Method(server.GetType(), "CanUseSleepingBlock");
        var sync = AccessTools.Method(server.GetType(), "SyncSleepInteractionSettings");
        var command = AccessTools.Method(server.GetType(), "HandleSleepCommand");
        AccessTools.Method(server.GetType(), "InstallSleepInteractionRestrictions").Invoke(server, null);
        check(subscriptions.GetValueOrDefault("OnTestBlockAccess") is TestBlockAccessDelegate,
            "Server did not register the access callback with access-type flags.");
        check(!subscriptions.ContainsKey("CanPlaceOrBreakBlock"),
            "Server still registers the callback that also receives traversal queries.");
        var serverAccess = (TestBlockAccessDelegate)subscriptions["OnTestBlockAccess"]!;
        var bedSelection = new BlockSelection { Block = new Block { Code = new AssetLocation("bed-wood-head-north") }, Position = new BlockPos(1, 2, 3) };
        const string ExpectedClaimant = "custommessage-vsscsmod-sleep-build-restricted";
        EnumWorldAccessResponse Access(EnumBlockAccessFlags flags, EnumWorldAccessResponse previous = EnumWorldAccessResponse.Granted)
        {
            object?[] args = [player, bedSelection, flags, "owner", previous];
            var result = (EnumWorldAccessResponse)access.Invoke(client, args)!;
            check((string)args[3]! == (result == EnumWorldAccessResponse.DeniedByMod && previous == EnumWorldAccessResponse.Granted
                ? ExpectedClaimant : "owner"), "Client sleep restriction must preserve the error code and earlier denials: " + args[3]);
            if (result == EnumWorldAccessResponse.DeniedByMod && previous == EnumWorldAccessResponse.Granted)
                CheckGameMessage((string)args[3]!);
            return result;
        }
        void CheckGameMessage(string claimant)
        {
            // ClientMain and WorldMap build this game-domain key from the custommessage suffix.
            string key = "ingameerror-noprivilege-buildbreak-" + claimant["custommessage-".Length..];
            string expected = language == "zh-cn" ? "睡眠时无法放置或破坏方块，请先起床。"
                : "You cannot place or break blocks while sleeping. Wake up first.";
            check(Lang.Get(key, claimant) == expected, "The native game error lookup must resolve the localized sleep message: " + key);
        }
        EnumWorldAccessResponse ServerAccess(EnumBlockAccessFlags flags, EnumWorldAccessResponse previous = EnumWorldAccessResponse.Granted)
        {
            string claimant = "owner";
            var result = serverAccess(player, bedSelection, flags, ref claimant, previous);
            check(claimant == (result == EnumWorldAccessResponse.DeniedByMod && previous == EnumWorldAccessResponse.Granted
                ? ExpectedClaimant : "owner"), "Server sleep restriction must preserve the error code and earlier denials.");
            if (result == EnumWorldAccessResponse.DeniedByMod && previous == EnumWorldAccessResponse.Granted)
                CheckGameMessage(claimant);
            return result;
        }

        sync.Invoke(server, null);
        check(entity.WatchedAttributes.GetBool(enabled), "Default sleep restriction was not synchronized to the client.");
        check(Access(EnumBlockAccessFlags.BuildOrBreak) == EnumWorldAccessResponse.Granted, "Awake players cannot break blocks.");
        check(ServerAccess(EnumBlockAccessFlags.BuildOrBreak) == EnumWorldAccessResponse.Granted, "Server blocked awake players from building.");
        tiredness.SetInt("isSleeping", 1);
        var positionOnly = new BlockSelection { Position = bedSelection.Position };
        bool CanUse(BlockSelection selection) => (bool)canUse.Invoke(server, [player, selection])!;
        check(CanUse(bedSelection), "Server blocked a fully populated bed selection.");
        check(CanUse(positionOnly), "Claims.TryAccess blocked bed-head use with a position-only selection.");
        worldBlock = new Block { Code = new AssetLocation("bed-wood-feet-north") };
        check(CanUse(positionOnly), "Claims.TryAccess blocked bed-feet use with a position-only selection.");
        worldBlock = new Block { Code = new AssetLocation("chest-north") };
        check(!CanUse(positionOnly), "Resolving position-only selections allowed other blocks while asleep.");
        worldBlock = new Block { Code = new AssetLocation("bed-wood-head-north") };
        check(Access(EnumBlockAccessFlags.BuildOrBreak) == EnumWorldAccessResponse.DeniedByMod, "Client prediction can destroy a sleeping player's bed.");
        check(Access(EnumBlockAccessFlags.BuildOrBreak | EnumBlockAccessFlags.Use) == EnumWorldAccessResponse.DeniedByMod, "Combined access flags bypass sleeping block restrictions.");
        check(Access(EnumBlockAccessFlags.Use) == EnumWorldAccessResponse.Granted, "The client blocked bed use and waking up.");
        check(Access(EnumBlockAccessFlags.Traverse) == EnumWorldAccessResponse.Granted, "The client blocked traversal.");
        check(Access(EnumBlockAccessFlags.Use, EnumWorldAccessResponse.LandClaimed) == EnumWorldAccessResponse.LandClaimed, "The sleep guard granted access denied by a claim.");
        foreach (var flags in new[] { EnumBlockAccessFlags.Use, EnumBlockAccessFlags.Traverse, EnumBlockAccessFlags.Use | EnumBlockAccessFlags.Traverse, EnumBlockAccessFlags.None })
            check(ServerAccess(flags) == EnumWorldAccessResponse.Granted, "Server blocked use or traversal while asleep.");
        foreach (var flags in new[] { EnumBlockAccessFlags.BuildOrBreak, EnumBlockAccessFlags.BuildOrBreak | EnumBlockAccessFlags.Use })
            check(ServerAccess(flags) == EnumWorldAccessResponse.DeniedByMod, "Server allowed sleeping players to build or break blocks.");
        check(ServerAccess(EnumBlockAccessFlags.BuildOrBreak, EnumWorldAccessResponse.LandClaimed) == EnumWorldAccessResponse.LandClaimed,
            "Server overwrote a land-claim denial.");
        check(Access(EnumBlockAccessFlags.BuildOrBreak, EnumWorldAccessResponse.LandClaimed) == EnumWorldAccessResponse.LandClaimed,
            "Client overwrote a land-claim denial.");

        // A real bed mount must be protected even before its queued sleepy flag is set.
        var survival = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "Mods", "VSSurvivalMod.dll"));
        var bed = (IMountableSeat)Activator.CreateInstance(survival.GetType("Vintagestory.GameContent.BlockEntityBed")!)!;
        AccessTools.Property(typeof(EntityAgent), "MountedOn").SetValue(entity, bed);
        tiredness.SetInt("isSleeping", 0);
        check(CanUse(positionOnly), "Mount-to-sleep transition blocked bed use before the sleepy flag was set.");
        check(ServerAccess(EnumBlockAccessFlags.BuildOrBreak) == EnumWorldAccessResponse.DeniedByMod && entity.MountedOn == bed,
            "Mount-to-sleep permission query unmounted the player before the sleepy flag was set.");
        check(Access(EnumBlockAccessFlags.BuildOrBreak) == EnumWorldAccessResponse.DeniedByMod, "Mount-to-sleep transition leaves a bed destruction window.");
        AccessTools.Property(typeof(EntityAgent), "MountedOn").SetValue(entity, null);

        command.Invoke(server, [new CmdArgs("off")]);
        tiredness.SetInt("isSleeping", 1);
        worldBlock = new Block { Code = new AssetLocation("chest-north") };
        check(CanUse(positionOnly), "Disabling sleep restrictions still blocked position-only block use.");
        check(!entity.WatchedAttributes.GetBool(enabled) && Access(EnumBlockAccessFlags.BuildOrBreak) == EnumWorldAccessResponse.Granted,
            "Disabling sleep restrictions did not unblock the client.");
        check(ServerAccess(EnumBlockAccessFlags.BuildOrBreak) == EnumWorldAccessResponse.Granted, "Disabling sleep restrictions did not unblock the server.");
        command.Invoke(server, [new CmdArgs("on")]);
        check(entity.WatchedAttributes.GetBool(enabled) && Access(EnumBlockAccessFlags.BuildOrBreak) == EnumWorldAccessResponse.DeniedByMod,
            "Enabling sleep restrictions did not block the client.");
        server.Config.Sleep.Enabled = false;
        sync.Invoke(server, null);
        check(!entity.WatchedAttributes.GetBool(enabled), "Reloaded sleep configuration was not synchronized.");
        server.Config.Sleep.Enabled = true;

        bool didUnmount = false;
        var supplier = Proxy.Make<IMountable>((m, a) => null);
        var seat = Proxy.Make<IMountableSeat>((m, a) => m.Name switch
        {
            "CanUnmount" => true, "get_MountSupplier" => supplier,
            "DidUnmount" => Unmounted(), _ => null
        });
        object? Unmounted() { didUnmount = true; tiredness.SetInt("isSleeping", 0); return null; }
        AccessTools.Property(typeof(EntityAgent), "MountedOn").SetValue(entity, seat);
        entity.WatchedAttributes.SetAttribute("mountedOn", new TreeAttribute());
        for (int i = 0; i < 20; i++)
        {
            check(ServerAccess(EnumBlockAccessFlags.BuildOrBreak) == EnumWorldAccessResponse.DeniedByMod,
                "Server accepted a build/break query while sleeping.");
            check(ServerAccess(EnumBlockAccessFlags.Traverse) == EnumWorldAccessResponse.Granted,
                "Server blocked a repeated traversal query while sleeping.");
        }
        check(!didUnmount && entity.MountedOn == seat && entity.WatchedAttributes.HasAttribute("mountedOn") && tiredness.GetInt("isSleeping") == 1,
            "Repeated permission queries forcibly woke the sleeping player.");
        check(entity.TryUnmount() && didUnmount && entity.MountedOn == null && tiredness.GetInt("isSleeping") == 0,
            "Explicitly waking the player no longer unmounts normally.");
        check(Access(EnumBlockAccessFlags.BuildOrBreak) == EnumWorldAccessResponse.Granted, "The client remains locked after waking.");
        check(ServerAccess(EnumBlockAccessFlags.BuildOrBreak) == EnumWorldAccessResponse.Granted, "The server remains locked after waking.");
        tiredness.SetInt("isSleeping", 1);
        check(ServerAccess(EnumBlockAccessFlags.BuildOrBreak) == EnumWorldAccessResponse.DeniedByMod && tiredness.GetInt("isSleeping") == 1,
            "A permission query mutated the sleep flag without a mount.");
        AccessTools.Method(server.GetType(), "UninstallSleepInteractionRestrictions").Invoke(server, null);
        check(subscriptions.Values.All(callback => callback == null), "Sleep restriction callbacks remain registered after disposal.");
    }
}
