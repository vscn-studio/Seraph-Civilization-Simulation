using Vintagestory.API.Common;
using Vintagestory.Common;

namespace SeraphCivilizationSimulation;

public static class EconomyInventory
{
    public static string CoinKey(ItemStack? stack) => stack?.Collectible?.Code is { Domain: "coinage" } code
        && code.Path.StartsWith("coin-", StringComparison.Ordinal) && stack.Attributes.HasAttribute("coinType")
        && stack.Attributes.HasAttribute("coinShape") ? $"{code}|{stack.Attributes.GetLong("coinType")}|{stack.Attributes.GetLong("coinShape")}" : "";

    public static string ItemKey(ItemStack stack, bool exact = false) => exact
        ? EconomyItemMatching.ExactKey(stack) : EconomyItemMatching.Key(stack);
    public static List<ItemSlot> Slots(IPlayer player)
    {
        var result = new List<ItemSlot>();
        foreach (string name in new[] { "hotbar", "backpack" })
        {
            var inventory = player.InventoryManager.GetOwnInventory(name);
            if (inventory == null) continue;
            int start = inventory is InventoryPlayerBackpacks bags ? bags.bagSlots.Length : 0;
            for (int i = start; i < inventory.Count; i++) if (inventory[i] is { } slot) result.Add(slot);
        }
        var offhand = player.InventoryManager.OffhandHotbarSlot;
        if (offhand != null && !result.Contains(offhand)) result.Add(offhand);
        foreach (var slot in result.Where(s => !s.Empty)) EconomyItemMatching.ResolveParts(slot.Itemstack!, slot.Inventory.Api.World);
        return result.Distinct().ToList();
    }
    public static int Count(IEnumerable<ItemSlot> slots, string key, bool coin = false, EconomyItemRequirement? requirement = null) =>
        (int)Math.Min(int.MaxValue, slots.Where(s => !s.Empty && s.CanTake()
            && (coin ? CoinKey(s.Itemstack) == key : EconomyItemMatching.Matches(s.Itemstack, key, requirement, s.Inventory?.Api.World))).Sum(s => (long)s.StackSize));

    public static List<EconomyItemView> Read(IPlayer player) => Read(Slots(player));
    public static List<EconomyItemView> Read(IEnumerable<ItemSlot> slots) => slots.Where(s => !s.Empty && s.CanTake())
        .GroupBy(s => ItemKey(s.Itemstack!)).Select(g => DisplayGroup(g.Select(s => s.Itemstack!))).ToList();
    public static EconomyItemView DisplayGroup(IEnumerable<ItemStack> stacks)
    {
        var pieces = stacks.ToList();
        var result = Display(pieces[0], (int)Math.Min(int.MaxValue, pieces.Sum(s => (long)s.StackSize)));
        if (result.DurabilityMax > 0)
        {
            result.DurabilityLow = pieces.Min(s => s.Collectible.GetRemainingDurability(s));
            result.DurabilityHigh = pieces.Max(s => s.Collectible.GetRemainingDurability(s));
        }
        return result;
    }
    public static Dictionary<string, int> CoinBalances(IEnumerable<ItemSlot> slots) => slots.Where(s => !s.Empty)
        .GroupBy(s => CoinKey(s.Itemstack)).Where(g => g.Key.Length > 0)
        .ToDictionary(g => g.Key, g => (int)Math.Min(int.MaxValue, g.Sum(s => (long)s.StackSize)));

    // The preview and Take visit the same slots. Reject a changed tool/name before direct purchase.
    // Temperature and natural ageing do not invalidate an otherwise unchanged quote.
    public static string PreviewStamp(IEnumerable<ItemSlot> slots, string key, int count, EconomyItemRequirement? requirement = null)
    {
        var stamps = new List<(string State, int Count)>();
        foreach (var slot in slots)
        {
            if (count == 0) break;
            if (slot.Empty || !slot.CanTake() || !EconomyItemMatching.Matches(slot.Itemstack, key, requirement, slot.Inventory?.Api.World)) continue;
            int n = Math.Min(count, slot.StackSize); count -= n;
            stamps.Add((EconomyItemMatching.StateKey(slot.Itemstack), n));
        }
        return PreviewStamp(stamps, count);
    }
    // Slot boundaries can change when earlier basket lines take identical stacks.
    private static string PreviewStamp(IEnumerable<(string State, int Count)> pieces, int missing) => string.Join("|",
        pieces.GroupBy(p => p.State).Select(g => g.Key + ":" + g.Sum(p => (long)p.Count))) + ":missing:" + missing;

