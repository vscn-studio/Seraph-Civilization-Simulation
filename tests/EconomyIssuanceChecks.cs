using System.Collections;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using SeraphCivilizationSimulation;

internal static class EconomyIssuanceChecks
{
    private sealed class DesignDetails
    {
        public string CreatedBy { get; set; } = "Original mold designer";
    }
    private sealed class DesignDatabase : ModSystem
    {
        public int Reads;
        public object? GetCoinDesign(long design) { Reads++; return design == 30 ? new DesignDetails() : null; }
    }
    private static object? Call(object target, string name, params object?[] args) => target.GetType()
        .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
    private static void Set(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
    private static object Get(object target, string name) => target.GetType()
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;

    public static void Run(Action<bool, string> check)
    {
        var policy = new EconomyPolicy { ReferenceCoin = "A", Rates = { ["A"] = 1, ["B"] = 10, ["C"] = .5m } };
        check(policy.TryRebase("B", "999", out _) && policy.Rates["A"] == .1m && policy.Rates["B"] == 1 && policy.Rates["C"] == .05m,
            "A priced external reference must divide the entire table by its existing rate, ignoring draft overrides.");
        string before = JsonConvert.SerializeObject(policy);
        check(!policy.TryRebase("D", "", out var error) && error == "economy-reference-rate-required"
            && JsonConvert.SerializeObject(policy) == before, "An unpriced replacement reference must not default to parity or partially mutate policy.");
        foreach (string invalid in new[] { "0", "-1", "1e2", "NaN", "1000001", "0.0000001" })
            check(!policy.TryRebase("D", invalid, out _) && JsonConvert.SerializeObject(policy) == before, "Invalid replacement reference rate changed policy.");
        check(policy.TryRebase("D", "2", out _) && policy.Rates["B"] == .5m && policy.Rates["D"] == 1,
            "An explicitly priced replacement reference must be automatically recognized at one.");
        var bounded = new EconomyPolicy { ReferenceCoin = "A", Rates = { ["A"] = 1, ["B"] = 1000000, ["C"] = .000001m } };
        before = JsonConvert.SerializeObject(bounded);
        check(!bounded.TryRebase("B", "", out error) && error == "economy-reference-range" && JsonConvert.SerializeObject(bounded) == before,
            "Reject an entire rebase when one resulting rate falls below the bound.");
        check(!bounded.TryRebase("C", "", out _) && JsonConvert.SerializeObject(bounded) == before,
            "Reject an entire rebase when one resulting rate exceeds the bound.");
        var initial = new EconomyPolicy();
        check(initial.TryRebase("external", "", out _) && initial.ReferenceCoin == "external" && initial.Rates["external"] == 1,
            "The first reference may be external and needs no previous exchange rate.");

        var a = new TribeRecord { Id = "a", Name = "甲部落", LeaderUid = "alice", Members = ["alice"] };
        var b = new TribeRecord { Id = "b", Name = "乙部落", LeaderUid = "bob", Members = ["bob"] };
        var tribes = new TribeState { Tribes = [a, b] };
        var legacy = JsonConvert.DeserializeObject<EconomyState>("{\"Coins\":{\"old\":{\"Key\":\"old\",\"Design\":9,\"LegacyOwnerUid\":\"alice\",\"Minters\":{\"bob\":2}}},\"DesignOwners\":{\"9\":\"alice\",\"10\":\"bob\"}}")!;
        check(legacy.MigrateIssuers() && legacy.DesignIssuers.Count == 2 && legacy.DesignIssuers.Values.All(i => i.Pending)
            && legacy.DesignIssuers[9].OwnerUid == "alice" && !legacy.Issuers(legacy.Coins["old"], tribes).Any(),
            "Old creators and mint operators must not be used to guess old issuance tribes, including designs with no observed coins.");
        before = JsonConvert.SerializeObject(legacy);
        check(!legacy.MigrateIssuers() && JsonConvert.SerializeObject(legacy) == before, "Issuer migration must be idempotent.");

        var saved = new Dictionary<string, byte[]>(); bool failSave = false;
        var snapshots = new Dictionary<string, EconomySnapshot>();
        var save = Proxy.Make<ISaveGame>((m, args) =>
        {
            if (m.Name == "StoreData") { if (failSave) throw new IOException("test failure"); saved[(string)args![0]!] = (byte[])args[1]!; }
            return m.Name == "GetData" ? saved.GetValueOrDefault((string)args![0]!) : null;
        });
        var manager = Proxy.Make<IWorldManagerAPI>((m, args) => m.Name == "get_SaveGame" ? save : null);
        var inv = Proxy.Make<IPlayerInventoryManager>((m, args) => null);
        IServerPlayer Player(string uid) => Proxy.Make<IServerPlayer>((m, args) => m.Name switch
        { "get_PlayerUID" or "get_PlayerName" => uid, "get_LanguageCode" => "en", "get_InventoryManager" => inv, "HasPrivilege" => false, _ => null });
        var alice = Player("alice"); var bob = Player("bob"); var creator = Player("creator");
        IPlayer[] online = [alice, bob, creator];
        var world = Proxy.Make<IServerWorldAccessor>((m, args) => m.Name == "get_AllOnlinePlayers" ? online : null);
        var data = Proxy.Make<IPlayerDataManager>((m, args) => m.Name == "get_PlayerDataByUid" ? new Dictionary<string, IServerPlayerData>() : null);
        var designs = new DesignDatabase();
        var loader = Proxy.Make<IModLoader>((m, args) => m.Name switch
        { "IsModEnabled" => true, "GetModSystem" => designs, _ => null });
        var api = Proxy.Make<ICoreServerAPI>((m, args) => m.Name switch
        { "get_World" => world, "get_WorldManager" => manager, "get_PlayerData" => data, "get_ModLoader" => loader,
            "get_Logger" => Proxy.Make<ILogger>((m, args) => null), _ => null });
        var channel = Proxy.Make<IServerNetworkChannel>((m, args) =>
        {
            if (m.Name == "SendPacket" && args![0] is EconomySnapshot snapshot)
                foreach (var p in (IServerPlayer[])args[1]!) snapshots[p.PlayerUID] = snapshot;
            return null;
        });
        var server = new VscnCivilizationModSystem();
        Set(server, "sapi", api); Set(server, "channel", channel); Set(server, "tribeState", tribes); Set(server, "economy", legacy);
        EconomyState State() => (EconomyState)Get(server, "economy");
        void Request(IServerPlayer p, EconomyRequest request)
        { ((IDictionary)Get(server, "economyRequests")).Clear(); Call(server, "OnEconomyRequest", p, request); }
        void Refresh(IServerPlayer p) => Call(server, "SendEconomy", p, "");
        TextCommandResult Command(string text, IServerPlayer? player = null)
        {
            var parser = Proxy.Make<ICommandArgumentParser>((m, args) => m.Name == "GetValue" ? "economy " + text : null);
            return (TextCommandResult)Call(server, "HandleCommand", new TextCommandCallingArgs
            { Caller = player == null ? new Caller { Type = EnumCallerType.Console } : new Caller { Player = player }, Parsers = [parser] })!;
        }
        legacy.Coins["old"].Code = "coinage:coin-copper-small";
        Request(alice, new() { Action = "reference", Coin = "old" });
        check(State().Recognizes(a, "old", tribes) && snapshots["alice"].Coins.Single().Issuers == "alice",
            "Old currencies must display their authenticated designer and remain adoptable despite pending legacy issuance.");
        var alicePolicy = State().Policies["a"];
        alicePolicy.Rates["old"] = 1; alicePolicy.Rates["other"] = 2;
        var oldRevision = alicePolicy.Revision;
        var member = Player("member"); a.Members.Add("member");
        Request(member, new() { Action = "reference-cancel", Revision = oldRevision }); a.Members.Remove("member");
        check(alicePolicy.ReferenceCoin == "old", "A non-leader must not cancel the tribe reference.");
        Request(alice, new() { Action = "reference-cancel", Revision = oldRevision - 1 });
        check(alicePolicy.ReferenceCoin == "old", "A stale policy revision must not cancel the tribe reference.");
        failSave = true; Request(alice, new() { Action = "reference-cancel", Revision = oldRevision }); failSave = false;
        check(State().Policies["a"].ReferenceCoin == "old" && State().Policies["a"].Rates.Count == 2,
            "A failed save must restore the reference and its rates.");
        Request(alice, new() { Action = "reference-cancel", Revision = oldRevision });
        check(State().Policies["a"].ReferenceCoin.Length == 0 && State().Policies["a"].Rates.Count == 0
            && !State().Recognizes(a, "old", tribes) && State().Policies["a"].Revision == oldRevision + 1,
            "The leader can cancel the reference, clearing dependent rates and recognition.");
        Request(alice, new() { Action = "reference", Coin = "old", Revision = State().Policies["a"].Revision });
        check(State().Recognizes(a, "old", tribes), "A tribe must be able to select a new reference after cancellation.");
        check(Command("issuer 9 b", alice).Status == EnumCommandStatus.Error && State().DesignIssuers[9].Pending,
            "Ordinary players must not resolve or correct fixed issuance using administrator commands.");
        failSave = true; check(Command("issuer 9 b").Status == EnumCommandStatus.Error, "Failed issuer persistence must report a failed command."); failSave = false;
        check(State().DesignIssuers[9].Pending, "Administrator issuer persistence failure must restore the pending record.");
        check(Command("issuer 9 b").Status == EnumCommandStatus.Success && State().DesignIssuers[9].TribeId == "b"
            && State().Recognizes(a, "old", tribes), "An administrator may resolve an old design without changing recognition.");
        a.Name = b.Name;
        check(Command("issuer 9 " + a.Name).Status == EnumCommandStatus.Error && State().DesignIssuers[9].TribeId == "b",
            "An ambiguous tribe name must not bind a currency to an arbitrary tribe."); a.Name = "甲部落";
        check(Command("list").Status == EnumCommandStatus.Success && Command("issuer 999 b").Status == EnumCommandStatus.Error,
            "Administrator list and unknown-design validation must work.");
        Refresh(alice);
        check(!snapshots["alice"].Coins.Single().Own && snapshots["alice"].ReferenceCoin == "old", "Adopting an external reference must not claim its issuance.");

        Call(server, "RegisterCoinDesign", 20L, "creator");
        State().Coins["personal"] = new() { Key = "personal", Code = "coinage:coin-copper-small", Design = 20 };
        a.Members.Add("creator");
        Refresh(creator);
        check(!snapshots["creator"].Coins.Single(c => c.Key == "personal").CanRequestIssuer
            && snapshots["creator"].Coins.Single(c => c.Key == "personal").Issuers == "creator"
            && State().DesignIssuers[20] is { TribeId.Length: 0, OwnerUid: "creator", Pending: false },
            "Joining a tribe preserves the designer and no longer offers tribal issuance registration.");
        Request(alice, new() { Action = "issuer-request", Coin = "personal" });
        check(State().DesignIssuers[20].RequestedTribeId == "", "The leader cannot register another creator's personal currency without their request.");
        Request(creator, new() { Action = "issuer-request", Coin = "personal" });
        check(State().DesignIssuers[20] is { RequestedTribeId: "a", TribeId.Length: 0, Revision: 1 }, "Creator registration must wait for leader approval.");
        Request(bob, new() { Action = "issuer-approve", Coin = "personal", IssuerRevision = 1 });
        Request(alice, new() { Action = "issuer-approve", Coin = "personal", IssuerRevision = 0 });
        check(State().DesignIssuers[20].TribeId.Length == 0, "Wrong leader and stale issuer revision must not approve registration.");
        a.Members.Remove("creator"); Refresh(alice);
        check(!snapshots["alice"].Coins.Single(c => c.Key == "personal").CanApproveIssuer, "The creator must still belong to the requested tribe at approval.");
        Request(alice, new() { Action = "issuer-approve", Coin = "personal", IssuerRevision = 1 });
        check(State().DesignIssuers[20].TribeId.Length == 0, "A departed creator's request must not be approved.");
        a.Members.Add("creator"); failSave = true;
        Request(alice, new() { Action = "issuer-approve", Coin = "personal", IssuerRevision = 1 }); failSave = false;
        check(State().DesignIssuers[20].RequestedTribeId == "a" && State().DesignIssuers[20].TribeId.Length == 0,
            "Registration persistence failure must restore the unapproved request.");
        Request(alice, new() { Action = "issuer-approve", Coin = "personal", IssuerRevision = 1 });
        check(State().DesignIssuers[20] is { TribeId: "a", TribeName: "甲部落", Revision: 2, RequestedTribeId.Length: 0 },
            "Creator request plus leader approval binds a personal design once.");
        a.Members.Remove("creator"); b.Members.Add("creator"); Call(server, "RegisterCoinDesign", 20L, "bob");
        check(State().DesignIssuers[20].TribeId == "a" && State().DesignIssuers[20].OwnerUid == "creator",
            "Changing tribe or replaying a design callback must not overwrite a bound issuer or creator.");
        Call(server, "RegisterCoinDesign", 21L, "bob");
        var metal = new Item { Code = new AssetLocation("coinage:coin-copper-small"), MaxStackSize = 64 };
        var stack = new ItemStack(metal, 3); stack.Attributes.SetLong("coinType", 21); stack.Attributes.SetLong("coinShape", -6);
        b.Members.Remove("bob"); a.Members.Add("bob"); Call(server, "RegisterCoin", stack, "bob");
        string coin = EconomyInventory.CoinKey(stack);
        stack.Attributes.SetLong("coinShape", -5); Call(server, "RegisterCoin", stack, "alice");
        check(State().DesignIssuers[21].TribeId == "b" && State().Coins.Values.Count(c => c.Design == 21) == 2
            && State().Mints.Any(m => m.Uid == "bob" && m.TribeId == "a" && m.Count == 3),
            "All shapes share the creation-time issuer while mint records follow the actual operator's current tribe.");
        tribes.Tribes.Remove(b);
        Request(alice, new() { Action = "reference", Coin = coin, Text = "2", Revision = State().Policies["a"].Revision });
        check(State().Recognizes(a, coin, tribes) && snapshots["alice"].Coins.Single(c => c.Key == coin).Issuers == "bob",
            "Dissolving a tribe preserves the mold designer and allows external reference adoption.");
        Request(alice, new() { Action = "reference", Coin = "personal", Revision = State().Policies["a"].Revision });
        check(State().Policies["a"].ReferenceCoin == coin, "Server reference requests without an unpriced coin value must fail.");
        var snapshot = snapshots["alice"];
        using var stream = new MemoryStream(); Serializer.Serialize(stream, snapshot); stream.Position = 0;
        check(Serializer.Deserialize<EconomySnapshot>(stream).Coins.Single(c => c.Key == "personal").IssuerRevision == 2,
            "Issuer revisions and display metadata must survive actual protobuf serialization.");
        check(Command("personal 9 alice").Status == EnumCommandStatus.Success && State().DesignIssuers[9] is { OwnerUid: "alice", TribeId.Length: 0, Pending: false },
            "Administrator correction may explicitly resolve a legacy personal issuer.");
        Call(server, "SaveEconomy");
        var restored = new VscnCivilizationModSystem(); Set(restored, "sapi", api); Call(restored, "LoadEconomy");
        var persisted = (EconomyState)Get(restored, "economy");
        check(persisted.DesignIssuers[20].TribeId == "a" && persisted.DesignIssuers[21].TribeId == "b" && !persisted.MigrateIssuers(),
            "Fixed issuance, personal registration and historical dissolved tribes must survive a save reload.");
        State().Coins["existing-design"] = new() { Key = "existing-design", Code = "coinage:coin-copper-small", Design = 30,
            Minters = { ["bob"] = 8 }, LegacyOwnerUid = "wrong-legacy-owner" };
        State().DesignIssuers[30] = new() { OwnerUid = "wrong-legacy-owner", Pending = true };
        Refresh(alice);
        check(snapshots["alice"].Coins.Single(c => c.Key == "existing-design").Issuers == "Original mold designer",
            "An already discovered coin must read its original Coinage mold designer, not the minter or stale legacy attribution.");
        int reads = designs.Reads; Refresh(alice);
        check(designs.Reads == reads && State().DesignOwners.GetValueOrDefault(30) == null,
            "Designer display must cache database metadata without inventing authenticated mint ownership.");
        State().Coins["missing-design"] = new() { Key = "missing-design", Code = "coinage:coin-copper-small", Design = 31 };
        Refresh(alice);
        check(snapshots["alice"].Coins.Single(c => c.Key == "missing-design").Issuers.Contains("designer-unknown"),
            "An unknown designer must use the neutral missing-record label, with no tribal confirmation requirement.");
        Call(server, "RegisterCoinDesign", 30L, "creator"); Refresh(alice);
        check(snapshots["alice"].Coins.Single(c => c.Key == "existing-design").Issuers == "creator",
            "A verified creator record must take precedence over cached author text and stale legacy records.");
        check(snapshots["alice"].Coins.Where(c => c.Design == 21).All(c => c.Issuers == "bob"),
            "All shapes of the same design must display the same designer despite different mint operators.");
        saved["vscncsEconomy"] = Encoding.UTF8.GetBytes("{\"DesignIssuers\":{\"1\":null}}");
        bool rejected = false;
        try { Call(restored, "LoadEconomy"); } catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { rejected = true; }
        check(rejected, "Malformed persisted issuers must be rejected rather than overwritten.");
    }
}
