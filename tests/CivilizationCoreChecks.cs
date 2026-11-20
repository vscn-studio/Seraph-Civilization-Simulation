using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProtoBuf;
using SeraphCivilizationSimulation;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Server;

internal static class CivilizationCoreChecks
{
    private static void CheckInventory(Action<bool, string> check)
    {
        Item? item = null;
        var registry = Proxy.Make<IClassRegistryAPI>((m,a) => m.Name == "CreateInvNetworkUtil" ? Proxy.Make<IInventoryNetworkUtil>((m,a) => null) : null);
        var world = Proxy.Make<IWorldAccessor>((m,a) => m.Name == "GetItem" ? item : null);
        var api = Proxy.Make<ICoreAPI>((m,a) => m.Name switch { "get_World" => world, "get_ClassRegistry" => registry, _ => null });
        var entity = new EntityPlayer { Api = api, World = world };
        var all = new Vintagestory.API.Datastructures.OrderedDictionary<string, InventoryBase>();
        var hotbar = new InventoryGeneric(12, "hotbar-coretest", api); all.Add(hotbar.InventoryID, hotbar);
        ServerPlayerInventoryManager? manager = null;
        var player = Proxy.Make<IPlayer>((m,a) => m.Name switch { "get_PlayerUID" => "coretest", "get_Entity" => entity, "get_InventoryManager" => manager, _ => null });
        manager = new ServerPlayerInventoryManager(all, player, null!);
        var inv = new CivilizationCoreInventory("preview", api);
        inv.EnsureItem(null);
        check(inv[0].Empty, "Unloaded registry must leave a recoverable preview.");
        item = new ItemCivilizationCore { Code = new AssetLocation("vsscsmod:civilizationcore"), ItemId = 123, MaxStackSize = 1 };
        inv.EnsureItem(item);
        check(inv[0].StackSize == 1, "Late registry must populate the preview.");
        inv[0].Itemstack = null; inv.EnsureItem(item);
        check(inv[0].StackSize == 1, "Cleared preview must regenerate.");
        inv.DropAll(new Vec3d());
        check(!inv[0].Empty && all.Count == 1 && inv[0].TakeOutWhole() == null, "Preview must never enter player inventory or death drops.");
        entity.WatchedAttributes.SetBool("vsscsCoreSelected", true);
        check(manager.ActiveHotbarSlot == hotbar[0], "Legacy selection must not replace the held item.");
        check(typeof(VscnClientModSystem).Assembly.GetType("SeraphCivilizationSimulation.CivilizationCoreCompatibility") == null,
            "Display-only core must not install inventory or hotbar patches.");
        var renderApi = Proxy.Make<ICoreClientAPI>((m,a) => m.Name == "get_ElapsedMilliseconds" ? 5000L : null);
        var info = new ItemRenderInfo { Transform = new ModelTransform() };
        var template = info.Transform;
        item!.OnBeforeRender(renderApi, new ItemStack(item), EnumItemRenderTarget.Gui, ref info);
        check(info.Transform.Rotation.Y == 100 && !ReferenceEquals(template, info.Transform), "Virtual core rotation must update during rendering without inventory idle ticks or mutating shared transforms.");
    }
    public static void Run(Action<bool, string> check)
    {
        CheckInventory(check);
        foreach (var screen in new[] { (1280,800,1d), (640,480,1.5d), (1920,1080,1.125d) })
        {
            var bounds = CivilizationCoreHud.SlotBounds(screen.Item1, screen.Item2, screen.Item3);
            check(bounds.fixedX >= 8 && bounds.fixedY >= 8
                && bounds.fixedX + bounds.fixedWidth <= screen.Item1 / screen.Item3 - 8
                && bounds.fixedY + bounds.fixedHeight <= screen.Item2 / screen.Item3 - 8,
                "Core slot must fit every viewport and scale.");
        }
        for (int i = 0; i < 100; i++) check(TribeRosterHud.Cell(i) == (i / 5, i % 5), "Roster exceeded five rows per column.");
        new TribeRosterConfig().Validate();
        var legacyHeader = new TribeRosterConfig { HeaderSvg = TribeRosterConfig.LegacyDefaultHeader }; legacyHeader.Validate();
        check(legacyHeader.HeaderSvg == TribeRosterConfig.DefaultHeader, "Old default roster SVG must migrate to the centered design.");
        var customHeader = new TribeRosterConfig { HeaderSvg = "<svg xmlns='http://www.w3.org/2000/svg'><circle r='10'/></svg>" };
        string custom = customHeader.HeaderSvg; customHeader.Validate();
        check(customHeader.HeaderSvg == custom, "Customized SVG must not be replaced.");
        var hudApi = Proxy.Make<ICoreClientAPI>((m, a) => null);
        using (var hud = new CivilizationCoreHud(hudApi, () => { }))
            check(!hud.TryClose() && !hud.OnEscapePressed() && !hud.ShouldReceiveKeyboardEvents(), "Esc and closing ordinary dialogs must not dismiss the core HUD.");
        var keys = new bool[256];
        var mapping = new KeyCombination { KeyCode = (int)GlKeys.Tab };
        keys[(int)GlKeys.Tab] = true;
        check(VscnClientModSystem.RosterHeld(mapping, keys), "Holding Tab must show the roster.");
        keys[(int)GlKeys.RControl] = true;
        check(!VscnClientModSystem.RosterHeld(mapping, keys), "Ctrl+Tab chat must not show the roster.");
        mapping.Ctrl = true;
        check(VscnClientModSystem.RosterHeld(mapping, keys), "Custom roster modifiers must be respected.");
        keys[(int)GlKeys.Tab] = false;
        check(!VscnClientModSystem.RosterHeld(mapping, keys), "Releasing Tab must hide the roster.");
        var gesture = new CoreMenuKeyGesture();
        gesture.Begin((int)GlKeys.Tab,1000);
        check(gesture.Release((int)GlKeys.Tab,1020), "A tap shorter than a polling tick must still toggle the menu.");
        check(!gesture.Release((int)GlKeys.Tab,1030), "One tap must toggle only once.");
        gesture.Begin((int)GlKeys.Tab,2000); gesture.Begin((int)GlKeys.Tab,2200);
        check(!gesture.Holding(2249) && gesture.Holding(2250), "Auto-repeat must not restart the roster hold threshold.");
        check(!gesture.Release((int)GlKeys.Tab,2300), "Releasing a roster hold must not open the menu.");
        gesture.Begin((int)GlKeys.Tab,3000);
        check(!gesture.Release((int)GlKeys.T,3010) && gesture.KeyCode == (int)GlKeys.Tab, "Unrelated releases must not complete the gesture.");
        gesture.Cancel();
        check(!gesture.Release((int)GlKeys.Tab,3020), "Changing context or leaving the world must cancel the pending tap.");
        try { new TribeRosterConfig { HeaderSvg = "<!DOCTYPE svg [<!ENTITY ext SYSTEM 'file:///private'>]><svg>&ext;</svg>" }.Validate(); check(false, "SVG accepted external entities."); }
        catch (System.Xml.XmlException) { check(true, "External entities rejected."); }
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, new TribeRosterPacket { TribeId = "tribe", HeaderSvg = TribeRosterConfig.DefaultHeader,
            Members = [new() { Uid = "member", Name = "游戏名称", PingMs = 123 }] });
        stream.Position = 0;
        var roster = Serializer.Deserialize<TribeRosterPacket>(stream);
        check(roster.Members.Single().PingMs == 123 && roster.HeaderSvg == TribeRosterConfig.DefaultHeader, "Roster lost names, ping or SVG on the wire.");
    }
}
