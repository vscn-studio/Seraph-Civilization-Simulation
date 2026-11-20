using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using SeraphCivilizationSimulation;

internal static class CoinageEconomyChecks
{
    public static void Run(string package, Action<bool, string> check)
    {
        string game = Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? "D:/Vintagestory";
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            foreach (string dir in new[] { game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods") })
                if (File.Exists(Path.Combine(dir, name.Name + ".dll"))) return context.LoadFromAssemblyPath(Path.GetFullPath(Path.Combine(dir, name.Name + ".dll")));
            return null;
        }
        AssemblyLoadContext.Default.Resolving += Resolve;
        try
        {
            using var zip = ZipFile.OpenRead(package); using var source = zip.GetEntry("Coinage.dll")!.Open(); using var bytes = new MemoryStream();
            source.CopyTo(bytes); bytes.Position = 0;
            Verify(AssemblyLoadContext.Default.LoadFromStream(bytes), check);
        }
        finally { CoinageEconomyCompatibility.OpenClientTrade = null; AssemblyLoadContext.Default.Resolving -= Resolve; }
    }
    private static void Verify(Assembly assembly, Action<bool, string> check)
    {
        const string patchId = "vsscsmod.coinageeconomy";
        object? Call(object target, string name, params object?[] args) => AccessTools.Method(target.GetType(), name).Invoke(target, args);
        var server = new VscnCivilizationModSystem(); var state = new EconomyState();
        var actor = new EntityPlayer(); var target = new EntityPlayer();
        var actorPlayer = Proxy.Make<IServerPlayer>((m, args) => m.Name switch { "get_PlayerUID" => "minter", "get_Entity" => actor, _ => null });
        var targetPlayer = Proxy.Make<IServerPlayer>((m, args) => m.Name switch { "get_PlayerUID" => "target", "get_Entity" => target, _ => null });
        EnumAppSide side = EnumAppSide.Server;
        int spawned = 0;
        var world = Proxy.Make<IServerWorldAccessor>((m, args) =>
        {
            if (m.Name == "SpawnItemEntity") spawned++;
            return m.Name switch { "get_Side" => side, "get_Rand" => new Random(5), "get_AllOnlinePlayers" => new IPlayer[] { actorPlayer, targetPlayer },
                "PlayerByUid" => (string)args![0]! == "minter" ? actorPlayer : targetPlayer, _ => null };
        });
        var save = Proxy.Make<ISaveGame>((m, args) => null);
        var manager = Proxy.Make<IWorldManagerAPI>((m, args) => m.Name == "get_SaveGame" ? save : null);
        var logger = Proxy.Make<ILogger>((m, args) => null);
        var data = Proxy.Make<IPlayerDataManager>((m, args) => m.Name == "get_PlayerDataByUid" ? new Dictionary<string, IServerPlayerData>() : null);
        object? coinSystem = null;
        var loader = Proxy.Make<IModLoader>((m, args) => m.Name == "GetModSystem" ? m.IsGenericMethod ? server : coinSystem : null);
        var api = Proxy.Make<ICoreServerAPI>((m, args) => m.Name switch
        { "get_Side" => side, "get_World" => world, "get_WorldManager" => manager, "get_ModLoader" => loader, "get_PlayerData" => data, "get_Logger" => logger, _ => null });
        AccessTools.Field(typeof(VscnCivilizationModSystem), "sapi").SetValue(server, api);
        AccessTools.Field(typeof(VscnCivilizationModSystem), "economy").SetValue(server, state);
        var issuingTribe = new TribeRecord { Id = "issuer", Name = "Issuing tribe", LeaderUid = "minter", Members = ["minter"] };
        var tribes = new TribeState { Tribes = [issuingTribe, new() { Id = "operator", Name = "Minting tribe", LeaderUid = "other", Members = ["other"] }] };
        AccessTools.Field(typeof(VscnCivilizationModSystem), "tribeState").SetValue(server, tribes);
        actor.WatchedAttributes.SetString("playerUID", "minter"); target.WatchedAttributes.SetString("playerUID", "target");
        actor.Api = api; actor.World = world; target.World = world;
        var coin = (Item)Activator.CreateInstance(assembly.GetType("Coinage.Items.ItemCoin", true)!, true)!;
        coin.Code = new AssetLocation("coinage:coin-copper-small"); coin.MaxStackSize = 64;
        var stack = new ItemStack(coin); stack.Attributes.SetLong("coinType", 42); stack.Attributes.SetLong("coinShape", -6);
        string key = EconomyInventory.CoinKey(stack);
        var held = AccessTools.Method(assembly.GetType("Coinage.Items.ItemShapableMetalDisc"), "OnHeldInteractStart");
        var pressType = assembly.GetType("Coinage.BlockEntities.BECoinpress", true)!;
        var spawn = AccessTools.Method(pressType, "SpawnCoinInBestSpot");
        using var first = new CoinageEconomyCompatibility(api);
        var second = new CoinageEconomyCompatibility(api);
        check(Harmony.GetPatchInfo(held).Prefixes.Count(p => p.owner == patchId) == 1 && Harmony.GetPatchInfo(spawn).Postfixes.Count(p => p.owner == patchId) == 1,
            "Actual Coinage 2.2.2 must accept every Harmony signature and install one shared patch per method.");
        object?[] interaction = [new DummySlot(stack), actor, null, new EntitySelection { Entity = target }, true, EnumHandHandling.NotHandled];
        held.Invoke(coin, interaction);
        check((EnumHandHandling)interaction[^1]! == EnumHandHandling.PreventDefault, "Server-side held-coin player interaction must suppress Coinage's original animation.");
        string opened = "";
        var clientPlayer = Proxy.Make<IClientPlayer>((m, args) => m.Name switch { "get_PlayerUID" => "minter", "get_Entity" => actor, _ => null });
        var clientWorld = Proxy.Make<IClientWorldAccessor>((m, args) => m.Name switch { "get_Side" => EnumAppSide.Client, "get_Player" => clientPlayer, "PlayerByUid" => clientPlayer, _ => null });
        var clientApi = Proxy.Make<ICoreClientAPI>((m, args) => m.Name == "get_World" ? clientWorld : null);
        actor.World = clientWorld; actor.Api = clientApi;
        CoinageEconomyCompatibility.OpenClientTrade = uid => opened = uid;
        held.Invoke(coin, interaction);
        check(opened == "target", "Actual coin interaction must send the selected player's UID to the economy UI.");
        opened = ""; interaction[^2] = false; held.Invoke(coin, interaction);
        check(opened.Length == 0, "Repeated held interaction events must not duplicate trade requests.");
        actor.World = world; actor.Api = api;
        var press = (BlockEntity)Activator.CreateInstance(pressType, true)!; press.Api = api; press.Pos = new BlockPos(0);
        spawn.Invoke(press, [actor.Player, stack]);
        check(spawned == 1 && state.Coins[key].Minters.Count == 0, "The real Coinage spawn method must register exactly one minted coin for its actual operator.");
        Call(server, "RegisterCoinDesign", 42L, "minter");
        spawn.Invoke(press, [null, stack]);
        check(spawned == 2 && state.Coins[key].Minters.Count == 0, "Automatic coinpress output must use authenticated design ownership.");
        var designType = assembly.GetType("Coinage.Database.Entities.CoinDesign", true)!;
        var design = Activator.CreateInstance(designType, true)!; designType.GetProperty("Id")!.SetValue(design, 99L);
        coinSystem = Activator.CreateInstance(assembly.GetType("Coinage.CoinageModSystem", true)!, true)!;
        AccessTools.Field(coinSystem.GetType(), "_api").SetValue(coinSystem, api);
        designType.GetProperty("CreatedBy")!.SetValue(design, "Original mold author");
        ((System.Collections.IDictionary)AccessTools.Field(coinSystem.GetType(), "_coinDesigns").GetValue(coinSystem)!).Add(99L, design);
        check((string)Call(server, "DesignDesigner", 99L, "")! == "Original mold author" && !state.DesignOwners.ContainsKey(99),
            "Real Coinage design metadata must display the original mold author without granting authenticated mint ownership.");
        var scope = AccessTools.Field(typeof(CoinageEconomyCompatibility), "creatingPlayer"); scope.SetValue(null, "minter");
        AccessTools.Method(typeof(CoinageEconomyCompatibility), "AfterCreatedDesign").Invoke(null, [coinSystem, design]); scope.SetValue(null, null);
        check(state.DesignOwners[99] == "minter", "Actual Coinage design ID properties must bind to the authenticated connection rather than an author string.");
        check((string)Call(server, "DesignDesigner", 99L, "")! == "minter",
            "Authenticated creator identity must override author text cached from real Coinage metadata.");
        check(state.DesignIssuers[99] is { TribeId: "issuer", TribeName: "Issuing tribe", OwnerUid: "minter", Pending: false },
            "Authenticated creation through the real Coinage callback must snapshot issuing tribe and creator.");
        issuingTribe.Members.Remove("minter"); tribes.Tribes[1].Members.Add("minter");
        coinSystem = null; stack.Attributes.SetLong("coinType", 99);
        spawn.Invoke(press, [actor.Player, stack]);
        string newKey = EconomyInventory.CoinKey(stack);
        check(state.DesignIssuers[99].TribeId == "issuer" && state.Mints.Count == 0,
            "Real press output after a member moves must retain fixed issuance and record the actual minting tribe independently.");
        first.Dispose();
        check(Harmony.GetPatchInfo(held)?.Owners.Contains(patchId) == true, "Disposing one side of an integrated world must preserve the other side's patches.");
        second.Dispose();
        check(Harmony.GetPatchInfo(held)?.Owners.Contains(patchId) != true && Harmony.GetPatchInfo(spawn)?.Owners.Contains(patchId) != true,
            "Disposing the final Coinage compatibility instance must remove all shared patches.");
    }
}
