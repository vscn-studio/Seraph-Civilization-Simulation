using System.Text;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnCivilizationModSystem
{
    private const string EconomyStateKey = "vscncsEconomy";
    private EconomyState economy = new();
    private CoinageEconomyCompatibility? coinageEconomy;
    private long economyListener;
    private readonly Dictionary<string, long> economyReaders = [];
    private readonly Dictionary<string, (long Since, int Count)> economyRequests = [];
    private readonly Dictionary<string, TradeSession> tradeSessions = [];

    private sealed class TradeSession
    {
        public string Id = Guid.NewGuid().ToString("N"), Buyer = "", Seller = "", Coin = "", Offer = "", Task = "", Mode = "", Terms = "";
        public string Item = "", Text = "", Content = "", PricedCoin = "", SellerTribe = "", BuyerTribe = "";
        public int Quantity, Amount;
        public long Revision, OfferRevision, PolicyRevision, Expires;
        public bool BuyerConfirmed, SellerConfirmed;
        public List<EconomyExchangeItem> Exchange = [];
        public Dictionary<string, string> StockPreviews = [];
        public long BrowsePolicyRevision;
    }

    private void StartEconomy()
    {
        economyListener = sapi!.Event.RegisterGameTickListener(dt =>
        {
            try
            {
                FlushEconomyUpdates();
            }
            catch (Exception ex) { sapi.Logger.Error("Seraph Civilization Simulation economy refresh failed: {0}", ex); }
        }, 250);
    }
    private void LoadEconomy()
    {
        var data = sapi!.WorldManager.SaveGame.GetData(EconomyStateKey);
        if (data is not { Length: > 0 }) return;
        economy = JsonConvert.DeserializeObject<EconomyState>(Encoding.UTF8.GetString(data)) ?? throw new InvalidDataException("Null economy state.");
        if (economy.Coins == null || economy.Policies == null || economy.Offers == null || economy.Tasks == null || economy.History == null
            || economy.DesignOwners == null || economy.DesignIssuers == null || economy.Mints == null || economy.Coins.Values.Any(c => c.Minters == null)
            || economy.DesignIssuers.Values.Any(i => i == null || i.OwnerUid == null || i.TribeId == null
                || i.TribeName == null || i.RequestedTribeId == null || i.Revision < 0)
            || economy.Policies.Values.Any(p => p.Rates == null || p.Accountants == null || p.Rates.Values.Any(r => r is < .000001m or > 1000000m))
            || economy.Offers.Values.Any(offers => offers == null || offers.Any(o => !ValidExchange(o.Exchange) || !ValidRequirement(o.Requirement)))
            || economy.Tasks.Any(t => !ValidExchange(t.Exchange)))
            throw new InvalidDataException("Invalid economy state; refusing to overwrite the ledger.");
    }
    private static bool ValidExchange(List<EconomyExchangeItem>? entries) => entries != null && entries.Count <= EconomyState.MaxExchangeItems
        && entries.All(e => e != null && e.Key != null && e.Coin != null && e.Quantity is >= 1 and <= 1000000
            && (e.Key.Length > 0 || e.Coin.Length > 0) && e.Display?.Stack is not { Length: > 65536 } && ValidRequirement(e.Requirement));
    private static bool ValidRequirement(EconomyItemRequirement? requirement)
    {
        if (requirement == null) return true;
        if (requirement.Code is not { Length: > 0 and <= 256 } || requirement.Attributes is not { Length: > 0 and <= 65536 }
            || requirement.Class is not (EnumItemClass.Item or EnumItemClass.Block) || requirement.Mappings is not { Count: <= 4096 }
            || requirement.Mappings.Any(m => m == null || m.Code is not { Length: > 0 and <= 256 } || m.Id < 0
                || m.Class is not (EnumItemClass.Item or EnumItemClass.Block))) return false;
        var identities = requirement.Mappings.Select(m => (m.Class, m.Id)).ToHashSet();
        if (identities.Count != requirement.Mappings.Count) return false;
        try
        {
            var attributes = new TreeAttribute(); attributes.FromBytes(requirement.Attributes);
            // Older releases omitted unresolved embedded parts from Mappings. Keep
            // their ledger readable; they remain untradeable until verified recovery.
            return true;
        }
        catch (Exception e) when (e is IOException or ArgumentException or IndexOutOfRangeException) { return false; }
    }
    private void SaveEconomy()
    {
        sapi!.WorldManager.SaveGame.StoreData(EconomyStateKey, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(economy)));
        InvalidateEconomyViews();
    }
    private void StopEconomy()
    {
        if (economyListener != 0) sapi?.Event.UnregisterGameTickListener(economyListener);
        economyListener = 0;
        foreach (string uid in economyInventoryHooks.Keys.ToList()) UnwatchEconomyInventory(uid);
        tradeSessions.Clear(); economyReaders.Clear(); economyRequests.Clear(); economyDirtyReaders.Clear(); economyDirtyTrades.Clear();
        economyCatalogReaders.Clear(); economyCatalogPackets.Clear(); economySent.Clear();
        coinDesignAuthors.Clear();
    }
    private void DisconnectEconomy(string uid)
    {
        economyReaders.Remove(uid); economyRequests.Remove(uid); UnwatchEconomyInventory(uid); InvalidateEconomyViews();
        foreach (var session in tradeSessions.Values.Where(s => s.Buyer == uid || s.Seller == uid).ToList()) EndTrade(session, "economy-trade-expired");
    }

    internal void RegisterCoin(ItemStack stack, string? minter = null)
    {
        string key = EconomyInventory.CoinKey(stack);
        if (key.Length == 0 || sapi == null) return;
        if (!economy.Coins.TryGetValue(key, out var coin))
        {
            long design = stack.Attributes.GetLong("coinType");
            var system = sapi.ModLoader.GetModSystem("Coinage.CoinageModSystem");
            object? details = system?.GetType().GetMethod("GetCoinDesign")?.Invoke(system, [design]);
            string name = details?.GetType().GetProperty("Name")?.GetValue(details)?.ToString() ?? "";
            string author = details?.GetType().GetProperty("CreatedBy")?.GetValue(details)?.ToString() ?? "";
            string owner = economy.DesignOwners.GetValueOrDefault(design) ?? "";
            if (owner.Length == 0 && author.Length > 0)
            {
                var matching = sapi.PlayerData.PlayerDataByUid.Values.Where(p => p.LastKnownPlayername.Equals(author, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
                if (matching.Count == 1) owner = matching[0].PlayerUID;
            }
            economy.Coins[key] = coin = new EconomyCoin
            {
                Key = key, Code = stack.Collectible.Code.ToString(), Design = design, Shape = stack.Attributes.GetLong("coinShape"),
                Name = name.Length > 100 ? name[..100] : name, LegacyOwnerUid = owner
            };
            if (!economy.DesignIssuers.ContainsKey(design))
                economy.DesignIssuers[design] = new() { OwnerUid = owner, Pending = true };
        }
        if (!string.IsNullOrEmpty(minter)) SaveEconomy();
    }
    internal void RegisterCoinDesign(long design, string uid)
    {
        if (sapi == null) return;
        coinDesignAuthors.Remove(design);
        economy.DesignOwners.TryAdd(design, uid);
        if (!economy.DesignIssuers.TryGetValue(design, out var issuer))
        {
            var tribe = tribeState.FindMember(uid);
            economy.DesignIssuers[design] = new() { OwnerUid = uid, TribeId = tribe?.Id ?? "", TribeName = tribe?.Name ?? "" };
        }
        else if (issuer.Pending && issuer.OwnerUid.Length == 0) issuer.OwnerUid = uid;
        foreach (var coin in economy.Coins.Values.Where(c => c.Design == design && c.LegacyOwnerUid.Length == 0)) coin.LegacyOwnerUid = uid;
        SaveEconomy();
    }
    internal string AutomaticCoinOwner(ItemStack stack) => economy.DesignOwners.GetValueOrDefault(stack.Attributes.GetLong("coinType")) ?? "";
    private void DiscoverCoins()
    {
        int before = economy.Coins.Count;
        foreach (var player in Online)
            foreach (var slot in EconomyInventory.Slots(player).Where(s => !s.Empty && EconomyInventory.CoinKey(s.Itemstack).Length > 0)) RegisterCoin(slot.Itemstack!);
        if (economy.Coins.Count != before) SaveEconomy();
    }
    private string CoinName(string key, string? language = null)
    {
        string Translate(string text) => language == null ? Lang.Get(text) : Lang.GetL(language, text);
        if (!economy.Coins.TryGetValue(key, out var coin)) return Translate("vsscsmod:ui.economy-unknown-coin");
        string name = string.IsNullOrWhiteSpace(coin.Name) || coin.Name == coin.Code.Split(':').Last()
            ? Translate("vsscsmod:ui.economy-unnamed-coin") : coin.Name;
        string metal = coin.Code.Split(':').Last().Split('-').ElementAtOrDefault(1) ?? "";
        return name + "[" + Translate("game:material-" + metal) + "]";
    }
    private EconomyPolicy Policy(TribeRecord tribe)
    {
        if (!economy.Policies.TryGetValue(tribe.Id, out var policy)) economy.Policies[tribe.Id] = policy = new();
        return policy;
    }
    private bool Accepted(TribeRecord? tribe, string coin) => economy.Active(coin, tribeState);
    private long TradePolicyRevision(TribeRecord? tribe) => 0;
    private bool Quote(TribeRecord? tribe, string pricedCoin, string paidCoin, int price, out int amount)
    { amount=price; return pricedCoin==paidCoin && price is >=1 and <=1000000 && economy.Active(pricedCoin,tribeState); }
    private void OnEconomyRequest(IServerPlayer player, EconomyRequest request)
    {
        if (disposed || request.Action is null or { Length: > 32 } || request.Id is null or { Length: > 180 }
            || request.Item is null or { Length: > 512 } || request.Coin is null or { Length: > 180 } || request.Text is null or { Length: > 500 }
            || request.Content is null or { Length: > 2000 } || request.Exchange == null || request.Exchange.Count > EconomyState.MaxExchangeItems
            || request.Exchange.Any(e => e == null || e.Key is null or { Length: > 512 } || e.Coin is null or { Length: > 180 }
                || e.Display?.Stack is { Length: > 65536 }) || request.Exchange.Sum(e => e.Display?.Stack?.Length ?? 0) > 262144) return;
        long now = Environment.TickCount64;
        var frequency = economyRequests.GetValueOrDefault(player.PlayerUID);
        if (now - frequency.Since >= 1000) frequency = (now, 0);
        if (frequency.Count >= 40) return;
        economyRequests[player.PlayerUID] = (frequency.Since, frequency.Count + 1);
        string? previous = request.Action is "offer-save" or "offer-delete"
            ? JsonConvert.SerializeObject(economy) : null;
        try
        {
            switch (request.Action)
            {
                case "open": case "refresh":
                    WatchEconomyInventory(player); economyReaders[player.PlayerUID] = long.MaxValue;
                    economySent.Remove(player.PlayerUID); SendEconomy(player); break;
                case "close": economyReaders.Remove(player.PlayerUID); economyDirtyReaders.Remove(player.PlayerUID); economySent.Remove(player.PlayerUID); break;
                case "policy": case "reference": case "reference-cancel":
                case "issuer-request": case "issuer-approve": case "issuer-cancel": case "accountant":
                    SendEconomy(player, "invalid-action"); break;
                case "offer-save": SaveOffer(player, request); break;
                case "offer-delete": DeleteOffer(player, request); break;
                case "open-trade": BeginTrade(player, request.Id); break;
                case "propose": case "buy": ProposeTrade(player, request); break;
                case "confirm": ConfirmTrade(player, request); break;
                case "cancel":
                    if (tradeSessions.TryGetValue(request.Id, out var session) && (session.Buyer == player.PlayerUID || session.Seller == player.PlayerUID)) EndTrade(session, "economy-trade-cancelled");
                    break;
            }
        }
        catch (Exception ex)
        {
            if (previous != null) economy = JsonConvert.DeserializeObject<EconomyState>(previous)!;
            sapi!.Logger.Error("Seraph Civilization Simulation economy request {0} failed: {1}", request.Action, ex);
            try { SendEconomy(player, "economy-error"); }
            catch (Exception refreshError) { sapi.Logger.Error("Seraph Civilization Simulation economy error response failed: {0}", refreshError); }
        }
    }
    private void SaveOffer(IServerPlayer player, EconomyRequest request)
    {
        var tribe = tribeState.FindMember(player.PlayerUID);
        var exchange = NormalizeExchange(request.Exchange, tribe);
        if (request.Quantity is < 1 or > 1000000
            || request.Exchange.Count > 0 && exchange == null
            || request.Exchange.Count == 0 && (!Accepted(tribe, request.Coin) || request.Price is < 1 or > 1000000))
        { SendEconomy(player, "economy-invalid"); return; }
        if (!economy.Offers.TryGetValue(player.PlayerUID, out var offers)) economy.Offers[player.PlayerUID] = offers = [];
        var offer = offers.FirstOrDefault(o => o.Id == request.Id);
        if (offer != null && (offer.Revision != request.Revision || economy.Tasks.Any(t => !t.Closed && t.OfferId == offer.Id))) { SendEconomy(player, "economy-offer-conflict"); return; }
        if (offer == null && (request.Id.Length != 0 || offers.Count >= 100)) { SendEconomy(player, "economy-invalid"); return; }
        string name, item, content = "";
        EconomyItemRequirement? requirement = null;
        if (request.IsTask)
        {
            name = new string(request.Text.Where(c => !char.IsControl(c)).ToArray()).Trim(); item = "";
            content = new string(request.Content.Where(c => !char.IsControl(c) || c is '\n' or '\r' or '\t').ToArray()).Trim();
            // Existing tasks from older saves may be toggled without inventing new terms.
            bool legacy = offer is { IsTask: true, Content.Length: 0 } && offer.Name == name;
            if (name.Length is 0 or > 100 || content.Length == 0 && !legacy) { SendEconomy(player, "economy-task-invalid"); return; }
        }
        else
        {
            var current = EconomyInventory.Read(player).FirstOrDefault(i => i.Key == request.Item);
            if (current == null)
            {
                if (offer is not { IsTask: false, Requirement: not null } || offer.ItemKey != request.Item)
                { SendEconomy(player, "economy-item-missing"); return; }
                name = offer.Name; item = offer.ItemKey; requirement = offer.Requirement;
            }
            else
            {
                name = current.Name; item = current.Key;
                var actual = EconomyInventory.Slots(player).First(s => !s.Empty && s.CanTake() && EconomyInventory.ItemKey(s.Itemstack!) == item).Itemstack!;
                requirement = EconomyItemMatching.Requirement(actual);
            }
        }
        if (offer == null) { offer = new(); offers.Add(offer); }
        offer.Name = name; offer.Content = content; offer.ItemKey = item; offer.IsTask = request.IsTask; offer.Quantity = request.IsTask ? 1 : request.Quantity;
        offer.Requirement = requirement;
        offer.Coin = request.Coin; offer.Price = request.Price; offer.Enabled = request.Enabled; offer.Revision++;
        offer.Exchange = exchange ?? [];
        if (exchange is { Count: > 0 })
        { offer.Coin = exchange.Count == 1 ? exchange[0].Coin : ""; offer.Price = exchange[0].Quantity; }
        SaveEconomy(); SendEconomy(player);
    }
    private List<EconomyExchangeItem>? NormalizeExchange(List<EconomyExchangeItem> entries, TribeRecord? tribe)
    {
        if (entries.Count == 0) return null;
        var result = new List<EconomyExchangeItem>();
        foreach (var entry in entries)
        {
            if (entry.Quantity is < 1 or > 1000000 || entry.Display?.Stack is not { Length: > 0 and <= 65536 } bytes) return null;
            ItemStack stack;
            try
            {
                using var reader = new BinaryReader(new MemoryStream(bytes, false)); stack = new ItemStack(reader);
                if (!stack.ResolveBlockOrItem(sapi!.World) || !EconomyItemMatching.ResolveParts(stack, sapi.World)) return null;
            }
            catch (Exception e) when (e is IOException or ArgumentException or IndexOutOfRangeException) { return null; }
            string coin = EconomyInventory.CoinKey(stack);
            if (coin.Length > 0 && !Accepted(tribe, coin)) return null;
            result.Add(new() { Key = coin.Length == 0 ? EconomyInventory.ItemKey(stack) : "", Coin = coin,
                Quantity = entry.Quantity, Display = EconomyInventory.Display(stack), Requirement = coin.Length == 0 ? EconomyItemMatching.Requirement(stack) : null });
        }
        return result;
    }
    private static List<EconomyExchangeItem> Payment(EconomyOffer offer) => offer.Exchange.Count > 0
        ? offer.Exchange : [new() { Coin = offer.Coin, Quantity = offer.Price }];
    private static List<EconomyExchangeItem> Payment(EconomyTask task) => task.Exchange.Count > 0
        ? task.Exchange : [new() { Coin = task.Coin, Quantity = task.Reward }];
    private bool PaymentAccepted(TribeRecord? tribe, IEnumerable<EconomyExchangeItem> payment) =>
        payment.All(e => e.Coin.Length == 0 || Accepted(tribe, e.Coin));
    private void DeleteOffer(IServerPlayer player, EconomyRequest request)
    {
        var offer = economy.Offers.GetValueOrDefault(player.PlayerUID)?.FirstOrDefault(o => o.Id == request.Id);
        if (offer == null || offer.Revision != request.Revision || economy.Tasks.Any(t => !t.Closed && t.OfferId == offer.Id)) { SendEconomy(player, "economy-offer-conflict"); return; }
        economy.Offers[player.PlayerUID].Remove(offer); SaveEconomy(); SendEconomy(player);
    }
    private List<EconomyOfferView> ReadOffers(IServerPlayer player, bool permittedOnly,
        List<EconomyItemView>? inventory = null, Dictionary<string, int>? balances = null)
    {
        MigrateEconomyOffers(player);
        var offers = economy.Offers.GetValueOrDefault(player.PlayerUID);
        if (offers == null || offers.Count == 0) return [];
        var slots = EconomyInventory.Slots(player);
        if (inventory == null || balances == null)
        {
            inventory ??= EconomyInventory.Read(slots); balances ??= EconomyInventory.CoinBalances(slots);
        }
        var items = inventory.ToDictionary(i => i.Key);
        // Group once and cache per kind, including native mod-specific Satisfies rules.
        var stock = slots.Where(s => !s.Empty && s.CanTake()).GroupBy(s => EconomyInventory.ItemKey(s.Itemstack!))
            .ToDictionary(g => g.Key, g => g.ToList());
        var matched = new Dictionary<string, EconomyItemView?>();
        var tribe = tribeState.FindMember(player.PlayerUID);
        return offers.Select(o =>
        {
            var display = o.IsTask ? null : items.GetValueOrDefault(o.ItemKey);
            if (display != null && o.Requirement != null)
            {
                if (!matched.TryGetValue(o.ItemKey, out display))
                {
                    var candidates = stock.GetValueOrDefault(o.ItemKey)?.Where(s => EconomyItemMatching.MatchesKind(
                        s.Itemstack!, o.ItemKey, o.Requirement, sapi!.World)).Select(s => s.Itemstack!).ToList() ?? [];
                    matched[o.ItemKey] = display = candidates.Count > 0 ? EconomyInventory.DisplayGroup(candidates) : null;
                }
            }
            var payment = Payment(o);
            bool funded = !o.IsTask || EconomyInventory.Has(slots, payment);
            int count = o.IsTask ? (funded ? o.Price : 0) : display?.Count ?? 0;
            if (!o.IsTask && display == null && RequirementStack(o.Requirement) is { } preview)
                display = EconomyInventory.Display(preview, 0);
            bool assigned = o.IsTask && economy.Tasks.Any(t => !t.Closed && t.OfferId == o.Id);
            return new EconomyOfferView { Id = o.Id, ItemKey = o.ItemKey, Name = o.Name, Content = o.Content,
                DisplayItem = display, Quantity = o.Quantity, Coin = o.Coin, Price = o.Price, Exchange = o.Exchange,
                NeedsItemSelection = !o.IsTask && (!EconomyItemMatching.Current(o.ItemKey) || display == null),
                Enabled = o.Enabled, IsTask = o.IsTask, Revision = o.Revision, Available = count,
                Ready = o.Enabled && PaymentAccepted(tribe, payment) && count >= (o.IsTask ? o.Price : o.Quantity) && !assigned, Contract = assigned, CoinName = CoinName(o.Coin, player.LanguageCode) };
        }).Where(o => !permittedOnly || o.Ready).ToList();
    }
    private ItemStack? RequirementStack(EconomyItemRequirement? requirement, bool previous = false)
    {
        if (requirement == null) return null;
        var collectible = requirement.Class == EnumItemClass.Item ? (CollectibleObject?)sapi!.World.GetItem(new AssetLocation(requirement.Code))
            : sapi!.World.GetBlock(new AssetLocation(requirement.Code));
        return collectible == null ? null : requirement.Template(collectible, sapi.World, previous);
    }
    private ItemStack? PreviousKindStack(string key, EconomyItemRequirement? requirement)
    {
        if (!EconomyItemMatching.Modern(key) || EconomyItemMatching.Current(key)) return null;
        var stack = RequirementStack(requirement, previous: true);
        return stack != null && requirement!.TemplateKey == key ? stack : null;
    }
    private void MigrateEconomyOffers(IServerPlayer player)
    {
        var offers = economy.Offers.GetValueOrDefault(player.PlayerUID);
        if (offers == null || !offers.Any(o => !o.IsTask && !EconomyItemMatching.Current(o.ItemKey)
            || IncompleteMappings(o.Requirement)
            || o.Exchange.Any(e => e.Coin.Length == 0 && (!EconomyItemMatching.Current(e.Key) || IncompleteMappings(e.Requirement))))) return;
        string? before = null;
        var slots = EconomyInventory.Slots(player);
        foreach (var offer in offers)
        {
            if (offer.IsTask && economy.Tasks.Any(t => !t.Closed && t.OfferId == offer.Id)) continue;
            bool changed = false;
            if (!offer.IsTask && (!EconomyItemMatching.Current(offer.ItemKey) || IncompleteMappings(offer.Requirement)))
            {
                var stack = RecoverUnresolvedRequirement(offer.ItemKey, offer.Requirement, slots)
                    ?? PreviousKindStack(offer.ItemKey, offer.Requirement)
                    ?? slots.FirstOrDefault(s => !s.Empty && !EconomyItemMatching.Modern(offer.ItemKey)
                        && EconomyItemMatching.LegacyKey(s.Itemstack!) == offer.ItemKey)?.Itemstack;
                if (stack != null)
                {
                    before ??= JsonConvert.SerializeObject(economy);
                    offer.Requirement = EconomyItemMatching.Requirement(stack); offer.ItemKey = EconomyInventory.ItemKey(stack); changed = true;
                }
            }
            var migrated = new List<EconomyExchangeItem>();
            foreach (var entry in offer.Exchange)
            {
                var replacement = entry;
                var previous = entry.Coin.Length == 0 ? RecoverUnresolvedRequirement(entry.Key, entry.Requirement, slots)
                    ?? PreviousKindStack(entry.Key, entry.Requirement) : null;
                if (previous != null)
                {
                    before ??= JsonConvert.SerializeObject(economy);
                    replacement = new() { Key = EconomyInventory.ItemKey(previous), Quantity = entry.Quantity,
                        Display = EconomyInventory.Display(previous), Requirement = EconomyItemMatching.Requirement(previous) }; changed = true;
                }
                else if (entry.Coin.Length == 0 && !EconomyItemMatching.Current(entry.Key) && entry.Display?.Stack is { Length: > 0 } bytes)
                {
                    try
                    {
                        using var reader = new BinaryReader(new MemoryStream(bytes, false)); var stack = new ItemStack(reader);
                        if (stack.ResolveBlockOrItem(sapi!.World) && (EconomyItemMatching.Modern(entry.Key)
                            ? EconomyItemMatching.Key(stack.Class, stack.Collectible.Code.ToString(), EconomyItemMatching.Attributes(stack, true), true) == entry.Key
                            : EconomyItemMatching.LegacyKey(stack) == entry.Key))
                        {
                            if (!EconomyItemMatching.ResolveParts(stack, sapi.World)) { migrated.Add(entry); continue; }
                            before ??= JsonConvert.SerializeObject(economy);
                            replacement = new() { Key = EconomyInventory.ItemKey(stack), Quantity = entry.Quantity,
                                Display = EconomyInventory.Display(stack), Requirement = EconomyItemMatching.Requirement(stack) }; changed = true;
                        }
                    }
                    catch (Exception e) when (e is IOException or ArgumentException or IndexOutOfRangeException) { }
                }
                migrated.Add(replacement);
            }
            if (changed) { offer.Exchange = migrated; offer.Revision++; }
        }
        if (before == null) return;
        try { SaveEconomy(); }
        catch { economy = JsonConvert.DeserializeObject<EconomyState>(before)!; throw; }
    }
    private static bool IncompleteMappings(EconomyItemRequirement? requirement)
    {
        if (requirement == null) return false;
        var attributes = new TreeAttribute(); attributes.FromBytes(requirement.Attributes);
        bool missing = false;
        EconomyItemMatching.Visit(attributes, item =>
        {
            if (item.value != null && !requirement.Mappings.Any(m => m.Class == item.value.Class && m.Id == item.value.Id)) missing = true;
        });
        return missing;
    }
    private ItemStack? RecoverUnresolvedRequirement(string key, EconomyItemRequirement? requirement, List<ItemSlot> slots)
    {
        if (!EconomyItemMatching.Modern(key) || !IncompleteMappings(requirement)) return null;
        // Verify the original unresolved fingerprint AND every part/variant against
        // a carried item. Never infer a tool or material from its translated name.
        bool previous = !EconomyItemMatching.Current(key);
        var attributes = new TreeAttribute(); attributes.FromBytes(requirement!.Attributes);
        bool resolved = true;
        EconomyItemMatching.Visit(attributes, item =>
        {
            if (item.value == null) return;
            var mapping = requirement.Mappings.FirstOrDefault(m => m.Class == item.value.Class && m.Id == item.value.Id);
            if (mapping == null) return;
            var part = mapping.Class == EnumItemClass.Item ? (CollectibleObject?)sapi!.World.GetItem(new AssetLocation(mapping.Code))
                : sapi!.World.GetBlock(new AssetLocation(mapping.Code));
            if (part == null) { resolved = false; return; }
            item.value = new ItemStack(part, item.value.StackSize) { Attributes = item.value.Attributes };
        });
        if (!resolved) return null;
        if (EconomyItemMatching.Key(requirement.Class, requirement.Code, attributes, previous) != key) return null;
        foreach (var slot in slots.Where(s => !s.Empty && s.CanTake()))
        {
            var actual = slot.Itemstack!;
            if (actual.Class != requirement.Class || actual.Collectible.Code.ToString() != requirement.Code) continue;
            var unresolved = actual.Clone();
            EconomyItemMatching.Visit(unresolved.Attributes, item =>
            {
                if (item.value == null || requirement.Mappings.Any(m => m.Class == item.value.Class
                    && m.Code == item.value.Collectible?.Code.ToString())) return;
                using var reader = new BinaryReader(new MemoryStream(item.value.ToBytes(), false));
                item.value = new ItemStack(reader);
            });
            if (EconomyItemMatching.Key(actual.Class, requirement.Code, EconomyItemMatching.Attributes(unresolved, previous), previous) != key) continue;
            if (EconomyItemMatching.ResolveParts(actual, sapi!.World)) return actual;
        }
        return null;
    }
    private void SendEconomy(IServerPlayer player, string message = "")
    {
        var tribe = tribeState.FindMember(player.PlayerUID); var policy = tribe == null ? new() : Policy(tribe);
        bool ledger = false;
        var slots = EconomyInventory.Slots(player);
        var inventory = EconomyInventory.Read(slots); var balances = EconomyInventory.CoinBalances(slots);
        var packet = new EconomySnapshot { TribeId = tribe?.Id ?? "", IsLeader = tribe?.LeaderUid == player.PlayerUID,
            CanReadLedger = ledger, CoinageAvailable = sapi!.ModLoader.IsModEnabled("coinage"), ReferenceCoin = "",
            PolicyRevision = 0, Message = message.Length == 0 ? "" : Ui(player, message), Items = inventory,
            Offers = ReadOffers(player, false, inventory, balances),
            // PlayerDataByUid includes historical accounts from other worlds in this data directory.
            WorldPlayerCount = Online.Where(p => p.ConnectionState == EnumClientState.Playing && !string.IsNullOrWhiteSpace(p.PlayerUID))
                .Select(p => p.PlayerUID).Distinct(StringComparer.Ordinal).Count() };
        foreach (var coin in economy.Coins.Values.OrderBy(c => c.Name))
        {
            economy.DesignIssuers.TryGetValue(coin.Design, out var issuer);
            packet.Coins.Add(new EconomyCoinView { Key = coin.Key, Name = CoinName(coin.Key, player.LanguageCode), Code = coin.Code, Design = coin.Design, Shape = coin.Shape,
                Issuers = CoinDesigner(coin, player), Own = false,
                Accepted = Accepted(tribe, coin.Key), Rate = "",
                IssuerRevision = issuer?.Revision ?? 0,
                Minted = 0,
                Held = balances.GetValueOrDefault(coin.Key), Legacy = coin.Minters.Count == 0,
                Minters = "" });
        }
        string fingerprint = EconomyGuiFingerprint.Of(packet);
        if (message.Length == 0 && economySent.GetValueOrDefault(player.PlayerUID) == fingerprint) return;
        economySent[player.PlayerUID] = fingerprint; economyDirtyReaders.Remove(player.PlayerUID);
        channel!.SendPacket(packet, player);
    }
    private bool Pair(TradeSession session, out IServerPlayer buyer, out IServerPlayer seller)
    {
        buyer = ByUid(session.Buyer)!; seller = ByUid(session.Seller)!;
        return buyer != null && seller != null && buyer.Entity.Alive && seller.Entity.Alive && DistanceTo(buyer, seller) <= 5;
    }
    private string TradePaymentCoin(string heldCoin, string pricedCoin, TribeRecord? tribe)
        => pricedCoin;

    private void BeginTrade(IServerPlayer buyer, string target)
    {
        if (target == buyer.PlayerUID || ByUid(target) is not { } seller || DistanceTo(buyer, seller) > 5 || !buyer.Entity.Alive || !seller.Entity.Alive) return;
        DiscoverCoins();
        string key = EconomyInventory.CoinKey(buyer.InventoryManager.ActiveHotbarSlot?.Itemstack);
        bool contract = economy.Tasks.Any(t => !t.Closed && (t.Employer == target && t.Worker == buyer.PlayerUID || t.Worker == target && t.Employer == buyer.PlayerUID));
        var available = ReadOffers(seller, true);
        // Browsing and barter do not require a coin in the active hand.
        if (!contract && available.Count == 0) { SendEconomy(buyer, "economy-no-trade"); return; }
        if (tradeSessions.Values.Any(s => s.Buyer == buyer.PlayerUID || s.Mode.StartsWith("task-", StringComparison.Ordinal)
            && (s.Seller == buyer.PlayerUID || s.Buyer == target || s.Seller == target)))
        { SendEconomy(buyer, "economy-trade-busy"); return; }
        var sellerTribe = tribeState.FindMember(target);
        var session = new TradeSession { Buyer = buyer.PlayerUID, Seller = target, Coin = key, Expires = Environment.TickCount64 + 120000,
            SellerTribe = sellerTribe?.Id ?? "", BuyerTribe = tribeState.FindMember(buyer.PlayerUID)?.Id ?? "",
            BrowsePolicyRevision = 0 };
        WatchEconomyInventory(buyer); WatchEconomyInventory(seller);
        tradeSessions[session.Id] = session; SendTrade(session, buyer);
    }
    private void ProposeTrade(IServerPlayer player, EconomyRequest request)
    {
        if (!tradeSessions.TryGetValue(request.Id, out var session) || player.PlayerUID != session.Buyer || session.Revision != request.Revision
            || session.Mode.Length > 0 || !Pair(session, out var buyer, out var seller)) return;
        // A browse session may predate another customer's task proposal. Recheck before opening either participant's dialog.
        if (tradeSessions.Values.Any(s => s != session && s.Mode.StartsWith("task-", StringComparison.Ordinal)
            && (s.Buyer == session.Buyer || s.Seller == session.Buyer || s.Buyer == session.Seller || s.Seller == session.Seller)))
        { EndTrade(session, "economy-trade-busy"); return; }
        var tribe = tribeState.FindMember(seller.PlayerUID);
        if (session.Expires < Environment.TickCount64 || session.BuyerTribe != (tribeState.FindMember(buyer.PlayerUID)?.Id ?? "")
            || session.SellerTribe != (tribe?.Id ?? "") || !request.IsTask && (session.BrowsePolicyRevision != TradePolicyRevision(tribe)))
        { EndTrade(session, "economy-offer-conflict"); return; }
        if (request.Action == "buy" && request.Selections.Count > 0)
        {
            if (request.IsTask) { EndTrade(session, "economy-offer-conflict"); return; }
            PurchaseGoods(session, buyer, seller, tribe, request.Selections); return;
        }
        session.BuyerConfirmed = session.SellerConfirmed = false;
        session.Mode = session.Offer = session.Task = session.Terms = "";
        if (request.IsTask)
        {
            var task = economy.Tasks.FirstOrDefault(t => t.Id == request.Item && !t.Closed &&
                (t.Employer == session.Buyer && t.Worker == session.Seller || t.Employer == session.Seller && t.Worker == session.Buyer));
            if (task == null) { EndTrade(session, "economy-offer-conflict"); return; }
            session.Task = task.Id; session.Mode = request.Text == "cancel-task" ? "task-cancel" : "task-pay"; session.Amount = task.Reward; session.PricedCoin = task.Coin;
            session.Exchange = Payment(task);
            session.Text = task.Text; session.Content = task.Content; session.Quantity = 0;
            session.Terms = session.Mode == "task-cancel" ? Ui(player, "economy-task-cancel-terms", task.Text)
                : Ui(player, "economy-task-pay-terms", PlayerName(task.Employer), PlayerName(task.Worker), task.Text, task.Reward, CoinName(task.Coin, player.LanguageCode));
            if (task.Content.Length > 0) session.Terms += "\n\n" + task.Content;
        }
        else
        {
            var offer = economy.Offers.GetValueOrDefault(session.Seller)?.FirstOrDefault(o => o.Id == request.Item);
            if (offer is { IsTask: false, Enabled: true } && request.Action == "buy"
                && session.StockPreviews.TryGetValue(offer.Id, out string? preview)
                && preview != EconomyInventory.PreviewStamp(EconomyInventory.Slots(seller), offer.ItemKey, offer.Quantity, offer.Requirement))
            { EndTrade(session, "economy-stock-changed"); return; }
            if (offer == null || !ReadOffers(seller, true).Any(o => o.Id == offer.Id)
                || request.Action == "buy" && offer.IsTask || !offer.IsTask && (request.Action != "buy" || request.OfferRevision != offer.Revision))
            { EndTrade(session, "economy-offer-conflict"); return; }
            session.Offer = offer.Id; session.OfferRevision = offer.Revision; session.PolicyRevision = TradePolicyRevision(tribe);
            session.Item = offer.ItemKey; session.Text = offer.Name; session.Content = offer.Content; session.Quantity = offer.Quantity; session.PricedCoin = offer.Coin;
            if (offer.IsTask)
            {
                session.Mode = "task-accept"; session.Amount = offer.Price;
                session.Exchange = Payment(offer);
                session.Terms = Ui(player, "economy-task-terms", seller.PlayerName, buyer.PlayerName, offer.Name, offer.Price, CoinName(offer.Coin, player.LanguageCode));
                if (offer.Content.Length > 0) session.Terms += "\n\n" + offer.Content;
            }
            else
            {
                if (offer.Exchange.Count > 0) { session.Exchange = offer.Exchange; session.Amount = offer.Price; }
                else
                {
                    session.Coin = TradePaymentCoin(session.Coin, offer.Coin, tribe);
                    if (!Accepted(tribe, session.Coin) || !Quote(tribe, offer.Coin, session.Coin, offer.Price, out session.Amount)) { EndTrade(session, "economy-unrecognized"); return; }
                    session.Exchange = [new() { Coin = session.Coin, Quantity = session.Amount }];
                }
                session.Mode = "sale";
                session.Terms = Ui(player, "economy-sale-terms", seller.PlayerName, offer.Quantity, offer.Name, buyer.PlayerName, session.Amount, CoinName(session.Coin, player.LanguageCode));
            }
        }
        session.SellerTribe = tribeState.FindMember(session.Seller)?.Id ?? ""; session.BuyerTribe = tribeState.FindMember(session.Buyer)?.Id ?? "";
        session.Revision++; session.Expires = Environment.TickCount64 + 120000;
        if (session.Mode == "sale")
        {
            if (!ValidateTerms(session, buyer, seller)) { EndTrade(session, "economy-offer-conflict"); return; }
            CompleteTrade(session, buyer, seller); return;
        }
        session.Terms = session.Mode == "task-cancel" ? session.Terms
            : Ui(player, "economy-task-barter-terms", session.Text, PaymentText(session.Exchange, player.LanguageCode)) + "\n\n" + session.Content;
        SendTrade(session, buyer); SendTrade(session, seller);
    }
    private void ConfirmTrade(IServerPlayer player, EconomyRequest request)
    {
        if (!tradeSessions.TryGetValue(request.Id, out var session) || session.Mode.Length == 0 || request.Revision != session.Revision
            || player.PlayerUID != session.Buyer && player.PlayerUID != session.Seller) return;
        if (!Pair(session, out var buyer, out var seller) || session.Expires < Environment.TickCount64) { EndTrade(session, "economy-trade-expired"); return; }
        if (!ValidateTerms(session, buyer, seller)) { EndTrade(session, "economy-offer-conflict"); return; }
        if (player.PlayerUID == session.Buyer) session.BuyerConfirmed = true; else session.SellerConfirmed = true;
        if (!session.BuyerConfirmed || !session.SellerConfirmed) { SendTrade(session, buyer); SendTrade(session, seller); return; }
        CompleteTrade(session, buyer, seller);
    }
    private string PaymentText(IEnumerable<EconomyExchangeItem> payment, string language) => string.Join(" + ",
        payment.Select(e => e.Quantity + " × " + (e.Coin.Length > 0 ? CoinName(e.Coin, language) : e.Display?.Name ?? "")));
    private void CompleteTrade(TradeSession session, IServerPlayer buyer, IServerPlayer seller)
    {
        if (session.Mode == "task-cancel")
        {
            var task = economy.Tasks.First(t => t.Id == session.Task && !t.Closed); task.Closed = true;
            var history=TradeRecord(session,seller,buyer,"",task.Text,0,task.Exchange,"task-cancel",task.Content);
            economy.History.Add(history);
            try { SaveEconomy(); } catch { economy.History.Remove(history); task.Closed = false; throw; }
            EndTrade(session, "economy-task-cancelled"); return;
        }
        if (session.Mode == "task-accept")
        {
            var missing = MissingPayment(seller, session.Exchange, buyer.LanguageCode);
            if (missing.Length > 0) { EndTrade(session, "economy-payment-missing", missing); return; }
            var task = new EconomyTask { OfferId = session.Offer, Employer = session.Seller, Worker = session.Buyer, Text = session.Text, Content = session.Content, Coin = session.PricedCoin, Reward = session.Amount, Exchange = session.Exchange };
            var offer = economy.Offers[session.Seller].First(o => o.Id == session.Offer);
            bool enabled = offer.Enabled; long revision = offer.Revision;
            offer.Enabled = false; offer.Revision++;
            economy.Tasks.Add(task);
            var history=TradeRecord(session,seller,buyer,"",task.Text,0,task.Exchange,"task-accept",task.Content);
            history.TaskId=task.Id;
            economy.History.Add(history);
            try { SaveEconomy(); } catch { economy.History.Remove(history); economy.Tasks.Remove(task); offer.Enabled = enabled; offer.Revision = revision; EndTrade(session, "economy-error"); throw; }
            EndTrade(session, "economy-task-agreed"); return;
        }
        EconomyTask? contract = session.Mode == "task-pay" ? economy.Tasks.FirstOrDefault(t => t.Id == session.Task && !t.Closed) : null;
        var sender = contract == null ? seller : ByUid(contract.Worker)!;
        var payer = contract == null ? buyer : ByUid(contract.Employer)!;
        var source = EconomyInventory.Slots(sender); var destination = EconomyInventory.Slots(payer);
        var requirement = contract == null ? economy.Offers.GetValueOrDefault(session.Seller)?.FirstOrDefault(o => o.Id == session.Offer)?.Requirement : null;
        if (contract == null && EconomyInventory.Count(source, session.Item, requirement: requirement) < session.Quantity)
        { EndTrade(session, "economy-goods-missing", session.Text, session.Quantity - EconomyInventory.Count(source, session.Item, requirement: requirement)); return; }
        string shortages = MissingPayment(payer, session.Exchange, buyer.LanguageCode);
        if (shortages.Length > 0) { EndTrade(session, "economy-payment-missing", shortages); return; }
        var plan = EconomyInventory.Plan(source, destination, session.Item, contract == null ? session.Quantity : 0, session.Exchange, requirement);
        if (plan == null) { EndTrade(session, "economy-inventory-changed"); return; }
        var completed=TradeRecord(session,sender,payer,session.Item,session.Text,contract==null?session.Quantity:0,session.Exchange,
            contract==null?"trade":"task-pay",session.Content,requirement);
        try
        {
            plan.Apply(); if (contract != null) contract.Closed = true;
            economy.History.Add(completed);
            SaveEconomy();
        }
        catch
        {
            economy.History.Remove(completed);
            plan.Rollback(); if (contract != null) contract.Closed = false;
            // A failing persistence operation invalidates both confirmations.
            EndTrade(session, "economy-error"); throw;
        }
        // Close before inventory callbacks so repeated confirmation cannot transfer twice.
        EndTrade(session, "economy-trade-done");
        plan.Notify(ex => sapi!.Logger.Error("Seraph Civilization Simulation economy inventory notification failed: {0}", ex));
        SendEconomy(buyer); SendEconomy(seller);
    }
    private bool ValidateTerms(TradeSession s, IServerPlayer buyer, IServerPlayer seller)
    {
        if (s.BuyerTribe != (tribeState.FindMember(buyer.PlayerUID)?.Id ?? "") || s.SellerTribe != (tribeState.FindMember(seller.PlayerUID)?.Id ?? "")) return false;
        if (s.Mode is "task-pay" or "task-cancel")
        {
            var task = economy.Tasks.FirstOrDefault(t => t.Id == s.Task && !t.Closed);
            return task != null && (task.Employer == s.Buyer && task.Worker == s.Seller || task.Employer == s.Seller && task.Worker == s.Buyer)
                && s.Amount == task.Reward && s.PricedCoin == task.Coin
                && Payment(task).Select(e => (e.Key, e.Coin, e.Quantity)).SequenceEqual(s.Exchange.Select(e => (e.Key, e.Coin, e.Quantity)));
        }
        var offer = economy.Offers.GetValueOrDefault(s.Seller)?.FirstOrDefault(o => o.Id == s.Offer);
        var tribe = tribeState.FindMember(s.Seller);
        if (offer == null || offer.Revision != s.OfferRevision || TradePolicyRevision(tribe) != s.PolicyRevision
            || !ReadOffers(seller, true).Any(o => o.Id == s.Offer)) return false;
        if (s.Mode == "task-accept") return PaymentAccepted(tribeState.FindMember(s.Buyer), s.Exchange);
        if (offer.Exchange.Count > 0) return PaymentAccepted(tribe, offer.Exchange);
        return Accepted(tribe, s.Coin) && Quote(tribe, offer.Coin, s.Coin, offer.Price, out int amount) && amount == s.Amount;
    }
    private string MissingPayment(IServerPlayer player, IEnumerable<EconomyExchangeItem> exchange, string language)
    {
        var slots = EconomyInventory.Slots(player);
        return string.Join("、", exchange.GroupBy(e => (e.Key, e.Coin)).Select(g =>
        {
            var first = g.First(); long missing = g.Sum(e => (long)e.Quantity)
                - EconomyInventory.Count(slots, first.Coin.Length > 0 ? first.Coin : first.Key, first.Coin.Length > 0, first.Requirement);
            return missing > 0 ? (first.Coin.Length > 0 ? CoinName(first.Coin, language) : first.Display?.Name ?? "") + " × " + missing : "";
        }).Where(s => s.Length > 0));
    }
    private void SendTrade(TradeSession session, IServerPlayer player)
    {
        bool buyer = session.Buyer == player.PlayerUID;
        var packet = new EconomyTradeView { Id = session.Id, PartnerUid = buyer ? session.Seller : session.Buyer,
            Partner = PlayerName(buyer ? session.Seller : session.Buyer), Coin = session.Coin, CoinName = CoinName(session.Coin, player.LanguageCode), Terms = session.Terms,
            Revision = session.Revision, YouConfirmed = buyer ? session.BuyerConfirmed : session.SellerConfirmed,
            OtherConfirmed = buyer ? session.SellerConfirmed : session.BuyerConfirmed, Proposed = session.Mode.Length > 0,
            TaskProposal = session.Mode.StartsWith("task-", StringComparison.Ordinal), Exchange = session.Exchange };
        if (buyer && ByUid(session.Seller) is { } seller)
        {
            packet.Offers = ReadOffers(seller, true);
            var slots = EconomyInventory.Slots(seller);
            session.StockPreviews = packet.Offers.Where(o => !o.IsTask).ToDictionary(o => o.Id,
                o => EconomyInventory.PreviewStamp(slots, o.ItemKey, o.Quantity, economy.Offers[session.Seller].First(saved => saved.Id == o.Id).Requirement));
            foreach (var offer in packet.Offers.Where(o => !o.IsTask && o.Exchange.Count == 0))
            {
                var tribe = tribeState.FindMember(seller.PlayerUID);
                string paymentCoin = TradePaymentCoin(session.Coin, offer.Coin, tribe);
                if (Accepted(tribe, paymentCoin) && Quote(tribe, offer.Coin, paymentCoin, offer.Price, out int price)) { offer.Price = price; offer.Coin = paymentCoin; offer.CoinName = CoinName(paymentCoin, player.LanguageCode); }
                else offer.Ready = false;
            }
            foreach (var task in economy.Tasks.Where(t => !t.Closed && (t.Employer == session.Buyer && t.Worker == session.Seller || t.Employer == session.Seller && t.Worker == session.Buyer)))
                packet.Offers.Add(new EconomyOfferView { Id = task.Id, Name = task.Text, Content = task.Content, Price = task.Reward, Coin = task.Coin, CoinName = CoinName(task.Coin, player.LanguageCode), IsTask = true, Contract = true, Ready = true, Enabled = true, Quantity = 1, Exchange = task.Exchange });
        }
        foreach (string key in packet.Offers.Select(o => o.Coin).Concat(packet.Offers.SelectMany(o => o.Exchange.Select(e => e.Coin)))
            .Concat(session.Exchange.Select(e => e.Coin)).Append(session.Coin).Distinct())
            if (economy.Coins.TryGetValue(key, out var coin))
                packet.Coins.Add(new EconomyCoinView { Key = key, Name = CoinName(key, player.LanguageCode), Code = coin.Code, Design = coin.Design, Shape = coin.Shape,
                    Issuers = CoinDesigner(coin, player) });
        if (buyer)
        {
            var stock = EconomyInventory.Slots(player);
            foreach (var entry in packet.Offers.Where(o => !o.IsTask).SelectMany(o => o.Exchange.Count > 0
                ? o.Exchange : [new EconomyExchangeItem { Coin = o.Coin, Quantity = o.Price }]))
            {
                string key = entry.Coin.Length > 0 ? entry.Coin : entry.Key;
                if (!packet.PaymentStock.ContainsKey(key)) packet.PaymentStock[key] = EconomyInventory.Count(stock, key, entry.Coin.Length > 0, entry.Requirement);
            }
        }
        channel!.SendPacket(packet, player);
    }
    private void EndTrade(TradeSession session, string message, params object[] arguments)
    {
        if (!tradeSessions.Remove(session.Id)) return;
        economyDirtyTrades.Remove(session.Id);
        foreach (var player in (session.Mode.StartsWith("task-", StringComparison.Ordinal)
            ? new[] { ByUid(session.Buyer), ByUid(session.Seller) } : new[] { ByUid(session.Buyer) }).OfType<IServerPlayer>())
            channel!.SendPacket(new EconomyTradeView { Id = session.Id, Closed = true, Message = Ui(player, message, arguments) }, player);
    }
}
