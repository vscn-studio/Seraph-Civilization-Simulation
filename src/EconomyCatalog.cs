using Vintagestory.API.Common;
using Vintagestory.API.Client;

namespace SeraphCivilizationSimulation;

// The server resolves creative variants once, after every mod has finalized its assets.
// Cached packets are streamed at login, never requested by an editor or a search field.
public static class EconomyCatalog
{
    public static List<EconomyCatalogPacket> Build(ICoreAPI api)
    {
        var packets = new List<EconomyCatalogPacket>();
        var keys = new HashSet<string>();
        var items = new List<EconomyItemView>(); int bytes = 0;
        foreach (var collectible in api.World.Collectibles ?? [])
        {
            if (collectible?.Code == null || collectible.Code.Domain == "coinage" && collectible.Code.Path.StartsWith("coin-")) continue;
            if (collectible.Attributes?["handbook"]["exclude"].AsBool() == true) continue;
            IEnumerable<ItemStack?> stacks = collectible.CreativeInventoryStacks is { Length: > 0 } variants
                && collectible.Attributes?["handbook"]["ignoreCreativeInvStacks"].AsBool() != true
                ? variants.SelectMany(v => v?.Stacks ?? []).Select(s => Resolve(s, collectible))
                : collectible.CreativeInventoryTabs is { Length: > 0 } || collectible.Attributes?["handbook"]["include"].AsBool() == true
                    ? [new ItemStack(collectible)] : [];
            foreach (var stack in stacks)
            {
                if (stack?.Collectible?.Code == null) continue;
                try
                {
                    if (!EconomyItemMatching.ResolveParts(stack, api.World)) continue;
                    var item = EconomyInventory.Display(stack);
                    if (item.Stack.Length is 0 or > 65536 || !keys.Add(item.Key)) continue;
                    if (items.Count >= 64 || bytes + item.Stack.Length > 131072) Flush();
                    items.Add(item); bytes += item.Stack.Length;
                }
                catch (Exception ex) { api.Logger.Debug("Seraph Civilization Simulation creative catalog skipped {0}: {1}", collectible.Code, ex.Message); }
            }
        }
        Flush();
        if (packets.Count == 0) packets.Add(new());
        for (int i = 0; i < packets.Count; i++) { packets[i].Index = i; packets[i].Total = packets.Count; }
        return packets;
        void Flush() { if (items.Count == 0) return; packets.Add(new() { Items = items }); items = []; bytes = 0; }
        ItemStack? Resolve(JsonItemStack? json, CollectibleObject collectible)
        {
            if (json?.ResolvedItemstack != null) return json.ResolvedItemstack;
            if (json?.Code == null) return null;
            try
            {
                var copy = json.Clone();
                return copy.Resolve(api.World, "Seraph Civilization Simulation economy catalog", collectible.Code, false) ? copy.ResolvedItemstack : null;
            }
            catch (Exception ex) { api.Logger.Debug("Seraph Civilization Simulation creative variant skipped {0}: {1}", collectible.Code, ex.Message); return null; }
        }
    }
}

// One cache per client world. Localization happens incrementally before any picker is opened.
public sealed class EconomyClientCatalog(ICoreClientAPI api) : IDisposable
{
    public List<EconomyItemView> Items { get; } = [];
    public int Revision { get; private set; }
    public bool Loading => pending.Count > 0 || received.Count < expected;
    private readonly Queue<EconomyItemView> pending = [];
    private readonly HashSet<string> keys = [];
    private readonly HashSet<int> received = [];
    private int expected;
    private long listener;
    public void Start() { if (listener == 0) listener = api.Event.RegisterGameTickListener(_ => Advance(), 50); }
    public void Receive(EconomyCatalogPacket packet)
    {
        if (packet.Total is < 1 or > 4096 || packet.Index < 0 || packet.Index >= packet.Total
            || packet.Items == null || packet.Items.Count > 64 || !received.Add(packet.Index)) return;
        expected = packet.Total;
        foreach (var item in packet.Items) pending.Enqueue(item);
    }
    public void Advance()
    {
        if (pending.Count == 0) return;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        int added = 0;
        while (pending.Count > 0 && added < 32 && timer.ElapsedMilliseconds < 2)
        {
            var item = pending.Dequeue(); added++;
            if (item?.Stack is not { Length: > 0 and <= 65536 } || string.IsNullOrEmpty(item.Key) || !keys.Add(item.Key)) continue;
            try
            {
                using var reader = new BinaryReader(new MemoryStream(item.Stack, false));
                var stack = new ItemStack(reader);
                if (!stack.ResolveBlockOrItem(api.World)) continue;
                item.Name = stack.GetName(); Items.Add(item); Revision++;
            }
            catch (Exception ex) { api.Logger.Debug("Seraph Civilization Simulation creative preview skipped {0}: {1}", item.Code, ex.Message); }
        }
    }
    public void Dispose()
    {
        if (listener != 0) api.Event.UnregisterGameTickListener(listener);
        listener = 0; Items.Clear(); pending.Clear(); keys.Clear(); received.Clear();
    }
}
