using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Client.NoObf;

namespace SeraphCivilizationSimulation;

public sealed class ItemCivilizationCore : Item
{
    public override void OnBeforeRender(ICoreClientAPI capi, ItemStack stack, EnumItemRenderTarget target, ref ItemRenderInfo info)
    {
        // Virtual slots are outside the engine's inventory idle loop. Animate at render time.
        if (info.Transform == null) return;
        info.Transform = info.Transform.Clone();
        info.Transform.Rotation.Y = (target == EnumItemRenderTarget.Gui ? 1 : -1) * (capi.ElapsedMilliseconds / 50f % 360);
    }
}

// Never registered with player inventories: not saved, traded, crafted with, or dropped by death mods.
internal sealed class CivilizationCoreInventory : InventoryGeneric
{
    internal CivilizationCoreInventory(string uid, ICoreAPI api)
        : base(1, "vsscs-core-" + uid, api, (_, inv) => new CoreSlot(inv))
    { TakeLocked = PutLocked = true; }
    internal void EnsureItem(Item? item)
    {
        if (item == null) return; // Registry may arrive after the HUD. Retry on every access.
        if (!ReferenceEquals(this[0].Itemstack?.Item, item) || this[0].StackSize != 1)
        { this[0].Itemstack = new ItemStack(item); DirtySlots.Add(0); }
    }
    public override void DropAll(Vec3d pos, int maxStackSize = 0) { }
    private sealed class CoreSlot(InventoryBase inventory) : ItemSlot(inventory)
    {
        public override bool CanTake() => false;
        public override bool CanHold(ItemSlot source) => false;
        public override bool CanTakeFrom(ItemSlot source, EnumMergePriority priority = EnumMergePriority.AutoMerge) => false;
        public override bool TryFlipWith(ItemSlot slot) => false;
        public override ItemStack TakeOut(int quantity) => null!;
        public override ItemStack TakeOutWhole() => null!;
    }
}

public sealed partial class VscnClientModSystem
{
    private CivilizationCoreHud? coreHud;
    private long coreListener;
    private void StartCoreClient()
    {
        coreListener = capi!.Event.RegisterGameTickListener(_ =>
        {
            if (!ready || capi.World.Player?.Entity == null) return;
            (coreHud ??= new CivilizationCoreHud(capi, OpenCivilizationPage)).Show();
        }, 250);
    }
    private void OpenCivilizationCore()
    {
        if (!ready || channel == null || guidance?.IsOpened() == true) return;
        channel.SendPacket(new CivilizationRequestPacket { Action = "open" });
    }
}

