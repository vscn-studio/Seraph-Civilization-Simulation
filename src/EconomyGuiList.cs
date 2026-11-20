using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.Common;

namespace SeraphCivilizationSimulation;

// Native controls share the same clipped, scrollable viewport as the handbook.
internal sealed class EconomyGuiList
{
    private readonly ICoreClientAPI api;
    private readonly GuiElementContainer container;
    public GuiElementContainer Container => container;
    public double Width { get; }
    public double Y { get; set; } = 4;
    public GuiElementEconomyItem Slot(EconomyItemView? item, double x, double y, double size = 48, Action? click = null, int? count = null, bool selected = false, bool task = false, bool? listed = null, string? quantity = null, Action? clear = null)
    {
        var slot = new GuiElementEconomyItem(api, item, ElementBounds.Fixed(x, y, size, size), click, count, selected, task: task, listed: listed, quantity: quantity, clear: clear);
        container.Add(slot); return slot;
    }
    public void CoinSlot(EconomyCoinView coin, double x, double y, double size = 48, int? count = null, Action? click = null, string? quantity = null, bool? listed = null, bool selected = false)
        => container.Add(new GuiElementEconomyCoin(api, coin, ElementBounds.Fixed(x, y, size, size), count, click, quantity, listed, selected));
    public void CurrencySelectors(string[] values, string[] names, string from, string to, Action<string> changeFrom, Action<string> changeTo)
    {
        CompleteRow();
        const double arrow = 26, gap = 8;
        double width = (Width - 12 - arrow - gap * 2) / 2;
        DropAt(from, 0, changeFrom); DropAt(to, width + arrow + gap * 2, changeTo);
        container.Add(new GuiElementEconomyArrow(api, ElementBounds.Fixed(width + gap, Y, arrow, 32)));
        Y += 48;
        void DropAt(string selected, double x, Action<string> changed) => container.Add(new GuiElementDropDown(api, values, names,
            Math.Max(0, Array.IndexOf(values, selected)), (code, _) => changed(code), ElementBounds.Fixed(x, Y, width, 32), CairoFont.WhiteSmallText(), false));
    }
    public void CurrencyConversion(EconomyCoinView from, EconomyCoinView to, decimal rate)
    {
        CompleteRow();
        const double arrow = 26, gap = 8;
        double size = Math.Min(64, (Width - 12 - arrow - gap * 2) / 2);
        CoinSlot(from, 0, Y, size, quantity: "1");
        container.Add(new GuiElementEconomyArrow(api, ElementBounds.Fixed(size + gap, Y, arrow, size)));
        CoinSlot(to, size + arrow + gap * 2, Y, size, quantity: EconomyRateCalculator.FormatQuantity(rate));
        Y += size + 8;
    }
    public void AddSlotButton(double x, double y, double size, Action click)
        => container.Add(new GuiElementEconomyItem(api, null, ElementBounds.Fixed(x, y, size, size), click, plus: true));
    public void Exchange(IEnumerable<EconomyExchangeItem> entries, IEnumerable<EconomyCoinView> coins)
    {
        CompleteRow();
        int columns = Math.Max(1, (int)((Width + 6) / 54));
        double size = (Width - (columns - 1) * 6) / columns, top = Y; int index = 0;
        foreach (var entry in entries)
        {
            double x = index % columns * (size + 6), y = top + index / columns * (size + 6);
            if (entry.Coin.Length > 0 && coins.FirstOrDefault(c => c.Key == entry.Coin) is { } coin)
                CoinSlot(coin, x, y, size, entry.Quantity);
            else Slot(entry.Display, x, y, size, count: entry.Quantity);
            index++;
        }
        if (index > 0) Y += ((index + columns - 1) / columns) * (size + 6) + 6;
    }
    private double actionInset;
    private double rowBottom;
    private bool inRow;
    private const double PictureSize = 48;
    private void CompleteRow()
    {
        if (!inRow) return;
        Y = Math.Max(Y, rowBottom + 4);
        inRow = false; actionInset = 0;
    }
    private double BeginRow(bool picture)
    {
        CompleteRow(); inRow = true;
        actionInset = picture ? PictureSize + 10 : 0;
        rowBottom = Y + (picture ? PictureSize : 0);
        return Y;
    }
    public EconomyGuiList(ICoreClientAPI api, GuiComposer composer, ElementBounds view)
    {
        this.api = api; Width = view.fixedWidth - 28;
        var clip = ElementBounds.Fixed(view.fixedX, view.fixedY, Width, view.fixedHeight);
        var body = ElementBounds.Fixed(0, 0, Width, view.fixedHeight);
        composer.BeginClip(clip).AddInteractiveElement(new EconomyGuiContainer(api, body), "economy-content");
        container = composer.GetContainer("economy-content"); container.unscaledCellSpacing = 12;
        composer.EndClip().AddInteractiveElement(new GuiElementAutoScrollbar(api, value =>
        { body.fixedY = -value / Math.Max(.25, RuntimeEnv.GUIScale); body.CalcWorldBounds(); },
            ElementBounds.Fixed(view.fixedX + view.fixedWidth - 16, view.fixedY, 16, view.fixedHeight)), "scrollbar");
    }
    public static double Footer(ICoreClientAPI api, GuiComposer composer, ElementBounds view,
        params (string Key, string Label, Action Click)[] actions)
        => Footer(api, composer, view, null, actions);
    public static double Footer(ICoreClientAPI api, GuiComposer composer, ElementBounds view,
        (string Label, bool On, Action<bool> Change)? toggle,
        params (string Key, string Label, Action Click)[] actions)
    {
        var font = CairoFont.SmallButtonText(EnumButtonStyle.Small);
        const double gap = 8, padding = 8, checkSize = 24;
        double toggleSpace = toggle.HasValue ? checkSize + gap * 2 : 0;
        double available = view.fixedWidth - toggleSpace - gap * (actions.Length - 1) - actions.Length * 2 * padding;
        double textWidth = actions.Sum(a => font.GetTextExtents(a.Label).Width / Math.Max(.25, RuntimeEnv.GUIScale))
            + (toggle.HasValue ? font.GetTextExtents(toggle.Value.Label).Width / Math.Max(.25, RuntimeEnv.GUIScale) : 0);
        if (textWidth > available) font.UnscaledFontsize *= available / textWidth;
        var buttons = actions.Select(a => new GuiElementTextButton(api, a.Label, font,
            font.Clone().WithColor(GuiStyle.ActiveButtonTextColor), () => { a.Click(); return true; },
            ElementBounds.Fixed(0, 0, 0, 0).WithFixedPadding(padding, 5), EnumButtonStyle.Small)).ToArray();
        foreach (var button in buttons) button.BeforeCalcBounds();
        double height = Math.Max(checkSize, buttons.Max(b => b.Bounds.fixedHeight + 10)), x = view.fixedX;
        double top = view.fixedY + view.fixedHeight - height;
        if (toggle is { } check)
        {
            composer.AddInteractiveElement(new GuiElementSwitch(api, check.Change,
                ElementBounds.Fixed(x, top + (height - checkSize) / 2, checkSize, checkSize), checkSize) { On = check.On },
                "economy-footer-permit");
            double labelWidth = font.GetTextExtents(check.Label).Width / Math.Max(.25, RuntimeEnv.GUIScale);
            double labelHeight = font.GetFontExtents().Height / Math.Max(.25, RuntimeEnv.GUIScale);
            composer.AddStaticText(check.Label, font, ElementBounds.Fixed(x + checkSize + gap,
                top + (height - labelHeight) / 2, labelWidth + 1, labelHeight), "economy-footer-permit-label");
            x += toggleSpace + labelWidth;
        }
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].Bounds.fixedX = x; buttons[i].Bounds.fixedY = top;
            composer.AddInteractiveElement(buttons[i], "economy-footer-" + actions[i].Key);
            x += buttons[i].Bounds.fixedWidth + padding * 2 + gap;
        }
        return height + 12;
    }
    public double Text(string text, bool heading = false, bool detail = false, double x = 0, double width = -1)
    {
        CompleteRow();
        var font = heading ? CairoFont.WhiteSmallishText() : detail ? CairoFont.WhiteDetailText() : CairoFont.WhiteSmallText();
        width = width < 0 ? Width - x - 4 : width;
        double h = Math.Max(22, api.Gui.Text.GetMultilineTextHeight(font, text, GuiElement.scaled(width)) / Math.Max(.25, RuntimeEnv.GUIScale) + 2);
        container.Add(new GuiElementWrappedText(api, text, ElementBounds.Fixed(x, Y, width, h), font));
        Y += h + (heading ? 12 : 6); return h;
    }
    private double TextAt(string text, double x, double y, double width, bool detail = false)
    {
        var font = detail ? CairoFont.WhiteDetailText() : CairoFont.WhiteSmallText();
        double height = Math.Max(20, api.Gui.Text.GetMultilineTextHeight(font, text, GuiElement.scaled(width)) / Math.Max(.25, RuntimeEnv.GUIScale) + 2);
        container.Add(new GuiElementWrappedText(api, text, ElementBounds.Fixed(x, y, width, height), font));
        return height;
    }
    public void Row(string title, string detail, EconomyCoinView? coin = null, string summary = "")
    {
        double top = BeginRow(coin != null), x = actionInset;
        if (coin != null) container.Add(new GuiElementEconomyCoin(api, coin, ElementBounds.Fixed(0, top, PictureSize, PictureSize)));
        double width = Width - x - 4;
        double height = TextAt(title, x, top, width);
        string info = string.Join(" · ", new[] { detail, summary }.Where(s => s.Length > 0));
        if (info.Length > 0) height += 2 + TextAt(info, x, top + height + 2, width, true);
        Y = top + height + 4;
    }
    public void OfferRow(EconomyOfferView offer, EconomyCoinView? coin, string detail)
    {
        double top = BeginRow(!offer.IsTask || offer.DisplayItem != null), x = actionInset;
        if (!offer.IsTask || offer.DisplayItem != null) container.Add(new GuiElementEconomyItem(api, offer.DisplayItem,
            ElementBounds.Fixed(0, top, PictureSize, PictureSize), count: offer.Quantity, listed: offer.Ready));
        double textWidth = Width - x - 4;
        string name = offer.Name + (offer.DisplayItem != null && !offer.IsTask && offer.Available > 0
            ? Lang.Get("vsscsmod:ui.economy-carried-name", offer.Available) : "");
        double h = TextAt(name, x, top, textWidth);
        if (detail.Length > 0) h += TextAt(detail, x, top + h + 2, textWidth, true) + 2;
        if (offer.Content.Length > 0) h += TextAt(offer.Content, x, top + h + 4, textWidth, true) + 4;
        if (offer.Exchange.Count > 0)
        {
            double cell = 36, sx = x, sy = top + h + 4;
            foreach (var entry in offer.Exchange)
            {
                if (sx + cell > Width) { sx = x; sy += cell + 4; }
                if (entry.Coin == offer.Coin && coin != null) CoinSlot(coin, sx, sy, cell, entry.Quantity);
                else Slot(entry.Display, sx, sy, cell, count: entry.Quantity);
                sx += cell + 4;
            }
            Y = sy + cell + 4; return;
        }
        double paymentY = top + h + 4;
        if (coin != null) CoinSlot(coin, x, paymentY, 36, offer.Price);
        Y = paymentY + 40;
    }
    public void RowDetail(string text)
    {
        Y += TextAt(text, actionInset, Y, Width - actionInset - 4, true) + 4;
    }
    public void TribeRow(EconomyTribeView tribe, IEnumerable<EconomyCoinView> coins, IEnumerable<EconomyRateView> rates)
    {
        actionInset = 0;
        Text(tribe.Name, heading: true);
        bool any = false;
        foreach (var coin in coins)
        {
            any = true;
            Row(coin.Name, Lang.Get("vsscsmod:ui.economy-world-stats-short", coin.Minted), coin);
            Y += 8;
        }
        if (!any) Text(Lang.Get("vsscsmod:ui.economy-no-tribe-coins"), detail: true);
        foreach (var rate in rates) Text($"1 {rate.Coin} = {rate.Rate} {rate.ReferenceCoin}", detail: true);
        EndRow();
    }
    public GuiElementTextInput? RowActions(string? toggleLabel, bool on, Action<bool>? change,
        string? value, Action<string>? valueChange, params (string Label, Action Click)[] actions)
    {
        double start = Math.Min(actionInset, Math.Max(0, Width - 96)), x = start, height = 0;
        GuiElementTextInput? input = null;
        void Place(double width, double h, Action<double, double> add)
        {
            if (x > start && x + width > Width) { Y += height + 6; x = start; height = 0; }
            add(x, Y); x += width + 8; height = Math.Max(height, h);
        }
        if (toggleLabel != null && change != null)
        {
            double labelWidth = Math.Min(Width - start - 30, CairoFont.WhiteDetailText().GetTextExtents(toggleLabel).Width / Math.Max(.25, RuntimeEnv.GUIScale) + 6);
            Place(labelWidth + 30, 28, (px, py) =>
            {
                container.Add(new GuiElementSwitch(api, change, ElementBounds.Fixed(px, py + 2, 24, 24), 24) { On = on });
                TextAt(toggleLabel, px + 30, py + 4, labelWidth, true);
            });
        }
        if (value != null && valueChange != null)
            Place(Math.Min(124, Width - start), 32, (px, py) =>
            {
                TextAt(Lang.Get("vsscsmod:ui.economy-rate-label"), px, py + 5, 38, true);
                input = new GuiElementTextInput(api, ElementBounds.Fixed(px + 40, py, Math.Min(72, Width - start - 52), 32), valueChange, CairoFont.TextInput());
                input.OnTryTextChangeText = lines => lines.Sum(line => line.Length) <= 36;
                container.Add(input); input.SetValue(value);
            });
        foreach (var action in actions)
        {
            var font = CairoFont.SmallButtonText(EnumButtonStyle.Small);
            var button = new GuiElementTextButton(api, action.Label, font, font.Clone().WithColor(GuiStyle.ActiveButtonTextColor),
                () => { action.Click(); return true; }, ElementBounds.Fixed(0, 0, 0, 0).WithFixedPadding(8, 4), EnumButtonStyle.Small);
            button.BeforeCalcBounds();
            Place(button.Bounds.fixedWidth + 16, button.Bounds.fixedHeight + 8, (px, py) =>
            { button.Bounds.fixedX = px; button.Bounds.fixedY = py; container.Add(button); });
        }
        Y += height + 4;
        return input;
    }
    public void EndRow()
    {
        CompleteRow();
        container.Add(new GuiElementEconomyRule(api, ElementBounds.Fixed(0, Y + 2, Width, 1)));
        Y += 10; actionInset = 0;
    }
    public void Toggle(string label, bool on, Action<bool> change)
    {
        CompleteRow();
        double top = Y;
        container.Add(new GuiElementSwitch(api, change, ElementBounds.Fixed(0, top, 24, 24), 24) { On = on });
        Text(label, x: 34); Y = Math.Max(Y, top + 36);
    }
    public void Buttons(params (string Label, Action Click)[] actions)
    {
        CompleteRow();
        double x = 0, h = 0;
        foreach (var action in actions)
        {
            var font = CairoFont.SmallButtonText(EnumButtonStyle.Small);
            var button = new GuiElementTextButton(api, action.Label, font, font.Clone().WithColor(GuiStyle.ActiveButtonTextColor),
                () => { action.Click(); return true; }, ElementBounds.Fixed(0, 0, 0, 0).WithFixedPadding(10, 5), EnumButtonStyle.Small);
            button.BeforeCalcBounds(); double w = button.Bounds.fixedWidth + 20, height = button.Bounds.fixedHeight + 10;
            if (x > 0 && x + w > Width) { Y += h + 8; x = h = 0; }
            button.Bounds.fixedX = x; button.Bounds.fixedY = Y; container.Add(button);
            x += w + 8; h = Math.Max(h, height);
        }
        Y += h + 12;
    }
    public GuiElementTextInput Input(string value, Action<string> change, int limit = 100, double width = -1)
    {
        CompleteRow();
        var input = new GuiElementTextInput(api, ElementBounds.Fixed(0, Y, Math.Max(40, (width < 0 ? Width : width) - 12), 32), change, CairoFont.TextInput());
        input.OnTryTextChangeText = lines => lines.Sum(l => l.Length) <= limit;
        container.Add(input); input.SetValue(value); Y += 42; return input;
    }
    public GuiElementTextInput Field(string label, string value, Action<string> change, int limit = 100, double x = 0)
    {
        CompleteRow();
        double labelWidth = Math.Min(110, (Width - x) * .38);
        double h = TextAt(label, x, Y + 5, labelWidth - 8, true);
        var input = new GuiElementTextInput(api, ElementBounds.Fixed(x + labelWidth, Y, Width - x - labelWidth - 12, 32), change, CairoFont.TextInput());
        input.OnTryTextChangeText = lines => lines.Sum(l => l.Length) <= limit;
        container.Add(input); input.SetValue(value); Y += Math.Max(32, h + 5) + 8; return input;
    }
    public GuiElementTextArea TextArea(string value, Action<string> change)
    {
        CompleteRow();
        var editor = new GuiElementEconomyTaskEditor(api, ElementBounds.Fixed(0, Y, Width - 20, 116), change);
        editor.Scrollbar = new GuiElementAutoScrollbar(api, value => editor.ScrollTo(value), ElementBounds.Fixed(Width - 16, Y, 16, 116));
        editor.OnTryTextChangeText = lines => lines.Sum(l => l.Length) <= 2000;
        container.Add(editor); container.Add(editor.Scrollbar);
        editor.Bounds.CalcWorldBounds(); editor.SetValue(value, false); editor.RefreshScroll();
        Y += 128; return editor;
    }
    public void Drop(string[] values, string[] names, string selected, Action<string> changed)
    {
        CompleteRow();
        if (values.Length == 0) { values = [""]; names = [Vintagestory.API.Config.Lang.Get("vsscsmod:ui.economy-choice-empty")]; }
        int index = Math.Max(0, Array.IndexOf(values, selected)); changed(values[index]);
        container.Add(new GuiElementDropDown(api, values, names, index, (code, _) => changed(code),
            ElementBounds.Fixed(0, Y, Width - 12, 32), CairoFont.WhiteSmallText(), false));
        Y += 44;
    }
    public void Finish()
    {
        CompleteRow();
        container.Tabbable = container.FirstTabbableElement != null;
        container.Add(new GuiElementEconomyRule(api, ElementBounds.Fixed(0, Y, 1, 1)));
    }
}

