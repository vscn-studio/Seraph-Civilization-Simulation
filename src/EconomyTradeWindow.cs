using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace SeraphCivilizationSimulation;

public sealed class EconomyTradeWindow(ICoreClientAPI api, Action<EconomyRequest> send) : GuiDialog(api)
{
    private EconomyTradeView snapshot = new();
    private bool dirty, closingFromServer, pointerDown, disposed, resetScroll;
    private double width, height;
    private string fingerprint = "";
    private int tab;
    private readonly HashSet<string> selectedOffers = [];
    private string paymentFilter = "";
    public override string ToggleKeyCombinationCode => null!;
    public override bool UnregisterOnClose => true;
    private string L(string key, params object[] args) => Lang.Get("vsscsmod:ui." + key, args);
    public void Update(EconomyTradeView value)
    {
        if (disposed) return;
        if (value.Closed)
        {
            if (value.Id == snapshot.Id) { closingFromServer = true; TryClose(); closingFromServer = false; }
            return;
        }
        if (value.Id != snapshot.Id) { selectedOffers.Clear(); paymentFilter = ""; tab = 0; resetScroll = true; }
        string current = EconomyGuiFingerprint.Of(value);
        if (current != fingerprint) dirty = true;
        fingerprint = current; snapshot = value;
        if (!IsOpened()) { Compose(); TryOpen(); }
    }
    public override void OnGuiClosed()
    {
        (SingleComposer?.GetElement("economy-content") as EconomyGuiContainer)?.ClearHover();
        if (!closingFromServer && snapshot.Id.Length > 0) send(new() { Action = "cancel", Id = snapshot.Id });
        pointerDown = false; base.OnGuiClosed();
    }
    public override void OnMouseDown(MouseEvent args) { pointerDown = true; base.OnMouseDown(args); }
    public override void OnMouseUp(MouseEvent args) { base.OnMouseUp(args); pointerDown = false; }
    public override void OnRenderGUI(float dt)
    {
        if (!IsOpened() || disposed) return;
        var size = Size();
        if (!pointerDown && (dirty || size.Width != width || size.Height != height)) Compose();
        base.OnRenderGUI(dt);
    }
    private (double Width, double Height) Size() => (Math.Clamp(capi.Render.FrameWidth / Math.Max(.25, RuntimeEnv.GUIScale) - 48, 280, 840),
        Math.Clamp(capi.Render.FrameHeight / Math.Max(.25, RuntimeEnv.GUIScale) - 80, 240, 540));
    private void Compose()
    {
        var dimensions = Size(); width = dimensions.Width; height = dimensions.Height;
        float scroll = !resetScroll && SingleComposer?.GetElement("scrollbar") is GuiElementScrollbar old ? old.CurrentYPosition : 0;
        resetScroll = false;
        var bounds = ElementBounds.Fixed(0, 0, width, height).WithAlignment(EnumDialogArea.CenterMiddle);
        var hovered = (SingleComposer?.GetElement("economy-content") as EconomyGuiContainer)?.DetachHover();
        ClearComposers();
        var composer = capi.Gui.CreateCompo("vscncs-trade", bounds)
            .AddStaticCustomDraw(ElementBounds.Fill, (ctx, _, b) =>
            {
                ctx.SetSourceRGB(GuiStyle.DialogStrongBgColor[0], GuiStyle.DialogStrongBgColor[1], GuiStyle.DialogStrongBgColor[2]);
                GuiElement.RoundRectangle(ctx, b.drawX, b.drawY, b.InnerWidth, b.InnerHeight, GuiStyle.DialogBGRadius); ctx.Fill();
            }).AddShadedDialogBG(ElementBounds.Fill, alpha: 1)
            .AddDialogTitleBar(L("economy-trade-with", snapshot.Partner), () => TryClose());
        SingleComposer = composer;
        var tabFont = CairoFont.WhiteDetailText();
        composer.AddHorizontalTabs([
            new GuiTab { Name = L("economy-goods-trade"), DataInt = 0 },
            new GuiTab { Name = L("economy-task-trade"), DataInt = 1 }],
            ElementBounds.Fixed(0, -25, width, 25), index =>
            {
                if (snapshot.Proposed || tab == index) return;
                tab = index; selectedOffers.Clear(); paymentFilter = ""; dirty = resetScroll = true;
            }, tabFont, tabFont.Clone().WithColor(GuiStyle.ActiveButtonTextColor), "trade-tabs");
        var tabs = composer.GetHorizontalTabs("trade-tabs");
        tabs.activeElement = tab; tabs.Enabled = !snapshot.Proposed;
        var actions = new List<(string Key, string Label, Action Click, bool Enabled)>();
        var list = new EconomyGuiList(capi, composer, ElementBounds.Fixed(20, 48, width - 40, height - 114));
        if (snapshot.Proposed)
        {
            list.Text(snapshot.Terms, true);
            if (snapshot.Exchange.Count > 0) list.Exchange(snapshot.Exchange, snapshot.Coins);
            else if (snapshot.Coins.FirstOrDefault(c => c.Key == snapshot.Coin) is { } currency)
                list.Row(currency.Name, L("economy-designer", currency.Issuers), currency);
            list.Text(L("economy-confirm-help"), detail: true);
            list.Text(L("economy-confirm-status", L(snapshot.YouConfirmed ? "economy-confirmed" : "economy-waiting"), L(snapshot.OtherConfirmed ? "economy-confirmed" : "economy-waiting")));
            actions.Add(("confirm", L("economy-confirm"), () => send(new() { Action = "confirm", Id = snapshot.Id, Revision = snapshot.Revision }), !snapshot.YouConfirmed));
        }
        else
        {
            if (tab == 0)
            {
                var selected = BuildGoods(list);
                actions.Add(("buy", L("economy-buy"), () =>
                {
                    if (selected.Count > 0) send(new() { Action = "buy", Id = snapshot.Id, Revision = snapshot.Revision,
                        Selections = selected.Select(o => new EconomyOfferSelection { Id = o.Id, Revision = o.Revision }).ToList() });
                }, selected.Count > 0 && CanPay(Payments(selected))));
            }
            else BuildTasks(list);
        }
        actions.Add(("cancel", L("cancel"), () => TryClose(), true));
        list.Finish(); BuildFooter(composer, actions);
        composer.Compose(false);
        var bar = (GuiElementAutoScrollbar)composer.GetScrollbar("scrollbar");
        double viewport = list.Container.Bounds.ParentBounds.InnerHeight;
        bar.SetContentHeights((float)viewport, (float)Math.Max(viewport, list.Container.Bounds.OuterHeight));
        bar.CurrentYPosition = Math.Clamp(scroll, 0, (float)Math.Max(0, list.Container.Bounds.OuterHeight - viewport)); bar.TriggerChanged();
        (list.Container as EconomyGuiContainer)?.RestoreHover(hovered);
        dirty = false;
    }
    private static string PaymentKey(EconomyExchangeItem entry) => entry.Coin.Length > 0 ? entry.Coin : entry.Key;
    private static IEnumerable<EconomyExchangeItem> Price(EconomyOfferView offer) => offer.Exchange.Count > 0
        ? offer.Exchange : [new() { Coin = offer.Coin, Quantity = offer.Price }];
    private static List<EconomyExchangeItem> Payments(IEnumerable<EconomyOfferView> offers) => offers.SelectMany(Price)
        .GroupBy(PaymentKey).Select(g => new EconomyExchangeItem { Key = g.First().Key, Coin = g.First().Coin,
            Quantity = (int)Math.Min(int.MaxValue, g.Sum(e => (long)e.Quantity)), Display = g.First().Display }).ToList();
    private bool CanPay(IEnumerable<EconomyExchangeItem> payment) => payment.All(e => snapshot.PaymentStock.GetValueOrDefault(PaymentKey(e)) >= e.Quantity);
    private List<EconomyOfferView> BuildGoods(EconomyGuiList list)
    {
        var goods = snapshot.Offers.Where(o => o.Ready && !o.IsTask).ToList();
        selectedOffers.IntersectWith(goods.Select(o => o.Id));
        if (paymentFilter.Length > 0 && !goods.Any(o => Price(o).Any(e => PaymentKey(e) == paymentFilter))) paymentFilter = "";
        var visible = goods.Where(o => paymentFilter.Length == 0 || Price(o).Any(e => PaymentKey(e) == paymentFilter)).ToList();
        var selected = goods.Where(o => selectedOffers.Contains(o.Id)).ToList();
        // With no selection the right grid is the merchant's complete price catalog.
        var payment = Payments(selected.Count > 0 ? selected : goods);
        const double arrow = 26, gap = 8, cellGap = 6;
        double panelWidth = (list.Width - arrow - gap * 2) / 2;
        double right = panelWidth + arrow + gap * 2, headerTop = list.Y;
        list.Text(L("economy-trader-goods"), detail: true, width: panelWidth);
        double top = list.Y;
        list.Y = headerTop;
        list.Text(L("economy-trader-payment"), detail: true, x: right, width: panelWidth);
        top = Math.Max(top, list.Y);
        int columns = Math.Max(1, (int)Math.Ceiling((panelWidth + cellGap) / (48 + cellGap)));
        double size = (panelWidth - (columns - 1) * cellGap) / columns;
        double gridHeight = Math.Max(size, height - 115 - top);
        int fittingRows = Math.Max(1, (int)Math.Floor((gridHeight + cellGap) / (size + cellGap)));
        int rows = Math.Max(fittingRows, (Math.Max(visible.Count, payment.Count) + columns - 1) / columns);
        double rowStep = rows == fittingRows && rows > 1 ? (gridHeight - size) / (rows - 1) : size + cellGap;
        list.Container.unscaledCellSpacing = 0;
        // Keep the arrow centered in the visible grids even while their catalogs scroll.
        SingleComposer.AddStaticElement(new GuiElementEconomyArrow(capi,
            ElementBounds.Fixed(20 + panelWidth + gap, 48 + top + (gridHeight - size) / 2, arrow, size)), "trade-arrow");
        for (int i = 0; i < rows * columns; i++)
        {
            double x = i % columns * (size + cellGap), y = top + i / columns * rowStep;
            if (i < visible.Count)
            {
                var offer = visible[i];
                list.Slot(offer.DisplayItem, x, y, size, () =>
                {
                    if (!selectedOffers.Add(offer.Id)) selectedOffers.Remove(offer.Id);
                    dirty = true;
                }, offer.Quantity, selectedOffers.Contains(offer.Id));
            }
            else list.Slot(null, x, y, size);
            if (i < payment.Count)
            {
                var entry = payment[i]; string key = PaymentKey(entry);
                bool enough = snapshot.PaymentStock.GetValueOrDefault(key) >= entry.Quantity;
                Action filter = () => { paymentFilter = paymentFilter == key ? "" : key; dirty = true; };
                if (entry.Coin.Length > 0 && snapshot.Coins.FirstOrDefault(c => c.Key == entry.Coin) is { } coin)
                    list.CoinSlot(coin, right + x, y, size, entry.Quantity, filter, listed: enough, selected: paymentFilter == key);
                else list.Slot(entry.Display, right + x, y, size, filter, entry.Quantity, paymentFilter == key, listed: enough);
            }
            else list.Slot(null, right + x, y, size);
        }
        list.Y = top + (rows - 1) * rowStep + size;
        return selected;
    }
    private void BuildTasks(EconomyGuiList list)
    {
        var tasks = snapshot.Offers.Where(o => o.Ready && o.IsTask).ToList();
        if (tasks.Count == 0) list.Text(L("economy-no-trade"));
        foreach (var offer in tasks)
        {
            double top = list.Y;
            double actionWidth = Math.Clamp(list.Width * .27, 82, 132), textWidth = list.Width - actionWidth - 14;
            list.Text(offer.Name, width: textWidth);
            list.Text(offer.Content.Length > 0 ? offer.Content : L("economy-task-no-content"), detail: true, width: textWidth);
            list.Text(L("economy-reward-items"), detail: true, width: textWidth);
            const double gap = 6;
            int columns = Math.Max(1, (int)Math.Ceiling((textWidth + gap) / 54));
            double size = (textWidth - (columns - 1) * gap) / columns;
            var reward = Price(offer).ToList();
            for (int i = 0; i < reward.Count; i++)
            {
                var entry = reward[i]; double x = i % columns * (size + gap), y = list.Y + i / columns * (size + gap);
                if (entry.Coin.Length > 0 && snapshot.Coins.FirstOrDefault(c => c.Key == entry.Coin) is { } coin)
                    list.CoinSlot(coin, x, y, size, entry.Quantity);
                else list.Slot(entry.Display, x, y, size, count: entry.Quantity);
            }
            list.Y += Math.Max(1, (reward.Count + columns - 1) / columns) * (size + gap);
            double buttonY = top;
            AddAction(L(offer.Contract ? "economy-task-finish-short" : "economy-task-accept-short"), false);
            if (offer.Contract) AddAction(L("economy-task-abandon-short"), true);
            list.Y = Math.Max(list.Y, buttonY); list.EndRow();
            void AddAction(string label, bool abandon)
            {
                var font = CairoFont.SmallButtonText(EnumButtonStyle.Small);
                double measured = font.GetTextExtents(label).Width / Math.Max(.25, RuntimeEnv.GUIScale);
                if (measured > actionWidth - 16) font.UnscaledFontsize *= (actionWidth - 16) / measured;
                var button = new GuiElementTextButton(capi, label, font, font.Clone().WithColor(GuiStyle.ActiveButtonTextColor), () =>
                {
                    send(new() { Action = "propose", Id = snapshot.Id, Revision = snapshot.Revision,
                        Item = offer.Id, IsTask = offer.Contract, Text = abandon ? "cancel-task" : "" }); return true;
                }, ElementBounds.Fixed(textWidth + 14, buttonY, actionWidth - 16, 0).WithFixedPadding(8, 5), EnumButtonStyle.Small);
                button.BeforeCalcBounds(); list.Container.Add(button);
                buttonY += button.Bounds.fixedHeight + 10 + 8;
            }
        }
        if (tasks.Count > 0) list.Text(L("economy-task-actions-help"), detail: true);
    }
    private void BuildFooter(GuiComposer composer, List<(string Key, string Label, Action Click, bool Enabled)> actions)
    {
        const double gap = 8, padding = 8;
        double fontScale = 1;
        var buttons = Create(1);
        double available = width - 40, rowWidth = RowWidth();
        while (rowWidth > available)
        {
            // Native text bounds include a fixed inset and round glyph widths to pixels.
            // Recheck the measured row after scaling rather than assuming a linear width.
            double chrome = actions.Count * (padding * 2 + 1) + (actions.Count - 1) * gap;
            fontScale *= Math.Clamp((available - chrome - 2) / Math.Max(1, rowWidth - chrome), .1, .95);
            foreach (var button in buttons) button.Dispose();
            buttons = Create(fontScale); rowWidth = RowWidth();
        }
        double x = (width - rowWidth) / 2;
        double rowHeight = buttons.Max(b => b.Bounds.fixedHeight + 2 * b.Bounds.fixedPaddingY);
        for (int i = 0; i < buttons.Length; i++)
        {
            var button = buttons[i];
            button.Bounds.fixedX = x; button.Bounds.fixedY = height - 20 - rowHeight;
            button.Enabled = actions[i].Enabled;
            composer.AddInteractiveElement(button, "trade-action-" + actions[i].Key);
            x += button.Bounds.fixedWidth + 2 * button.Bounds.fixedPaddingX + gap;
        }
        double RowWidth() => buttons.Sum(b => b.Bounds.fixedWidth + 2 * b.Bounds.fixedPaddingX) + (buttons.Length - 1) * gap;
        GuiElementTextButton[] Create(double scale) => actions.Select(action =>
        {
            var font = CairoFont.SmallButtonText(EnumButtonStyle.Small); font.UnscaledFontsize *= scale;
            var button = new GuiElementTextButton(capi, action.Label, font, font.Clone().WithColor(GuiStyle.ActiveButtonTextColor),
                () => { action.Click(); return true; }, ElementBounds.Fixed(0, 0, 0, 0).WithFixedPadding(padding, 5), EnumButtonStyle.Small);
            button.BeforeCalcBounds(); return button;
        }).ToArray();
    }
    public override void Dispose() { if (disposed) return; TryClose(); disposed = true; base.Dispose(); }
}
