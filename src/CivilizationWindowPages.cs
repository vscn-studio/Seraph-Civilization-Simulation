using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.Common;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnClusterWindow
{
    private int page, composedPage = -1;
    private string? announcementDraft, pendingSave;
    private long draftRevision;
    private bool announcementPreview, announcementNotify, newAnnouncement;
    private string announcementId = "", announcementTitle = "";
    private GuiElementTextInput? announcementTitleInput;
    private readonly Dictionary<string, (string Html, string Title, bool Notify, bool New, long Revision)> announcementDrafts = [];
    private List<AnnouncementEntry> AnnouncementEntries => snapshot.AnnouncementCollection || snapshot.Announcements.Count > 0
        ? snapshot.Announcements : [new() { Id = "legacy", Title = L("announcement"), Html = snapshot.AnnouncementHtml }];
    private AnnouncementEntry? SelectedAnnouncement => AnnouncementEntries.FirstOrDefault(e => e.Id == announcementId) ?? AnnouncementEntries.FirstOrDefault();
    private double AnnouncementTabWidth => Math.Clamp(AnnouncementEntries.Select(e => e.Title).Append(L("announcement"))
        .Max(t => CairoFont.WhiteDetailText().GetTextExtents(t).Width) / Math.Max(.25, RuntimeEnv.GUIScale) + 18, 72, 160);
    private void StashAnnouncementDraft()
    {
        if (announcementDraft != null) announcementDrafts[announcementId] = (announcementDraft, announcementTitle, announcementNotify, newAnnouncement, draftRevision);
    }
    private void SelectAnnouncement(string id)
    {
        if (pendingSave != null || id == announcementId) return;
        StashAnnouncementDraft(); announcementId = id; announcementPreview = false;
        announcementDraft = null; newAnnouncement = false;
        if (announcementDrafts.TryGetValue(id, out var draft))
        { announcementDraft = draft.Html; announcementTitle = draft.Title; announcementNotify = draft.Notify; newAnnouncement = draft.New; draftRevision = draft.Revision; }
        dirty = true;
    }
    private GuiElementAnnouncementEditor? announcementEditor;
    private AnnouncementResourcePicker? announcementResourcePicker;
    private (string Source, string Id, int Start, int End)? resourceInsertion;
    private bool resourceForTutorial;
    private string? pendingResourceCategory;
    private int resourceCatalogRevision, resourceSnapshotCount;
    private long resourceRefreshAt;
    private AnnouncementInteraction announcementInteraction = new();
    private string renderedAnnouncement = "";
    private void OpenAnnouncementResource(string category)
    {
        if (announcementEditor == null || announcementDraft == null) return;
        // Defer composition until the dropdown has finished handling its mouse event.
        var range = announcementEditor.Selection();
        resourceInsertion = (announcementDraft, announcementId, range.Start, range.End);
        resourceForTutorial = false;
        pendingResourceCategory = category;
    }
    private void OpenTutorialResource(string category)
    {
        if (tutorialEditor == null || tutorialDraft == null) return;
        var range = tutorialEditor.Selection();
        resourceInsertion = (tutorialDraft, tutorialId, range.Start, range.End);
        resourceForTutorial = true;
        pendingResourceCategory = category;
    }
    private IEnumerable<EconomyItemView> AnnouncementResourceItems()
    {
        // Catalog packets can append between frames; do not retain a List enumerator.
        var catalog = economyCatalog?.Items;
        if (catalog != null) for (int i = 0; i < catalog.Count; i++) yield return catalog[i];
        var items = economySnapshot.Items;
        for (int i = 0; i < items.Count; i++) yield return items[i];
    }
    private void ComposeAnnouncementResource(string category)
    {
        CloseAnnouncementResource(false);
        if (resourceInsertion is not { } insertion) return;
        var resources = AnnouncementGameResources.Catalog(capi, category, AnnouncementResourceItems());
        announcementResourcePicker = new(capi, category, resources, AnnouncementResourceBounds(), html =>
        {
            if (resourceForTutorial)
            {
                if (tutorialDraft != insertion.Source || tutorialId != insertion.Id || tutorialEditor == null)
                { capi.ShowChatMessage(L("html-draft-changed")); return; }
                var tutorialEdit = AnnouncementHtmlTools.Insert(insertion.Source, insertion.Start, insertion.End, html);
                if (tutorialEdit.Text == insertion.Source) { capi.ShowChatMessage(L("announcement-too-long")); return; }
                tutorialEditor.ApplyEdit(tutorialEdit);
                var tutorialContent = SingleComposer.GetContainer("tutorial-editor-content");
                SingleComposer.FocusElement(tutorialContent.TabIndex); tutorialContent.FocusElement(tutorialEditor.TabIndex);
                return;
            }
            if (announcementDraft != insertion.Source || announcementId != insertion.Id || announcementEditor == null)
            { capi.ShowChatMessage(L("html-draft-changed")); return; }
            var edit = AnnouncementHtmlTools.Insert(insertion.Source, insertion.Start, insertion.End, html);
            if (edit.Text == insertion.Source) { capi.ShowChatMessage(L("announcement-too-long")); return; }
            announcementEditor.ApplyEdit(edit);
            SingleComposer.FocusElement(content!.TabIndex); content.FocusElement(announcementEditor.TabIndex);
        });
        resourceCatalogRevision = economyCatalog?.Revision ?? 0;
        resourceSnapshotCount = economySnapshot.Items.Count; resourceRefreshAt = 0;
        Composers["announcement-resource"] = announcementResourcePicker.Composer;
        SingleComposer.UnfocusOwnElements();
        announcementResourcePicker.Composer.FocusElement(announcementResourcePicker.Composer.GetTextInput("search").TabIndex);
    }
    private ElementBounds AnnouncementResourceBounds()
    {
        double scale = Math.Max(.25, RuntimeEnv.GUIScale);
        var main = SingleComposer.Bounds;
        bool compact = CompactPicker;
        return ElementBounds.Fixed(main.renderX / scale + (compact ? 20 : windowWidth + 12),
            main.renderY / scale + (compact ? 20 : 0), compact ? windowWidth - 40 : PickerSideWidth,
            compact ? windowHeight - 40 : windowHeight);
    }
    private void CloseAnnouncementResource(bool clearInsertion = true)
    {
        Composers.Remove("announcement-resource");
        announcementResourcePicker?.Dispose(); announcementResourcePicker = null;
        if (clearInsertion) { resourceInsertion = null; pendingResourceCategory = null; resourceForTutorial = false; }
    }
    private double RightTabWidth => page == AnnouncementPage ? AnnouncementTabWidth : page == TribePage ? TribeTabWidth : EconomyTabWidth;
    private double TabWidth => Math.Clamp(new[] { L("announcement"), L("tutorial"), L("tribes"), L("economy") }
        .Max(text => CairoFont.WhiteSmallText().GetTextExtents(text).Width) / Math.Max(.25, RuntimeEnv.GUIScale) + 18, 64, 180);

    private void ComposeAnnouncement(GuiComposer composer, ElementBounds view)
    {
        var entries = AnnouncementEntries;
        bool compact = view.fixedHeight < 300 || view.fixedWidth < 420;
        if (announcementDraft == null && !entries.Any(e => e.Id == announcementId)) announcementId = entries.FirstOrDefault()?.Id ?? "";
        bool editing = snapshot.CanEditAnnouncement && announcementDraft != null;
        if (entries.Count > 0)
            composer.AddInteractiveElement(new GuiElementPageTabs(capi, entries.Select(e => new GuiTab { Name = e.Title }).ToArray(),
                ElementBounds.Fixed(windowWidth, view.fixedY, RightTabWidth, view.fixedHeight), (index, _) => SelectAnnouncement(entries[index].Id)), "announcement-tabs");
        double buttonsHeight = 0;
        var buttons = new List<GuiElementTextButton>();
        int newButton = -1;
        if (snapshot.CanEditAnnouncement)
        {
            if (!editing)
            {
                if (SelectedAnnouncement is { } entry) Add("announcement-edit", () =>
                {
                    string source = entry.IsMarkdown ? AnnouncementMarkdown.ToHtml(entry.Html) : entry.Html;
                    if (source.Length > AnnouncementState.MaxLength) { capi.ShowChatMessage(L("html-migration-too-long")); return; }
                    announcementId = entry.Id; announcementDraft = source; announcementTitle = entry.Title;
                    announcementNotify = entry.Notify; draftRevision = snapshot.AnnouncementRevision;
                    newAnnouncement = false; announcementPreview = false; dirty = true;
                });
                if (entries.Count < AnnouncementState.MaxAnnouncements)
                {
                    newButton = buttons.Count;
                    Add("announcement-new", () =>
                    {
                        StashAnnouncementDraft(); announcementId = ""; announcementTitle = L("announcement");
                        announcementDraft = ""; announcementNotify = false; newAnnouncement = true;
                        draftRevision = snapshot.AnnouncementRevision; announcementPreview = false; dirty = true;
                    });
                }
            }
            else
            {
                Add(announcementPreview ? "announcement-edit" : "announcement-preview", () => { announcementPreview = !announcementPreview; dirty = true; });
                Add(pendingSave == null ? "save" : "announcement-saving", () => Save(newAnnouncement ? "announcement-create" : "announcement-save"));
                Add("cancel", () => { announcementDrafts.Remove(announcementId); announcementDraft = null; announcementPreview = false; newAnnouncement = false; dirty = true; });
                if (!newAnnouncement) Add("announcement-delete", () => Save("announcement-delete"));
            }
            double available = view.fixedWidth, x = 0, y = 0, rowHeight = 0;
            foreach (var button in buttons)
            {
                double width = button.Bounds.fixedWidth + 2 * button.Bounds.fixedPaddingX;
                if (x > 0 && x + width > available) { y += rowHeight + 8; rowHeight = 0; x = 0; }
                button.Bounds.fixedX = view.fixedX + x; button.Bounds.fixedY = y;
                x += width + 10; rowHeight = Math.Max(rowHeight, button.Bounds.fixedHeight + 2 * button.Bounds.fixedPaddingY);
            }
            if (editing && x - 10 > available - 90) { y += rowHeight + 8; rowHeight = 28; }
            buttonsHeight = y + rowHeight + 12;
            foreach (var button in buttons) button.Bounds.fixedY += view.fixedY + view.fixedHeight - buttonsHeight + 12;
            if (editing)
            {
                double notifyY = view.fixedY + view.fixedHeight - 28;
                composer.AddInteractiveElement(new GuiElementSwitch(capi, on => announcementNotify = on,
                    ElementBounds.Fixed(view.fixedX + view.fixedWidth - 84, notifyY, 24, 24), 24) { On = announcementNotify }, "announcement-notify");
                composer.AddStaticText(L("announcement-notify"), CairoFont.WhiteDetailText(),
                    ElementBounds.Fixed(view.fixedX + view.fixedWidth - 54, notifyY + 3, 54, 24));
            }
        }
        double titleHeight = editing ? compact ? 36 : 44 : 0;
        if (editing)
        {
            announcementTitleInput = new GuiElementTextInput(capi, ElementBounds.Fixed(view.fixedX, view.fixedY, view.fixedWidth - 12, 32),
                value => announcementTitle = value, AnnouncementFonts.Readable(CairoFont.TextInput(), "公告标题"));
            announcementTitleInput.OnTryTextChangeText = lines => lines.Sum(l => l.Length) <= AnnouncementState.MaxTitleLength;
            announcementTitleInput.SetValue(announcementTitle);
            composer.AddInteractiveElement(announcementTitleInput, "announcement-title");
        }
        if (editing && !announcementPreview)
        {
            double x = 0, y = view.fixedY + titleHeight;
            double available = view.fixedWidth - 24;
            bool iconMenus = available < 440;
            double toolGap = iconMenus ? 1 : 4;
            int toolCount = AnnouncementHtmlTools.Tools.Length;
            double toolSize = Math.Min(32, (available - 3 * 26 - (toolCount + 2) * toolGap) / toolCount);
            double menuWidth = (available - toolCount * toolSize - (toolCount + 2) * toolGap) / 3;
            foreach (string tool in AnnouncementHtmlTools.Tools)
            {
                var button = new GuiElementAnnouncementTool(capi, tool, () =>
                    {
                        if (announcementEditor == null) return;
                        announcementEditor.ApplyHtml(tool);
                        composer.FocusElement(content!.TabIndex);
                        content.FocusElement(announcementEditor.TabIndex);
                    }, ElementBounds.Fixed(0, 0, toolSize, toolSize));
                double width = button.Bounds.fixedWidth + 2 * button.Bounds.fixedPaddingX;
                button.Bounds.fixedX = view.fixedX + x; button.Bounds.fixedY = y;
                x += width + toolGap;
                composer.AddInteractiveElement(button, "announcement-html-" + tool);
                string shortcut = tool switch { "bold" => " (Ctrl+B)", "italic" => " (Ctrl+I)", "link" => " (Ctrl+K)", _ => "" };
                var hint = new GuiElementHoverText(capi, Esc(L("html-" + tool) + shortcut), AnnouncementFonts.Readable(CairoFont.WhiteSmallText(), L("html-" + tool) + shortcut),
                    (int)Math.Min(220, view.fixedWidth), button.Bounds.FlatCopy(), OpaqueTooltipBackground());
                hint.SetAutoWidth(true);
                composer.AddInteractiveElement(hint, "announcement-html-hint-" + tool);
            }

            AddMenu("format", AnnouncementHtmlTools.FormatTools, 0, tool => announcementEditor?.ApplyHtml(tool));
            AddMenu("insert", AnnouncementHtmlTools.InsertTools, 1, tool => announcementEditor?.ApplyHtml(tool));
            AddMenu("embed", ["handbook", "itemhandbook", "item", "recipe", "slideshow"], 2, OpenAnnouncementResource);
            titleHeight = y - view.fixedY + Math.Max(toolSize, 28) + (compact ? 4 : 8);
            void AddMenu(string title, string[] options, int column, Action<string> action)
            {
                var values = new[] { "" }.Concat(options).ToArray();
                var labels = new[] { L("html-" + title) }.Concat(options.Select(t => L("html-" + t))).ToArray();
                GuiElementDropDown? menu = null;
                menu = new GuiElementAnnouncementMenu(capi, title, iconMenus, values, labels, (value, _) =>
                {
                    if (value.Length == 0) return;
                    action(value); menu!.SetSelectedIndex(0);
                    if (title != "embed" && announcementEditor != null)
                    { composer.FocusElement(content!.TabIndex); content.FocusElement(announcementEditor.TabIndex); }
                }, ElementBounds.Fixed(view.fixedX + x + column * (menuWidth + toolGap), y, menuWidth, toolSize), AnnouncementFonts.Readable(CairoFont.WhiteSmallText().WithFontSize(compact ? 12 : 14), string.Join("", labels)));
                composer.AddInteractiveElement(menu, "announcement-html-" + title);
                var hint = new GuiElementHoverText(capi, Esc(L("html-" + title)), AnnouncementFonts.Readable(CairoFont.WhiteSmallText(), L("html-" + title)),
                    180, menu.Bounds.FlatCopy(), OpaqueTooltipBackground());
                hint.SetAutoWidth(true); composer.AddInteractiveElement(hint, "announcement-html-hint-" + title);
            }
        }
        var clip = ElementBounds.Fixed(view.fixedX, view.fixedY + titleHeight, view.fixedWidth - 24, Math.Max(36, view.fixedHeight - buttonsHeight - titleHeight));
        var body = ElementBounds.Fixed(0, 0, clip.fixedWidth, clip.fixedHeight);
        composer.BeginClip(clip).AddInteractiveElement(new GuiElementAnnouncementContainer(capi, body), "announcement-content");
        content = composer.GetContainer("announcement-content"); content.unscaledCellSpacing = 8;
        if (editing && !announcementPreview)
        {
            var input = HtmlDocumentApi.CreateEditor(capi, ElementBounds.Fixed(0, 0, clip.fixedWidth - 2, clip.fixedHeight - 8), text =>
            {
                announcementDraft = text;
                if (announcementEditor == null) return;
                if (composer.Composed) UpdateAnnouncementScroll();
            }, CairoFont.TextInput());
            input.OnTryTextChangeText = lines => lines.Sum(line => line.Length) <= AnnouncementState.MaxLength;
            content.Add(input); announcementEditor = input;
            // Native wrapping requires a resolved width before loading the first draft.
            input.Bounds.CalcWorldBounds();
            input.SetValue(announcementDraft!, false);
            input.OnCursorMoved = (_, y) =>
            {
                if (!composer.Composed) return;
                var bar = composer.GetScrollbar("scrollbar");
                float top = bar.CurrentYPosition;
                float height = (float)clip.InnerHeight;
                if (y < top || y + 30 * guiScale > top + height)
                {
                    bar.CurrentYPosition = (float)Math.Clamp(y < top ? y : y + 30 * guiScale - height, 0, Math.Max(0, announcementEditor!.ContentHeight - height));
                    bar.TriggerChanged();
                }
            };
        }
        else
        {
            string html = editing ? announcementDraft! : SelectedAnnouncement?.Html ?? "";
            if (!editing && SelectedAnnouncement?.IsMarkdown == true) html = AnnouncementMarkdown.ToHtml(html);
            if (renderedAnnouncement != announcementId + "\n" + html)
            { renderedAnnouncement = announcementId + "\n" + html; announcementInteraction = new(); }
            announcementInteraction.Dirty = false;
            var notice = HtmlDocumentApi.CreateReader(capi, string.IsNullOrWhiteSpace(html) ? Esc(L("announcement-empty")) : html,
                ElementBounds.Fixed(0, 0, clip.fixedWidth - 2, 20), announcementInteraction);
            announcementInteraction.Navigate = href =>
            {
                if (!href.StartsWith('#')) return;
                if (notice.Anchor(Uri.UnescapeDataString(href[1..])) is { } target)
                {
                    var bar = composer.GetScrollbar("scrollbar");
                    bar.CurrentYPosition = (float)Math.Clamp(target, 0, Math.Max(0, notice.Bounds.InnerHeight - clip.InnerHeight));
                    bar.TriggerChanged();
                }
            };
            content.Add(notice);
        }
        // The native container focuses its first editable child without a null check.
        // Reading and previewing still receive mouse events, but have no keyboard focus target.
        content.Tabbable = content.FirstTabbableElement != null;
        composer.EndClip().AddInteractiveElement(new GuiElementAutoScrollbar(capi, value =>
        {
            if (announcementEditor != null) announcementEditor.ScrollTo(value);
            else { body.fixedY = -value / Math.Max(.25, RuntimeEnv.GUIScale); body.CalcWorldBounds(); }
        }, ElementBounds.Fixed(view.fixedX + view.fixedWidth - 16, clip.fixedY, 16, clip.fixedHeight)), "scrollbar");
        foreach (var button in buttons) composer.AddInteractiveElement(button,
            buttons.IndexOf(button) == newButton ? "announcement-new" : "announcement-" + buttons.IndexOf(button));

        void Save(string action)
        {
            if (pendingSave != null) return;
            pendingSave = Guid.NewGuid().ToString("N");
            send(new CivilizationRequestPacket { Action = action, Target = announcementId, Name = announcementTitle,
                Html = announcementDraft!, IsMarkdown = false, Notify = announcementNotify, Revision = draftRevision, RequestId = pendingSave });
            dirty = true;
        }

        void Add(string key, System.Action action)
        {
            var font = AnnouncementFonts.Readable(CairoFont.SmallButtonText(EnumButtonStyle.Small), L(key));
            if (compact) font.WithFontSize(14);
            var button = new GuiElementTextButton(capi, L(key), font, font.Clone().WithColor(GuiStyle.ActiveButtonTextColor),
                () => { action(); return true; }, ElementBounds.Fixed(0, 0, 0, 0).WithFixedPadding(compact ? 6 : 12, compact ? 4 : 6), EnumButtonStyle.Small);
            button.BeforeCalcBounds(); buttons.Add(button);
        }
        void UpdateAnnouncementScroll()
        {
            var bar = (GuiElementAutoScrollbar)composer.GetScrollbar("scrollbar");
            bar.SetContentHeights((float)clip.InnerHeight, (float)Math.Max(clip.InnerHeight, announcementEditor!.ContentHeight));
            bar.TriggerChanged();
        }
    }

}

