using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using SeraphCivilizationSimulation;

internal static class LegacyMapRetirementChecks
{
    public static void Run(Action<bool, string> check)
    {
        bool full = false, threadAvailable = true;
        var returned = new List<ItemStack>();
        var dropped = new List<ItemStack>();
        var thread = new Item { ItemId = 1, Code = new AssetLocation("game:flaxtwine"), MaxStackSize = 64 };
        var map = new Item { ItemId = 2, Code = new AssetLocation("immersivemaps:map-papyrus"), MaxStackSize = 1 };
        var needle = new Item { ItemId = 3, Code = new AssetLocation("butchering:needle-bone"), MaxStackSize = 1 };
        var entity = new EntityPlayer();
        var manager = Proxy.Make<IPlayerInventoryManager>((m, a) =>
        {
            if (m.Name == "TryGiveItemstack" && !full)
            { var stack = (ItemStack)a![0]!; returned.Add(stack.Clone()); stack.StackSize = 0; return true; }
            return null;
        });
        var player = Proxy.Make<IPlayer>((m, a) => m.Name switch
        { "get_PlayerUID" => "test", "get_Entity" => entity, "get_InventoryManager" => manager, _ => null });
        var world = Proxy.Make<IWorldAccessor>((m, a) =>
        {
            if (m.Name == "SpawnItemEntity") dropped.Add(((ItemStack)a![0]!).Clone());
            return m.Name switch
            { "GetItem" => a?[0] is int id ? id == 2 ? map : id == 3 ? needle : thread
                : threadAvailable ? thread : null, "PlayerByUid" => player, _ => null };
        });
        var registry = Proxy.Make<IClassRegistryAPI>((m, a) => m.Name == "CreateInvNetworkUtil" ? Proxy.Make<IInventoryNetworkUtil>((m, a) => null) : null);
        var api = Proxy.Make<ICoreAPI>((m, a) => m.Name switch
        { "get_Side" => EnumAppSide.Server, "get_World" => world, "get_ClassRegistry" => registry, _ => null });
        CivilizationMapInventory Load()
        {
            var inv = new CivilizationMapInventory("vscncsmap-test", api);
            inv[0].Itemstack = new ItemStack(map); inv[1].Itemstack = new ItemStack(map);
            inv[CivilizationMapInventory.NeedleSlot].Itemstack = new ItemStack(needle);
            inv[CivilizationMapInventory.ThreadSlot].Itemstack = new ItemStack(thread, 31);
            var saved = new TreeAttribute(); inv.ToTreeAttributes(saved);
            saved.SetString("vscncsStitchLedger", "[{\"A\":0,\"B\":1,\"SheetA\":\"a\",\"SheetB\":\"b\"}]");
            var loaded = new CivilizationMapInventory("vscncsmap-test", api); loaded.FromTreeAttributes(saved);
            return loaded;
        }
        foreach (bool overflow in new[] { false, true })
        {
            returned.Clear(); dropped.Clear(); full = overflow;
            var inv = Load(); inv.Retire(player);
            var all = returned.Concat(dropped).ToArray();
            check(all.Where(s => s.Item == thread).Sum(s => s.StackSize) == 32, "Retirement must refund sewn thread and remaining thread exactly once.");
            check(all.Count(s => s.Item == map) == 2 && all.Count(s => s.Item == needle) == 1 && inv.Empty && inv.StitchCount == 0,
                "Legacy retirement must return every saved map and tool, including full-inventory drops.");
            int count = all.Length; inv.Retire(player);
            check(returned.Count + dropped.Count == count, "Repeated login/disconnect retirement must not duplicate items.");
            check(!inv[0].CanHold(new DummySlot(new ItemStack(map))), "Retired inventories must reject new map deposits.");
        }
        returned.Clear(); dropped.Clear(); full = false; threadAvailable = false;
        var debt = Load(); debt.Retire(player); threadAvailable = true; debt.Retire(player);
        check(returned.Where(s => s.Item == thread).Sum(s => s.StackSize) == 32, "Unavailable refund items must preserve debt for a later retry.");
    }
}