// Display-only native item rendering retains Coinage's own custom shape and design.
internal class GuiElementEconomyItem : GuiElement
{
    protected readonly ItemSlot slot;
    private byte[]? displayBytes;
    private string displayCode = "";
    private int backgroundTexture;
    private int clearTexture;
    private readonly Action? clear;
    internal bool CanClear => clear != null;
    private int count = 1;
    private readonly bool showQuantity;
    internal string QuantityText => quantity ?? count.ToString();
    private readonly string? quantity;
    private LoadedTexture? quantityTexture;
    private EconomyItemOverlay? statusOverlay;
    private readonly Action? click;
    private bool selected;
    private readonly bool plus;
    private readonly bool task;
    private readonly bool? listed;
    internal ItemSlot DisplaySlot { get { ResolveItem(); return slot; } }
    public void SetQuantity(int value)
    {
        if (count == value) return;
        count = value; if (!slot.Empty) slot.Itemstack.StackSize = value;
        quantityTexture?.Dispose(); quantityTexture = null;
    }
    protected GuiElementEconomyItem(ICoreClientAPI api, ElementBounds bounds) : base(api, bounds)
    { slot = new EconomyPreviewInventory(api)[0]; }
    public GuiElementEconomyItem(ICoreClientAPI api, EconomyItemView? item, ElementBounds bounds, Action? click = null, int? count = null, bool selected = false, bool plus = false, string? quantity = null, bool task = false, bool? listed = null, Action? clear = null) : this(api, bounds)
    {
        displayBytes = item?.Stack; displayCode = item?.Code ?? "";
        this.click = click; this.count = count ?? 1; showQuantity = count.HasValue || quantity != null; this.selected = selected; this.plus = plus; this.quantity = quantity;
        this.task = task; this.listed = listed; this.clear = clear;
    }
    public override bool Focusable => click != null || clear != null;
    internal void SetSelected(bool value)
    {
        if (selected == value) return;
        selected = value;
        if (backgroundTexture != 0) { api.Gui.DeleteTexture(backgroundTexture); backgroundTexture = 0; }
    }
    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        if (args.Button != EnumMouseButton.Left) return;
        if (clear != null && Math.Pow(args.X - (Bounds.renderX + Bounds.InnerWidth - scaled(9)), 2)
            + Math.Pow(args.Y - (Bounds.renderY + scaled(9)), 2) <= Math.Pow(scaled(8), 2))
        { args.Handled = true; clear(); return; }
        if (click != null) { args.Handled = true; click(); }
    }
    private void ResolveItem()
    {
        var bytes = displayBytes; displayBytes = null;
        if (bytes is not { Length: > 0 }) return;
        try
        {
            using var reader = new BinaryReader(new MemoryStream(bytes, false));
            var stack = new ItemStack(reader);
            if (stack.ResolveBlockOrItem(api.World)) { stack.StackSize = count; slot.Itemstack = stack; }
        }
        catch (Exception e) when (e is IOException or ArgumentException or IndexOutOfRangeException)
        { api.Logger.Warning("Seraph Civilization Simulation economy item preview could not be read: {0}", displayCode); }
    }
    public override void RenderInteractiveElements(float dt)
    {
        if (backgroundTexture == 0)
            backgroundTexture = GuiElementItemSlotGridBase.DrawSlotBackground(api, Bounds.InnerWidth, Bounds.InnerHeight,
                GuiStyle.DialogSlotBackColor, GuiStyle.DialogSlotFrontColor, selected || plus || task || listed.HasValue ? ctx =>
                {
                    ctx.SetSourceRGBA(1, .85, .45, 1); ctx.LineWidth = scaled(2);
                    if (selected) { double inset = scaled(listed.HasValue ? 6 : 2);
                        ctx.Rectangle(inset, inset, Bounds.InnerWidth - inset * 2, Bounds.InnerHeight - inset * 2); ctx.Stroke(); }
                    if (listed.HasValue)
                    {
                        if (listed.Value) ctx.SetSourceRGBA(.2, .85, .3, 1); else ctx.SetSourceRGBA(.95, .15, .12, 1);
                        ctx.LineWidth = scaled(2);
                        ctx.Rectangle(scaled(2), scaled(2), Bounds.InnerWidth - scaled(4), Bounds.InnerHeight - scaled(4)); ctx.Stroke();
                    }
                    if (plus) { double cx = Bounds.InnerWidth / 2, cy = Bounds.InnerHeight / 2, arm = Bounds.InnerWidth * .2;
                        ctx.MoveTo(cx - arm, cy); ctx.LineTo(cx + arm, cy); ctx.MoveTo(cx, cy - arm); ctx.LineTo(cx, cy + arm);
                        ctx.SetSourceRGBA(.16, .11, .07, 1); ctx.LineWidth = scaled(1.5); ctx.Stroke(); }
                    if (task) { ctx.SetSourceRGBA(.28, .21, .12, 1);
                        api.Gui.Icons.DrawPen(ctx, Bounds.InnerWidth * .2, Bounds.InnerHeight * .2, scaled(2), Bounds.InnerWidth * .55); }
                } : null);
        api.Render.Render2DTexturePremultipliedAlpha(backgroundTexture, Bounds.renderX, Bounds.renderY, (float)Bounds.InnerWidth, (float)Bounds.InnerHeight);
        ResolveItem();
        if (!slot.Empty)
        {
            api.Render.PushScissor(Bounds, stacking: true);
            try
            {
                api.Render.RenderItemstackToGui(slot, Bounds.renderX + Bounds.InnerWidth / 2,
                    Bounds.renderY + Bounds.InnerHeight / 2, 100,
                    (float)(Math.Min(Bounds.InnerWidth, Bounds.InnerHeight) * GuiElementPassiveItemSlot.unscaledItemSize / GuiElementPassiveItemSlot.unscaledSlotSize),
                    -1, dt, showStackSize: false);
            }
            finally { api.Render.PopScissor(); }
        }
        if (!slot.Empty)
        {
            statusOverlay ??= new EconomyItemOverlay(api, slot, Bounds);
            statusOverlay.RenderInteractiveElements(dt);
        }
        if (!slot.Empty && showQuantity)
        {
            if (quantityTexture == null)
            {
                quantityTexture = new(api);
                using var surface = new ImageSurface(Format.Argb32, Math.Max(1, (int)Math.Ceiling(Bounds.InnerWidth)), Math.Max(1, (int)Math.Ceiling(Bounds.InnerHeight)));
                using var ctx = new Context(surface);
                using var font = CairoFont.WhiteSmallText().WithFontSize((float)(GuiStyle.SmallishFontSize * Math.Min(1, Bounds.fixedWidth / 48))).WithWeight(FontWeight.Bold);
                string number = QuantityText; double margin = scaled(3);
                font.SetupContext(ctx); var extents = ctx.TextExtents(number);
                double fit = Math.Min(1, (Bounds.InnerWidth - margin * 2) / Math.Max(1, extents.Width));
                font.UnscaledFontsize *= fit; font.SetupContext(ctx); extents = ctx.TextExtents(number);
                ctx.MoveTo(Bounds.InnerWidth - margin - extents.XBearing - extents.Width,
                    Bounds.InnerHeight - margin - extents.YBearing - extents.Height - (statusOverlay?.HasOverlay == true ? scaled(12) : 0));
                ctx.TextPath(number); ctx.SetSourceRGBA(0, 0, 0, 1); ctx.LineWidth = scaled(1.5); ctx.StrokePreserve();
                ctx.SetSourceRGBA(1, 1, 1, 1); ctx.Fill();
                api.Gui.LoadOrUpdateCairoTexture(surface, false, ref quantityTexture);
            }
            api.Render.Render2DTexturePremultipliedAlpha(quantityTexture.TextureId, Bounds.renderX, Bounds.renderY, quantityTexture.Width, quantityTexture.Height, z: 200);
        }
        RenderClear();
    }
    private void RenderClear()
    {
        if (clear == null) return;
        if (clearTexture == 0)
        {
            using var surface = new ImageSurface(Format.Argb32, (int)Math.Ceiling(scaled(18)), (int)Math.Ceiling(scaled(18)));
            using var ctx = new Context(surface);
            ctx.Scale(scaled(1), scaled(1)); ctx.Arc(9, 9, 8, 0, Math.PI * 2);
            ctx.SetSourceRGBA(.78, .12, .1, 1); ctx.Fill();
            ctx.MoveTo(6, 6); ctx.LineTo(12, 12); ctx.MoveTo(12, 6); ctx.LineTo(6, 12);
            ctx.SetSourceRGBA(1, 1, 1, 1); ctx.LineWidth = 1.6; ctx.Stroke();
            generateTexture(surface, ref clearTexture);
        }
        api.Render.Render2DTexturePremultipliedAlpha(clearTexture, Bounds.renderX + Bounds.InnerWidth - scaled(18),
            Bounds.renderY, scaled(18), scaled(18), z: 220);
    }
    public override void Dispose() { if (clearTexture != 0) api.Gui.DeleteTexture(clearTexture); clearTexture = 0; if (backgroundTexture != 0) api.Gui.DeleteTexture(backgroundTexture); backgroundTexture = 0; quantityTexture?.Dispose(); statusOverlay?.Dispose(); base.Dispose(); }
}
// Keep a real inventory context for mod tooltips, without registering it with the player's inventory manager.
internal sealed class EconomyPreviewInventory(ICoreClientAPI api) : DummyInventory(api);
// Use the game's overlay composer so installed mods can supply their own bars (e.g. Toolsmith sharpness).
// This detached inventory never participates in clicks, network packets or actual item transfers.
internal sealed class EconomyItemOverlay : GuiElementItemSlotGridBase
{
    private static readonly System.Reflection.MethodInfo composeOverlay = typeof(GuiElementItemSlotGridBase)
        .GetMethod("ComposeSlotOverlays", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    internal bool HasOverlay => slotQuantityTextures[0].TextureId != 0;
    public EconomyItemOverlay(ICoreClientAPI api, ItemSlot slot, ElementBounds bounds)
        : base(api, slot.Inventory, _ => { }, 1, bounds)
    {
        inventory[0] = slot;
        availableSlots.Add(0, slot);
        SlotBounds = [bounds];
        slotQuantityTextures = [new LoadedTexture(api)];
        try { composeOverlay.Invoke(this, [slot, 0, 0]); }
        catch { Dispose(); throw; }
    }
    public override void RenderInteractiveElements(float deltaTime)
    {
        if (HasOverlay) api.Render.Render2DTexturePremultipliedAlpha(slotQuantityTextures[0].TextureId,
            Bounds.renderX, Bounds.renderY, Bounds.InnerWidth, Bounds.InnerHeight, z: 200);
    }
}
internal sealed class GuiElementEconomyCoin : GuiElementEconomyItem
{
    public GuiElementEconomyCoin(ICoreClientAPI api, EconomyCoinView coin, ElementBounds bounds, int? count = null, Action? click = null, string? quantity = null, bool? listed = null, bool selected = false)
        : base(api, null, bounds, click, count, selected, quantity: quantity, listed: listed)
    {
        if (string.IsNullOrWhiteSpace(coin.Code)) return;
        var item = api.World.GetItem(new AssetLocation(coin.Code));
        if (item == null) return;
        slot.Itemstack = new ItemStack(item);
        slot.Itemstack.Attributes.SetLong("coinType", coin.Design);
        slot.Itemstack.Attributes.SetLong("coinShape", coin.Shape);
        slot.Itemstack.StackSize = count ?? 1;
    }
}

internal sealed class GuiElementEconomyArrow(ICoreClientAPI api, ElementBounds bounds) : GuiElement(api, bounds)
{
    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        ctx.Save();
        double scale = Bounds.InnerWidth / 128;
        ctx.Translate(Bounds.drawX, Bounds.drawY + (Bounds.InnerHeight - 72 * scale) / 2);
        ctx.Scale(scale, scale);
        ctx.SetSourceRGBA(.88, .81, .68, 1);
        api.Gui.Icons.DrawArrowRight(ctx, strokeOrFill: false, defaultPattern: false);
        ctx.Restore();
    }
}