// Native right tabs fitted to the viewport.
internal sealed class GuiElementPageTabs : GuiElementVerticalTabs
{
    public GuiElementPageTabs(ICoreClientAPI api, GuiTab[] tabs, ElementBounds bounds, Action<int, GuiTab> clicked)
        : base(api, tabs, CairoFont.WhiteDetailText(), CairoFont.WhiteDetailText(), bounds, clicked)
    {
        Right = true;
        unscaledTabSpacing = bounds.fixedHeight >= tabs.Length * 30 ? 5 : 0;
        unscaledTabHeight = Math.Min(25, bounds.fixedHeight / Math.Max(1, tabs.Length) - unscaledTabSpacing);
    }
    public override void ComposeTextElements(Context ctx, ImageSurface surface)
    {
        var names = tabs.Select(t => t.Name).ToArray();
        try
        {
            double width = scaled(Bounds.fixedWidth - 12);
            for (int i = 0; i < tabs.Length; i++)
            {
                string label = names[i];
                if (Font.GetTextExtents(label).Width <= width) continue;
                while (label.Length > 1 && Font.GetTextExtents(label + "…").Width > width) label = label[..^1];
                tabs[i].Name = label + "…";
            }
            base.ComposeTextElements(ctx, surface);
        }
        finally { for (int i = 0; i < tabs.Length; i++) tabs[i].Name = names[i]; }
    }
    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        // The native click calculation assumes left tabs even when Right is set.
        double x = args.X - Bounds.absX, y = args.Y - Bounds.absY, top = 0;
        for (int index = 0; index < tabs.Length; index++)
        {
            top += tabs[index].PaddingTop;
            if (x >= 0 && x <= tabWidths[index] + 3 && y >= top && y < top + tabHeight)
            { SetValue(index); args.Handled = true; return; }
            top += tabHeight + scaled(unscaledTabSpacing);
        }
    }
}
