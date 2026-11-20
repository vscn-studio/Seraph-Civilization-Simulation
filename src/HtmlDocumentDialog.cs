using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace SeraphCivilizationSimulation;

internal sealed class HtmlDocumentDialog : GuiDialog
{
    private string title, source;
    private readonly Action<string, string, bool>? save;
    private readonly Action? cancel, transcribe;
    private bool preview, dirty, submitted, finished, disposed;
    private int frameWidth, frameHeight;
    private double scale;
    private GuiElementAnnouncementEditor? editor;
    private GuiElementAnnouncement? reader;
    private GuiElementAnnouncementContainer? content;
    private readonly AnnouncementInteraction interaction = new();
    private AnnouncementResourcePicker? picker;
    private string? pendingCategory;
    private (int Start, int End) selection;
    private GuiElementScrollbar? draggedBar;
    private string? pendingSaveSource;
    private GuiDialogConfirm? confirmation;
    public override string ToggleKeyCombinationCode => null!;

    internal HtmlDocumentDialog(ICoreClientAPI api, string title, string source,
        Action<string, string, bool>? save, Action? cancel, Action? transcribe) : base(api)
    {
        if (save != null && source.Length > HtmlDocumentApi.MaxSourceLength) throw new ArgumentOutOfRangeException(nameof(source));
        this.title = title; this.source = source; this.save = save; this.cancel = cancel; this.transcribe = transcribe;
        OnClosed += () => { if (!finished) { finished = true; if (!submitted) cancel?.Invoke(); } };
        Compose();
    }