    // A single display stack retains native block/item identity, custom shapes and tool attributes.
    // This copy is never registered as an inventory or used for delivery.
    public static EconomyItemView Display(ItemStack stack, int count = 1)
    {
        var copy = stack.Clone(); copy.StackSize = 1;
        int maximum = stack.Collectible.GetMaxDurability(stack);
        return new EconomyItemView { Key = ItemKey(stack), Name = stack.GetName(), Code = stack.Collectible.Code.ToString(),
            Count = count, Stack = copy.ToBytes(), State = EconomyItemMatching.StateKey(stack), DurabilityMax = maximum,
            DurabilityLow = maximum > 0 ? stack.Collectible.GetRemainingDurability(stack) : 0,
            DurabilityHigh = maximum > 0 ? stack.Collectible.GetRemainingDurability(stack) : 0 };
    }

    // Plan on clones first. A full receiving inventory or a changed source never mutates live slots.
    public static EconomyExchangePlan? Plan(List<ItemSlot> seller, List<ItemSlot> buyer,
        string itemKey, int quantity, string currency, int price)
    {
        return Plan(seller, buyer, itemKey, quantity, [new() { Coin = currency, Quantity = price }]);
    }
    public static bool Has(IEnumerable<ItemSlot> slots, IReadOnlyList<EconomyExchangeItem> exchange)
    {
        var stock = slots.ToList();
        return exchange.Count is > 0 and <= EconomyState.MaxExchangeItems && exchange.All(e => e.Quantity is >= 1 and <= 1000000)
            && exchange.GroupBy(e => (e.Key, e.Coin)).All(g =>
                Count(stock, g.Key.Coin.Length > 0 ? g.Key.Coin : g.Key.Key, g.Key.Coin.Length > 0, g.First().Requirement) >= g.Sum(e => (long)e.Quantity));
    }
    public static EconomyExchangePlan? Plan(List<ItemSlot> seller, List<ItemSlot> buyer,
        string itemKey, int quantity, IReadOnlyList<EconomyExchangeItem> exchange, EconomyItemRequirement? requirement = null)
        => PlanMany(seller, buyer, [new(itemKey, quantity, exchange, requirement)]);