internal sealed class GuiElementEconomyTaskEditor : GuiElementAnnouncementEditor
{
    public GuiElementAutoScrollbar Scrollbar { get; set; } = null!;
    public GuiElementEconomyTaskEditor(ICoreClientAPI api, ElementBounds bounds, Action<string> changed)
        : base(api, bounds, changed, CairoFont.TextInput())
    {
        OnTextChanged = text => { changed(text); if (Scrollbar != null) RefreshScroll(); };
        OnCursorMoved = (_, y) =>
        {
            if (Scrollbar == null) return;
            RefreshScroll(); double line = Font.GetFontExtents().Height;
            float top = Scrollbar.CurrentYPosition;
            if (y < top || y + line > top + Bounds.InnerHeight)
            { Scrollbar.CurrentYPosition = (float)Math.Clamp(y < top ? y : y + line - Bounds.InnerHeight, 0, ContentHeight - Bounds.InnerHeight); Scrollbar.TriggerChanged(); }
        };
    }
    public void RefreshScroll()
    {
        Scrollbar.SetContentHeights((float)Bounds.InnerHeight, (float)ContentHeight); Scrollbar.TriggerChanged();
    }
    public override void RenderInteractiveElements(float dt)
    {
        api.Render.PushScissor(Bounds, stacking: true);
        try { base.RenderInteractiveElements(dt); }
        finally { api.Render.PopScissor(); }
    }
    public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
    {
        if (!IsPositionInside(api.Input.MouseX, api.Input.MouseY)) return;
        Scrollbar.CurrentYPosition = (float)Math.Clamp(Scrollbar.CurrentYPosition - args.deltaPrecise * scaled(30), 0, ContentHeight - Bounds.InnerHeight);
        Scrollbar.TriggerChanged(); args.SetHandled();
    }
}

