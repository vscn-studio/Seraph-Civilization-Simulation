using Newtonsoft.Json;
using Vintagestory.API.Server;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnCivilizationModSystem
{
    private EconomyHistory TradeRecord(TradeSession session, IServerPlayer seller, IServerPlayer buyer,
        string item, string text, int quantity, IEnumerable<EconomyExchangeItem> exchange, string eventName = "trade", string content = "",
        EconomyItemRequirement? requirement = null)
    {
        var selling = tribeState.FindMember(seller.PlayerUID);
        var buying = tribeState.FindMember(buyer.PlayerUID);
        return new()
        {
            Id = Guid.NewGuid().ToString("N"), SessionId = session.Id, Event = eventName,
            OfferId = session.Offer, TaskId = session.Task,
            GoodsRequirement = requirement == null ? null : JsonConvert.DeserializeObject<EconomyItemRequirement>(JsonConvert.SerializeObject(requirement)),
            Utc = DateTimeOffset.UtcNow.ToString("O"), SellerUid = seller.PlayerUID, SellerName = PlayerName(seller.PlayerUID),
            BuyerUid = buyer.PlayerUID, BuyerName = PlayerName(buyer.PlayerUID),
            SellerTribeId = selling?.Id ?? "", SellerTribe = selling?.Name ?? "",
            BuyerTribeId = buying?.Id ?? "", BuyerTribe = buying?.Name ?? "",
            ItemKey = item, Text = text, Quantity = quantity, Content = content, IsTask = eventName.StartsWith("task-", StringComparison.Ordinal),
            Exchange = JsonConvert.DeserializeObject<List<EconomyExchangeItem>>(JsonConvert.SerializeObject(exchange))!
        };
    }
}
