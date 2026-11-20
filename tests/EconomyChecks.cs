using System.Collections;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using SeraphCivilizationSimulation;

internal static class EconomyChecks
{
    private static object? Call(object target, string method, params object?[] args) => target.GetType()
        .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.Invoke(target, args);
    private static void Set(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
    private static object Get(object target, string name) => target.GetType()
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private sealed class Goods : Item
    {
        public static int NameReads;
        public bool RejectTrades;
        public void Initialize(ICoreAPI core) => api = core;
        public override bool Satisfies(ItemStack template, ItemStack actual) => !RejectTrades && base.Satisfies(template, actual);
        public override string GetHeldItemName(ItemStack stack) { NameReads++; return Code.ToString(); }
    }
    public static void Run(Action<bool, string> check)
    {
        if (!Lang.AvailableLanguages.ContainsKey("en"))
            Lang.AvailableLanguages["en"] = Proxy.Make<ITranslationService>((m, args) => m.Name.StartsWith("Get") ? args?[0] : null);
        Lang.ChangeLanguage("en");
        var goods = new Goods { Code = new AssetLocation("game:firewood"), ItemId = 1, MaxStackSize = 64 };
        var metal = new Goods { Code = new AssetLocation("coinage:coin-copper-small"), ItemId = 2, MaxStackSize = 64 };
        ItemStack Coin(int n = 1, long design = 7, long shape = -6)
        {
            var s = new ItemStack(metal, n); s.Attributes.SetLong("coinType", design); s.Attributes.SetLong("coinShape", shape); return s;
        }
        string coin = EconomyInventory.CoinKey(Coin()), foreign = EconomyInventory.CoinKey(Coin(design: 8));
        check(coin != foreign && coin != EconomyInventory.CoinKey(Coin(shape: -5)), "Coin identity must preserve design and shape.");
        check(EconomyInventory.CoinKey(new ItemStack(metal)) == "" && EconomyInventory.CoinKey(new ItemStack(goods)) == "", "Only valid Coinage coins may pay for trades.");
        var a = new ItemStack(goods, 10); a.Attributes.SetInt("durability", 40); a.Attributes.SetString("owner", "a");
        var b = new ItemStack(goods, 5); b.Attributes.SetString("owner", "a"); b.Attributes.SetInt("durability", 40);
        check(EconomyInventory.ItemKey(a) == EconomyInventory.ItemKey(b), "Attribute ordering and count must not change an offer key.");
        b.Attributes.SetFloat("temperature", 45);
        check(EconomyInventory.ItemKey(a) == EconomyInventory.ItemKey(b) && EconomyInventory.ItemKey(a, true) != EconomyInventory.ItemKey(b, true), "Natural temperature changes retain offers but prevent exact stack merging.");
        b.Attributes.SetInt("durability", 39);
        check(EconomyInventory.ItemKey(a) == EconomyInventory.ItemKey(b) && EconomyInventory.ItemKey(a, true) != EconomyInventory.ItemKey(b, true),
            "Durability changes must retain the same kind while preserving exact inventory state.");
        var policy = new EconomyPolicy { ReferenceCoin = coin, Rates = { [coin] = 1, [foreign] = 3 } };
        check(EconomyState.TryQuote(policy, coin, foreign, 10, out int quote) && quote == 4, "Physical-coin exchange must round upwards.");
        foreach (string invalid in new[] { "0", "-1", "NaN", "1,5", "1e3", "1000001", ".0000001" })
            check(!EconomyState.TryRate(invalid, out _), "Invalid exchange rate accepted: " + invalid);
        check(EconomyState.TryRate("0.125", out var rate) && rate == .125m && !EconomyState.TryQuote(policy, foreign, coin, 1000000, out _), "Bounded decimal rates and totals must prevent overflow.");

        var saved = new Dictionary<string, byte[]>(); bool failStore = false;
        var languages = new Dictionary<string, string>();
        var snapshots = new Dictionary<string, EconomySnapshot>(); var trades = new Dictionary<string, EconomyTradeView>();
        var snapshotSends = new Dictionary<string, int>();
        var messages = new List<string>(); var logger = Proxy.Make<ILogger>((m, args) => { if (m.Name == "Error") messages.Add(string.Join(" ", args!.Select(a => a is object[] values ? string.Join(" ", values.Select(v => v.ToString())) : a?.ToString()))); return null; });
        var save = Proxy.Make<ISaveGame>((m, args) =>
        {
            if (m.Name == "GetData") return saved.GetValueOrDefault((string)args![0]!);
            if (m.Name == "StoreData") { if (failStore) throw new IOException("intentional persistence failure"); saved[(string)args![0]!] = (byte[])args[1]!; }
            return null;
        });
        var wm = Proxy.Make<IWorldManagerAPI>((m, args) => m.Name == "get_SaveGame" ? save : null);
        var registry = Proxy.Make<IClassRegistryAPI>((m, args) => m.Name == "CreateInvNetworkUtil" ? Proxy.Make<IInventoryNetworkUtil>((m, args) => null) : null);
        var loader = Proxy.Make<IModLoader>((m, args) => m.Name == "IsModEnabled" ? true : null);
        var historicalPlayers = new Dictionary<string, IServerPlayerData>();
        var playerData = Proxy.Make<IPlayerDataManager>((m, args) => m.Name == "get_PlayerDataByUid" ? historicalPlayers : null);
        IPlayer[] online = [];
        var world = Proxy.Make<IServerWorldAccessor>((m, args) => m.Name switch
        { "get_AllOnlinePlayers" => online, "GetItem" => args?[0] is int id ? id == metal.ItemId ? metal : goods
            : args?[0] is AssetLocation code && code == metal.Code ? metal : goods, _ => null });
        var api = Proxy.Make<ICoreServerAPI>((m, args) => m.Name switch
        { "get_World" => world, "get_WorldManager" => wm, "get_Logger" => logger, "get_ClassRegistry" => registry, "get_ModLoader" => loader, "get_PlayerData" => playerData, _ => null });
        goods.Initialize(api); metal.Initialize(api);
        var channel = Proxy.Make<IServerNetworkChannel>((m, args) =>
        {
            if (m.Name == "SendPacket") foreach (var p in (IServerPlayer[])args![1]!)
            {
                if (args[0] is EconomySnapshot s) { snapshots[p.PlayerUID] = s; snapshotSends[p.PlayerUID] = snapshotSends.GetValueOrDefault(p.PlayerUID) + 1; }
                if (args[0] is EconomyTradeView t) trades[p.PlayerUID] = t;
            }
            return null;
        });
        (IServerPlayer Player, EntityPlayer Entity, InventoryGeneric Hotbar, InventoryGeneric Bag) Player(string uid, EnumClientState connection = EnumClientState.Playing)
        {
            var hotbar = new InventoryGeneric(4, "hotbar-" + uid, api); var bag = new InventoryGeneric(4, "backpack-" + uid, api);
            var entity = new EntityPlayer();
            var manager = Proxy.Make<IPlayerInventoryManager>((m, args) => m.Name switch
            { "GetOwnInventory" => (string)args![0]! == "hotbar" ? hotbar : (string)args[0]! == "backpack" ? bag : null,
                "get_ActiveHotbarSlot" or "get_OffhandHotbarSlot" => hotbar[0], _ => null });
            var player = Proxy.Make<IServerPlayer>((m, args) => m.Name switch
            { "get_PlayerUID" or "get_PlayerName" => uid, "get_Entity" => entity, "get_InventoryManager" => manager,
                "get_ConnectionState" => connection, "get_LanguageCode" => languages.GetValueOrDefault(uid, "en"), _ => null });
            entity.WatchedAttributes.SetString("playerUID", uid); entity.World = world;
            return (player, entity, hotbar, bag);
        }
        var seller = Player("seller"); var buyer = Player("buyer"); var outsider = Player("outsider");
        online = [seller.Player, buyer.Player, outsider.Player];
        var tribe = new TribeRecord { Id = "a", Name = "A", LeaderUid = "seller", Members = ["seller", "buyer"] };
        var other = new TribeRecord { Id = "b", Name = "B", LeaderUid = "outsider", Members = ["outsider"] };
        var tribes = new TribeState { Tribes = [tribe, other] };
        var economy = new EconomyState
        {
            DesignIssuers = { [7] = new() { OwnerUid = "seller", TribeId = "a", TribeName = "A" },
                [8] = new() { OwnerUid = "outsider", TribeId = "b", TribeName = "B" } },
            Coins = { [coin] = new() { Key = coin, Name = "A coin", Code = metal.Code.ToString(), Design = 7, Shape = -6, Minters = { ["seller"] = 100 } },
                [foreign] = new() { Key = foreign, Code = metal.Code.ToString(), Design = 8, Shape = -6, Name = "B coin", Minters = { ["outsider"] = 100 } } },
            Policies = { [tribe.Id] = policy, [other.Id] = new() { ReferenceCoin = foreign, Rates = { [foreign] = 1, [coin] = .333333m } } }
        };
        var system = new VscnCivilizationModSystem(); Set(system, "sapi", api); Set(system, "channel", channel); Set(system, "tribeState", tribes); Set(system, "economy", economy);
        EconomyState State() => (EconomyState)Get(system, "economy");
        void Request(IServerPlayer p, EconomyRequest r) { ((IDictionary)Get(system, "economyRequests")).Clear(); Call(system, "OnEconomyRequest", p, r); }
        void Refresh(IServerPlayer p) => Request(p, new());
        void Stock()
        {
            foreach (var inv in new[] { seller.Hotbar, seller.Bag, buyer.Hotbar, buyer.Bag }) foreach (var slot in inv) slot.Itemstack = null;
            seller.Hotbar[0].Itemstack = Coin(20); seller.Bag[0].Itemstack = new ItemStack(goods, 10); buyer.Hotbar[0].Itemstack = Coin(20);
        }
        Stock();
        check(EconomyInventory.Slots(seller.Player).Count == 8 && EconomyInventory.Read(seller.Player).Single(i => i.Code == "game:firewood").Count == 10, "Read own hotbar and backpack, without counting offhand twice.");
        Refresh(seller.Player); Refresh(buyer.Player); Refresh(outsider.Player);
        check(snapshots.ContainsKey("seller"), "Economy refresh failed: " + string.Join("\n", messages));
        historicalPlayers["seller"] = Proxy.Make<IServerPlayerData>((m, args) => m.Name == "get_PlayerUID" ? "seller" : null);
        historicalPlayers["previous-account"] = Proxy.Make<IServerPlayerData>((m, args) => m.Name == "get_PlayerUID" ? "previous-account" : null);
        online = [seller.Player]; Refresh(seller.Player);
        check(snapshots["seller"].WorldPlayerCount == 1 && historicalPlayers.Count == 2,
            "A solo player with two stored historical accounts must display one online player without changing the records.");
        online = [seller.Player, Player("seller").Player, buyer.Player, Player("loading", EnumClientState.Connecting).Player,
            Player("left", EnumClientState.Offline).Player, Player("").Player]; Refresh(seller.Player);
        check(snapshots["seller"].WorldPlayerCount == 2,
            "Online population must count distinct playing UIDs, excluding duplicate entries, loading connections and offline records.");
        using (var populationStream = new MemoryStream())
        {
            Serializer.Serialize(populationStream, snapshots["seller"]); populationStream.Position = 0;
            check(Serializer.Deserialize<EconomySnapshot>(populationStream).WorldPlayerCount == 2,
                "The corrected online population must reach clients through protobuf unchanged.");
        }
        online = []; Refresh(seller.Player);
        check(snapshots["seller"].WorldPlayerCount == 0, "Historical player records must not create online population when nobody is online.");
        online = [seller.Player, buyer.Player, outsider.Player]; historicalPlayers.Clear(); Refresh(seller.Player);
        check(snapshots["seller"].Coins.Single(c => c.Key == coin) is { Name: "A coin[game:material-copper]", Issuers: "seller", Design: 7, Shape: -6 },
            "Currency rows must receive a readable name, designer and the exact native item design and shape.");
        Lang.AvailableLanguages["vscneconomytest"] = Proxy.Make<ITranslationService>((m, args) => m.Name == "HasTranslation"
            ? true : m.Name.StartsWith("Get") ? args?[0] is "game:material-copper" ? "铜" : args?[0] : null);
        languages["seller"] = "vscneconomytest"; Refresh(seller.Player);
        check(snapshots["seller"].Coins.Single(c => c.Key == coin).Name == "A coin[铜]",
            "Currency material names must follow the recipient's language, even when the server uses a different language.");
        languages.Remove("seller"); Lang.AvailableLanguages.Remove("vscneconomytest"); Refresh(seller.Player);
        State().Coins[foreign].Name = metal.Code.Path; Refresh(seller.Player);
        check(snapshots["seller"].Coins.Single(c => c.Key == foreign).Name != metal.Code.Path,
            "Old fallback currency names must not expose a technical item code.");
        State().Coins[foreign].Name = "B coin";
        check(snapshots["seller"].WorldTribes.Count==0 && snapshots["seller"].WorldRates.Count==0,"Removed world economy must not be transmitted.");
        var display = snapshots["seller"].Items.Single(i => i.Code == "game:firewood");
        using (var reader = new BinaryReader(new MemoryStream(display.Stack)))
        {
            var stack = new ItemStack(reader); stack.ResolveBlockOrItem(world);
            check(stack.Collectible.Code == goods.Code && stack.StackSize == 1 && seller.Bag[0].StackSize == 10,
                "Native item previews must serialize goods and never change real stock.");
        }
        foreach(var action in new[]{"accountant","policy","reference","issuer-request"})
            Request(seller.Player,new(){Action=action,Id="buyer",Coin=foreign,Text="2",Enabled=true,Revision=State().Policies["a"].Revision});
        check(!snapshots["seller"].CanReadLedger && snapshots["seller"].Ledger.Count==0
            && State().Policies["a"].Rates[foreign]==3 && !State().Policies["a"].Accountants.Contains("buyer"),"Removed economy management endpoints remain active.");

        string key = EconomyInventory.ItemKey(seller.Bag[0].Itemstack!);
        Request(seller.Player, new() { Action = "offer-save", Item = key, Coin = coin, Quantity = 3, Price = 4, Enabled = false });
        var offer = State().Offers["seller"].Single();
        Request(buyer.Player, new() { Action = "open-trade", Id = "seller" });
        check(!trades.ContainsKey("buyer"), "An unpermitted item must not open a trade.");
        Request(seller.Player, new() { Action = "offer-save", Id = offer.Id, Revision = offer.Revision, Item = key, Coin = coin, Quantity = 3, Price = 4, Enabled = true });
        seller.Bag[0].Itemstack!.StackSize = 1; Refresh(seller.Player);
        check(snapshots["seller"].Offers.Single().Available == 1 && !snapshots["seller"].Offers.Single().Ready, "Insufficient stock keeps the saved price and disables trading.");
        seller.Bag[0].Itemstack = null; Refresh(seller.Player);
        check(snapshots["seller"].Offers.Single() is { Available: 0, Ready: false, DisplayItem: not null }
            && State().Offers["seller"].Single().Price == 4
            && ((List<EconomyOfferView>)Call(system, "ReadOffers", seller.Player, true, null, null)!).Count == 0,
            "No stock must keep the seller's item preview and quote visible while hiding it from buyers.");
        Request(seller.Player, new() { Action = "offer-save", Id = offer.Id, Revision = offer.Revision, Item = key, Coin = coin, Quantity = 3, Price = 5, Enabled = false });
        check(State().Offers["seller"].Single() is { Price: 5, Enabled: false } && snapshots["seller"].Offers.Single().Available == 0,
            "An out-of-stock quote must remain editable without selecting an unrelated inventory item.");
        Request(seller.Player, new() { Action = "offer-save", Id = offer.Id, Revision = offer.Revision, Item = key, Coin = coin, Quantity = 3, Price = 4, Enabled = true });
        Stock();
        string Begin(string? task = null, bool cancelTask = false)
        {
            Request(buyer.Player, new() { Action = "open-trade", Id = "seller" });
            var view = trades["buyer"];
            check(!view.Closed && view.Offers.Count > 0, "A valid held coin must open the nearby player's trade list.");
            if (task != null || State().Offers["seller"].First().IsTask)
            {
                Request(buyer.Player, new() { Action = "propose", Id = view.Id, Revision = view.Revision, Item = task ?? State().Offers["seller"].First().Id, IsTask = task != null, Text = cancelTask ? "cancel-task" : "" });
                check(trades["buyer"].Proposed && trades.GetValueOrDefault("seller")?.Proposed == true, "Tasks require both parties' confirmed terms.");
            }
            return view.Id;
        }
        void Buy(string sessionId, IServerPlayer? actor = null)
        {
            var view = trades["buyer"]; var chosen = view.Offers.First();
            Request(actor ?? buyer.Player, new() { Action = "buy", Id = sessionId, Revision = view.Revision, Item = chosen.Id, OfferRevision = chosen.Revision });
        }
        void Confirm(IServerPlayer p, string id) => Request(p, new() { Action = "confirm", Id = id, Revision = trades.GetValueOrDefault(p.PlayerUID)?.Revision ?? trades["buyer"].Revision });
        int Balance(IServerPlayer p) => EconomyInventory.Count(EconomyInventory.Slots(p), coin, true);
        int Items(IServerPlayer p) => EconomyInventory.Count(EconomyInventory.Slots(p), key);
        string id = Begin();
        check(trades["buyer"].Offers.Single().DisplayItem?.Code == "game:firewood"
            && trades["buyer"].Offers.Single().DisplayItem!.Stack.Length > 0, "The trade window must receive goods images independently from payment coins.");
        check(trades["buyer"].Coins.Single(c => c.Key == coin) is { Code: "coinage:coin-copper-small", Design: 7, Shape: -6, Issuers: "seller" },
            "The standalone player trade window must receive its currency's native item image metadata.");
        check(Balance(buyer.Player) == 20 && Items(seller.Player) == 10 && State().History.Count == 0, "Browsing must not deduct either party's inventory.");
        Buy(id, outsider.Player); check(State().History.Count == 0, "An unrelated player cannot buy in another session.");
        check(!trades.ContainsKey("seller"), "Browsing must not open seller UI.");
        var replay = new EconomyRequest { Action = "buy", Id = id, Revision = trades["buyer"].Revision, Item = trades["buyer"].Offers[0].Id, OfferRevision = trades["buyer"].Offers[0].Revision };
        Buy(id);
        check(Balance(buyer.Player) == 16 && Balance(seller.Player) == 24 && Items(buyer.Player) == 3 && Items(seller.Player) == 7 && State().History.Count == 1, "One buy click must deliver actual goods and coins exactly once.");

        Request(buyer.Player, replay); Confirm(seller.Player, id); Confirm(buyer.Player, id);
        check(State().History.Count == 1 && Balance(buyer.Player) == 16, "Duplicate confirmations cannot duplicate delivery or history.");
        Stock(); id = Begin(); seller.Bag[0].Itemstack = null; Buy(id);
        check(Balance(buyer.Player) == 20 && State().History.Count == 1 && trades["buyer"].Closed, "Source removal between confirmations must invalidate the trade.");
        Stock(); id = Begin(); failStore = true; Buy(id); failStore = false;
        check(Items(seller.Player) == 10 && Balance(buyer.Player) == 20 && State().History.Count == 1, "Failed history persistence must roll back both real inventories.");
        foreach (string condition in new[] { "distance", "dimension", "offline", "permission", "revision", "funds", "full" })
        {
            Stock(); id = Begin();
            if (condition == "distance") seller.Entity.Pos.X = 10;
            if (condition == "dimension") seller.Entity.Pos.Dimension = 1;
            if (condition == "offline") online = [buyer.Player, outsider.Player];
            if (condition == "policy") State().Policies["a"].Revision++;
            if (condition == "permission") { State().Offers["seller"].First().Enabled = false; State().Offers["seller"].First().Revision++; }
            if (condition == "revision") State().Offers["seller"].First().Revision++;
            if (condition == "funds") buyer.Hotbar[0].Itemstack = Coin(1);
            if (condition == "full") foreach (var slot in buyer.Hotbar.Skip(1).Concat(buyer.Bag)) { slot.Itemstack = new ItemStack(goods, 64); slot.Itemstack.Attributes.SetString("unique", "occupied"); }
            int startingBalance = Balance(buyer.Player);
            Buy(id);
            check(State().History.Count == 1 && Balance(buyer.Player) == startingBalance && Items(seller.Player) == 10, "Unsafe transaction must not deliver: " + condition);
            Request(buyer.Player, new() { Action = "cancel", Id = id });
            seller.Entity.Pos.X = 0; seller.Entity.Pos.Dimension = 0; online = [seller.Player, buyer.Player, outsider.Player]; State().Offers["seller"].First().Enabled = true;
        }
        Stock(); id = Begin(); Request(buyer.Player, new() { Action = "cancel", Id = id });
        check(!trades.ContainsKey("seller") && trades["buyer"].Closed && Balance(buyer.Player) == 20, "Goods browsing and completion must never open seller UI.");

        Request(seller.Player, new() { Action = "offer-delete", Id = State().Offers["seller"].First().Id, Revision = State().Offers["seller"].First().Revision });
        Request(seller.Player, new() { Action = "offer-save", IsTask = true, Text = "建造公共道路", Coin = coin, Quantity = 1, Price = 5, Enabled = true });
        check(State().Offers["seller"].Count == 0, "A new task must require details as well as a title.");
        const string taskDetails = "修建从部落广场到农田的道路。\n宽三格，铺设石材。";
        Request(seller.Player, new() { Action = "offer-save", IsTask = true, Text = "建造公共道路", Content = taskDetails, Coin = coin, Quantity = 1, Price = 5, Enabled = true });
        id = Begin(); Confirm(buyer.Player, id); Confirm(seller.Player, id);
        check(State().Tasks.Count == 1 && !State().Offers["seller"].Single().Enabled && Balance(seller.Player) == 20 && State().History.Count == 2, "Task agreement must persist and stop advertising, without paying in advance.");
        check(State().Tasks.Single().Text == "建造公共道路" && State().Tasks.Single().Content == taskDetails,
            "Agreed task contracts must retain both the title and multiline details.");
        string contract = State().Tasks.Single().Id;
        Request(seller.Player, new() { Action = "reference", Coin = foreign, Revision = State().Policies["a"].Revision });
        Request(seller.Player, new() { Action = "policy", Coin = coin, Enabled = false, Revision = State().Policies["a"].Revision });
        id = Begin(contract); Confirm(seller.Player, id); Confirm(buyer.Player, id);
        check(State().Tasks.Single().Closed && Balance(seller.Player) == 15 && Balance(buyer.Player) == 25 && State().History.Count == 3, "Task completion must pay the worker from the employer's real currency.");

        check(!State().Offers["seller"].Single().Enabled, "Completed tasks must require explicit reposting.");
        Request(seller.Player, new() { Action = "reference", Coin = coin, Text = "0.3333333333333333333333333333", Revision = State().Policies["a"].Revision });
        State().Policies["a"].Rates[foreign] = 3;
        var taskOffer = State().Offers["seller"].Single(); taskOffer.Enabled = true;
        Stock(); id = Begin(); Confirm(seller.Player, id); Confirm(buyer.Player, id);
        contract = State().Tasks.Last().Id;
        State().Policies["a"].Rates.Remove(coin);
        id = Begin(contract, true); Confirm(seller.Player, id); Confirm(buyer.Player, id);
        check(State().Tasks.Last().Closed && Balance(seller.Player) == 20 && Balance(buyer.Player) == 20, "An agreed task can be mutually cancelled after recognition changes, without payment.");
        State().Policies["a"].Rates[coin] = 1;

        Call(system, "RegisterCoin", Coin(3), "buyer");
        tribe.Members.Remove("buyer"); other.Members.Add("buyer");
        Call(system, "RegisterCoin", Coin(4), "buyer"); Refresh(seller.Player); Refresh(buyer.Player);
        check(snapshots["seller"].Ledger.Count==0 && snapshots["buyer"].Ledger.Count==0,"Ledger data must remain removed after membership changes.");
        tribe.Members.Remove("seller"); other.Members.Add("seller");
        check(State().Issuers(State().Coins[coin], tribes).Single().Id == "a" && State().Recognizes(tribe, coin, tribes), "Issuing tribe and recognition must survive all minters leaving.");
        var old = new EconomyCoin { LegacyOwnerUid = "outsider" };
        check(!State().Issuers(old, tribes).Any(), "Old coins must not guess creation-time tribe from current design-owner membership.");
        old.Minters["missing"] = 1;
        check(!State().Issuers(old, tribes).Any(), "Known mint history must take precedence over legacy design ownership.");
        Call(system, "SaveEconomy");
        var restored = new VscnCivilizationModSystem(); Set(restored, "sapi", api); Call(restored, "LoadEconomy");
        var restoredState = (EconomyState)Get(restored, "economy");
        check(restoredState.History.Count == 5 && restoredState.Mints.Count == 0 && restoredState.Tasks.All(t => t.Closed), "Restarting must preserve contracts, historical tribe membership and mint records.");
        check(restoredState.Tasks.All(t => t.Content == taskDetails),
            "Task details must survive save-game reloads.");
        using var stream = new MemoryStream(); Serializer.Serialize(stream, snapshots["seller"]); stream.Position = 0;
        var snapshot = Serializer.Deserialize<EconomySnapshot>(stream);
        check(snapshot.Ledger.Count == 0 && snapshot.History.Count == 0 && snapshot.Coins.Count == 2, "Economy snapshots must round-trip through the real protobuf network serializer.");
        check(snapshot.Coins.Single(c => c.Key == coin) is { Design: 7, Shape: -6 }
            && snapshot.Ledger.All(r => r.CoinKey.Length > 0), "Native coin appearance and distinct ledger currency keys must survive protobuf serialization.");

        other.Members.Remove("buyer"); other.Members.Remove("seller"); other.Members.Remove("outsider");
        tribe.Members.Add("outsider"); tribe.LeaderUid = "outsider"; Refresh(outsider.Player);
        var archived = snapshots["outsider"];
        check(archived.Coins.Single(c => c.Key == coin) is { Design: 7, Shape: -6, Issuers: "seller" }
            && archived.History.Count == 0 && archived.Ledger.Count == 0,
            "When all minters leave, fixed issuing currency, pictures and departed-member records remain available.");
        using (var archivedStream = new MemoryStream())
        {
            Serializer.Serialize(archivedStream, archived); archivedStream.Position = 0;
            check(Serializer.Deserialize<EconomySnapshot>(archivedStream).Coins.Single(c => c.Key == coin).Code == metal.Code.ToString(),
                "Archived currency appearance must survive the network serializer.");
        }
        tribe.Members.Remove("outsider"); tribe.LeaderUid = "seller"; other.Members.AddRange(["outsider", "buyer", "seller"]);
        Refresh(seller.Player);

        using var tradeStream = new MemoryStream();
        Serializer.Serialize(tradeStream, new EconomyTradeView { Coins = snapshot.Coins }); tradeStream.Position = 0;
        check(Serializer.Deserialize<EconomyTradeView>(tradeStream).Coins.Single(c => c.Key == coin) is { Design: 7, Shape: -6 },
            "The standalone trade packet must preserve real currency appearance.");
        saved["vscncsEconomy"] = Encoding.UTF8.GetBytes("{\"Policies\":null}");
        bool rejected = false; try { Call(restored, "LoadEconomy"); } catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { rejected = true; }
        check(rejected, "A damaged ledger must not be silently replaced by an empty economy.");

        Stock();
        var source = EconomyInventory.Slots(seller.Player); var destination = EconomyInventory.Slots(buyer.Player);
        source.Single(s => s.Itemstack?.Collectible.Code == goods.Code).Itemstack!.Attributes.SetInt("durability", 12);
        string precise = EconomyInventory.ItemKey(source.Single(s => s.Itemstack?.Collectible.Code == goods.Code).Itemstack!);
        var plan = EconomyInventory.Plan(source, destination, precise, 2, coin, 2)!;
        source[0].Itemstack!.StackSize--;
        rejected = false; try { plan.Apply(); } catch (InvalidOperationException) { rejected = true; }
        check(rejected && Items(buyer.Player) == 0, "A precomputed plan must reject any intervening source inventory change.");
        source[0].Itemstack!.StackSize++;
        plan = EconomyInventory.Plan(source, destination, precise, 2, coin, 2)!; plan.Apply();
        check(destination.Single(s => s.Itemstack?.Collectible.Code == goods.Code).Itemstack!.Attributes.GetInt("durability") == 12, "Transferred stacks must preserve real durability and attributes.");
        plan.Rollback(); check(Balance(buyer.Player) == 20 && EconomyInventory.Count(source, precise) == 10, "Rollback must restore the original real stacks.");

        // Exercise the new multi-slot path against actual inventory slots and the server request handlers.
        other.Members.Remove("seller"); other.Members.Remove("buyer"); tribe.Members.AddRange(["seller", "buyer"]);
        State().Offers["seller"].Clear(); Stock();
        var barterItem = new ItemStack(goods, 6); barterItem.Attributes.SetString("barter-material", "linen");
        string barterKey = EconomyInventory.ItemKey(barterItem);
        EconomyExchangeItem Material(int n) => new() { Key = "forged-key", Coin = foreign, Quantity = n, Display = EconomyInventory.Display(barterItem) };
        EconomyExchangeItem Money(int n) => new() { Key = "forged-key", Coin = foreign, Quantity = n, Display = EconomyInventory.Display(Coin()) };
        buyer.Bag[0].Itemstack = barterItem.Clone();
        Request(seller.Player, new() { Action = "offer-save", Item = key, Quantity = 3, Enabled = true, Exchange = [Material(2), Money(2), Money(3)] });
        var barterOffer = State().Offers["seller"].Single();
        check(barterOffer.Exchange[0].Key == barterKey && barterOffer.Exchange[0].Coin == "" && barterOffer.Exchange.Skip(1).All(e => e.Coin == coin),
            "Server must derive payment identities from resolved stack data, ignoring forged keys and currencies.");
        buyer.Bag[0].Itemstack!.StackSize = 1; int historyBefore = State().History.Count;
        id = Begin(); Buy(id);
        check(Balance(buyer.Player) == 20 && Items(seller.Player) == 10 && State().History.Count == historyBefore,
            "Missing any ingredient must abort all payment slots without partial deduction.");
        Stock(); buyer.Bag[0].Itemstack = barterItem.Clone(); id = Begin(); Buy(id);
        check(Balance(buyer.Player) == 15 && Items(buyer.Player) == 3 && EconomyInventory.Count(EconomyInventory.Slots(seller.Player), barterKey) == 2
            && EconomyInventory.Count(EconomyInventory.Slots(buyer.Player), barterKey) == 4,
            "One trade must deliver the goods and every ordinary-item and duplicate-currency payment slot together.");
        Refresh(seller.Player);
        check(snapshots["seller"].Coins.Single(c => c.Key == coin).Volume == 0 && snapshots["seller"].Coins.Single(c => c.Key == coin).TradeCount == 0,
            "Retired trade totals must not be populated.");
        check(snapshots["seller"].Ledger.Count == 0, "Removed ledger must not be transmitted.");
        using (var mixedStream = new MemoryStream())
        {
            Serializer.Serialize(mixedStream, snapshots["seller"]); mixedStream.Position = 0;
            check(Serializer.Deserialize<EconomySnapshot>(mixedStream).History.Count == 0,
                "Transaction history must remain absent from the serialized economy snapshot.");
        }
        Stock(); buyer.Bag[0].Itemstack = barterItem.Clone();
        Request(seller.Player, new() { Action = "offer-save", Id = barterOffer.Id, Revision = barterOffer.Revision, Item = key, Quantity = 3, Enabled = true,
            Exchange = [Material(2), Material(5)] });
        id = Begin(); Buy(id);
        check(EconomyInventory.Count(EconomyInventory.Slots(buyer.Player), barterKey) == 6 && Items(seller.Player) == 10,
            "Duplicate ordinary-item requirements must be summed rather than spending the same stack twice.");
        int offerRevision = (int)barterOffer.Revision;
        Request(seller.Player, new() { Action = "offer-save", Id = barterOffer.Id, Revision = barterOffer.Revision, Item = key, Quantity = 3, Enabled = true,
            Exchange = [new() { Quantity = 2, Display = new() { Stack = [1, 2, 3] } }] });
        check(barterOffer.Revision == offerRevision, "Malformed item previews must be rejected without changing a saved offer.");
        Request(seller.Player, new() { Action = "offer-save", Id = barterOffer.Id, Revision = barterOffer.Revision, Item = key, Quantity = 10, Enabled = true, Exchange = [Material(2)] });
        buyer.Hotbar[0].Itemstack = null; outsider.Hotbar[0].Itemstack = null; outsider.Bag[0].Itemstack = barterItem.Clone();
        id = Begin();
        Request(outsider.Player, new() { Action = "open-trade", Id = "seller" });
        var outsiderView = trades["outsider"];
        check(!outsiderView.Closed && outsiderView.Offers.Count == 1, "Ordinary-item barter must open without a held coin and allow concurrent browsing.");
        Buy(id);
        historyBefore = State().History.Count;
        Request(outsider.Player, new() { Action = "buy", Id = outsiderView.Id, Revision = outsiderView.Revision, Item = barterOffer.Id, OfferRevision = barterOffer.Revision });
        check(State().History.Count == historyBefore && EconomyInventory.Count(EconomyInventory.Slots(outsider.Player), barterKey) == 6,
            "A second buyer must not purchase already consumed stock.");

        State().Offers["seller"].Clear(); Stock(); seller.Bag[1].Itemstack = barterItem.Clone();
        Request(seller.Player, new() { Action = "offer-save", IsTask = true, Text = "物资酬劳任务", Content = taskDetails,
            Quantity = 1, Enabled = true, Exchange = [Material(2), Money(3)] });
        Request(outsider.Player, new() { Action = "open-trade", Id = "seller" });
        outsiderView = trades["outsider"];
        id = Begin();
        Request(outsider.Player, new() { Action = "propose", Id = outsiderView.Id, Revision = outsiderView.Revision, Item = outsiderView.Offers[0].Id });
        check(trades["outsider"].Closed && trades["seller"].Id == id && trades["seller"].Proposed,
            "A concurrent browser must not replace the seller's active task proposal.");
        Confirm(buyer.Player, id); Confirm(seller.Player, id);
        var barterTask = State().Tasks.Last();
        check(!barterTask.Closed && barterTask.Exchange.Count == 2 && Balance(seller.Player) == 20,
            "Task agreement must retain ordinary item rewards without paying before completion.");
        id = Begin(barterTask.Id); Confirm(buyer.Player, id); Confirm(seller.Player, id);
        check(barterTask.Closed && Balance(seller.Player) == 17 && Balance(buyer.Player) == 23
            && EconomyInventory.Count(EconomyInventory.Slots(buyer.Player), barterKey) == 2,
            "Task completion must pay all agreed ordinary-item and coin rewards together.");
        Call(system, "SaveEconomy"); Call(restored, "LoadEconomy"); restoredState = (EconomyState)Get(restored, "economy");
        check(restoredState.Tasks.Last().Exchange.Count == 2,
            "Barter contracts and payment history must survive save-game reloads.");

        // The UI limit must agree with server validation, inventory planning, task payment and persistence.
        State().Offers["seller"].Clear(); Stock();
        var payments24 = Enumerable.Range(0, 24).Select(_ => Material(1)).ToList();
        var payments25 = Enumerable.Range(0, 25).Select(_ => Material(1)).ToList();
        Request(seller.Player, new() { Action = "offer-save", Item = key, Quantity = 1, Enabled = true, Exchange = payments25 });
        check(State().Offers["seller"].Count == 0, "Server must reject 25 payment slots without publishing an offer.");
        Request(seller.Player, new() { Action = "offer-save", Item = key, Quantity = 1, Enabled = true, Exchange = payments24 });
        check(State().Offers["seller"].Single().Exchange.Count == 24, "Server must accept all 24 payment slots.");
        Call(system, "SaveEconomy"); Call(restored, "LoadEconomy");
        check(((EconomyState)Get(restored, "economy")).Offers["seller"].Single().Exchange.Count == 24,
            "All 24 payment slots must survive reload.");
        buyer.Bag[0].Itemstack = barterItem.Clone(); buyer.Bag[0].Itemstack!.StackSize = 30;
        var valid25 = State().Offers["seller"].Single().Exchange.ToList(); valid25.Add(valid25[0]);
        check(!EconomyInventory.Has(EconomyInventory.Slots(buyer.Player), valid25)
            && EconomyInventory.Plan(EconomyInventory.Slots(seller.Player), EconomyInventory.Slots(buyer.Player), key, 1, valid25) == null,
            "Inventory checks must reject 25 slots even when their combined stock is sufficient.");
        id = Begin(); Buy(id);
        check(EconomyInventory.Count(EconomyInventory.Slots(buyer.Player), barterKey) == 6 && Items(buyer.Player) == 1
           ,
            "A 24-slot purchase must deduct the total payment once and deliver the goods.");
        State().Offers["seller"].Clear(); Stock(); seller.Bag[1].Itemstack = barterItem.Clone(); seller.Bag[1].Itemstack!.StackSize = 24;
        Request(seller.Player, new() { Action = "offer-save", IsTask = true, Text = "24 rewards", Content = taskDetails,
            Quantity = 1, Enabled = true, Exchange = payments25 });
        check(State().Offers["seller"].Count == 0, "Server must reject 25 task reward slots.");
        Request(seller.Player, new() { Action = "offer-save", IsTask = true, Text = "24 rewards", Content = taskDetails,
            Quantity = 1, Enabled = true, Exchange = payments24 });
        check(State().Offers["seller"].Single().Exchange.Count == 24, "Server must accept all 24 task reward slots.");
        id = Begin(); Confirm(buyer.Player, id); Confirm(seller.Player, id);
        var rewardTask24 = State().Tasks.Last();
        check(!rewardTask24.Closed && rewardTask24.Exchange.Count == 24, "Task acceptance must retain every one of the 24 rewards.");
        Call(system, "SaveEconomy"); Call(restored, "LoadEconomy");
        check(((EconomyState)Get(restored, "economy")).Tasks.Last().Exchange.Count == 24,
            "A signed 24-slot reward contract must survive reload.");
        id = Begin(rewardTask24.Id); Confirm(buyer.Player, id); Confirm(seller.Player, id);
        check(rewardTask24.Closed && EconomyInventory.Count(EconomyInventory.Slots(buyer.Player), barterKey) == 24
            && EconomyInventory.Count(EconomyInventory.Slots(seller.Player), barterKey) == 0,
            "Task completion must pay all 24 rewards together without losing or duplicating items.");

        // Game loading resolves the outer tool, but Toolsmith parts can remain unresolved.
        State().Offers["seller"].Clear(); Stock();
        var assembled = seller.Bag[0].Itemstack!;
        assembled.Attributes.SetItemstack("tinkeredToolHead", new ItemStack(goods));
        assembled.Attributes.SetItemstack("tinkeredToolHandle", new ItemStack(metal));
        using (var reader = new BinaryReader(new MemoryStream(assembled.ToBytes()))) assembled = new ItemStack(reader);
        assembled.ResolveBlockOrItem(world); seller.Bag[0].Itemstack = assembled;
        Request(seller.Player, new() { Action = "offer-save", Item = EconomyInventory.Read(seller.Player).Single(i => i.Code == goods.Code.ToString()).Key,
            Coin = coin, Quantity = 2, Price = 8, Enabled = true });
        check(snapshots["seller"].Offers.Single() is { NeedsItemSelection: false, Ready: true, Available: 10 }
            && State().Offers["seller"].Single().Requirement?.Mappings.Count == 2,
            "A reloaded Toolsmith tool must publish with resolved head/handle mappings instead of a false legacy-item warning.");
        Call(restored, "LoadEconomy");
        check(((EconomyState)Get(restored, "economy")).Offers["seller"].Single().Requirement?.Mappings.Count == 2,
            "New modular tool quotes must load successfully with stable part mappings.");
        var savedModular = State().Offers["seller"].Single();
        assembled.Attributes.SetInt("durability", 27);
        using (var reader = new BinaryReader(new MemoryStream(assembled.ToBytes()))) assembled = new ItemStack(reader);
        assembled.ResolveBlockOrItem(world);
        // Reproduce a partially resolved old quote: the head was resolved, the handle was not.
        assembled.Attributes.GetItemstack("tinkeredToolHead").ResolveBlockOrItem(world);
        var brokenKey = EconomyInventory.ItemKey(assembled);
        var brokenRequirement = EconomyItemMatching.Requirement(assembled);
        var brokenPayment = new EconomyExchangeItem { Key = brokenKey, Quantity = 3,
            Requirement = brokenRequirement, Display = EconomyInventory.Display(assembled) };
        savedModular.ItemKey = brokenKey; savedModular.Requirement = brokenRequirement; savedModular.Exchange = [brokenPayment];
        savedModular.Revision = 5;
        var signedModular = new EconomyOffer { IsTask = true, Name = "Signed modular reward", Exchange = [brokenPayment] };
        var signedModularTask = new EconomyTask { Employer = "seller", Worker = "buyer", OfferId = signedModular.Id, Exchange = signedModular.Exchange };
        State().Offers["seller"].Add(signedModular); State().Tasks.Add(signedModularTask);
        Call(system, "SaveEconomy"); Call(restored, "LoadEconomy");
        check(((EconomyState)Get(restored, "economy")).Offers["seller"].First().Requirement?.Mappings.Count == 1,
            "Incomplete old part mappings must not prevent loading the entire economy ledger.");
        // Other material variants must not be substituted based only on the tool's code/name.
        seller.Bag[0].Itemstack!.Attributes.SetItemstack("tinkeredToolHandle", new ItemStack(goods));
        Refresh(seller.Player);
        check(savedModular.ItemKey == brokenKey && savedModular.Revision == 5
            && snapshots["seller"].Offers.Single(o => o.Id == savedModular.Id).NeedsItemSelection,
            "A different handle material must not recover an incomplete quote.");
        seller.Bag[0].Itemstack!.Attributes.SetItemstack("tinkeredToolHandle", new ItemStack(metal));
        seller.Bag[0].Itemstack!.Attributes.SetInt("durability", 15);
        Refresh(seller.Player);
        check(savedModular.Requirement?.Mappings.Count == 2 && savedModular.ItemKey != brokenKey && savedModular.Revision == 6
            && savedModular.Exchange.Single() is { Quantity: 3, Requirement.Mappings.Count: 2 }
            && snapshots["seller"].Offers.Single(o => o.Id == savedModular.Id) is { NeedsItemSelection: false, Ready: true, Quantity: 2 },
            "Incomplete goods and payment quotes must recover verified part identities after wear without losing prices or quantities.");
        check(signedModular.Revision == 0 && signedModularTask.Exchange.Single().Key == brokenKey
            && signedModularTask.Exchange.Single().Requirement?.Mappings.Count == 1,
            "Part mapping recovery must not rewrite signed contracts or shared reward references.");
        Refresh(seller.Player); Call(restored, "LoadEconomy");
        check(savedModular.Revision == 6 && ((EconomyState)Get(restored, "economy")).Offers["seller"].First().Requirement?.Mappings.Count == 2,
            "Recovered part mappings must persist and migration must run only once.");
        signedModularTask.Closed = true;

        // Upgrade only recoverable unsigned offers; keep accepted contracts and unknown hashes intact.
        ((IDictionary)Get(system, "tradeSessions")).Clear(); Stock(); State().Offers["seller"].Clear();
        var legacyStock = seller.Bag[0].Itemstack!;
        legacyStock.Attributes.SetInt("durability", 60);
        var legacyReward = legacyStock.Clone(); legacyReward.Attributes.SetInt("durability", 45);
        var legacyOffer = new EconomyOffer { ItemKey = EconomyItemMatching.LegacyKey(legacyStock), Name = "Legacy goods", Quantity = 2,
            Enabled = true, Exchange = [new() { Key = EconomyItemMatching.LegacyKey(legacyReward), Quantity = 3, Display = EconomyInventory.Display(legacyReward) }] };
        State().Offers["seller"].Add(legacyOffer); Refresh(seller.Player);
        check(EconomyItemMatching.Modern(legacyOffer.ItemKey) && legacyOffer.Requirement != null
            && EconomyItemMatching.Modern(legacyOffer.Exchange[0].Key) && legacyOffer.Exchange[0].Requirement != null && legacyOffer.Revision == 1,
            "Recoverable legacy goods and payment templates must migrate together with a new offer revision.");
        Call(restored, "LoadEconomy");
        var migratedSaved = ((EconomyState)Get(restored, "economy")).Offers["seller"].Single();
        check(migratedSaved.Requirement != null && migratedSaved.Exchange[0].Requirement != null
            && migratedSaved.Quantity == 2 && migratedSaved.Exchange[0].Quantity == 3,
            "Migrated templates and unchanged prices must persist across save-game reloads.");
        var unknownOffer = new EconomyOffer { ItemKey = new string('F', 64), Name = "Unavailable old item", Quantity = 4, Coin = coin, Price = 8, Enabled = true };
        State().Offers["seller"].Add(unknownOffer); Refresh(seller.Player);
        check(snapshots["seller"].Offers.Single(o => o.Id == unknownOffer.Id) is { NeedsItemSelection: true, Quantity: 4, Price: 8, Ready: false }
            && !((List<EconomyOfferView>)Call(system, "ReadOffers", seller.Player, true, null, null)!).Any(o => o.Id == unknownOffer.Id),
            "An unrecoverable legacy quote must keep its settings and request reselection without being offered to buyers.");
        var legacyTaskOffer = new EconomyOffer { IsTask = true, Name = "Existing contract", Content = "Keep agreed terms", Enabled = false,
            Exchange = [new() { Key = EconomyItemMatching.LegacyKey(legacyReward), Quantity = 1, Display = EconomyInventory.Display(legacyReward) }] };
        var oldTask = new EconomyTask { Employer = "seller", Worker = "buyer", OfferId = legacyTaskOffer.Id, Text = legacyTaskOffer.Name,
            Reward = 1, Exchange = legacyTaskOffer.Exchange };
        State().Offers["seller"].Add(legacyTaskOffer); State().Tasks.Add(oldTask); Refresh(seller.Player);
        check(!EconomyItemMatching.Modern(legacyTaskOffer.Exchange[0].Key) && oldTask.Exchange[0].Key == EconomyItemMatching.LegacyKey(legacyReward)
            && legacyTaskOffer.Revision == 0, "An accepted old task and its source quote must retain their agreed exact reward condition.");
        var unsignedTask = new EconomyOffer { IsTask = true, Name = "Unsigned contract", Content = "Can migrate", Enabled = true,
            Exchange = [new() { Key = EconomyItemMatching.LegacyKey(legacyReward), Quantity = 1, Display = EconomyInventory.Display(legacyReward) }] };
        State().Offers["seller"].Add(unsignedTask); Refresh(seller.Player);
        check(EconomyItemMatching.Modern(unsignedTask.Exchange[0].Key) && !EconomyItemMatching.Modern(oldTask.Exchange[0].Key),
            "Migrating a different task quote must not mutate an existing contract's reward references.");
        oldTask.Closed = true;
        // Version 1.0.49 stored tool mode and Toolsmith wear in kind2 identities.
        State().Offers["seller"].Clear(); Stock();
        var previousTool = seller.Bag[0].Itemstack!;
        previousTool.Attributes.SetInt("toolMode", 1); previousTool.Attributes.SetInt("toolSharpnessCurrent", 80);
        previousTool.Attributes.SetFloat("sp:smithingQuality", .9f);
        string PreviousKey(ItemStack stack) => (string)typeof(EconomyItemMatching).GetMethod("Key", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [stack.Class, stack.Collectible.Code.ToString(), stack.Attributes, true])!;
        string previousKey = PreviousKey(previousTool);
        var previousRequirement = new EconomyItemRequirement { Class = previousTool.Class, Code = goods.Code.ToString(), Attributes = ((TreeAttribute)previousTool.Attributes).ToBytes() };
        var previousPayment = new EconomyExchangeItem { Key = previousKey, Quantity = 3, Requirement = previousRequirement, Display = EconomyInventory.Display(previousTool) };
        var previousOffer = new EconomyOffer { ItemKey = previousKey, Requirement = previousRequirement, Quantity = 2, Enabled = true, Exchange = [previousPayment] };
        var signedOffer = new EconomyOffer { IsTask = true, Exchange = [previousPayment] };
        var signedTask = new EconomyTask { OfferId = signedOffer.Id, Exchange = signedOffer.Exchange };
        State().Offers["seller"].AddRange([previousOffer, signedOffer]); State().Tasks.Add(signedTask);
        previousTool.Attributes.SetInt("toolMode", 0); previousTool.Attributes.SetInt("toolSharpnessCurrent", 20);
        Refresh(seller.Player);
        check(previousOffer.ItemKey == EconomyInventory.ItemKey(previousTool) && previousOffer.Revision == 1
            && previousOffer.Exchange.Single().Key == previousOffer.ItemKey && previousOffer.Exchange.Single().Quantity == 3
            && snapshots["seller"].Offers.Single(o => o.Id == previousOffer.Id) is { Available: 10, Ready: true },
            "Old kind2 tool goods and payments must migrate from saved attributes even after current tool mode and wear change.");
        check(signedOffer.Revision == 0 && signedTask.Exchange.Single().Key == previousKey
            && !EconomyItemMatching.Matches(previousTool, previousKey, previousRequirement, world),
            "Signed kind2 task rewards must retain their original mode and sharpness requirements without shared-reference migration.");
        var agreedTool = previousTool.Clone(); agreedTool.Attributes.SetInt("toolMode", 1); agreedTool.Attributes.SetInt("toolSharpnessCurrent", 80);
        check(EconomyItemMatching.Matches(agreedTool, previousKey, previousRequirement, world), "An unchanged signed kind2 tool reward must still match.");
        Call(restored, "LoadEconomy");
        check(((EconomyState)Get(restored, "economy")).Offers["seller"].First().ItemKey == previousOffer.ItemKey,
            "Upgraded tool keys and requirements must persist across save reloads.");
        Refresh(seller.Player);
        check(previousOffer.Revision == 1, "Tool key migration must be idempotent.");
        signedTask.Closed = true;
        State().Offers["seller"].Clear(); Stock();
        var rollbackOffer = new EconomyOffer { ItemKey = EconomyItemMatching.LegacyKey(seller.Bag[0].Itemstack!), Name = "Rollback quote", Coin = coin, Price = 2 };
        State().Offers["seller"].Add(rollbackOffer);
        string rollbackKey = rollbackOffer.ItemKey;
        failStore = true; rejected = false;
        try { Call(system, "MigrateEconomyOffers", seller.Player); }
        catch (TargetInvocationException ex) when (ex.InnerException is IOException) { rejected = true; }
        failStore = false;
        check(rejected && State().Offers["seller"].Single() is { Requirement: null, Revision: 0 }
            && State().Offers["seller"].Single().ItemKey == rollbackKey, "A failed migration save must restore the original quote and revision.");

        // Drive state-insensitive sales through the real handlers, including preview checks and payment history.
        State().Offers["seller"].Clear(); Stock(); goods.Durability = 100;
        seller.Bag[0].Itemstack!.Attributes.SetInt("durability", 70);
        Request(seller.Player, new() { Action = "offer-save", Item = key, Coin = coin, Quantity = 1, Price = 2, Enabled = true });
        seller.Bag[0].Itemstack!.Attributes.SetInt("durability", 60); Refresh(seller.Player);
        check(snapshots["seller"].Offers.Single() is { Ready: true, Available: 10, DisplayItem.DurabilityLow: 60 },
            "Published goods must remain available after use, displaying current condition rather than hiding the quote.");
        goods.RejectTrades = true; Refresh(seller.Player);
        check(snapshots["seller"].Offers.Single() is { Ready: false, Available: 0 } && EconomyInventory.Count(EconomyInventory.Slots(seller.Player), key,
            requirement: State().Offers["seller"].Single().Requirement) == 0,
            "A mod's native Satisfies rejection must disable a retained seller quote and prevent inventory counting.");
        goods.RejectTrades = false;
        historyBefore = State().History.Count; id = Begin(); seller.Bag[0].Itemstack!.Attributes.SetInt("durability", 59); Buy(id);
        check(State().History.Count == historyBefore && Balance(buyer.Player) == 20 && trades["buyer"].Closed
            && trades["buyer"].Message == "vsscsmod:ui.economy-stock-changed",
            "A buyer must review goods again when their condition changes after browsing, with no deduction.");
        id = Begin(); Buy(id);
        check(Items(buyer.Player) == 1 && buyer.Bag.Concat(buyer.Hotbar).Single(s => s.Itemstack?.Collectible == goods).Itemstack!.Attributes.GetInt("durability") == 59,
            "Reopened sales must deliver and record the actual current condition without repairing the goods.");
        Stock(); State().Offers["seller"].Clear();
        var ordinaryPayment = new ItemStack(goods); ordinaryPayment.Attributes.SetString("barter-material", "payment");
        var wornPayment = ordinaryPayment.Clone(); wornPayment.Attributes.SetInt("durability", 12); wornPayment.StackSize = 5;
        buyer.Bag[0].Itemstack = wornPayment;
        Request(seller.Player, new() { Action = "offer-save", Item = key, Quantity = 1, Enabled = true,
            Exchange = [new() { Quantity = 3, Display = EconomyInventory.Display(ordinaryPayment) }] });
        var modernBarter = State().Offers["seller"].Single(); id = Begin(); Buy(id);
        check(buyer.Bag[0].StackSize == 2 && seller.Bag.Concat(seller.Hotbar).Single(s => s.Itemstack?.Attributes.GetString("barter-material") == "payment")
                .Itemstack!.Attributes.GetInt("durability") == 12,
            "Catalog-style payment templates must accept used goods while preserving actual payment condition in stock and history.");
        using (var paymentWire = new MemoryStream())
        {
            Serializer.Serialize(paymentWire, modernBarter.Exchange[0]); paymentWire.Position = 0;
            var receivedPayment = Serializer.Deserialize<EconomyExchangeItem>(paymentWire);
            check(receivedPayment.Requirement == null && receivedPayment.Display?.Stack.Length > 0
                && modernBarter.Exchange[0].Requirement != null, "Server matching templates must persist without duplicating attribute payloads in client packets.");
        }
        string missing = (string)Call(system, "MissingPayment", buyer.Player, new[] {
            new EconomyExchangeItem { Key = modernBarter.Exchange[0].Key, Quantity = 3, Requirement = modernBarter.Exchange[0].Requirement, Display = modernBarter.Exchange[0].Display },
            new EconomyExchangeItem { Key = modernBarter.Exchange[0].Key, Quantity = 4, Requirement = modernBarter.Exchange[0].Requirement, Display = modernBarter.Exchange[0].Display }
        }, "en")!;
        check(missing.EndsWith(" × 5"), "Shortage messages must sum repeated requirements and report only the missing quantity.");
        var validRequirement = EconomyItemMatching.Requirement(new ItemStack(goods));
        check((bool)typeof(VscnCivilizationModSystem).GetMethod("ValidRequirement", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [validRequirement])!,
            "Saved ordinary templates must pass structural validation.");
        validRequirement.Mappings = null!;
        check(!(bool)typeof(VscnCivilizationModSystem).GetMethod("ValidRequirement", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [validRequirement])!,
            "A damaged nested-ID mapping must be rejected when loading saved templates.");
        // Multi-select purchases share one inventory plan and persistence transaction.
        goods.Durability = 100;
        State().Offers["seller"].Clear(); Stock(); buyer.Bag[0].Itemstack = wornPayment.Clone();
        for (int i = 0; i < 2; i++) Request(seller.Player, new() { Action = "offer-save", Item = key, Quantity = 3, Enabled = true,
            Exchange = [new() { Quantity = 3, Display = EconomyInventory.Display(ordinaryPayment) }, Money(4)] });
        EconomyRequest Batch(string sessionId) => new() { Action = "buy", Id = sessionId, Revision = trades["buyer"].Revision,
            Selections = trades["buyer"].Offers.Where(o => !o.IsTask).Select(o => new EconomyOfferSelection { Id = o.Id, Revision = o.Revision }).ToList() };
        id = Begin(); historyBefore = State().History.Count;
        check(trades["buyer"].PaymentStock[modernBarter.Exchange[0].Key] == 5 && trades["buyer"].PaymentStock[coin] == 20,
            "Payment stock must use server template matching and count used tools by kind.");
        using (var batchWire = new MemoryStream())
        {
            Serializer.Serialize(batchWire, trades["buyer"]); batchWire.Position = 0;
            check(Serializer.Deserialize<EconomyTradeView>(batchWire).PaymentStock[coin] == 20,
                "Buyer payment counts must survive protobuf serialization.");
        }
        Request(buyer.Player, Batch(id));
        check(State().History.Count == historyBefore && Balance(buyer.Player) == 20 && Items(seller.Player) == 10
            && buyer.Bag[0].StackSize == 5 && trades["buyer"].Message.EndsWith("economy-payment-missing"),
            "Two individually affordable offers must not spend the same payment stack twice or partially transfer goods.");
        Stock(); buyer.Bag[0].Itemstack = wornPayment.Clone(); buyer.Bag[0].Itemstack!.StackSize = 8;
        id = Begin(); var batchRequest = Batch(id);
        using (var batchWire = new MemoryStream())
        {
            Serializer.Serialize(batchWire, batchRequest); batchWire.Position = 0;
            batchRequest = Serializer.Deserialize<EconomyRequest>(batchWire);
            check(batchRequest.Selections.Count == 2 && batchRequest.Selections[1].Revision > 0,
                "Multi-select IDs and individual offer revisions must survive the request wire format.");
        }
        Request(outsider.Player, batchRequest);
        check(State().History.Count == historyBefore && !trades["buyer"].Closed, "Other players cannot execute a selected shopping basket.");
        Request(buyer.Player, batchRequest);
        check(State().History.Count == historyBefore + 2 && Items(buyer.Player) == 6 && Items(seller.Player) == 4
            && Balance(buyer.Player) == 12 && buyer.Bag[0].StackSize == 2,
            "Multi-select must deliver every selected quantity and deduct the combined mixed payment atomically.");

        Request(buyer.Player, batchRequest);
        check(State().History.Count == historyBefore + 2 && Balance(buyer.Player) == 12, "Replaying a completed basket cannot charge twice.");
        historyBefore = State().History.Count;
        Stock(); buyer.Bag[0].Itemstack = ordinaryPayment.Clone(); buyer.Bag[0].Itemstack!.StackSize = 8;
        seller.Bag[0].Itemstack!.StackSize = 5; id = Begin(); Request(buyer.Player, Batch(id));
        check(State().History.Count == historyBefore && Balance(buyer.Player) == 20 && Items(seller.Player) == 5,
            "Overlapping goods quotes must share the seller stock and reject the whole basket on cumulative shortage.");
        Stock(); buyer.Bag[0].Itemstack = ordinaryPayment.Clone(); buyer.Bag[0].Itemstack!.StackSize = 8;
        id = Begin(); batchRequest = Batch(id); batchRequest.Selections[1] = batchRequest.Selections[0];
        Request(buyer.Player, batchRequest);
        check(State().History.Count == historyBefore && Balance(buyer.Player) == 20, "Duplicate basket IDs must be rejected.");
        id = Begin(); batchRequest = Batch(id); batchRequest.Selections[1].Revision++;
        Request(buyer.Player, batchRequest);
        check(State().History.Count == historyBefore && Items(seller.Player) == 10, "One stale offer revision must invalidate the entire basket.");
        id = Begin(); batchRequest = Batch(id); seller.Bag[0].Itemstack!.Attributes.SetInt("durability", 31);
        Request(buyer.Player, batchRequest);
        check(State().History.Count == historyBefore && trades["buyer"].Message.EndsWith("economy-stock-changed"),
            "Basket purchases must reject changed goods previews before any deduction.");
        Stock(); buyer.Bag[0].Itemstack = ordinaryPayment.Clone(); buyer.Bag[0].Itemstack!.StackSize = 8;
        foreach (var slot in buyer.Hotbar.Skip(1).Concat(buyer.Bag.Skip(1)))
        { slot.Itemstack = new ItemStack(goods, 64); slot.Itemstack.Attributes.SetString("unique", "occupied"); }
        id = Begin(); Request(buyer.Player, Batch(id));
        check(State().History.Count == historyBefore && Items(seller.Player) == 10 && Balance(buyer.Player) == 20 && buyer.Bag[0].StackSize == 8,
            "A full buyer inventory must reject the entire basket without taking payment.");
        Stock(); buyer.Bag[0].Itemstack = ordinaryPayment.Clone(); buyer.Bag[0].Itemstack!.StackSize = 8;
        foreach (var slot in seller.Hotbar.Skip(1).Concat(seller.Bag.Skip(1)))
        { slot.Itemstack = new ItemStack(goods, 64); slot.Itemstack.Attributes.SetString("unique", "occupied"); }
        id = Begin(); Request(buyer.Player, Batch(id));
        check(State().History.Count == historyBefore && Balance(buyer.Player) == 20 && buyer.Bag[0].StackSize == 8,
            "A seller without room for ordinary payment must reject the entire basket without delivering goods.");
        Stock(); buyer.Bag[0].Itemstack = ordinaryPayment.Clone(); buyer.Bag[0].Itemstack!.StackSize = 8;
        seller.Bag[0].Itemstack!.StackSize = 4; seller.Bag[1].Itemstack = new ItemStack(goods, 6);
        id = Begin(); Request(buyer.Player, Batch(id));
        check(State().History.Count == historyBefore + 2 && Items(buyer.Player) == 6 && Balance(buyer.Player) == 12,
            "Identical goods split across slots must support multiple quotes even when the second delivery crosses a stack boundary.");
        historyBefore = State().History.Count;
        Stock(); buyer.Bag[0].Itemstack = ordinaryPayment.Clone(); buyer.Bag[0].Itemstack!.StackSize = 8;
        seller.Bag[0].Itemstack!.StackSize = 3; seller.Bag[1].Itemstack = new ItemStack(goods, 7);
        seller.Bag[1].Itemstack!.Attributes.SetInt("durability", 31);
        id = Begin(); Request(buyer.Player, Batch(id));
        check(State().History.Count == historyBefore && Items(buyer.Player) == 0 && Balance(buyer.Player) == 20,
            "Overlapping quotes must not substitute a differently worn tool after the first previewed stack is consumed.");
        Stock(); buyer.Bag[0].Itemstack = ordinaryPayment.Clone(); buyer.Bag[0].Itemstack!.StackSize = 8;
        id = Begin(); failStore = true; Request(buyer.Player, Batch(id)); failStore = false;
        check(State().History.Count == historyBefore && Items(seller.Player) == 10 && Balance(buyer.Player) == 20 && buyer.Bag[0].StackSize == 8,
            "Persistence failure must restore every basket slot and remove every new history entry.");
        id = Begin(); var oldBatch = Batch(id); buyer.Bag[0].Itemstack!.StackSize = 1; buyer.Bag[0].MarkDirty();
        Set(system, "economyFlushAt", 0L); Call(system, "FlushEconomyUpdates");
        check(trades["buyer"].PaymentStock[EconomyInventory.ItemKey(ordinaryPayment)] == 1
            && trades["buyer"].Revision > oldBatch.Revision && !trades["buyer"].Closed,
            "Inventory changes must refresh payment counts and browse revision in the open trade window.");
        Request(buyer.Player, oldBatch);
        check(State().History.Count == historyBefore && !trades["buyer"].Closed, "A basket from an outdated preview cannot execute.");
        Request(buyer.Player, new() { Action = "cancel", Id = id });
        goods.Durability = 0;

        ((IDictionary)Get(system, "tradeSessions")).Clear(); Stock(); Refresh(seller.Player); Refresh(buyer.Player);
        for (int i = 0; i < 10; i++) { Set(system, "economyFlushAt", 0L); Call(system, "FlushEconomyUpdates"); }
        int readsAtIdle = Goods.NameReads, sendsAtIdle = snapshotSends.Values.Sum();
        for (int i = 0; i < 500; i++) Call(system, "FlushEconomyUpdates");
        check(snapshotSends.Values.Sum() == sendsAtIdle && Goods.NameReads == readsAtIdle,
            "500 idle economy ticks must perform no snapshot construction, item-name reads or packet sends.");
        int previousSellerSends = snapshotSends["seller"];
        seller.Bag[0].Itemstack!.StackSize = 7; seller.Bag[0].MarkDirty();
        for (int i = 0; i < 5; i++) seller.Bag[0].MarkDirty();
        for (int i = 0; i < 2; i++) { Set(system, "economyFlushAt", 0L); Call(system, "FlushEconomyUpdates"); }
        check(snapshots["seller"].Items.Single(i => i.Code == goods.Code.ToString()).Count == 7
            && snapshotSends["seller"] == previousSellerSends + 1, "A real changed slot must update current stock, with duplicate dirty events coalesced.");
        Set(system, "economyFlushAt", 0L); Call(system, "FlushEconomyUpdates");
        int sendsAfterChange = snapshotSends.Values.Sum(); readsAtIdle = Goods.NameReads;
        seller.Bag[0].Itemstack!.Attributes.SetFloat("temperature", 45); seller.Bag[0].MarkDirty();
        seller.Bag[0].Itemstack!.Attributes.SetFloat("temperature", 46); seller.Bag[0].MarkDirty();
        for (int i = 0; i < 10; i++) Call(system, "FlushEconomyUpdates");
        check(snapshotSends.Values.Sum() == sendsAfterChange && Goods.NameReads == readsAtIdle,
            "Natural temperature changes must not construct or send economy snapshots.");
        seller.Bag[0].Itemstack!.Attributes.SetItemstack("contents", new ItemStack(goods, 2)); seller.Bag[0].MarkDirty();
        for (int i = 0; i < 4; i++) { Set(system, "economyFlushAt", 0L); Call(system, "FlushEconomyUpdates"); }
        sendsAfterChange = snapshotSends["seller"];
        seller.Bag[0].Itemstack!.Attributes.GetItemstack("contents").StackSize = 1; seller.Bag[0].MarkDirty();
        for (int i = 0; i < 4; i++) { Set(system, "economyFlushAt", 0L); Call(system, "FlushEconomyUpdates"); }
        check(snapshotSends["seller"] == sendsAfterChange + 1, "Changing only a container's nested quantity must update the economy once.");
        Request(seller.Player, new() { Action = "close" }); int closedSends = snapshotSends["seller"];
        seller.Bag[0].Itemstack!.StackSize--; seller.Bag[0].MarkDirty();
        Set(system, "economyFlushAt", 0L); Call(system, "FlushEconomyUpdates");
        check(snapshotSends["seller"] == closedSends, "Closing the economy must unsubscribe from automatic snapshot delivery.");
        Call(system, "DisconnectEconomy", "seller");
        check(!((IDictionary)Get(system, "economyInventoryHooks")).Contains("seller"),
            "Disconnect must release the player's inventory change hooks.");
        Call(system, "DisconnectEconomy", "buyer");
        State().Offers["seller"].Clear(); Stock();
        Request(seller.Player, new() { Action = "offer-save", Item = key, Coin = coin, Quantity = 3, Price = 4, Enabled = true });
        foreach (bool ordinaryHand in new[] { false, true })
        {
            Stock(); buyer.Bag[1].Itemstack = buyer.Hotbar[0].Itemstack;
            buyer.Hotbar[0].Itemstack = ordinaryHand ? new ItemStack(goods) : null;
            int goodsBefore = Items(buyer.Player);
            id = Begin();
            check(trades["buyer"].Offers.Single() is { Ready: true, Price: 4 },
                "Empty hands and ordinary held items must show the original currency quote.");
            Buy(id);
            check(Balance(buyer.Player) == 16 && Items(buyer.Player) == goodsBefore + 3,
                "Currency payments must consume backpack coins without requiring a held coin.");
        }
        Stock(); buyer.Bag[1].Itemstack = buyer.Hotbar[0].Itemstack; buyer.Hotbar[0].Itemstack = null;
        id = Begin(); var batchView = trades["buyer"];
        Request(buyer.Player, new() { Action = "buy", Id = id, Revision = batchView.Revision,
            Selections = [new() { Id = batchView.Offers[0].Id, Revision = batchView.Offers[0].Revision }] });
        check(Balance(buyer.Player) == 16 && Items(buyer.Player) == 3,
            "Batch purchases must use the same empty-hand currency quote as individual purchases.");
        Stock(); buyer.Hotbar[0].Itemstack = null; int unpaidHistory = State().History.Count;
        id = Begin(); Buy(id);
        check(State().History.Count == unpaidHistory && Items(seller.Player) == 10 && Items(buyer.Player) == 0,
            "Removing the entry coin requirement must never make currency-priced goods free.");
        // Personal offers must work without either participant belonging to a tribe.
        foreach (var group in tribes.Tribes) { group.Members.Remove("seller"); group.Members.Remove("buyer"); }
        State().Offers["seller"].Clear(); Stock();
        Request(seller.Player, new() { Action = "offer-save", Item = key, Coin = coin, Quantity = 3, Price = 4, Enabled = true });
        check(State().Offers["seller"].Single().Enabled && snapshots["seller"].TribeId.Length == 0
            && snapshots["seller"].Offers.Single().Ready, "An unaffiliated seller must be able to publish a personal currency offer.");
        id = Begin(); Buy(id);
        check(Balance(buyer.Player) == 16 && Items(buyer.Player) == 3, "Unaffiliated players must complete a currency sale at its stated price.");
        State().Offers["seller"].Clear(); Stock(); buyer.Bag[1].Itemstack = barterItem.Clone();
        Request(seller.Player, new() { Action = "offer-save", Item = key, Quantity = 3, Enabled = true, Exchange = [Material(2)] });
        id = Begin(); var personalBatch = trades["buyer"];
        Request(buyer.Player, new() { Action = "buy", Id = id, Revision = personalBatch.Revision,
            Selections = [new() { Id = personalBatch.Offers.Single().Id, Revision = personalBatch.Offers.Single().Revision }] });
        check(Items(buyer.Player) == 3 && EconomyInventory.Count(EconomyInventory.Slots(seller.Player), barterKey) == 2
            && Balance(buyer.Player) == 20, "Unaffiliated players must complete a barter basket without consuming currency.");
        State().Offers["seller"].Clear(); Stock();
        Request(seller.Player, new() { Action = "offer-save", IsTask = true, Text = "Personal task", Content = "Complete the agreed work",
            Coin = coin, Quantity = 1, Price = 5, Enabled = true });
        id = Begin(); Confirm(buyer.Player, id); Confirm(seller.Player, id);
        var personalTask = State().Tasks.Last();
        check(!personalTask.Closed, "Unaffiliated players must be able to agree to a personal task.");
        id = Begin(personalTask.Id); Confirm(buyer.Player, id); Confirm(seller.Player, id);
        check(personalTask.Closed && Balance(seller.Player) == 15 && Balance(buyer.Player) == 25,
            "Unaffiliated task settlement must pay the worker exactly once.");
        check(State().History.Count > 0 && snapshots.Values.All(p => p.History.Count == 0)
            && JsonConvert.SerializeObject(State()).Contains("\"History\""), "Global transaction history must persist only on the server.");
        var legacyEconomy = JsonConvert.DeserializeObject<EconomyState>("{\"History\":[{\"Id\":\"legacy\"}]}")!;
        check(legacyEconomy.History.Count == 1, "Loading old saves must preserve transaction records.");
        Call(system, "StopEconomy");
        check(((IDictionary)Get(system, "economyInventoryHooks")).Count == 0, "Shutdown must release all inventory observers.");
        EconomyCacheChecks.Run(check);
        EconomyMatchingChecks.Run(check);
    }
}
