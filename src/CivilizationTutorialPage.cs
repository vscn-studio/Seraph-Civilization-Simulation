using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.Common;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnClusterWindow
{
    private string? tutorialDraft;
    private string tutorialId = "", tutorialTitle = "", tutorialQuery = "";
    private bool tutorialOnlyMine;
    private string tutorialReadId = "";
    private long tutorialDraftRevision;
    private bool tutorialPreview, tutorialPublished;
    private string? pendingTutorialSave;
    private GuiElementTextInput? tutorialTitleInput, tutorialSearchInput;
    private GuiElementAnnouncementEditor? tutorialEditor;
    private GuiElementContainer? tutorialContent;
    private string tutorialScrollbarKey = "", composedTutorialView = "", renderedTutorial = "";
    private AnnouncementInteraction tutorialInteraction = new();
    private string TutorialView => tutorialDraft != null ? $"editor:{tutorialId}:{tutorialPreview}"
        : tutorialReadId.Length > 0 ? "reader:" + tutorialReadId : $"list:{tutorialOnlyMine}:{tutorialQuery}";

    private IEnumerable<AnnouncementEntry> VisibleTutorials => snapshot.Tutorials
        .Where(t => t.Published || t.AuthorUid == capi.World.Player.PlayerUID || snapshot.CanApproveTutorials)
        .Where(t => !tutorialOnlyMine || t.AuthorUid == capi.World.Player.PlayerUID)
        .Where(t => tutorialQuery.Length == 0 || t.Title.Contains(tutorialQuery, StringComparison.OrdinalIgnoreCase)
            || t.AuthorName.Contains(tutorialQuery, StringComparison.OrdinalIgnoreCase)
            || t.Html.Contains(tutorialQuery, StringComparison.OrdinalIgnoreCase));

    private void ComposeTutorial(GuiComposer composer, ElementBounds view)
    {
        if (tutorialDraft == null && tutorialReadId.Length > 0 && VisibleTutorials.FirstOrDefault(t => t.Id == tutorialReadId) is { } reading)
        { ComposeTutorialReader(composer, view, reading); return; }
        tutorialReadId = "";
        if (tutorialDraft != null) { ComposeTutorialEditor(composer, view); return; }
        double onlyMineWidth = Math.Min(152, Math.Max(84, view.fixedWidth * .3));
        tutorialSearchInput = new GuiElementTextInput(capi, ElementBounds.Fixed(view.fixedX, view.fixedY, view.fixedWidth - onlyMineWidth - 12, 32), value => { tutorialQuery = value; dirty = true; }, AnnouncementFonts.Readable(CairoFont.TextInput(), L("tutorial-search")));
        tutorialSearchInput.SetPlaceHolderText(L("tutorial-search")); tutorialSearchInput.SetValue(tutorialQuery);
        composer.AddInteractiveElement(tutorialSearchInput, "tutorial-search");
        double onlyMineX = view.fixedX + view.fixedWidth - onlyMineWidth;
        var onlyMine = new GuiElementSwitch(capi, value => { tutorialOnlyMine = value; dirty = true; }, ElementBounds.Fixed(onlyMineX, view.fixedY + 4, 24, 24), 24) { On = tutorialOnlyMine };
        composer.AddInteractiveElement(onlyMine, "tutorial-only-mine");
        composer.AddStaticText(L("tutorial-only-mine"), CairoFont.WhiteDetailText(), ElementBounds.Fixed(onlyMineX + 30, view.fixedY + 6, onlyMineWidth - 30, 24));
        double footerY = view.fixedY + view.fixedHeight - 32;
        var clip = ElementBounds.Fixed(view.fixedX, view.fixedY + 42, view.fixedWidth - 20, Math.Max(36, footerY - (view.fixedY + 42) - 8));
        var tutorials = VisibleTutorials.ToList();
        bool compact = clip.fixedWidth < 340;
        double rowHeight = compact ? 84 : 48, actionY = compact ? 50 : 8;
        double actionScale = Math.Min(1, (clip.fixedWidth - 16) / 204);
        var body = ElementBounds.Fixed(0, 0, clip.fixedWidth, Math.Max(clip.fixedHeight, tutorials.Count * rowHeight + 8));
        composer.BeginClip(clip).AddContainer(body, "tutorial-content");
        var container = composer.GetContainer("tutorial-content");
        tutorialContent = container; tutorialScrollbarKey = "tutorial-scrollbar";
        double y = 4;
        foreach (var tutorial in tutorials)
        {
            bool canEdit = tutorial.AuthorUid == capi.World.Player.PlayerUID || snapshot.CanApproveTutorials;
            bool canDelete = canEdit;
            bool canPublish = snapshot.CanApproveTutorials && !tutorial.Published;
            double right = clip.fixedWidth - 8;
            double actionStart = right;
            var exclusions = new List<ElementBounds>();
            if (canDelete)
            {
                actionStart -= 64 * actionScale;
                var remove = new GuiElementTextButton(capi, L("tutorial-delete"), CairoFont.SmallButtonText(EnumButtonStyle.Small), CairoFont.WhiteSmallText(),
                    () => { DeleteTutorial(tutorial); return true; }, ElementBounds.Fixed(actionStart, y + actionY, 60 * actionScale, 28), EnumButtonStyle.Small);
                container.Add(remove);
                exclusions.Add(ElementBounds.Fixed(actionStart, actionY, 60 * actionScale, 28));
            }
            if (canEdit)
            {
                actionStart -= 64 * actionScale;
                var edit = new GuiElementTextButton(capi, L("tutorial-edit"), CairoFont.SmallButtonText(EnumButtonStyle.Small), CairoFont.WhiteSmallText(),
                    () => { BeginTutorial(tutorial); return true; }, ElementBounds.Fixed(actionStart, y + actionY, 60 * actionScale, 28), EnumButtonStyle.Small);
                container.Add(edit);
                exclusions.Add(ElementBounds.Fixed(actionStart, actionY, 60 * actionScale, 28));
            }
            if (canPublish)
            {
                actionStart -= 76 * actionScale;
                var approve = new GuiElementTextButton(capi, L("tutorial-publish"), CairoFont.SmallButtonText(EnumButtonStyle.Small), CairoFont.WhiteSmallText(),
                    () => { PublishTutorial(tutorial); return true; }, ElementBounds.Fixed(actionStart, y + actionY, 72 * actionScale, 28), EnumButtonStyle.Small);
                container.Add(approve);
                exclusions.Add(ElementBounds.Fixed(actionStart, actionY, 72 * actionScale, 28));
            }
            // The handbook uses a whole-row hit target.  Keep the action
            // controls out of that target so clicking Edit/Delete/Publish
            // never opens the tutorial accidentally.
            double textWidth = compact ? clip.fixedWidth - 8 : actionStart - 8;
            var title = new GuiElementTutorialLink(capi, tutorial.Title,
                ElementBounds.Fixed(0, y, clip.fixedWidth, rowHeight),
                exclusions,
                () => { tutorialReadId = tutorial.Id; dirty = true; },
                tutorial.AuthorUid == capi.World.Player.PlayerUID || snapshot.CanApproveTutorials
                    ? L(tutorial.Published ? "tutorial-public-status" : "tutorial-unpublished-status") : null,
                tutorial.Published, textWidth);
            container.Add(title);
            string author = tutorial.AuthorName + (tutorial.Published ? "" : " · " + L("tutorial-pending"));
            var authorFont = AnnouncementFonts.Readable(CairoFont.WhiteDetailText(), author);
            container.Add(new GuiElementWrappedText(capi, AnnouncementFonts.SingleLine(authorFont, author, GuiElement.scaled(textWidth - 8)),
                ElementBounds.Fixed(8, y + 26, textWidth - 8, 20), authorFont));
            y += rowHeight;
        }
        composer.EndClip().AddInteractiveElement(new GuiElementAutoScrollbar(capi, value => { body.fixedY = -value / Math.Max(.25, RuntimeEnv.GUIScale); body.CalcWorldBounds(); }, ElementBounds.Fixed(view.fixedX + view.fixedWidth - 16, clip.fixedY, 16, clip.fixedHeight)), "tutorial-scrollbar");
        var add = new GuiElementTextButton(capi, L("tutorial-new"), CairoFont.SmallButtonText(EnumButtonStyle.Small), CairoFont.WhiteSmallText(), () => { BeginTutorial(null); return true; }, ElementBounds.Fixed(view.fixedX, footerY, 112, 28), EnumButtonStyle.Small);
        composer.AddInteractiveElement(add, "tutorial-new");
    }

    private void BeginTutorial(AnnouncementEntry? entry)
    {
        tutorialReadId = "";
        tutorialId = entry?.Id ?? ""; tutorialTitle = entry?.Title ?? ""; tutorialDraft = entry == null ? "" : entry.IsMarkdown ? AnnouncementMarkdown.ToHtml(entry.Html) : entry.Html;
        tutorialPublished = entry?.Published == true; tutorialDraftRevision = snapshot.TutorialRevision; tutorialPreview = false; dirty = true;
    }
    private void PublishTutorial(AnnouncementEntry entry)
    {
        if (pendingTutorialSave != null) return;
        pendingTutorialSave = Guid.NewGuid().ToString("N");
        send(new CivilizationRequestPacket { Action = "tutorial-save", Target = entry.Id, Name = entry.Title, Html = entry.Html,
            Revision = snapshot.TutorialRevision, Notify = true, RequestId = pendingTutorialSave });
    }

    private void DeleteTutorial(AnnouncementEntry entry)
    {
        if (pendingTutorialSave != null) return;
        pendingTutorialSave = Guid.NewGuid().ToString("N");
        send(new CivilizationRequestPacket { Action = "tutorial-delete", Target = entry.Id, Revision = snapshot.TutorialRevision, RequestId = pendingTutorialSave });
    }

    private void ComposeTutorialReader(GuiComposer composer, ElementBounds view, AnnouncementEntry entry)
    {
        double footerY = view.fixedY + view.fixedHeight - 32;
        var back = new GuiElementTextButton(capi, L("tutorial-back"), CairoFont.SmallButtonText(EnumButtonStyle.Small), CairoFont.WhiteSmallText(),
            () => { tutorialReadId = ""; dirty = true; return true; }, ElementBounds.Fixed(view.fixedX, footerY, 76, 28), EnumButtonStyle.Small);
        composer.AddInteractiveElement(back, "tutorial-back");
        var titleFont = AnnouncementFonts.Readable(CairoFont.WhiteSmallishText(), entry.Title);
        composer.AddStaticText(AnnouncementFonts.SingleLine(titleFont, entry.Title, GuiElement.scaled(view.fixedWidth - 12)), titleFont,
            ElementBounds.Fixed(view.fixedX, view.fixedY + 2, view.fixedWidth - 12, 28));
        string author = entry.AuthorName + (entry.Published ? "" : " · " + L("tutorial-pending"));
        var authorFont = AnnouncementFonts.Readable(CairoFont.WhiteDetailText(), author);
        composer.AddStaticText(AnnouncementFonts.SingleLine(authorFont, author, GuiElement.scaled(view.fixedWidth - 12)), authorFont,
            ElementBounds.Fixed(view.fixedX, view.fixedY + 28, view.fixedWidth - 12, 22));
        if (entry.AuthorUid == capi.World.Player.PlayerUID || snapshot.CanApproveTutorials)
        {
            var edit = new GuiElementTextButton(capi, L("tutorial-edit"), CairoFont.SmallButtonText(EnumButtonStyle.Small), CairoFont.WhiteSmallText(),
                () => { BeginTutorial(entry); return true; }, ElementBounds.Fixed(view.fixedX + 84, footerY, 76, 28), EnumButtonStyle.Small);
            composer.AddInteractiveElement(edit, "tutorial-reader-edit");
        }
        double bodyTop = view.fixedY + 56;
        double bodyHeight = footerY - bodyTop - 8;
        var clip = ElementBounds.Fixed(view.fixedX, bodyTop, view.fixedWidth - 20, Math.Max(40, bodyHeight));
        var body = ElementBounds.Fixed(0, 0, clip.fixedWidth, clip.fixedHeight);
        composer.BeginClip(clip).AddInteractiveElement(new GuiElementAnnouncementContainer(capi, body), "tutorial-reader-content");
        var container = composer.GetContainer("tutorial-reader-content");
        tutorialContent = container; tutorialScrollbarKey = "tutorial-reader-scrollbar";
        string source = entry.IsMarkdown ? AnnouncementMarkdown.ToHtml(entry.Html) : entry.Html;
        AddTutorialArticle(composer, container, clip, source);
        composer.EndClip().AddInteractiveElement(new GuiElementAutoScrollbar(capi, value => { body.fixedY = -value / Math.Max(.25, RuntimeEnv.GUIScale); body.CalcWorldBounds(); },
            ElementBounds.Fixed(view.fixedX + view.fixedWidth - 16, clip.fixedY, 16, clip.fixedHeight)), "tutorial-reader-scrollbar");
    }

    private void ComposeTutorialEditor(GuiComposer composer, ElementBounds view)
    {
        tutorialTitleInput = new GuiElementTextInput(capi, ElementBounds.Fixed(view.fixedX, view.fixedY, view.fixedWidth - 12, 32), value => tutorialTitle = value, AnnouncementFonts.Readable(CairoFont.TextInput(), L("tutorial-title")));
        tutorialTitleInput.SetValue(tutorialTitle); composer.AddInteractiveElement(tutorialTitleInput, "tutorial-title");
        double footerY = view.fixedY + view.fixedHeight - 32;
        bool separatePublishRow = snapshot.CanApproveTutorials && view.fixedWidth < 394;
        double publishY = separatePublishRow ? footerY - 30 : footerY;
        double actionWidth = view.fixedWidth - (snapshot.CanApproveTutorials && !separatePublishRow ? 150 : 0);
        double buttonWidth = Math.Min(76, (actionWidth - 16) / 3);
        var preview = new GuiElementTextButton(capi, tutorialPreview ? L("tutorial-edit") : L("tutorial-preview"), CairoFont.SmallButtonText(EnumButtonStyle.Small), CairoFont.WhiteSmallText(), () => { tutorialPreview = !tutorialPreview; dirty = true; return true; }, ElementBounds.Fixed(view.fixedX, footerY, buttonWidth, 28), EnumButtonStyle.Small);
        var save = new GuiElementTextButton(capi, L("save"), CairoFont.SmallButtonText(EnumButtonStyle.Small), CairoFont.WhiteSmallText(), () => { SaveTutorialDraft(); return true; }, ElementBounds.Fixed(view.fixedX + buttonWidth + 8, footerY, buttonWidth, 28), EnumButtonStyle.Small);
        var cancel = new GuiElementTextButton(capi, L("cancel"), CairoFont.SmallButtonText(EnumButtonStyle.Small), CairoFont.WhiteSmallText(), () => { tutorialDraft = null; dirty = true; return true; }, ElementBounds.Fixed(view.fixedX + 2 * (buttonWidth + 8), footerY, buttonWidth, 28), EnumButtonStyle.Small);
        composer.AddInteractiveElement(preview, "tutorial-preview"); composer.AddInteractiveElement(save, "tutorial-save"); composer.AddInteractiveElement(cancel, "tutorial-cancel");
        double bodyTop = view.fixedY + 40;
        if (!tutorialPreview)
        {
            double toolbarY = bodyTop;
            double available = view.fixedWidth - 24;
            bool compact = available < 440, iconMenus = compact;
            double toolGap = compact ? 1 : 4;
            int toolCount = AnnouncementHtmlTools.Tools.Length;
            double toolSize = Math.Min(32, (available - 3 * 26 - (toolCount + 2) * toolGap) / toolCount);
            double menuWidth = (available - toolCount * toolSize - (toolCount + 2) * toolGap) / 3;
            double x = 0;
            foreach (string tool in AnnouncementHtmlTools.Tools)
            {
                var button = new GuiElementAnnouncementTool(capi, tool, () =>
                {
                    if (tutorialEditor == null) return;
                    tutorialEditor.ApplyHtml(tool);
                    var target = composer.GetContainer("tutorial-editor-content");
                    composer.FocusElement(target.TabIndex); target.FocusElement(tutorialEditor.TabIndex);
                }, ElementBounds.Fixed(view.fixedX + x, toolbarY, toolSize, toolSize));
                composer.AddInteractiveElement(button, "tutorial-html-" + tool);
                string shortcut = tool switch { "bold" => " (Ctrl+B)", "italic" => " (Ctrl+I)", "link" => " (Ctrl+K)", _ => "" };
                var hint = new GuiElementHoverText(capi, Esc(L("html-" + tool) + shortcut), AnnouncementFonts.Readable(CairoFont.WhiteSmallText(), L("html-" + tool) + shortcut),
                    (int)Math.Min(220, view.fixedWidth), button.Bounds.FlatCopy(), OpaqueTooltipBackground());
                hint.SetAutoWidth(true); composer.AddInteractiveElement(hint, "tutorial-html-hint-" + tool);
                x += toolSize + toolGap;
            }
            void AddMenu(string title, string[] options, int column, Action<string> action)
            {
                var values = new[] { "" }.Concat(options).ToArray();
                var labels = new[] { L("html-" + title) }.Concat(options.Select(t => L("html-" + t))).ToArray();
                GuiElementDropDown? menu = null;
                menu = new GuiElementAnnouncementMenu(capi, title, iconMenus, values, labels, (value, _) =>
                {
                    if (value.Length == 0) return;
                    action(value); menu!.SetSelectedIndex(0);
                    if (title != "embed" && tutorialEditor != null)
                    { var target = composer.GetContainer("tutorial-editor-content"); composer.FocusElement(target.TabIndex); target.FocusElement(tutorialEditor.TabIndex); }
                }, ElementBounds.Fixed(view.fixedX + x + column * (menuWidth + toolGap), toolbarY, menuWidth, toolSize),
                    AnnouncementFonts.Readable(CairoFont.WhiteSmallText().WithFontSize(compact ? 12 : 14), string.Join("", labels)));
                composer.AddInteractiveElement(menu, "tutorial-html-" + title);
                var hint = new GuiElementHoverText(capi, Esc(L("html-" + title)), AnnouncementFonts.Readable(CairoFont.WhiteSmallText(), L("html-" + title)), 180, menu.Bounds.FlatCopy(), OpaqueTooltipBackground());
                hint.SetAutoWidth(true); composer.AddInteractiveElement(hint, "tutorial-html-hint-" + title);
            }
            AddMenu("format", AnnouncementHtmlTools.FormatTools, 0, tool => tutorialEditor?.ApplyHtml(tool));
            AddMenu("insert", AnnouncementHtmlTools.InsertTools, 1, tool => tutorialEditor?.ApplyHtml(tool));
            AddMenu("embed", ["handbook", "itemhandbook", "item", "recipe", "slideshow"], 2, OpenTutorialResource);
            bodyTop = toolbarY + Math.Max(toolSize, 28) + (compact ? 4 : 8);
        }
        double bodyHeight = publishY - bodyTop - 8;
        var clip = ElementBounds.Fixed(view.fixedX, bodyTop, view.fixedWidth - 20, Math.Max(40, bodyHeight));
        var body = ElementBounds.Fixed(0, 0, clip.fixedWidth, clip.fixedHeight);
        composer.BeginClip(clip).AddInteractiveElement(new GuiElementAnnouncementContainer(capi, body), "tutorial-editor-content");
        var container = composer.GetContainer("tutorial-editor-content");
        tutorialContent = container; tutorialScrollbarKey = "tutorial-editor-scrollbar";
        if (tutorialPreview)
        {
            AddTutorialArticle(composer, container, clip, tutorialDraft ?? "");
        }
        else
        {
            tutorialEditor = HtmlDocumentApi.CreateEditor(capi, ElementBounds.Fixed(0, 0, clip.fixedWidth - 2, clip.fixedHeight - 8), value =>
            {
                tutorialDraft = value;
                if (composer.Composed && tutorialEditor != null) UpdateTutorialScroll(composer);
            }, CairoFont.TextInput());
            tutorialEditor.OnTryTextChangeText = lines => lines.Sum(line => line.Length) <= AnnouncementState.MaxLength;
            container.Add(tutorialEditor); tutorialEditor.Bounds.CalcWorldBounds(); tutorialEditor.SetValue(tutorialDraft ?? "", false);
            tutorialEditor.OnCursorMoved = (_, y) =>
            {
                if (!composer.Composed) return;
                UpdateTutorialScroll(composer);
                var bar = composer.GetScrollbar(tutorialScrollbarKey);
                double height = clip.InnerHeight;
                double top = bar.CurrentYPosition;
                double lineHeight = tutorialEditor.Font.GetFontExtents().Height;
                if (y < top || y + lineHeight > top + height)
                {
                    bar.CurrentYPosition = (float)Math.Clamp(y < top ? y : y + lineHeight - height, 0,
                        Math.Max(0, tutorialEditor.ContentHeight - height));
                    bar.TriggerChanged();
                }
            };
        }
        container.Tabbable = container.FirstTabbableElement != null;
        composer.EndClip().AddInteractiveElement(new GuiElementAutoScrollbar(capi, value =>
        {
            if (tutorialEditor != null) tutorialEditor.ScrollTo(value);
            else { body.fixedY = -value / Math.Max(.25, RuntimeEnv.GUIScale); body.CalcWorldBounds(); }
        }, ElementBounds.Fixed(view.fixedX + view.fixedWidth - 16, clip.fixedY, 16, clip.fixedHeight)), "tutorial-editor-scrollbar");
        if (snapshot.CanApproveTutorials)
        {
            var allow = new GuiElementSwitch(capi, value => tutorialPublished = value, ElementBounds.Fixed(view.fixedX + view.fixedWidth - 150, publishY + 2, 24, 24), 24) { On = tutorialPublished };
            composer.AddInteractiveElement(allow, "tutorial-published"); composer.AddStaticText(L("tutorial-publish"), CairoFont.WhiteDetailText(), ElementBounds.Fixed(view.fixedX + view.fixedWidth - 120, publishY + 5, 112, 24));
        }
    }

    private void AddTutorialArticle(GuiComposer composer, GuiElementContainer container, ElementBounds clip, string source)
    {
        if (renderedTutorial != TutorialView + "\n" + source)
        { renderedTutorial = TutorialView + "\n" + source; tutorialInteraction = new(); }
        tutorialInteraction.Dirty = false;
        var article = HtmlDocumentApi.CreateReader(capi, string.IsNullOrWhiteSpace(source) ? Esc(L("tutorial-empty")) : source,
            ElementBounds.Fixed(0, 0, clip.fixedWidth - 2, 20), tutorialInteraction);
        tutorialInteraction.Navigate = href =>
        {
            if (!href.StartsWith('#') || article.Anchor(Uri.UnescapeDataString(href[1..])) is not { } target) return;
            var bar = composer.GetScrollbar(tutorialScrollbarKey);
            bar.CurrentYPosition = (float)Math.Clamp(target, 0, Math.Max(0, container.Bounds.OuterHeight - clip.InnerHeight));
            bar.TriggerChanged();
        };
        container.Add(article);
    }

    // Resolve viewport/document dimensions only after the composer has laid out
    // the article. Editing, image completion and disclosure changes reuse this.
    private void UpdateTutorialScroll(GuiComposer composer, float? position = null)
    {
        if (tutorialContent == null) return;
        var bar = (GuiElementAutoScrollbar)composer.GetScrollbar(tutorialScrollbarKey);
        double viewport = tutorialContent.Bounds.ParentBounds.InnerHeight;
        double document = Math.Max(viewport, tutorialEditor?.ContentHeight ?? tutorialContent.Bounds.OuterHeight);
        bar.SetContentHeights((float)viewport, (float)document);
        if (position is { } value) bar.CurrentYPosition = Math.Clamp(value, 0, (float)(document - viewport));
        bar.TriggerChanged();
    }

    private void SaveTutorialDraft()
    {
        if (pendingTutorialSave != null || tutorialDraft == null) return;
        pendingTutorialSave = Guid.NewGuid().ToString("N");
        send(new CivilizationRequestPacket { Action = tutorialId.Length == 0 ? "tutorial-create" : "tutorial-save", Target = tutorialId, Name = tutorialTitle, Html = tutorialDraft, Revision = tutorialDraftRevision, Notify = tutorialPublished, RequestId = pendingTutorialSave });
    }
}
