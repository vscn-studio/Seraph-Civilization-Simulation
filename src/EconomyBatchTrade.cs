using Vintagestory.API.Server;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnCivilizationModSystem
{
    private void PurchaseGoods(TradeSession session, IServerPlayer buyer, IServerPlayer seller, TribeRecord? tribe,
        IReadOnlyList<EconomyOfferSelection> selections)
    {
        if (selections.Count is < 1 or > 100 || selections.Select(s => s.Id).Distinct().Count() != selections.Count)
        { EndTrade(session, "economy-offer-conflict"); return; }
        var ready = ReadOffers(seller, true).Where(o => !o.IsTask).Select(o => o.Id).ToHashSet();
        var source = EconomyInventory.Slots(seller); var destination = EconomyInventory.Slots(buyer);
        var lines = new List<EconomyInventoryTrade>();
        var records = new List<EconomyHistory>();
        foreach (var selection in selections)
        {
            var offer = economy.Offers.GetValueOrDefault(session.Seller)?.FirstOrDefault(o => o.Id == selection.Id);
            if (offer == null || offer.IsTask || !ready.Contains(offer.Id) || offer.Revision != selection.Revision
                || !session.StockPreviews.TryGetValue(offer.Id, out string? preview))
            { EndTrade(session, "economy-offer-conflict"); return; }
            if (preview != EconomyInventory.PreviewStamp(source, offer.ItemKey, offer.Quantity, offer.Requirement))
            { EndTrade(session, "economy-stock-changed"); return; }
            List<EconomyExchangeItem> payment;
            if (offer.Exchange.Count > 0)
            {
                if (!PaymentAccepted(tribe, offer.Exchange)) { EndTrade(session, "economy-unrecognized"); return; }
                payment = offer.Exchange;
            }
            else
            {
                string paymentCoin = TradePaymentCoin(session.Coin, offer.Coin, tribe);
                if (!Accepted(tribe, paymentCoin) || !Quote(tribe, offer.Coin, paymentCoin, offer.Price, out int amount))
                { EndTrade(session, "economy-unrecognized"); return; }
                payment = [new() { Coin = paymentCoin, Quantity = amount }];
            }
            lines.Add(new(offer.ItemKey, offer.Quantity, payment, offer.Requirement, preview));
            var record=TradeRecord(session,seller,buyer,offer.ItemKey,offer.Name,offer.Quantity,payment,requirement:offer.Requirement);
            record.OfferId=offer.Id; records.Add(record);

        }
        string shortages = MissingPayment(buyer, lines.SelectMany(l => l.Exchange), buyer.LanguageCode);
        if (shortages.Length > 0) { EndTrade(session, "economy-payment-missing", shortages); return; }
        var plan = EconomyInventory.PlanMany(source, destination, lines);
        if (plan == null) { EndTrade(session, "economy-inventory-changed"); return; }
        try
        {
            plan.Apply(); economy.History.AddRange(records); SaveEconomy();
        }
        catch
        {
            foreach (var record in records) economy.History.Remove(record);
            plan.Rollback();
            EndTrade(session, "economy-error"); throw;
        }
        EndTrade(session, "economy-trade-done");
        plan.Notify(ex => sapi!.Logger.Error("Seraph Civilization Simulation economy inventory notification failed: {0}", ex));
        SendEconomy(buyer); SendEconomy(seller);
    }
}
