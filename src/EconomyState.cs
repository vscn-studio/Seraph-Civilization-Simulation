using System.Globalization;
using ProtoBuf;

namespace SeraphCivilizationSimulation;

public sealed class EconomyState
{
    public const int MaxExchangeItems = 24;
    public Dictionary<string, EconomyCoin> Coins { get; set; } = [];
    public Dictionary<long, string> DesignOwners { get; set; } = [];
    public Dictionary<long, EconomyIssuer> DesignIssuers { get; set; } = [];
    public Dictionary<string, EconomyPolicy> Policies { get; set; } = [];
    public Dictionary<string, List<EconomyOffer>> Offers { get; set; } = [];
    public List<EconomyTask> Tasks { get; set; } = [];
    // Server-only global history, persisted with each completed transfer.
    public List<EconomyHistory> History { get; set; } = [];
    public List<EconomyMint> Mints { get; set; } = [];

    public IEnumerable<TribeRecord> Issuers(EconomyCoin coin, TribeState tribes) => tribes.Tribes.Where(t =>
        DesignIssuers.TryGetValue(coin.Design, out var issuer) && !issuer.Pending && issuer.TribeId == t.Id);
    public bool Active(string key, TribeState tribes) => Coins.ContainsKey(key);
    public bool Recognizes(TribeRecord? tribe, string key, TribeState tribes) => tribe != null && Active(key, tribes)
        && Policies.TryGetValue(tribe.Id, out var policy) && policy.Rates.ContainsKey(key)
        && Coins.ContainsKey(policy.ReferenceCoin) && policy.Rates.GetValueOrDefault(policy.ReferenceCoin) == 1;

    // Old saves have no trustworthy creation-time tribe snapshot. Never infer it from today's members.
    public bool MigrateIssuers()
    {
        bool changed = false;
        foreach (long design in Coins.Values.Select(c => c.Design).Concat(DesignOwners.Keys).Distinct())
        {
            if (DesignIssuers.ContainsKey(design)) continue;
            string owner = DesignOwners.GetValueOrDefault(design) ?? "";
            if (owner.Length == 0)
            {
                var owners = Coins.Values.Where(c => c.Design == design && c.LegacyOwnerUid.Length > 0)
                    .Select(c => c.LegacyOwnerUid).Distinct().Take(2).ToList();
                if (owners.Count == 1) owner = owners[0];
            }
            DesignIssuers[design] = new() { OwnerUid = owner, Pending = true }; changed = true;
        }
        return changed;
    }
    public bool CanReadLedger(TribeRecord? tribe, string uid) => tribe != null && tribe.Members.Contains(uid)
        && (tribe.LeaderUid == uid || Policies.TryGetValue(tribe.Id, out var p) && p.Accountants.Contains(uid));
    public IEnumerable<EconomyHistory> TribeHistory(string tribeId) => History.Where(h => h.SellerTribeId == tribeId || h.BuyerTribeId == tribeId);

    // Rates are published prices: one coin equals this many units of the tribe's reference coin.
    public static bool TryRate(string? value, out decimal rate) => decimal.TryParse(value, NumberStyles.AllowDecimalPoint,
        CultureInfo.InvariantCulture, out rate) && rate is >= .000001m and <= 1000000m;
    public static bool TryQuote(EconomyPolicy policy, string pricedCoin, string paidCoin, int price, out int amount)
    {
        amount = 0;
        if (price is < 1 or > 1000000 || !policy.Rates.TryGetValue(pricedCoin, out var a)
            || !policy.Rates.TryGetValue(paidCoin, out var b) || a <= 0 || b <= 0) return false;
        decimal result = decimal.Ceiling(price * a / b);
        if (result is < 1 or > 1000000) return false;
        amount = (int)result; return true;
    }
}

public sealed class EconomyIssuer
{
    public string OwnerUid { get; set; } = "";
    public string TribeId { get; set; } = "";
    public string TribeName { get; set; } = "";
    public bool Pending { get; set; }
    public string RequestedTribeId { get; set; } = "";
    public long Revision { get; set; }
}

public sealed class EconomyMint
{
    public string Uid { get; set; } = "";
    public string TribeId { get; set; } = "";
    public string Coin { get; set; } = "";
    public long Count { get; set; }
}
public sealed class EconomyCoin
{
    public string Key { get; set; } = "";
    public string Code { get; set; } = "";
    public long Design { get; set; }
    public long Shape { get; set; }
    public string Name { get; set; } = "";
    public string LegacyOwnerUid { get; set; } = "";
    public Dictionary<string, long> Minters { get; set; } = [];
}
public sealed class EconomyPolicy
{
    public string ReferenceCoin { get; set; } = "";
    public Dictionary<string, decimal> Rates { get; set; } = [];
    public HashSet<string> Accountants { get; set; } = [];
    public long Revision { get; set; }