// Display-only native item preview: no player inventory, hand selection or transfers.
internal sealed class CivilizationCoreHud(ICoreClientAPI api, Action<int> open) : HudElement(api)
{
    private bool disposed, menuOpen;
    internal bool KeyboardMenuOpen { get; private set; }
    private long hoverAt, leaveAt;
    private GuiComposer? Menu => Composers["core-menu"];
    internal CivilizationCoreHud(ICoreClientAPI api, Action open) : this(api,_ => open()) { }
    private CivilizationCoreInventory? inventory;
    private (double X, double Y, double Scale) layout;
    public override bool Focusable => false;
    public override bool DisableMouseGrab => KeyboardMenuOpen;
    public override double DrawOrder => 0.11;
    public override bool TryClose() => disposed && base.TryClose();
    public override bool OnEscapePressed() { if (!menuOpen) return false; HideMenu(); return true; }
    public override bool ShouldReceiveKeyboardEvents() => menuOpen;
    public override bool ShouldReceiveMouseEvents() => IsOpened() && !capi.Input.MouseGrabbed;
    public override void OnKeyDown(KeyEvent args)
    {
        if (args.KeyCode == (int)GlKeys.Escape && OnEscapePressed()) args.Handled = true;
    }
    internal void ToggleKeyboardMenu()
    {
        if (menuOpen) { HideMenu(); return; }
        Show(); ShowMenu(); KeyboardMenuOpen = menuOpen;
    }
    internal static ElementBounds SlotBounds(int width, int height, double scale, ElementBounds? firstSlot = null)
    {
        scale = Math.Max(.25, scale);
        double size = GuiElementPassiveItemSlot.unscaledSlotSize, margin = 8;
        // Follow the actual first hotbar cell, including GUI scale and native layout changes.
        // Keep the same row and the same spacing as the native grid.
        if (firstSlot != null)
            return ElementBounds.Fixed(firstSlot.renderX / scale - size - GuiElementItemSlotGridBase.unscaledSlotPadding,
                firstSlot.renderY / scale, size, size);
        double x = width / scale / 2 - 315 - size - GuiElementItemSlotGridBase.unscaledSlotPadding;
        double y = height / scale - 65;
        if (x < margin) { x = margin; y -= size + margin; }
        return ElementBounds.Fixed(Math.Clamp(x, margin, Math.Max(margin, width / scale - size - margin)),
            Math.Clamp(y, margin, Math.Max(margin, height / scale - size - margin)), size, size);
    }
    internal void Show()
    {
        if (disposed || capi.World.Player?.Entity == null) return;
        var current = inventory ??= new CivilizationCoreInventory("preview", capi);
        current.EnsureItem(capi.World.GetItem(new AssetLocation("vsscsmod:civilizationcore")));
        var native = capi.Gui.LoadedGuis.OfType<HudHotbar>().FirstOrDefault();
        var anchor = native?.Composers["hotbar"]?.GetSlotGrid("hotbargrid")?.SlotBounds?.FirstOrDefault();
        double scale = Math.Max(.25, RuntimeEnv.GUIScale);
        var bounds = SlotBounds(capi.Render.FrameWidth, capi.Render.FrameHeight, scale, anchor);
        var nextLayout = (bounds.fixedX, bounds.fixedY, scale);
        if (SingleComposer == null || !ReferenceEquals(inventory, current) || layout != nextLayout)
        {
            bool reopen = KeyboardMenuOpen;
            HideMenu(); ClearComposers(); inventory = current; layout = nextLayout;
            SingleComposer = capi.Gui.CreateCompo("vsscs-civilization-core", bounds.FlatCopy().FixedGrow(4, 4))
                .AddPassiveItemSlot(ElementBounds.Fixed(0, 0, bounds.fixedWidth, bounds.fixedHeight), current, current[0], drawBackground: false, key: "core")
                .Compose();
            SingleComposer.Tabbable = false;
            if (reopen) { ShowMenu(); KeyboardMenuOpen = menuOpen; }
        }
        if (!IsOpened()) TryOpen(false);
    }
    private void HideMenu()
    { menuOpen=false; KeyboardMenuOpen=false; hoverAt=leaveAt=0; Menu?.Dispose(); Composers.Remove("core-menu"); }
    private void ShowMenu()
    {
        if (menuOpen || SingleComposer==null) return;
        menuOpen=true;
        var anchor=SingleComposer.GetElement("core").Bounds;
        double scale=Math.Max(.25,RuntimeEnv.GUIScale), width=56;
        bool admin=capi.World.Player.HasPrivilege(Privilege.controlserver);
        int count=admin?6:5;
        double height=Math.Min(16+count*44,Math.Max(40,anchor.renderY/scale-8)), row=(height-16)/count;
        double left=Math.Clamp(anchor.renderX/scale,4,Math.Max(4,capi.Render.FrameWidth/scale-width-4));
        double top=Math.Max(4,anchor.renderY/scale-height-4);
        var composer=capi.Gui.CreateCompo("vsscs-core-menu",ElementBounds.Fixed(left,top,width,height))
            // A menu has no title bar; the default skips painting the top title-bar height.
            .AddShadedDialogBG(ElementBounds.Fill,withTitleBar: false,alpha: .96f);
        string[] labels=["公告","教程","部落","交易","营养手册","管理员设置"], icons=["speakerphone","book","users-group","arrows-exchange","meat","settings"];
        for (int i=0;i<count;i++)
        {
            int selected=i;
            var buttonBounds=ElementBounds.Fixed(8,8+i*row,width-16,row-6);
            composer.AddInteractiveElement(new GuiElementFixedButton(capi,"",() => { HideMenu(); open(selected); },buttonBounds),"entry-"+i);
            composer.AddHoverText(labels[i],CairoFont.WhiteSmallText(),180,buttonBounds.FlatCopy(),"entry-hint-"+i);
            string svg=capi.Assets.Get(new AssetLocation("vsscsmod:textures/icons/tabler/"+icons[i]+".svg")).ToText();
            double icon=Math.Min(24,row-12);
            composer.AddStaticCustomDraw(ElementBounds.Fixed((width-icon)/2,8+i*row+(row-6-icon)/2,icon,icon),(ctx,surface,b) => RosterSvg.Draw(capi,svg,ctx,surface,b));
        }
        Composers["core-menu"]=composer.Compose();
        composer.Tabbable=false;
    }
    public override void OnMouseDown(MouseEvent args)
    {
        if (menuOpen && Menu?.Bounds.PointInside(args.X,args.Y)==true)
        { Menu.OnMouseDown(args); args.Handled=true; return; }
        if (SingleComposer?.GetElement("core").Bounds.PointInside(args.X,args.Y)==true)
        { if (args.Button==EnumMouseButton.Left) { if (menuOpen) HideMenu(); else ShowMenu(); } args.Handled=true; return; }
        if (KeyboardMenuOpen) args.Handled=true;
        HideMenu();
    }
    public override void OnMouseUp(MouseEvent args)
    { if (menuOpen && Menu?.Bounds.PointInside(args.X,args.Y)==true) { Menu.OnMouseUp(args); args.Handled=true; } }
    public override void OnMouseMove(MouseEvent args)
    { if (menuOpen) Menu?.OnMouseMove(args); }
    public override void OnRenderGUI(float dt)
    {
        if (disposed) return;
        Show();
        if (KeyboardMenuOpen)
        {
            if (capi.Gui.OpenedGuis.Any(gui => gui.DialogType == EnumDialogType.Dialog && gui.ShouldReceiveKeyboardEvents())) HideMenu();
        }
        else if (capi.Input.MouseGrabbed) HideMenu();
        else
        {
            long now=Environment.TickCount64;
            bool icon=SingleComposer?.GetElement("core").Bounds.PointInside(capi.Input.MouseX,capi.Input.MouseY)==true;
            bool menu=menuOpen && Menu?.Bounds.PointInside(capi.Input.MouseX,capi.Input.MouseY)==true;
            if (icon || menu)
            { leaveAt=0; if (hoverAt==0) hoverAt=now; if (now-hoverAt>=200) ShowMenu(); }
            else { hoverAt=0; if (leaveAt==0) leaveAt=now; if (menuOpen && now-leaveAt>=250) HideMenu(); }
        }
        base.OnRenderGUI(dt);
    }
    public override void Dispose()
    {
        if (disposed) return;
        disposed = true; TryClose(); base.Dispose();
    }
}
