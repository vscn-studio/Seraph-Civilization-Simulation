using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Cairo;

namespace SeraphCivilizationSimulation;

// A draft belongs to the civilization window; it never registers another dialog.
internal sealed class EconomyOfferEditor
{
    private readonly ICoreClientAPI api;
    private readonly EconomyOfferView? offer;
    private readonly Action<EconomyRequest> send;
    private readonly Action changed, close;
    private readonly bool task;
    private bool enabled;
    private string quantity, title, content;
    private EconomyItemView? goods;
    private readonly List<EconomyExchangeItem?> exchange = [];
    private readonly List<string> counts = [];
    private int selected;
    public int? Picking { get; private set; }
    public string Search = "";
    public List<GuiElementEditableTextBase> Inputs { get; } = [];
    public string Title => L(task ? "economy-new-task" : "economy-edit-trade");
    private string L(string key, params object[] args) => Lang.Get("vsscsmod:ui." + key, args);
    public EconomyOfferEditor(ICoreClientAPI api, EconomySnapshot snapshot, EconomyOfferView? offer, bool task,
        Action<EconomyRequest> send, Action changed, Action close)
    {
        this.api = api; this.offer = offer; this.task = task; this.send = send; this.changed = changed; this.close = close;
        enabled = offer?.Enabled ?? false; quantity = (offer?.Quantity ?? 1).ToString();
        title = task ? offer?.Name ?? "" : ""; content = task ? offer?.Content ?? "" : "";
        goods = offer?.NeedsItemSelection == true ? null
            : offer == null ? snapshot.Items.FirstOrDefault()
            : offer.DisplayItem ?? snapshot.Items.FirstOrDefault(i => i.Key == offer.ItemKey);
        var entries = offer?.Exchange ?? [];
        if (entries.Count == 0 && offer != null && snapshot.Coins.FirstOrDefault(c => c.Key == offer.Coin) is { } coin)
            entries = [new() { Coin = coin.Key, Quantity = offer.Price, Display = CoinDisplay(api, coin) }];
        foreach (var entry in entries)
        { exchange.Add(new() { Key = entry.Key, Coin = entry.Coin, Quantity = entry.Quantity, Display = entry.Display }); counts.Add(entry.Quantity.ToString()); }
        if (exchange.Count == 0) AddSlot();
    }
    private void AddSlot() { exchange.Add(null); counts.Add("1"); selected = exchange.Count - 1; }
    public void Build(EconomyGuiList list)
    {
        Inputs.Clear();
        list.Text(Title, heading: true);
        if (task)
        {
            Inputs.Add(list.Field(L("economy-task-title"), title, s => title = s, 100));
            list.Text(L("economy-task-content"), detail: true); Inputs.Add(list.TextArea(content, s => content = s));
        }
        else
        {
            double top = list.Y;
            var goodsSlot = list.Slot(goods, 0, top, click: () => Pick(-1), count: int.TryParse(quantity, out int n) && n > 0 ? n : 1,
                clear: goods == null ? null : () => { goods = null; Picking = null; changed(); });
            list.Text(goods?.Name ?? L("economy-select-item"), x: 58);
            if (list.Width < 250) list.Y = Math.Max(list.Y, top + 56);
            Inputs.Add(list.Field(L("economy-quantity"), quantity, s =>
            { quantity = s; if (int.TryParse(s, out int value) && value is >= 1 and <= 1000000) goodsSlot.SetQuantity(value); }, 7, list.Width >= 250 ? 58 : 0));
            list.Y = Math.Max(list.Y, top + 56);
        }
        list.Text(L(task ? "economy-reward-items" : "economy-payment-items"), detail: true);
        int columns = Math.Max(1, (int)((list.Width + 6) / 54));
        double size = (list.Width - (columns - 1) * 6) / columns, gridTop = list.Y;
        var paymentSlots = new List<GuiElementEconomyItem>();
        for (int i = 0; i < exchange.Count; i++)
        {
            int index = i;
            paymentSlots.Add(list.Slot(exchange[i]?.Display, i % columns * (size + 6), gridTop + i / columns * (size + 6), size,
                () => { if (exchange[index] == null || selected == index) { selected = index; Pick(index); } else { selected = index; changed(); } },
                int.TryParse(counts[i], out int count) && count > 0 ? count : 1, selected == i, clear: exchange[i] == null ? null : () => Clear(index)));
        }
        int cells = exchange.Count;
        if (exchange.Count < EconomyState.MaxExchangeItems)
        {
            list.AddSlotButton(cells % columns * (size + 6), gridTop + cells / columns * (size + 6), size,
                () => { if (exchange.Any(e => e == null)) { selected = exchange.FindIndex(e => e == null); Pick(selected); }
                    else { AddSlot(); Pick(selected); } }); cells++;
        }
        list.Y = gridTop + ((cells + columns - 1) / columns) * (size + 6) + 6;
        list.Text(L("economy-all-payment"), detail: true);
        int selectedIndex = selected;
        Inputs.Add(list.Field(L("economy-slot-quantity", selected + 1), counts[selected], s =>
        { counts[selectedIndex] = s; if (int.TryParse(s, out int value) && value is >= 1 and <= 1000000) paymentSlots[selectedIndex].SetQuantity(value); }, 7));
    }
    public double BuildFooter(GuiComposer composer, ElementBounds view) => EconomyGuiList.Footer(api, composer, view,
        (L("economy-permit"), enabled, on => enabled = on),
        ("save", L("save"), Save), ("cancel", L("cancel"), close));
    private void Clear(int index)
    {
        if (exchange.Count > 1)
        {
            exchange.RemoveAt(index); counts.RemoveAt(index);
            if (selected > index) selected--;
            selected = Math.Min(selected, exchange.Count - 1);
        }
        else { exchange[0] = null; counts[0] = "1"; }
        Picking = null; changed();
    }
    private void Pick(int index) { Picking = index; Search = ""; changed(); }
    public void ClosePicker() { Picking = null; changed(); }
    public void Select(EconomyItemView item)
    {
        if (Picking is not { } index) return;
        if (index < 0) goods = item;
        else { exchange[index] = new() { Key = item.Key, Display = item }; counts[index] = "1"; selected = index; }
        Picking = null; changed();
    }
    private void Save()
    {
        var entries = new List<EconomyExchangeItem>();
        for (int i = 0; i < exchange.Count; i++) if (exchange[i] is { } entry)
        {
            if (!int.TryParse(counts[i], out int n) || n is < 1 or > 1000000) { api.ShowChatMessage(L("economy-invalid")); return; }
            entries.Add(new() { Key = entry.Key, Coin = entry.Coin, Quantity = n, Display = entry.Display });
        }
        if (entries.Count == 0 || !task && (goods == null || !int.TryParse(quantity, out int q) || q is < 1 or > 1000000))
        { api.ShowChatMessage(L("economy-invalid")); return; }
        if (task && (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content)))
        { api.ShowChatMessage(L("economy-task-invalid")); return; }
        send(new() { Action = "offer-save", Id = offer?.Id ?? "", Revision = offer?.Revision ?? 0, IsTask = task,
            Item = task ? "" : goods!.Key, Text = title, Content = content, Quantity = task ? 1 : int.Parse(quantity), Enabled = enabled, Exchange = entries });
        close();
    }
    internal static EconomyItemView? CoinDisplay(ICoreClientAPI api, EconomyCoinView coin)
    {
        var item = api.World.GetItem(new AssetLocation(coin.Code)); if (item == null) return null;
        var stack = new ItemStack(item); stack.Attributes.SetLong("coinType", coin.Design); stack.Attributes.SetLong("coinShape", coin.Shape);
        var view = EconomyInventory.Display(stack); view.Name = coin.Name; return view;
    }
}