    public static EconomyExchangePlan? PlanMany(List<ItemSlot> seller, List<ItemSlot> buyer, IReadOnlyList<EconomyInventoryTrade> trades)
    {
        if (trades.Count is < 1 or > 100 || seller.Intersect(buyer).Any() || trades.Any(t => t.Quantity is < 0 or > 1000000
            || t.Exchange.Count is < 1 or > EconomyState.MaxExchangeItems || t.Exchange.Any(e => e.Quantity is < 1 or > 1000000
                || (e.Coin.Length == 0 && e.Key.Length == 0)))) return null;
        var before = seller.Concat(buyer).Distinct().ToDictionary(s => s, s => s.Itemstack?.Clone());
        var after = before.ToDictionary(p => p.Key, p => p.Value?.Clone());
        var goods = new List<ItemStack>(); var payment = new List<ItemStack>();
        var deliveries = new List<EconomyExchangeDelivery>();
        // Remove every source before adding any delivery. Purchased items cannot fund another line.
        foreach (var trade in trades)
        {
            var deliveredGoods = Take(seller, trade.ItemKey, trade.Quantity, false, trade.Requirement);
            if (deliveredGoods == null) return null;
            if (trade.ExpectedPreview != null && trade.ExpectedPreview != PreviewStamp(deliveredGoods.Select(s =>
                (EconomyItemMatching.StateKey(s), s.StackSize)), 0)) return null;
            goods.AddRange(deliveredGoods);
            var deliveredPayment = new List<EconomyExchangeItem>();
            foreach (var entry in trade.Exchange)
            {
                var pieces = Take(buyer, entry.Coin.Length > 0 ? entry.Coin : entry.Key, entry.Quantity, entry.Coin.Length > 0, entry.Requirement);
                if (pieces == null) return null;
                deliveredPayment.Add(new() { Key = entry.Key, Coin = entry.Coin, Quantity = entry.Quantity,
                    Requirement = entry.Requirement, Display = DisplayGroup(pieces) });
                payment.AddRange(pieces);
            }
            deliveries.Add(new(deliveredGoods.Count > 0 ? DisplayGroup(deliveredGoods) : null, deliveredPayment));
        }
        if (!Put(buyer, goods) || !Put(seller, payment)) return null;
        return new EconomyExchangePlan(before, after) { DeliveredItem = deliveries[0].Item,
            DeliveredPayment = deliveries[0].Payment, Deliveries = deliveries };

        List<ItemStack>? Take(List<ItemSlot> slots, string key, int count, bool coin, EconomyItemRequirement? required)
        {
            var pieces = new List<ItemStack>();
            foreach (var slot in slots)
            {
                var stack = after[slot];
                if (count == 0) break;
                if (stack == null || !slot.CanTake() || !(coin ? CoinKey(stack) == key : EconomyItemMatching.Matches(stack, key, required, slot.Inventory?.Api.World))) continue;
                int n = Math.Min(count, stack.StackSize);
                var piece = stack.Clone(); piece.StackSize = n; pieces.Add(piece);
                stack.StackSize -= n; if (stack.StackSize == 0) after[slot] = null;
                count -= n;
            }
            return count == 0 ? pieces : null;
        }
        bool Put(List<ItemSlot> slots, List<ItemStack> pieces)
        {
            foreach (var piece in pieces)
            {
                var dummy = new DummySlot(piece.Clone());
                foreach (var slot in slots.OrderBy(s => after[s] == null))
                {
                    var existing = after[slot];
                    if (!slot.CanHold(dummy)) continue;
                    if (existing == null)
                    {
                        int n = Math.Min(dummy.StackSize, Math.Min(slot.MaxSlotStackSize, piece.Collectible.MaxStackSize));
                        if (n <= 0) continue;
                        after[slot] = dummy.Itemstack!.Clone(); after[slot]!.StackSize = n;
                        dummy.Itemstack.StackSize -= n;
                    }
                    else
                    {
                        // Merge only on detached slots: native temperature/freshness and mod behavior
                        // are preserved without mutating a real inventory during planning.
                        var sink = new DummySlot(existing.Clone(), slot.Inventory) { MaxSlotStackSize = slot.MaxSlotStackSize, CanStoreTags = slot.CanStoreTags };
                        var operation = new ItemStackMergeOperation(slot.Inventory.Api.World, EnumMouseButton.Left, 0,
                            EnumMergePriority.AutoMerge, dummy.StackSize) { SinkSlot = sink, SourceSlot = dummy };
                        existing.Collectible.TryMergeStacks(operation);
                        if (operation.MovedQuantity > 0) after[slot] = sink.Itemstack;
                    }
                    if (dummy.Empty || dummy.StackSize == 0) break;
                }
                if (!dummy.Empty && dummy.StackSize != 0) return false;
            }
            return true;
        }
    }
}

public sealed record EconomyInventoryTrade(string ItemKey, int Quantity, IReadOnlyList<EconomyExchangeItem> Exchange,
    EconomyItemRequirement? Requirement = null, string? ExpectedPreview = null);
public sealed record EconomyExchangeDelivery(EconomyItemView? Item, List<EconomyExchangeItem> Payment);

public sealed class EconomyExchangePlan(Dictionary<ItemSlot, ItemStack?> before, Dictionary<ItemSlot, ItemStack?> after)
{
    public List<EconomyExchangeDelivery> Deliveries { get; init; } = [];
    public EconomyItemView? DeliveredItem { get; init; }
    public List<EconomyExchangeItem> DeliveredPayment { get; init; } = [];
    private bool applied;
    public void Apply()
    {
        if (applied) throw new InvalidOperationException("Exchange already applied.");
        foreach (var pair in before)
            if (!Same(pair.Key.Itemstack, pair.Value)) throw new InvalidOperationException("Inventory changed before exchange.");
        foreach (var pair in after) pair.Key.Itemstack = pair.Value?.Clone();
        applied = true;
    }
    public void Rollback()
    {
        if (!applied) return;
        foreach (var pair in before) pair.Key.Itemstack = pair.Value?.Clone();
        applied = false;
    }
    public void Notify(Action<Exception> error)
    {
        if (!applied) return;
        foreach (var slot in after.Keys.Where(s => !Same(before[s], after[s])))
        {
            try { slot.Inventory.OnItemSlotModified(slot); slot.MarkDirty(); }
            catch (Exception ex) { error(ex); }
        }
    }
    private static bool Same(ItemStack? a, ItemStack? b) => a == null ? b == null : b != null
        && a.StackSize == b.StackSize && EconomyInventory.ItemKey(a, true) == EconomyInventory.ItemKey(b, true);
}
