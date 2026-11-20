using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using SeraphCivilizationSimulation;

internal static class EconomyCacheChecks
{
    private sealed class CatalogItem : Item
    {
        public override string GetHeldItemName(ItemStack stack) => "Localized " + Code.Path;
    }
    public static void Run(Action<bool, string> check)
    {
        var logger = Proxy.Make<ILogger>((m, args) => null);
        var items = Enumerable.Range(1, 140).Select(i => new CatalogItem
            { Code = new AssetLocation("game:catalog-" + i), ItemId = i, CreativeInventoryTabs = ["general"] }).Cast<Item>().ToList();
        var variant = new CatalogItem { Code = new("game:variant"), ItemId = 141 };
        var variantStack = new JsonItemStack { Type = EnumItemClass.Item, Code = variant.Code, Attributes = JsonObject.FromJson("{\"color\":\"blue\"}") };
        variant.CreativeInventoryStacks = [new() { Tabs = ["general"], Stacks = [variantStack, variantStack] }];
        var hidden = new CatalogItem { Code = new("game:hidden"), ItemId = 142, CreativeInventoryTabs = ["general"], Attributes = JsonObject.FromJson("{\"handbook\":{\"exclude\":true}}") };
        var technical = new CatalogItem { Code = new("game:technical"), ItemId = 143 };
        var coin = new CatalogItem { Code = new("coinage:coin-copper-sm"), ItemId = 144, CreativeInventoryTabs = ["general"] };
        items.AddRange([variant, hidden, technical, coin]);
        var world = Proxy.Make<IClientWorldAccessor>((m, args) => m.Name switch
        {
            "get_Collectibles" => items.Cast<CollectibleObject>().ToList(), "get_Logger" => logger,
            "GetItem" => args?[0] is int id ? items.FirstOrDefault(i => i.Id == id) : items.FirstOrDefault(i => i.Code == args?[0] as AssetLocation), _ => null
        });
        int starts = 0, stops = 0;
        var events = Proxy.Make<IClientEventAPI>((m, args) => m.Name switch
        {
            "RegisterGameTickListener" => (long)++starts,
            "UnregisterGameTickListener" => Stop(), _ => null
        });
        object? Stop() { stops++; return null; }
        var api = Proxy.Make<ICoreClientAPI>((m, args) => m.Name switch
            { "get_World" => world, "get_Event" => events, "get_Logger" => logger, _ => null });
        var pages = EconomyCatalog.Build(api); var entries = pages.SelectMany(p => p.Items).ToList();
        check(entries.Count == 141 && pages.Count == 3 && entries.Select(i => i.Key).Distinct().Count() == entries.Count,
            "Startup catalog must retain normal items and attributed creative variants, deduplicate tabs and exclude technical/hidden/base coins.");
        check(variantStack.ResolvedItemstack == null && entries.Single(i => i.Code == variant.Code.ToString()).Stack.Length > 0,
            "Unresolved server creative variants must be resolved on copies without changing the game's definitions.");
        check(pages.All(p => p.Items.Count <= 64 && p.Items.Sum(i => i.Stack.Length) <= 131072)
            && pages.Select(p => p.Index).SequenceEqual(Enumerable.Range(0, pages.Count)) && pages.All(p => p.Total == pages.Count),
            "Catalog packets must be bounded and preserve ordering metadata.");
        using var cache = new EconomyClientCatalog(api); cache.Start(); cache.Start();
        foreach (var packet in pages.AsEnumerable().Reverse())
        {
            using var stream = new MemoryStream(); Serializer.Serialize(stream, packet); stream.Position = 0;
            var received = Serializer.Deserialize<EconomyCatalogPacket>(stream); cache.Receive(received); cache.Receive(received);
        }
        check(cache.Loading && cache.Items.Count == 0 && starts == 1,
            "Catalog reception must enqueue background work, reject duplicate pages and register only one processing listener.");
        for (int i = 0; i < 100 && cache.Loading; i++) cache.Advance();
        check(!cache.Loading && cache.Items.Count == 141 && cache.Items.All(i => i.Name.StartsWith("Localized ")),
            "Client cache must localize all streamed items once without duplicates.");
        int revision = cache.Revision; cache.Receive(pages[0]); cache.Advance();
        check(cache.Revision == revision, "An already cached packet must not localize or allocate item previews again.");
        cache.Dispose(); check(stops == 1 && cache.Items.Count == 0, "Leaving a world must release its catalog listener and cached items.");

        var rates = new List<EconomyRateView>
        {
            new() { TribeId = "a", Tribe = "Same name", ReferenceKey = "copper", CoinKey = "silver", Rate = "10" },
            new() { TribeId = "a", Tribe = "Same name", ReferenceKey = "copper", CoinKey = "gold", Rate = "100" },
            new() { TribeId = "b", Tribe = "Same name", ReferenceKey = "silver", CoinKey = "gold", Rate = "5" }
        };
        check(EconomyRateCalculator.Convert(rates, "copper", "silver").Single().Rate == .1m
            && EconomyRateCalculator.Convert(rates, "silver", "copper").Single().Rate == 10m,
            "Converter must use the published reference policy in the correct direction.");
        check(EconomyRateCalculator.Convert(rates, "silver", "gold").Select(r => r.Rate).SequenceEqual(new[] { .1m, .2m }),
            "Cross-currency conversion must preserve each tribe's independent policy, including same-name tribes.");
        check(EconomyRateCalculator.Convert(rates, "unknown", "gold").Count == 0,
            "A missing currency policy must not invent a global exchange rate.");
        foreach (decimal value in new[] { .000001m, .0000010000000000000000000001m, 1.0000000000000000000000000001m, 999999.9999999999999999999999m, 1000000m })
            check(EconomyState.TryRate(EconomyRateCalculator.FormatExact(value), out decimal restored) && restored == value,
                "Published policies must round-trip all decimal digits through editable, non-scientific text.");
        rates = [new() { TribeId = "a", Tribe = "A", ReferenceKey = "reference", CoinKey = "tiny", Rate = "0.000001" },
            new() { TribeId = "a", Tribe = "A", ReferenceKey = "reference", CoinKey = "large", Rate = "1000000" }];
        decimal tiny = EconomyRateCalculator.Convert(rates, "tiny", "large").Single().Rate;
        decimal large = EconomyRateCalculator.Convert(rates, "large", "tiny").Single().Rate;
        check(tiny == .000000000001m && large == 1000000000000m
            && decimal.Parse(EconomyRateCalculator.FormatQuantity(tiny), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) == tiny
            && decimal.Parse(EconomyRateCalculator.FormatQuantity(large), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) == large,
            "Extreme cross-rates must remain nonzero and readable in both directions.");
        check(EconomyRateCalculator.FormatQuantity(1m / 3m) == "0.33333333",
            "Repeating ratios must render eight significant digits without endless calculations.");
        check(EconomyRateCalculator.Convert(rates, "tiny", "tiny").Single().Rate == 1m,
            "Recognized identical currencies must show a 1:1 conversion within their tribe policy.");
    }
}