// A creative-style virtual grid. Only visible rows allocate item previews and textures.
internal sealed class GuiElementEconomyPickerGrid(ICoreClientAPI api, ElementBounds bounds, Action<EconomyItemView> select) : GuiElement(api, bounds)
{
    private List<EconomyItemView> items = [];
    private readonly Dictionary<int, GuiElementEconomyItem> visible = [];
    private double scroll;
    private ItemSlot? hover;
    internal System.Func<EconomyItemView, bool>? IsSelected { get; set; }
    public GuiElementAutoScrollbar Scrollbar { get; set; } = null!;
    private int Columns => Math.Max(1, (int)((Bounds.fixedWidth + 4) / 48));
    private double Cell => Bounds.fixedWidth / Columns;
    public override bool Focusable => true;
    public void SetItems(List<EconomyItemView> value, bool preserveScroll = false)
    {
        // Appending catalog batches must not recreate unchanged visible GPU previews.
        foreach (int index in visible.Keys.Where(i => i >= value.Count || i >= items.Count || !ReferenceEquals(items[i], value[i])).ToArray())
        { ClearHover(); visible[index].Dispose(); visible.Remove(index); }
        items = value;
        if (!preserveScroll) scroll = 0;
        RefreshScroll();
    }
    public void RefreshScroll()
    {
        Bounds.CalcWorldBounds();
        Scrollbar.SetContentHeights((float)Bounds.InnerHeight,
            (float)Math.Max(Bounds.InnerHeight, GuiElement.scaled(((items.Count + Columns - 1) / Columns) * Cell)));
        Scrollbar.CurrentYPosition = (float)Math.Clamp(scaled(scroll), 0,
            Math.Max(0, scaled(((items.Count + Columns - 1) / Columns) * Cell) - Bounds.InnerHeight)); Scrollbar.TriggerChanged();
    }
    public void ScrollTo(float value) { scroll = value / Math.Max(.25, RuntimeEnv.GUIScale); ClearHover(); }
    private void EnsureVisible()
    {
        int first = Math.Max(0, (int)(scroll / Cell) * Columns), end = Math.Min(items.Count,
            ((int)((scroll + Bounds.fixedHeight) / Cell) + 1) * Columns);
        foreach (int index in visible.Keys.Where(i => i < first || i >= end).ToArray())
        { visible[index].Dispose(); visible.Remove(index); }
        for (int i = first; i < end; i++)
        {
            if (!visible.TryGetValue(i, out var slot))
            {
                var b = ElementBounds.Fixed(i % Columns * Cell, i / Columns * Cell - scroll, Cell - 4, Cell - 4); b.ParentBounds = Bounds;
                visible[i] = slot = new(api, items[i], b);
            }
            slot.Bounds.fixedY = i / Columns * Cell - scroll; slot.Bounds.CalcWorldBounds();
        }
    }
    public override void RenderInteractiveElements(float dt)
    {
        EnsureVisible();
        foreach (var (index, slot) in visible)
        {
            slot.SetSelected(IsSelected?.Invoke(items[index]) == true);
            slot.RenderInteractiveElements(dt);
        }
    }
    private int At(int x, int y)
    {
        if (!IsPositionInside(x, y)) return -1;
        double scale = Math.Max(.25, RuntimeEnv.GUIScale);
        int col = (int)((x - Bounds.renderX) / scale / Cell), row = (int)(((y - Bounds.renderY) / scale + scroll) / Cell);
        int index = row * Columns + col;
        return col >= 0 && col < Columns && index >= 0 && index < items.Count ? index : -1;
    }
    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    { int index = At(args.X, args.Y); if (index >= 0 && args.Button == EnumMouseButton.Left) { args.Handled = true; select(items[index]); } }
    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        EnsureVisible(); int index = At(args.X, args.Y);
        var next = index >= 0 && visible.TryGetValue(index, out var slot) ? slot.DisplaySlot : null;
        if (ReferenceEquals(next, hover)) return;
        ClearHover(); if (next is { Empty: false }) { hover = next; api.Input.TriggerOnMouseEnterSlot(next); }
    }
    public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
    {
        if (!IsPositionInside(api.Input.MouseX, api.Input.MouseY)) return;
        Scrollbar.CurrentYPosition = (float)Math.Clamp(Scrollbar.CurrentYPosition - args.deltaPrecise * scaled(48), 0,
            Math.Max(0, scaled(((items.Count + Columns - 1) / Columns) * Cell) - Bounds.InnerHeight));
        Scrollbar.TriggerChanged(); args.SetHandled();
    }
    private void ClearHover() { if (hover != null) api.Input.TriggerOnMouseLeaveSlot(hover); hover = null; }
    public override void Dispose() { ClearHover(); foreach (var slot in visible.Values) slot.Dispose(); visible.Clear(); base.Dispose(); }
}
