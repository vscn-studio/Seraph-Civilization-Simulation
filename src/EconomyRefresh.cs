using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnCivilizationModSystem
{
    private readonly HashSet<string> economyDirtyReaders = [];
    private readonly HashSet<string> economyDirtyTrades = [];
    private readonly Dictionary<string, string> economySent = [];
    private readonly Dictionary<string, List<(InventoryBase Inventory, Action<int> Changed)>> economyInventoryHooks = [];
    private readonly Dictionary<string, int> economyCatalogReaders = [];
    private List<EconomyCatalogPacket> economyCatalogPackets = [];
    private long economyFlushAt;

    private void ScheduleEconomyFlush()
    {
        // Bound the debounce: a continuous stream of slot events cannot postpone all updates.
        if (economyFlushAt == 0) economyFlushAt = Environment.TickCount64 + 150;
    }

    private void InitializeEconomyCatalog() => economyCatalogPackets = EconomyCatalog.Build(sapi!);
    private void InvalidateEconomyViews()
    {
        foreach (string uid in economyReaders.Keys) economyDirtyReaders.Add(uid);
        foreach (var trade in tradeSessions.Values.Where(s => s.Mode.Length == 0)) economyDirtyTrades.Add(trade.Id);
        ScheduleEconomyFlush();
    }
    private void WatchEconomyInventory(IServerPlayer player)
    {
        if (economyInventoryHooks.ContainsKey(player.PlayerUID)) return;
        var hooks = new List<(InventoryBase Inventory, Action<int> Changed)>();
        foreach (string name in new[] { "hotbar", "backpack", "character" })
        {
            if (player.InventoryManager.GetOwnInventory(name) is not InventoryBase inventory || hooks.Any(h => h.Inventory == inventory)) continue;
            var stamps = inventory.Select(Stamp).ToList();
            Action<int> changed = index =>
            {
                try
                {
                    bool resized = stamps.Count != inventory.Count;
                    if (resized)
                    {
                        stamps = inventory.Select(Stamp).ToList();
                        InvalidateEconomyViews();
                    }
                    if (index < 0 || index >= inventory.Count) return;
                    if (readyPlayers.Contains(player.PlayerUID)) ActivateParchment(player, inventory[index]);
                    string stamp = Stamp(inventory[index]);
                    string old = index < stamps.Count ? stamps[index] : "";
                    stamps[index] = stamp;
                    if (!resized && old == stamp) return; // Food ageing alone cannot refresh the economy.
                    int before = economy.Coins.Count;
                    if (EconomyInventory.CoinKey(inventory[index].Itemstack).Length > 0) RegisterCoin(inventory[index].Itemstack!);
                    if (economy.Coins.Count != before) SaveEconomy();
                    string tribe = tribeState.FindMember(player.PlayerUID)?.Id ?? "";
                    foreach (string uid in economyReaders.Keys)
                        if (uid == player.PlayerUID || tribe.Length > 0 && tribeState.FindMember(uid)?.Id == tribe) economyDirtyReaders.Add(uid);
                    foreach (var trade in tradeSessions.Values.Where(s => s.Mode.Length == 0 && (s.Buyer == player.PlayerUID || s.Seller == player.PlayerUID)))
                        economyDirtyTrades.Add(trade.Id);
                    ScheduleEconomyFlush();
                }
                catch (Exception ex)
                {
                    // SlotModified runs inside the server's inventory packet handler.
                    // A malformed third-party attribute must never escape that callback.
                    sapi!.Logger.Error("Seraph Civilization Simulation economy inventory watch skipped a malformed slot for {0}: {1}", player.PlayerName, ex.Message);
                }
            };
            inventory.SlotModified += changed; hooks.Add((inventory, changed));
        }
        economyInventoryHooks[player.PlayerUID] = hooks;
        int discovered = economy.Coins.Count;
        foreach (var slot in EconomyInventory.Slots(player))
            if (EconomyInventory.CoinKey(slot.Itemstack).Length > 0) RegisterCoin(slot.Itemstack!);
        if (economy.Coins.Count != discovered) SaveEconomy();
        static string Stamp(ItemSlot slot)
        {
            if (slot.Empty || slot.Itemstack == null) return "";
            try { return EconomyItemMatching.StateKey(slot.Itemstack) + ":" + slot.StackSize; }
            catch { return "unreadable:" + slot.Itemstack.Class + ":" + slot.Itemstack.Id + ":" + slot.StackSize; }
        }
    }
    private void UnwatchEconomyInventory(string uid)
    {
        if (economyInventoryHooks.Remove(uid, out var hooks)) foreach (var hook in hooks) hook.Inventory.SlotModified -= hook.Changed;
        economyDirtyReaders.Remove(uid); economySent.Remove(uid); economyCatalogReaders.Remove(uid);
    }
    private void FlushEconomyUpdates()
    {
        long now = Environment.TickCount64;
        if (economyDirtyReaders.Count == 0 && economyDirtyTrades.Count == 0) economyFlushAt = 0;
        foreach (var session in tradeSessions.Values.ToList())
            if (session.Expires < now || !Pair(session, out _, out _)) EndTrade(session, "economy-trade-expired");
        if (now >= economyFlushAt && economyDirtyTrades.Count > 0)
        {
            string id = economyDirtyTrades.First(); economyDirtyTrades.Remove(id);
            if (tradeSessions.TryGetValue(id, out var session) && session.Mode.Length == 0 && ByUid(session.Buyer) is { } buyer)
            {
                // The new inventory preview and its purchase revision must travel together.
                session.Revision++; SendTrade(session, buyer);
            }
        }
        // At most one complete snapshot per tick, with no work at all for idle windows.
        if (now >= economyFlushAt && economyDirtyReaders.Count > 0)
        {
            string uid = economyDirtyReaders.First(); economyDirtyReaders.Remove(uid);
            if (economyReaders.ContainsKey(uid) && ByUid(uid) is { } player) SendEconomy(player);
            economyFlushAt = 0;
        }
        // Reuse the server's immutable startup pages; distribute login traffic across ticks.
        for (int sent = 0; sent < 4 && economyCatalogReaders.Count > 0; sent++)
        {
            string uid = economyCatalogReaders.Keys.First(); int index = economyCatalogReaders[uid];
            if (ByUid(uid) is not { } player || index >= economyCatalogPackets.Count) economyCatalogReaders.Remove(uid);
            else
            {
                channel!.SendPacket(economyCatalogPackets[index], player);
                economyCatalogReaders.Remove(uid);
                if (index + 1 < economyCatalogPackets.Count) economyCatalogReaders[uid] = index + 1;
            }
        }
    }
}