    private void Compose()
    {
        float scroll = SingleComposer?.GetScrollbar("scroll")?.CurrentYPosition ?? 0;
        ClosePicker(); ClearComposers(); editor = null; reader = null;
        scale = Math.Max(.25, RuntimeEnv.GUIScale); frameWidth = capi.Render.FrameWidth; frameHeight = capi.Render.FrameHeight;
        double width = Math.Clamp(frameWidth / scale - 40, 320, 840);
        double height = Math.Clamp(frameHeight / scale - 48, 280, 680);
        var bounds = ElementBounds.Fixed(0, 0, width, height).WithAlignment(EnumDialogArea.CenterMiddle);
        var composer = capi.Gui.CreateCompo("vscn-html-document", bounds)
            .AddShadedDialogBG(ElementBounds.Fill, alpha: 1)
            .AddDialogTitleBar(save == null ? title : "HTML 公告编辑", () => TryClose());
        double top = 48;
        if (save != null)
        {
            var input = new GuiElementTextInput(capi, ElementBounds.Fixed(16, top, width - 32, 30), value => title = value,
                AnnouncementFonts.Readable(CairoFont.TextInput(), "公告标题"));
            input.OnTryTextChangeText = lines => lines.Sum(l => l.Length) <= 80;
            input.SetValue(title); composer.AddInteractiveElement(input, "title"); top += 40;
        }
        if (save != null && !preview)
        {
            double x = 16;
            foreach (string tool in AnnouncementHtmlTools.Tools)
            {
                composer.AddInteractiveElement(new GuiElementAnnouncementTool(capi, tool,
                    () => { editor?.ApplyHtml(tool); FocusEditor(); }, ElementBounds.Fixed(x, top, 28, 28)), "tool-" + tool);
                x += 32;
            }
            top += 34;
            Menu("format", AnnouncementHtmlTools.FormatTools, 0, tool => editor?.ApplyHtml(tool));
            Menu("insert", AnnouncementHtmlTools.InsertTools, 1, tool => editor?.ApplyHtml(tool));
            Menu("embed", ["handbook", "itemhandbook", "item", "recipe", "slideshow"], 2, category =>
            { selection = editor?.Selection() ?? (0, 0); pendingCategory = category; });
            void Menu(string code, string[] tools, int index, Action<string> action)
            {
                var labels = new[] { code }.Concat(tools).Select(t => Lang.Get("vsscsmod:ui.html-" + t)).ToArray();
                GuiElementDropDown? menu = null;
                double w = (width - 40) / 3;
                menu = new GuiElementAnnouncementMenu(capi, code, false, new[] { "" }.Concat(tools).ToArray(), labels,
                    (value, _) => { if (value.Length == 0) return; action(value); menu!.SetSelectedIndex(0); if (code != "embed") FocusEditor(); },
                    ElementBounds.Fixed(16 + index * (w + 4), top, w, 28), AnnouncementFonts.Readable(CairoFont.WhiteSmallText().WithFontSize(14), string.Join("", labels)));
                composer.AddInteractiveElement(menu, "menu-" + code);
            }
            top += 36;
        }
        var clip = ElementBounds.Fixed(16, top, width - 52, Math.Max(60, height - top - 56));
        var body = ElementBounds.Fixed(0, 0, clip.fixedWidth, clip.fixedHeight);
        content = new GuiElementAnnouncementContainer(capi, body);
        composer.BeginClip(clip).AddInteractiveElement(content, "content");
        if (save != null && !preview)
        {
            editor = HtmlDocumentApi.CreateEditor(capi, ElementBounds.Fixed(0, 0, clip.fixedWidth - 2, clip.fixedHeight - 8),
                value => { source = value; if (composer.Composed) UpdateScroll(); }, CairoFont.TextInput());
            editor.OnTryTextChangeText = lines => lines.Sum(l => l.Length) <= HtmlDocumentApi.MaxSourceLength;
            content.Add(editor); editor.Bounds.CalcWorldBounds(); editor.SetValue(source, false);
            editor.OnCursorMoved = (_, y) =>
            {
                if (!composer.Composed) return;
                var bar = composer.GetScrollbar("scroll");
                if (y < bar.CurrentYPosition || y + 30 * scale > bar.CurrentYPosition + clip.InnerHeight)
                { bar.CurrentYPosition = (float)Math.Clamp(y < bar.CurrentYPosition ? y : y + 30 * scale - clip.InnerHeight, 0, Math.Max(0, editor.ContentHeight - clip.InnerHeight)); bar.TriggerChanged(); }
            };
        }
        else
        {
            interaction.Dirty = false;
            reader = HtmlDocumentApi.CreateReader(capi, source, ElementBounds.Fixed(0, 0, clip.fixedWidth - 2, 20), interaction);
            content.Add(reader);
            interaction.Navigate = href =>
            {
                if (!href.StartsWith('#') || reader.Anchor(Uri.UnescapeDataString(href[1..])) is not { } y) return;
                var bar = composer.GetScrollbar("scroll");
                bar.CurrentYPosition = (float)Math.Clamp(y, 0, Math.Max(0, body.OuterHeight - clip.InnerHeight)); bar.TriggerChanged();
            };
        }
        content.Tabbable = content.FirstTabbableElement != null;
        composer.EndClip().AddInteractiveElement(new GuiElementAutoScrollbar(capi, value =>
        { if (editor != null) editor.ScrollTo(value); else { body.fixedY = -value / scale; body.CalcWorldBounds(); } },
            ElementBounds.Fixed(width - 30, top, 14, clip.fixedHeight)), "scroll");
        double buttonWidth = Math.Min(115, (width - 44) / 4), bx = 16;
        void Button(string text, ActionConsumable action, string key)
        { composer.AddSmallButton(text, action, ElementBounds.Fixed(bx, height - 40, buttonWidth, 26), key: key); bx += buttonWidth + 4; }
        if (save != null)
        {
            Button(preview ? "编辑源码" : "预览", () => { preview = !preview; dirty = true; return true; }, "preview");
            Button("保存", () => Submit(false), "save");
            Button("保存并签名", () =>
            {
                if (confirmation?.IsOpened() == true) return true;
                pendingSaveSource = source;
                var confirm = new GuiDialogConfirm(capi, "保存并签名后将不能再编辑，是否继续？", ok =>
                { if (ok && !finished && pendingSaveSource == source) Submit(true); pendingSaveSource = null; });
                confirmation = confirm;
                confirm.OnClosed += () =>
                { if (ReferenceEquals(confirmation, confirm)) confirmation = null; capi.Event.EnqueueMainThreadTask(confirm.Dispose, "vscn-html-confirm-close"); };
                // Native confirm composes both in its constructor and on opening.
                // Release the first set before it replaces the composer.
                confirm.ClearComposers();
                confirm.TryOpen(); return true;
            }, "sign");
        }
        else if (transcribe != null) Button("抄写", () => { transcribe(); return true; }, "transcribe");
        Button(save == null ? "关闭" : "取消", () => TryClose(), "close");
        SingleComposer = composer.Compose();
        UpdateScroll();
        var scrollbar = composer.GetScrollbar("scroll");
        scrollbar.CurrentYPosition = (float)Math.Clamp(scroll, 0, Math.Max(0, (editor?.ContentHeight ?? body.OuterHeight) - clip.InnerHeight)); scrollbar.TriggerChanged();
        dirty = false;
        void UpdateScroll() => ((GuiElementAutoScrollbar)composer.GetScrollbar("scroll")).SetContentHeights((float)clip.InnerHeight,
            (float)Math.Max(clip.InnerHeight, editor?.ContentHeight ?? body.OuterHeight));
    }