internal sealed class GuiElementEconomyRule(ICoreClientAPI api, ElementBounds bounds) : GuiElement(api, bounds)
{
    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        ctx.SetSourceRGBA(1, .9, .7, .18);
        ctx.Rectangle(Bounds.drawX, Bounds.drawY, Bounds.InnerWidth, Math.Max(1, Bounds.InnerHeight)); ctx.Fill();
    }
}

// Expanded native menus paint above subsequent fields and receive the click first.
internal sealed class EconomyGuiContainer(ICoreClientAPI api, ElementBounds bounds) : GuiElementContainer(api, bounds)
{
    // Compose and render only viewport contents. Clipping pixels alone still submits
    // every offscreen item model and text texture to the game's renderer.
    private sealed class RenderEntry(GuiElement element)
    {
        public GuiElement Element { get; } = element;
        public LoadedTexture? Texture;
        public bool Composed;
    }
    private readonly List<RenderEntry> rows = [];
    private readonly List<RenderEntry> visibleRows = [];
    private readonly List<GuiElementDropDown> menus = [];
    private double visibleY = double.NaN, clipY, clipHeight;
    private ItemSlot? hoveredSlot;
    private ItemSlot? hoveredElementSlot;
    private GuiElementDropDown? OpenMenu
    {
        get { foreach (var menu in menus) if (menu.listMenu.IsOpened) return menu; return null; }
    }
    public bool HasOpenMenu => OpenMenu != null;
    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        foreach (var row in rows) row.Texture?.Dispose();
        rows.Clear(); visibleRows.Clear(); menus.Clear(); visibleY = double.NaN;
        CalcTotalHeight(); Bounds.CalcWorldBounds();
        foreach (var element in Elements)
        {
            element.Bounds.CalcWorldBounds();
            rows.Add(new RenderEntry(element));
            if (element is GuiElementDropDown menu) menus.Add(menu);
        }
        EnsureVisibleComposed();
    }
    private void EnsureVisibleComposed()
    {
        var clip = InsideClipBounds ?? Bounds.ParentBounds;
        if (visibleY != Bounds.renderY || clipY != clip.renderY || clipHeight != clip.OuterHeight)
        {
            visibleY = Bounds.renderY; clipY = clip.renderY; clipHeight = clip.OuterHeight;
            visibleRows.Clear();
            foreach (var row in rows)
            {
                var b = row.Element.Bounds;
                if (b.renderY + b.OuterHeight + 2 > clipY && b.renderY - 2 < clipY + clipHeight)
                    visibleRows.Add(row);
            }
        }
        foreach (var entry in visibleRows)
        {
            if (entry.Composed) continue;
            var element = entry.Element;
            // Item previews have no static pixels. Buttons and scrollbars own their
            // native textures; give them a scratch surface without uploading a blank row.
            if (element is not GuiElementEconomyItem)
            {
                bool background = element is not (GuiElementTextButton or GuiElementScrollbar);
                using var row = new ImageSurface(Format.Argb32,
                    background ? Math.Max(1, (int)Math.Ceiling(element.Bounds.OuterWidth) + 4) : 1,
                    background ? Math.Max(1, (int)Math.Ceiling(element.Bounds.OuterHeight) + 4) : 1);
                using var draw = new Context(row);
                draw.Translate(2 - element.Bounds.drawX, 2 - element.Bounds.drawY);
                element.ComposeElements(draw, row);
                if (background)
                {
                    var texture = new LoadedTexture(api);
                    api.Gui.LoadOrUpdateCairoTexture(row, false, ref texture);
                    entry.Texture = texture;
                }
            }
            entry.Composed = true;
        }
    }
    public override void RenderInteractiveElements(float dt)
    {
        EnsureVisibleComposed();
        UpdateHover(api.Input.MouseX, api.Input.MouseY);
        var menu = OpenMenu;
        MouseOverCursor = null;
        foreach (var row in visibleRows)
            if (row.Texture is { } texture)
                api.Render.Render2DTexturePremultipliedAlpha(texture.TextureId, row.Element.Bounds.renderX - 2,
                    row.Element.Bounds.renderY - 2, texture.Width, texture.Height);
        foreach (var row in visibleRows)
        {
            var element = row.Element;
            if (element == menu || element is GuiElementHoverText) continue;
            element.RenderInteractiveElements(dt);
            if (element.IsPositionInside(api.Input.MouseX, api.Input.MouseY)) MouseOverCursor = element.MouseOverCursor;
        }
        menu?.RenderInteractiveElements(dt);
        // Native hover text removes the scissor itself. Draw it after the form controls
        // so fields cannot cover the detailed description.
        if ((InsideClipBounds ?? Bounds.ParentBounds).PointInside(api.Input.MouseX, api.Input.MouseY))
            foreach (var row in visibleRows)
                if (row.Element is GuiElementHoverText hint) hint.RenderInteractiveElements(dt);
    }
    private void UpdateHover(int x, int y)
    {
        ItemSlot? next = null;
        if (OpenMenu == null && (InsideClipBounds ?? Bounds.ParentBounds).PointInside(x, y))
            foreach (var row in visibleRows)
                if (row.Element is GuiElementEconomyItem item && item.IsPositionInside(x, y) && !item.DisplaySlot.Empty)
                { next = item.DisplaySlot; break; }
        if (ReferenceEquals(next, hoveredElementSlot) && (next != null || hoveredSlot == null)) return;
        if (next?.Itemstack != null && hoveredSlot?.Itemstack != null
            && next.Itemstack.ToBytes().SequenceEqual(hoveredSlot.Itemstack.ToBytes()))
        { hoveredElementSlot = next; return; }
        if (hoveredSlot != null) api.Input.TriggerOnMouseLeaveSlot(hoveredSlot);
        hoveredSlot = hoveredElementSlot = next;
        if (next != null) api.Input.TriggerOnMouseEnterSlot(next);
    }
    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    { EnsureVisibleComposed(); base.OnMouseMove(api, args); UpdateHover(args.X, args.Y); }
    public ItemSlot? DetachHover() { var old = hoveredSlot; hoveredSlot = hoveredElementSlot = null; return old; }
    public void RestoreHover(ItemSlot? old)
    { hoveredSlot = old; hoveredElementSlot = null; EnsureVisibleComposed(); UpdateHover(api.Input.MouseX, api.Input.MouseY); }
    public void ClearHover()
    {
        if (hoveredSlot != null) api.Input.TriggerOnMouseLeaveSlot(hoveredSlot);
        hoveredSlot = hoveredElementSlot = null;
    }
    public override void OnMouseDown(ICoreClientAPI api, MouseEvent args)
    {
        EnsureVisibleComposed();
        if (OpenMenu is { } menu)
        {
            menu.OnMouseDown(api, args);
            if (args.Handled) return;
        }
        base.OnMouseDown(api, args);
    }
    public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
    {
        EnsureVisibleComposed();
        if (OpenMenu is { } menu)
        {
            menu.OnMouseWheel(api, args);
            args.SetHandled(); return;
        }
        foreach (var row in visibleRows)
            if (row.Element is GuiElementEconomyTaskEditor editor && editor.IsPositionInside(api.Input.MouseX, api.Input.MouseY))
            { editor.OnMouseWheel(api, args); if (args.IsHandled) return; break; }
        base.OnMouseWheel(api, args);
    }
    public override void Dispose()
    {
        ClearHover();
        foreach (var row in rows) row.Texture?.Dispose();
        rows.Clear(); visibleRows.Clear(); menus.Clear(); base.Dispose();
    }
}