    public bool TryRebase(string coin, string valueInOldReference, out string error)
    {
        error = "economy-invalid";
        decimal divisor = 1;
        if (ReferenceCoin.Length > 0 && !Rates.TryGetValue(coin, out divisor)
            && !EconomyState.TryRate(valueInOldReference, out divisor))
        { error = "economy-reference-rate-required"; return false; }
        if (divisor is < .000001m or > 1000000m) return false;
        var rebased = Rates.ToDictionary(p => p.Key, p => p.Value / divisor);
        if (rebased.Values.Any(r => r is < .000001m or > 1000000m))
        { error = "economy-reference-range"; return false; }
        rebased[coin] = 1;
        ReferenceCoin = coin; Rates = rebased;
        return true;
    }
}
public sealed class EconomyOffer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ItemKey { get; set; } = "";
    public EconomyItemRequirement? Requirement { get; set; }
    public string Name { get; set; } = "";
    public string Content { get; set; } = "";
    public int Quantity { get; set; } = 1;
    public string Coin { get; set; } = "";
    public int Price { get; set; } = 1;
    public bool Enabled { get; set; }
    public bool IsTask { get; set; }
    public long Revision { get; set; }
    public List<EconomyExchangeItem> Exchange { get; set; } = [];
}
public sealed class EconomyTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OfferId { get; set; } = "";
    public string Employer { get; set; } = "";
    public string Worker { get; set; } = "";
    public string Text { get; set; } = "";
    public string Content { get; set; } = "";
    public string Coin { get; set; } = "";
    public int Reward { get; set; }
    public bool Closed { get; set; }
    public List<EconomyExchangeItem> Exchange { get; set; } = [];
}
[ProtoContract]
public sealed class EconomyExchangeItem
{
    [ProtoMember(1)] public string Key { get; set; } = "";
    [ProtoMember(2)] public string Coin { get; set; } = "";
    [ProtoMember(3)] public int Quantity { get; set; } = 1;
    [ProtoMember(4)] public EconomyItemView? Display { get; set; }
    // Matching templates stay in the save; clients already receive their item preview.
    [ProtoIgnore] public EconomyItemRequirement? Requirement { get; set; }
}
[ProtoContract]
public sealed class EconomyHistory
{
    public string Event { get; set; } = "trade";
    public string ItemKey { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string OfferId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public EconomyItemRequirement? GoodsRequirement { get; set; }
    [ProtoMember(1)] public string Id { get; set; } = "";
    [ProtoMember(2)] public string Utc { get; set; } = "";
    [ProtoMember(3)] public string SellerUid { get; set; } = "";
    [ProtoMember(4)] public string SellerName { get; set; } = "";
    [ProtoMember(5)] public string SellerTribeId { get; set; } = "";
    [ProtoMember(6)] public string SellerTribe { get; set; } = "";
    [ProtoMember(7)] public string BuyerUid { get; set; } = "";
    [ProtoMember(8)] public string BuyerName { get; set; } = "";
    [ProtoMember(9)] public string BuyerTribeId { get; set; } = "";
    [ProtoMember(10)] public string BuyerTribe { get; set; } = "";
    [ProtoMember(11)] public string Text { get; set; } = "";
    [ProtoMember(12)] public int Quantity { get; set; }
    [ProtoMember(13)] public string Coin { get; set; } = "";
    [ProtoMember(14)] public string CoinName { get; set; } = "";
    [ProtoMember(15)] public int Amount { get; set; }
    [ProtoMember(16)] public bool IsTask { get; set; }
    [ProtoMember(17)] public string Content { get; set; } = "";
    [ProtoMember(18)] public EconomyItemView? DisplayItem { get; set; }
    [ProtoMember(19)] public List<EconomyExchangeItem> Exchange { get; set; } = [];
}

[ProtoContract]
public sealed class EconomyRequest
{
    [ProtoMember(1)] public string Action { get; set; } = "open";
    [ProtoMember(2)] public string Id { get; set; } = "";
    [ProtoMember(3)] public string Item { get; set; } = "";
    [ProtoMember(4)] public string Coin { get; set; } = "";
    [ProtoMember(5)] public string Text { get; set; } = "";
    [ProtoMember(6)] public int Quantity { get; set; }
    [ProtoMember(7)] public int Price { get; set; }
    [ProtoMember(8)] public bool Enabled { get; set; }
    [ProtoMember(9)] public bool IsTask { get; set; }
    [ProtoMember(10)] public long Revision { get; set; }
    [ProtoMember(11)] public int Page { get; set; }
    [ProtoMember(12)] public string Content { get; set; } = "";
    [ProtoMember(13)] public List<EconomyExchangeItem> Exchange { get; set; } = [];
    [ProtoMember(14)] public long OfferRevision { get; set; }
    [ProtoMember(15)] public long IssuerRevision { get; set; }
    [ProtoMember(16)] public List<EconomyOfferSelection> Selections { get; set; } = [];
}
[ProtoContract]
public sealed class EconomyOfferSelection
{
    [ProtoMember(1)] public string Id { get; set; } = "";
    [ProtoMember(2)] public long Revision { get; set; }
}
[ProtoContract]
public sealed class EconomySnapshot
{
    [ProtoMember(1)] public List<EconomyCoinView> Coins { get; set; } = [];
    [ProtoMember(2)] public List<EconomyOfferView> Offers { get; set; } = [];
    [ProtoMember(3)] public List<EconomyItemView> Items { get; set; } = [];
    [ProtoIgnore, Newtonsoft.Json.JsonIgnore] public List<EconomyHistory> History { get; set; } = [];
    [ProtoMember(5)] public List<EconomyLedgerRow> Ledger { get; set; } = [];
    [ProtoMember(6)] public List<TribePerson> Accountants { get; set; } = [];
    [ProtoMember(7)] public bool CanReadLedger { get; set; }
    [ProtoMember(8)] public string ReferenceCoin { get; set; } = "";
    [ProtoMember(9)] public long PolicyRevision { get; set; }
    [ProtoMember(10)] public string Message { get; set; } = "";
    [ProtoIgnore, Newtonsoft.Json.JsonIgnore] public int HistoryPage { get; set; }
    [ProtoIgnore, Newtonsoft.Json.JsonIgnore] public int HistoryPages { get; set; }
    [ProtoMember(13)] public bool CoinageAvailable { get; set; }
    [ProtoMember(14)] public bool IsLeader { get; set; }
    [ProtoMember(15)] public string TribeId { get; set; } = "";
    [ProtoMember(16)] public List<EconomyRateView> WorldRates { get; set; } = [];
    [ProtoMember(17)] public List<EconomyTribeView> WorldTribes { get; set; } = [];
    [ProtoMember(18)] public List<EconomyCoinView> ArchivedCoins { get; set; } = [];
    [ProtoIgnore, Newtonsoft.Json.JsonIgnore] public long WorldTradeCount { get; set; }
    // Current world's online players, deduplicated by UID; field retained for wire compatibility.
    [ProtoMember(20)] public int WorldPlayerCount { get; set; }
}
[ProtoContract]
public sealed class EconomyTribeView
{
    [ProtoMember(1)] public string Id { get; set; } = "";
    [ProtoMember(2)] public string Name { get; set; } = "";
    [ProtoMember(3)] public List<string> Coins { get; set; } = [];
    [ProtoIgnore, Newtonsoft.Json.JsonIgnore] public long TradeCount { get; set; }
}
[ProtoContract]
public sealed class EconomyCoinView
{
    [ProtoMember(1)] public string Key { get; set; } = "";
    [ProtoMember(2)] public string Name { get; set; } = "";
    [ProtoMember(3)] public string Code { get; set; } = "";
    // Field/name retained for wire compatibility; now displays the coin mold designer.
    [ProtoMember(4)] public string Issuers { get; set; } = "";
    [ProtoMember(5)] public bool Own { get; set; }
    [ProtoMember(6)] public bool Accepted { get; set; }
    [ProtoMember(7)] public string Rate { get; set; } = "";
    [ProtoMember(8)] public long Minted { get; set; }
    [ProtoIgnore, Newtonsoft.Json.JsonIgnore] public long TradeCount { get; set; }
    [ProtoIgnore, Newtonsoft.Json.JsonIgnore] public long Volume { get; set; }
    [ProtoMember(11)] public long Held { get; set; }
    [ProtoMember(12)] public bool Legacy { get; set; }
    [ProtoMember(13)] public string Minters { get; set; } = "";
    [ProtoMember(14)] public long Design { get; set; }
    [ProtoMember(15)] public long Shape { get; set; }
    [ProtoMember(16)] public long IssuerRevision { get; set; }
    [ProtoMember(17)] public bool CanRequestIssuer { get; set; }
    [ProtoMember(18)] public bool CanApproveIssuer { get; set; }
    [ProtoMember(19)] public bool CanCancelIssuer { get; set; }
    [ProtoMember(20)] public string RequestedTribe { get; set; } = "";
}
[ProtoContract]
public sealed class EconomyItemView
{
    [ProtoMember(1)] public string Key { get; set; } = "";
    [ProtoMember(2)] public string Name { get; set; } = "";
    [ProtoMember(3)] public int Count { get; set; }
    [ProtoMember(4)] public string Code { get; set; } = "";
    [ProtoMember(5)] public byte[] Stack { get; set; } = [];
    [ProtoMember(6)] public int DurabilityLow { get; set; }
    [ProtoMember(7)] public int DurabilityHigh { get; set; }
    [ProtoMember(8)] public int DurabilityMax { get; set; }
    [ProtoMember(9)] public string State { get; set; } = "";
}
[ProtoContract]
public sealed class EconomyOfferView
{
    [ProtoMember(1)] public string Id { get; set; } = "";
    [ProtoMember(2)] public string ItemKey { get; set; } = "";
    [ProtoMember(3)] public string Name { get; set; } = "";
    [ProtoMember(4)] public int Quantity { get; set; }
    [ProtoMember(5)] public string Coin { get; set; } = "";
    [ProtoMember(6)] public int Price { get; set; }
    [ProtoMember(7)] public bool Enabled { get; set; }
    [ProtoMember(8)] public bool IsTask { get; set; }
    [ProtoMember(9)] public int Available { get; set; }
    [ProtoMember(10)] public long Revision { get; set; }
    [ProtoMember(11)] public bool Ready { get; set; }
    [ProtoMember(12)] public bool Contract { get; set; }
    [ProtoMember(13)] public string CoinName { get; set; } = "";
    [ProtoMember(14)] public string Content { get; set; } = "";
    [ProtoMember(15)] public EconomyItemView? DisplayItem { get; set; }
    [ProtoMember(16)] public List<EconomyExchangeItem> Exchange { get; set; } = [];
    [ProtoMember(17)] public bool NeedsItemSelection { get; set; }
}
[ProtoContract]
public sealed class EconomyRateView
{
    [ProtoMember(1)] public string Tribe { get; set; } = "";
    [ProtoMember(2)] public string Coin { get; set; } = "";
    [ProtoMember(3)] public string ReferenceCoin { get; set; } = "";
    [ProtoMember(4)] public string Rate { get; set; } = "";
    [ProtoMember(5)] public string TribeId { get; set; } = "";
    [ProtoMember(6)] public string CoinKey { get; set; } = "";
    [ProtoMember(7)] public string ReferenceKey { get; set; } = "";
}
[ProtoContract]
public sealed class EconomyCatalogPacket
{
    [ProtoMember(1)] public int Index { get; set; }
    [ProtoMember(2)] public int Total { get; set; }
    [ProtoMember(3)] public List<EconomyItemView> Items { get; set; } = [];
}
[ProtoContract]
public sealed class EconomyLedgerRow
{
    [ProtoMember(1)] public string Uid { get; set; } = "";
    [ProtoMember(2)] public string Name { get; set; } = "";
    [ProtoMember(3)] public string Coin { get; set; } = "";
    [ProtoMember(4)] public long Held { get; set; }
    [ProtoMember(5)] public long Received { get; set; }
    [ProtoMember(6)] public long Paid { get; set; }
    [ProtoMember(7)] public long Count { get; set; }
    [ProtoMember(8)] public bool Departed { get; set; }
    [ProtoMember(9)] public bool Online { get; set; }
    [ProtoMember(10)] public long Minted { get; set; }
    [ProtoMember(11)] public string CoinKey { get; set; } = "";
}
[ProtoContract]
public sealed class EconomyTradeView
{
    [ProtoMember(1)] public string Id { get; set; } = "";
    [ProtoMember(2)] public string Partner { get; set; } = "";
    [ProtoMember(3)] public string PartnerUid { get; set; } = "";
    [ProtoMember(4)] public List<EconomyOfferView> Offers { get; set; } = [];
    [ProtoMember(5)] public string Coin { get; set; } = "";
    [ProtoMember(6)] public string CoinName { get; set; } = "";
    [ProtoMember(7)] public string Terms { get; set; } = "";
    [ProtoMember(8)] public bool YouConfirmed { get; set; }
    [ProtoMember(9)] public bool OtherConfirmed { get; set; }
    [ProtoMember(10)] public long Revision { get; set; }
    [ProtoMember(11)] public bool Closed { get; set; }
    [ProtoMember(12)] public string Message { get; set; } = "";
    [ProtoMember(13)] public bool Proposed { get; set; }
    [ProtoMember(14)] public List<EconomyCoinView> Coins { get; set; } = [];
    [ProtoMember(15)] public bool TaskProposal { get; set; }
    [ProtoMember(16)] public List<EconomyExchangeItem> Exchange { get; set; } = [];
    [ProtoMember(17)] public Dictionary<string, int> PaymentStock { get; set; } = [];
}