    private bool Submit(bool sign)
    {
        if (submitted || finished || save == null || source.Length > HtmlDocumentApi.MaxSourceLength) return true;
        save(title, source, sign); submitted = true; TryClose(); return true;
    }
    private void FocusEditor()
    { if (editor == null || content == null) return; SingleComposer.FocusElement(content.TabIndex); content.FocusElement(editor.TabIndex); }
    private IEnumerable<EconomyItemView> Items()
    {
        foreach (var collectible in capi.World.Collectibles)
        {
            List<ItemStack>? stacks;
            try { stacks = collectible.GetHandBookStacks(capi); }
            catch (Exception e) { capi.Logger.Debug("HTML item catalog skipped {0}: {1}", collectible.Code, e.Message); continue; }
            foreach (var stack in stacks ?? []) yield return EconomyInventory.Display(stack);
        }
    }
    private void ClosePicker()
    { if (picker == null) return; Composers.Remove("resources"); picker.Dispose(); picker = null; }
    public override void OnRenderGUI(float dt)
    {
        if (dirty || reader?.HasNewImages == true || frameWidth != capi.Render.FrameWidth || frameHeight != capi.Render.FrameHeight || scale != RuntimeEnv.GUIScale) Compose();
        if (picker?.CloseRequested == true) { ClosePicker(); FocusEditor(); }
        if (pendingCategory is { } category)
        {
            pendingCategory = null; ClosePicker();
            double w = Math.Clamp(frameWidth / scale - 48, 280, 640), h = Math.Clamp(frameHeight / scale - 64, 220, 520);
            picker = new AnnouncementResourcePicker(capi, category, AnnouncementGameResources.Catalog(capi, category, Items()),
                ElementBounds.Fixed(0, 0, w, h).WithAlignment(EnumDialogArea.CenterMiddle), html =>
                { if (editor != null) HtmlDocumentApi.InsertHtml(editor, selection.Start, selection.End, html); });
            SingleComposer.UnfocusOwnElements();
        }
        if (picker != null) { picker.Tick(); Composers["resources"] = picker.Composer; }
        base.OnRenderGUI(dt);
    }
    // Modal resource picker consumes input; no click-through to the source underneath.
    public override void OnMouseDown(MouseEvent args)
    {
        if (picker != null) { picker.Composer.OnMouseDown(args); args.Handled = true; return; }
        if (editor != null && SingleComposer.GetScrollbar("scroll") is { } bar && bar.IsPositionInside(args.X, args.Y))
        { bar.OnMouseDown(capi, args); if (args.Handled) { draggedBar = bar; return; } }
        base.OnMouseDown(args);
    }
    public override void OnMouseMove(MouseEvent args)
    {
        if (picker != null) { picker.Composer.OnMouseMove(args); args.Handled = true; return; }
        if (draggedBar != null) { draggedBar.OnMouseMove(capi, args); args.Handled = true; return; }
        base.OnMouseMove(args);
    }
    public override void OnMouseUp(MouseEvent args)
    {
        if (picker != null) { picker.Composer.OnMouseUp(args); args.Handled = true; return; }
        if (draggedBar != null) { draggedBar.OnMouseUp(capi, args); draggedBar = null; args.Handled = true; return; }
        base.OnMouseUp(args);
    }
    public override void OnMouseWheel(MouseWheelEventArgs args)
    { if (picker != null) { picker.Composer.OnMouseWheel(args); args.SetHandled(); return; } base.OnMouseWheel(args); }
    public override void OnKeyDown(KeyEvent args)
    {
        if (picker != null)
        { if (args.KeyCode == (int)GlKeys.Escape) ClosePicker(); else picker.Composer.OnKeyDown(args, Focused); args.Handled = true; return; }
        base.OnKeyDown(args);
    }
    public override void OnKeyPress(KeyEvent args)
    { if (picker != null) { picker.Composer.OnKeyPress(args); args.Handled = true; return; } base.OnKeyPress(args); }
    public override void Dispose()
    { if (disposed) return; disposed = true; var confirm = confirmation; confirmation = null; confirm?.TryClose(); confirm?.Dispose(); ClosePicker(); if (!finished) { finished = true; if (!submitted) cancel?.Invoke(); } base.Dispose(); }
}
