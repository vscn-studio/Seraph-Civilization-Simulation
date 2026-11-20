using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using SeraphCivilizationSimulation;

internal static class EconomyMatchingChecks
{
    private sealed class MalformedAttribute : IAttribute
    {
        private readonly string token;
        public MalformedAttribute(string token) => this.token = token;
        public void ToBytes(BinaryWriter stream) => stream.Write(token);
        public void FromBytes(BinaryReader stream) => _ = stream.ReadString();
        public int GetAttributeId() => 127;
        public object GetValue() => token;
        public string ToJsonToken() => token;
        public bool Equals(IWorldAccessor world, IAttribute other) => other is MalformedAttribute value && value.token == token;
        public IAttribute Clone() => new MalformedAttribute(token);
    }

    private sealed class TestItem : Item
    {
        public void Initialize(ICoreAPI core) => api = core;
        public override string GetHeldItemName(ItemStack stack) => Code.Path;
    }
    public static void Run(Action<bool, string> check)
    {
        var tool = new TestItem { Code = new("game:axe-copper"), ItemId = 20, Durability = 100, MaxStackSize = 1 };
        var linen = new TestItem { Code = new("game:flaxfibers"), ItemId = 21, MaxStackSize = 64 };
        var cup = new TestItem { Code = new("game:cup"), ItemId = 22, MaxStackSize = 1 };
        var map = new TestItem { Code = new("immersivemaps:papermap"), ItemId = 23, MaxStackSize = 1 };
        var coin = new TestItem { Code = new("coinage:coin-copper-small"), ItemId = 24, MaxStackSize = 64 };
        var items = new[] { tool, linen, cup, map, coin };
        var logger = Proxy.Make<ILogger>((m, args) => null);
        var calendar = Proxy.Make<IGameCalendar>((m, args) => m.Name switch { "get_TotalHours" => 100d, "get_HoursPerDay" => 24f, _ => null });
        var world = Proxy.Make<IServerWorldAccessor>((m, args) => m.Name switch
        {
            "GetItem" => args?[0] is int id ? items.FirstOrDefault(i => i.Id == id) : items.FirstOrDefault(i => i.Code == args?[0] as AssetLocation),
            "get_Calendar" => calendar, "get_Logger" => logger, _ => null
        });
        var registry = Proxy.Make<IClassRegistryAPI>((m, args) => m.Name == "CreateInvNetworkUtil"
            ? Proxy.Make<IInventoryNetworkUtil>((m, args) => null) : null);
        var api = Proxy.Make<ICoreServerAPI>((m, args) => m.Name switch
            { "get_World" => world, "get_ClassRegistry" => registry, "get_Logger" => logger, _ => null });
        foreach (var item in items) item.Initialize(api);
        ItemStack Axe(int? durability = null)
        {
            var stack = new ItemStack(tool);
            if (durability.HasValue) stack.Attributes.SetInt("durability", durability.Value);
            return stack;
        }
        ItemStack Money(long design, long shape = 1)
        {
            var stack = new ItemStack(coin); stack.Attributes.SetLong("coinType", design); stack.Attributes.SetLong("coinShape", shape); return stack;
        }
        var unused = Axe(); var explicitFull = Axe(100); var worn = Axe(27);
        worn.Attributes.SetString("customName", "Old axe");
        worn.Attributes.SetFloat("temperature", 48); worn.Attributes["transitionstate"] = new TreeAttribute();
        string toolKey = EconomyInventory.ItemKey(unused);
        check(toolKey == EconomyInventory.ItemKey(explicitFull) && toolKey == EconomyInventory.ItemKey(worn),
            "Catalog, implicit full durability, explicit full durability and used/renamed tools must share a kind.");
        check(EconomyInventory.ItemKey(unused, true) != EconomyInventory.ItemKey(worn, true), "Real transfer and concurrency must still distinguish condition.");
        check(EconomyItemMatching.StateKey(unused) != EconomyItemMatching.StateKey(worn), "Browse state must retain tool condition and names.");
        var firstView = new EconomySnapshot { Items = [EconomyInventory.Display(Axe(27))] };
        var secondView = new EconomySnapshot { Items = [EconomyInventory.Display(Axe(26))] };
        // Include a stock range which can mask a representative item's individual condition change.
        firstView.Items[0].DurabilityLow = secondView.Items[0].DurabilityLow = 10;
        firstView.Items[0].DurabilityHigh = secondView.Items[0].DurabilityHigh = 100;
        var fingerprint = typeof(VscnCivilizationModSystem).Assembly.GetType("SeraphCivilizationSimulation.EconomyGuiFingerprint")!
            .GetMethod("Of", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        check((string)fingerprint.Invoke(null, [firstView])! != (string)fingerprint.Invoke(null, [secondView])!,
            "Changes to the real tooltip stack must refresh the GUI even with an unchanged kind, count and stock range.");
        var requirement = EconomyItemMatching.Requirement(unused);
        check(requirement.Code == tool.Code.ToString() && requirement.Class == EnumItemClass.Item
            && EconomyItemMatching.Matches(worn, toolKey, requirement), "Native recipe-style templates must accept added tool condition.");
        var pick = new Vintagestory.GameContent.ItemProspectingPick { Code = new("game:propick-copper"), ItemId = 25 };
        typeof(CollectibleObject).GetField("api", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(pick, api);
        var pickStack = new ItemStack(pick); string pickKey = EconomyInventory.ItemKey(pickStack);
        var pickRequirement = EconomyItemMatching.Requirement(pickStack);
        var pickSlot = new DummySlot(pickStack);
        pick.SetToolMode(pickSlot, null!, null!, 1);
        check(EconomyItemMatching.Matches(pickStack, pickKey, pickRequirement),
            "The real copper prospecting pick must match its offer after selecting node search mode.");
        var modular = Axe(85);
        modular.Attributes.SetInt("toolSharpnessMax", 100);
        modular.Attributes.SetItemstack("tinkeredToolHandle", new ItemStack(linen));
        modular.Attributes.SetFloat("sp:smithingQuality", .8f);
        var modularRequirement = EconomyItemMatching.Requirement(modular);
        string modularKey = EconomyInventory.ItemKey(modular), modularState = EconomyItemMatching.StateKey(modular);
        foreach (string attribute in new[] { "toolSharpnessCurrent", "totalPercentHoned", "tinkeredToolHeadDurability",
            "tinkeredToolHandleDurability", "tinkeredToolBindingDurability", "toolPartCurrentDurability", "toolMode",
            "sp:flipToolModeIndex", "toolsmithGrindstoneInUse", "toolsmithWhetstoneInUse", "toolsmithWhetstoneDoneSharpen" })
            modular.Attributes.SetInt(attribute, 7);
        modular.Attributes.GetItemstack("tinkeredToolHandle").Attributes.SetInt("toolPartCurrentDurability", 6);
        check(EconomyItemMatching.Matches(modular, modularKey, modularRequirement, world)
            && EconomyItemMatching.StateKey(modular) != modularState,
            "Toolsmith wear, honing and SmithingPlus modes must retain the kind while refreshing real preview state.");
        foreach (string variant in new[] { "sp:smithingQuality", "toolSharpnessMax", "tinkeredToolHandleMaxDurability", "speedBonus", "toolHandleTreatmentTag" })
        {
            var changed = modular.Clone(); changed.Attributes.SetString(variant, "different");
            check(EconomyInventory.ItemKey(changed) != modularKey, "Tool quality and structural stats must remain distinct: " + variant);
        }
        var otherHandle = modular.Clone(); otherHandle.Attributes.SetItemstack("tinkeredToolHandle", new ItemStack(cup));
        check(EconomyInventory.ItemKey(otherHandle) != modularKey, "Different Toolsmith handle materials must never be interchangeable.");
        var toolSeller = new InventoryGeneric(2, "modular-seller", api); var toolBuyer = new InventoryGeneric(2, "modular-buyer", api);
        toolSeller[0].Itemstack = modular; toolBuyer[0].Itemstack = new ItemStack(linen);
        var modularPlan = EconomyInventory.Plan(toolSeller.ToList(), toolBuyer.ToList(), modularKey, 1,
            [new() { Key = EconomyInventory.ItemKey(new ItemStack(linen)), Quantity = 1 }], modularRequirement)!;
        string originalModular = EconomyInventory.ItemKey(modular, true);
        modularPlan.Apply();
        check(toolBuyer.Any(s => !s.Empty && EconomyInventory.ItemKey(s.Itemstack, true) == originalModular),
            "A modded tool sale must transfer actual sharpness, durability, mode, parts and quality without normalizing delivery.");
        using (var stream = new MemoryStream())
        {
            Serializer.Serialize(stream, requirement); stream.Position = 0;
            requirement = Serializer.Deserialize<EconomyItemRequirement>(stream);
        }
        tool.ItemId = 120;
        check(EconomyInventory.ItemKey(Axe()) == toolKey && EconomyItemMatching.Matches(Axe(11), toolKey, requirement),
            "Persisted kind templates must resolve current collectible identity after numeric IDs change.");
        var block = new Block { Code = tool.Code, BlockId = tool.Id };
        check(EconomyInventory.ItemKey(new ItemStack(block)) != toolKey && !EconomyItemMatching.Matches(new ItemStack(block), toolKey, requirement),
            "Item/block class must prevent collisions even with identical codes and numeric IDs.");
        check(EconomyInventory.ItemKey(Money(1)) != EconomyInventory.ItemKey(Money(2))
            && EconomyInventory.ItemKey(Money(1)) != EconomyInventory.ItemKey(Money(1, 2)), "General goods matching must never erase coin design or shape.");
        var emptyCup = new ItemStack(cup); var filledCup = emptyCup.Clone();
        filledCup.Attributes.SetItemstack("contents", new ItemStack(linen, 2));
        var lessContent = filledCup.Clone(); lessContent.Attributes.GetItemstack("contents").StackSize = 1;
        check(EconomyInventory.ItemKey(emptyCup) != EconomyInventory.ItemKey(filledCup)
            && EconomyInventory.ItemKey(filledCup) != EconomyInventory.ItemKey(lessContent), "Empty/full containers and content quantities must remain distinct.");
        check(EconomyItemMatching.StateKey(filledCup) != EconomyItemMatching.StateKey(lessContent)
            && EconomyInventory.ItemKey(filledCup, true) != EconomyInventory.ItemKey(lessContent, true),
            "Preview and exact inventory checks must detect changes in nested content quantity.");
        check(!EconomyItemMatching.Matches(filledCup, EconomyInventory.ItemKey(emptyCup), EconomyItemMatching.Requirement(emptyCup)),
            "An empty template must not consume a filled container just because native subset matching permits extra attributes.");
        var ageingCup = filledCup.Clone(); ageingCup.Attributes.GetItemstack("contents").Attributes["transitionstate"] = new TreeAttribute();
        ageingCup.Attributes.GetItemstack("contents").Attributes.SetFloat("temperature", 34);
        check(EconomyInventory.ItemKey(ageingCup) == EconomyInventory.ItemKey(filledCup), "Ageing nested contents must retain a container's kind.");
        check(EconomyItemMatching.StateKey(ageingCup) == EconomyItemMatching.StateKey(filledCup), "Natural changes in nested contents must not trigger economy refreshes.");
        var contentRequirement = EconomyItemMatching.Requirement(filledCup);
        using (var stream = new MemoryStream())
        {
            Serializer.Serialize(stream, contentRequirement); stream.Position = 0;
            contentRequirement = Serializer.Deserialize<EconomyItemRequirement>(stream);
        }
        linen.ItemId = 121;
        ageingCup.Attributes.SetItemstack("contents", new ItemStack(linen, 2)
            { Attributes = ageingCup.Attributes.GetItemstack("contents").Attributes });
        check(EconomyItemMatching.Matches(ageingCup, EconomyInventory.ItemKey(filledCup), contentRequirement, world),
            "Nested item templates must resolve stable content codes when registry IDs change after a save.");
        var mapA = new ItemStack(map); mapA.Attributes.SetBytes("mapdata", [1, 2, 3]);
        var mapB = mapA.Clone(); mapB.Attributes.SetBytes("mapdata", [1, 2, 4]);
        check(EconomyInventory.ItemKey(mapA) != EconomyInventory.ItemKey(mapB), "Different recorded map contents must not be interchangeable.");
        var colored = new ItemStack(linen); colored.Attributes.SetString("color", "blue");
        check(EconomyInventory.ItemKey(colored) != EconomyInventory.ItemKey(new ItemStack(linen)), "Unknown mod variants must remain significant.");
        var malformed = new ItemStack(linen); malformed.Attributes["malformed"] = new MalformedAttribute("}");
        check(EconomyItemMatching.StateKey(malformed).Length > 0,
            "Malformed third-party attribute data must not throw while watching inventory changes.");
        var differentMalformed = malformed.Clone(); differentMalformed.Attributes["malformed"] = new MalformedAttribute("{broken");
        check(EconomyItemMatching.StateKey(malformed) != EconomyItemMatching.StateKey(differentMalformed),
            "Opaque fallback data must still distinguish unknown mod variants.");
        var seller = new InventoryGeneric(3, "match-seller", api); var buyer = new InventoryGeneric(3, "match-buyer", api);
        var sellerSlots = seller.ToList(); var buyerSlots = buyer.ToList();
        seller[0].Itemstack = Axe(27); seller[1].Itemstack = Axe(); buyer[0].Itemstack = new ItemStack(linen, 5);
        string preview = EconomyInventory.PreviewStamp(sellerSlots, toolKey, 1, requirement);
        seller[0].Itemstack!.Attributes.SetFloat("temperature", 48);
        check(preview == EconomyInventory.PreviewStamp(sellerSlots, toolKey, 1, requirement), "Natural temperature must not invalidate a buyer's preview.");
        seller[0].Itemstack!.Attributes.SetString("customName", "Named axe");
        check(preview != EconomyInventory.PreviewStamp(sellerSlots, toolKey, 1, requirement), "Renaming viewed goods must invalidate a buyer's preview.");
        var payment = new EconomyExchangeItem { Key = EconomyInventory.ItemKey(new ItemStack(linen)), Quantity = 3,
            Display = EconomyInventory.Display(new ItemStack(linen)), Requirement = EconomyItemMatching.Requirement(new ItemStack(linen)) };
        var display = EconomyInventory.Read(sellerSlots).Single();
        check(display.Count == 2 && display.DurabilityLow == 27 && display.DurabilityHigh == 100 && display.DurabilityMax == 100,
            "Mixed durability stock must aggregate quantities and expose its real durability range.");
        var plan = EconomyInventory.Plan(sellerSlots, buyerSlots, toolKey, 2, [payment], requirement)!;
        check(plan != null && seller[0].StackSize == 1 && buyer[0].StackSize == 5, "Planning mixed tools must not mutate live inventory.");
        plan!.Apply();
        check(buyerSlots.Where(s => !s.Empty && s.Itemstack.Collectible == tool).Select(s => s.Itemstack!.Collectible.GetRemainingDurability(s.Itemstack)).Order().SequenceEqual(new[] { 27, 100 })
            && buyer[0].StackSize == 2, "A type-based sale must deliver each real tool without resetting durability.");
        check(plan.DeliveredItem?.DurabilityLow == 27 && plan.DeliveredItem.DurabilityHigh == 100, "Recorded delivery must describe actual selected goods.");
        plan.Rollback();
        check(seller[0].Itemstack!.Attributes.GetInt("durability") == 27 && buyer[0].StackSize == 5, "Rollback must restore original tool condition and payment.");
        plan = EconomyInventory.Plan(sellerSlots, buyerSlots, toolKey, 1, [payment], requirement)!;
        seller[0].Itemstack!.Attributes.SetInt("durability", 26);
        bool rejected = false; try { plan.Apply(); } catch (InvalidOperationException) { rejected = true; }
        check(rejected && buyer[0].StackSize == 5, "Condition-only changes after planning must still abort atomically.");
        var toolPayment = new EconomyExchangeItem { Key = toolKey, Quantity = 1, Requirement = requirement, Display = EconomyInventory.Display(unused) };
        check(EconomyInventory.Has(sellerSlots, [toolPayment]) && EconomyInventory.Plan(buyerSlots, sellerSlots, payment.Key, 1, [toolPayment]) != null,
            "Payment selected from a pristine catalog template must accept a used inventory tool.");
        toolPayment.Quantity = 2;
        check(!EconomyInventory.Has(sellerSlots, [toolPayment, toolPayment])
            && EconomyInventory.Plan(buyerSlots, sellerSlots, payment.Key, 1, [toolPayment, toolPayment]) == null,
            "Duplicate same-kind payment cells must cumulatively require stock and never double-count tools.");
        var exact = EconomyItemMatching.LegacyKey(seller[0].Itemstack!);
        check(EconomyInventory.Count(sellerSlots, exact) == 1, "An existing signed task must retain its legacy condition requirement.");
        seller[0].Itemstack!.Attributes.SetInt("durability", 25);
        check(EconomyInventory.Count(sellerSlots, exact) == 0 && EconomyInventory.Count(sellerSlots, toolKey) == 2,
            "Legacy signed tasks must remain strict even though new offers accept condition differences.");
        var originalTemplateBytes = requirement.Attributes.ToArray();
        check(EconomyItemMatching.Matches(Axe(4), toolKey, requirement) && requirement.Attributes.SequenceEqual(originalTemplateBytes),
            "Matching must not modify a persisted requirement template.");
        var receiver = new InventoryGeneric(1, "match-receiver", api); var payer = new InventoryGeneric(1, "match-payer", api);
        receiver[0].Itemstack = new ItemStack(linen, 60); payer[0].Itemstack = new ItemStack(linen, 4);
        linen.SetTemperature(world, receiver[0].Itemstack, 40, false); linen.SetTemperature(world, payer[0].Itemstack, 50, false);
        var linenPayment = new EconomyExchangeItem { Key = payment.Key, Quantity = 4, Requirement = payment.Requirement };
        plan = EconomyInventory.Plan(receiver.ToList(), payer.ToList(), "", 0, [linenPayment])!;
        check(plan != null && receiver[0].StackSize == 60 && payer[0].StackSize == 4, "Native merging must be planned on detached stacks.");
        plan!.Apply();
        check(receiver[0].StackSize == 64 && payer[0].Empty && Math.Abs(linen.GetTemperature(world, receiver[0].Itemstack) - 40.625f) < .001,
            "Native compatible temperature stacks must merge with weighted temperature, even without empty slots.");
        plan.Rollback(); linen.SetTemperature(world, payer[0].Itemstack!, 250, false);
        check(EconomyInventory.Plan(receiver.ToList(), payer.ToList(), "", 0, [linenPayment]) == null && receiver[0].StackSize == 60 && payer[0].StackSize == 4,
            "Native incompatible temperature rules must fail without discarding attributes or mutating inventory.");
    }
}
