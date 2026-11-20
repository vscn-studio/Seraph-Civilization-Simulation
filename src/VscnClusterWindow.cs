using System.Globalization;
using System.Security;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnClusterWindow : GuiDialog
{
    internal const int AnnouncementPage = 0, TutorialPage = 1, TribePage = 2, EconomyPage = 3;
    private const string Owner = "vsscsmod";
    private readonly Action<CivilizationRequestPacket> send;
    private CivilizationSnapshotPacket snapshot = new();
    private string tribeName = "", renameName = "";
    private GuiElementTextInput? nameInput;
    private GuiElementContainer? content;
    private GuiElementScrollbar? draggedAnnouncementScrollbar;
    private bool renameEditing, dirty = true, disposed, pointerPressed, titleBarMenuOpen;
    private double windowWidth, windowHeight, guiScale;
    private int composedFrameWidth, composedFrameHeight;
    private string composedLocale = "", composedStandardFont = "", composedDecorativeFont = "";
    private string civilizationMembershipFingerprint = "", composedAnnouncementId = "";

    public override string ToggleKeyCombinationCode => null!;
    public override bool UnregisterOnClose => true;
    public bool IsVisible => !disposed && IsOpened();

    public VscnClusterWindow(ICoreClientAPI api, Action<CivilizationRequestPacket> sendAction, int initialPage = AnnouncementPage) : base(api)
    { send=sendAction; page=initialPage; }
    internal int WindowPage => page;
    internal void SelectWindowForTest(int value) { page=value; dirty=true; }
    private string WindowTitle => page switch { AnnouncementPage=>L("announcement"),TutorialPage=>L("tutorial"),TribePage=>L("tribes"),_=>"交易" };
    private string WindowId => "vsscs-window-"+page;

    private string L(string key, params object[] args) => Lang.Get(Owner + ":ui." + key, args);
    private static string Esc(string? text) => SecurityElement.Escape(text ?? "") ?? "";
    private void Action(string action, string target = "", string name = "")
        => send(new CivilizationRequestPacket { Action = action, Target = target, Name = name });

    public void Close() { if (!disposed) TryClose(); }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        // Closing unsubscribes from inventory updates; reopening may retain the economy page.
        if (page == EconomyPage) EconomyAction(new());
    }

    public override void OnGuiClosed()
    {
        (SingleComposer?.GetElement("economy-content") as EconomyGuiContainer)?.ClearHover();
        if (page == EconomyPage) EconomyAction(new() { Action = "close" });
        if (page == TribePage && tribePage == 0) { Action("territory-clear-preview"); territoryPreview = false; }
        Action("close");
        renameEditing = false;
        pointerPressed = pickerPointerPressed = titleBarMenuOpen = false;
        draggedAnnouncementScrollbar = null;
        dirty = true;
        // Retain the editor draft while another main window is open.
        CloseAnnouncementResource();
        base.OnGuiClosed();
    }

    public void Update(CivilizationSnapshotPacket value)
    {
        if (disposed) return;

        string membership = Newtonsoft.Json.JsonConvert.SerializeObject((value.MyTribeId, value.IsLeader, value.Members));
        bool economyMembersChanged = membership != civilizationMembershipFingerprint;
        civilizationMembershipFingerprint = membership;
        snapshot = value;
        authorizationReceivedAt = Environment.TickCount64;
        if (pendingSave != null && value.AnnouncementSaveRequestId == pendingSave)
        {
            pendingSave = null;
            if (value.AnnouncementSaved)
            {
                announcementDrafts.Remove(announcementId);
                if (newAnnouncement) announcementId = value.Announcements.LastOrDefault()?.Id ?? "";
                announcementDraft = null; announcementPreview = false; newAnnouncement = false;
            }
            else draftRevision = value.AnnouncementRevision;
            dirty = true;
        }
        if (pendingTutorialSave != null && value.TutorialSaveRequestId == pendingTutorialSave)
        {
            pendingTutorialSave = null;
            if (value.TutorialSaved) { tutorialDraft = null; tutorialPreview = false; tutorialId = ""; }
            else tutorialDraftRevision = value.TutorialRevision;
            dirty = true;
        }
        if (value.MyTribeId == null || !value.IsLeader) renameEditing = false;
        if (page != EconomyPage || economyMembersChanged) dirty = true;
        if (value.NotificationId.Length > 0)
        {
            StashAnnouncementDraft(); announcementId = value.NotificationId; announcementDraft = null;
            announcementPreview = false; page = AnnouncementPage; dirty = true;
        }
        if (value.Open && !IsOpened())
        {
            ComposeWindow();
            TryOpen();
        }
        if (value.NotificationId.Length > 0 && IsOpened())
        {
            if (dirty) ComposeWindow();
            send(new() { Action = "announcement-seen", Target = value.NotificationId, Revision = value.NotificationRevision });
        }
    }

    public override void OnRenderGUI(float deltaTime)
    {
        if (disposed || !IsOpened()) return;
        TickAuthorizationClocks();
        if (page == EconomyPage) RefreshPickerCache();
        if (page == AnnouncementPage && content?.Elements.OfType<GuiElementAnnouncement>().Any(c => c.HasNewImages) == true) dirty = true;
        if (page == TutorialPage && tutorialContent?.Elements.OfType<GuiElementAnnouncement>().Any(c => c.HasNewImages) == true) dirty = true;
        if (!pointerPressed && !titleBarMenuOpen && !EconomyTyping &&
            (dirty || composedFrameWidth != capi.Render.FrameWidth || composedFrameHeight != capi.Render.FrameHeight
                || guiScale != RuntimeEnv.GUIScale || composedLocale != Lang.CurrentLocale
                || composedStandardFont != GuiStyle.StandardFontName || composedDecorativeFont != GuiStyle.DecorativeFontName))
            ComposeWindow();
        bool resourceEditorOpen = page == AnnouncementPage
            ? announcementEditor != null && snapshot.CanEditAnnouncement
            : page == TutorialPage && tutorialEditor != null;
        if (!resourceEditorOpen || announcementResourcePicker?.CloseRequested == true)
            CloseAnnouncementResource();
        if (pendingResourceCategory is { } category)
        { pendingResourceCategory = null; ComposeAnnouncementResource(category); }
        if (announcementResourcePicker is { } resourcePicker)
        {
            if (resourcePicker.Category is "item" or "itemhandbook" or "slideshow" && Environment.TickCount64 >= resourceRefreshAt
                && (resourceCatalogRevision != (economyCatalog?.Revision ?? 0) || resourceSnapshotCount != economySnapshot.Items.Count))
            {
                resourceRefreshAt = Environment.TickCount64 + 500;
                resourceCatalogRevision = economyCatalog?.Revision ?? 0; resourceSnapshotCount = economySnapshot.Items.Count;
                resourcePicker.Refresh(AnnouncementGameResources.Catalog(capi, resourcePicker.Category, AnnouncementResourceItems()));
            }
            resourcePicker.Tick(); Composers["announcement-resource"] = resourcePicker.Composer;
        }
        base.OnRenderGUI(deltaTime);
    }

    private (double Width, double Height) GetWindowSize()
    {
        double scale = Math.Max(.25, RuntimeEnv.GUIScale);
        // Side tabs and the item picker never change the centered main window.
        return (Math.Clamp(capi.Render.FrameWidth / scale - 2 * Math.Max(TabWidth, 76) - 24, 240, 760),
            Math.Clamp(capi.Render.FrameHeight / scale - 48, 240, 600));
    }

    private GuiComposer? SidePickerComposer => Composers["announcement-resource"] ?? PickerComposer;

    public override void OnMouseDown(MouseEvent args)
    {
        pointerPressed = true;
        titleBarMenuOpen = false;
        if (SidePickerComposer is { } picker && picker.Bounds.PointInside(args.X, args.Y))
        {
            SingleComposer?.UnfocusOwnElements();
            (content as EconomyGuiContainer)?.ClearHover();
            pickerPointerPressed = true;
            picker.OnMouseDown(args); args.Handled = true; return;
        }
        SidePickerComposer?.UnfocusOwnElements();
        // Scrolling the HTML viewport must retain its caret, selection and keyboard focus.
        string editorScrollbar = tutorialEditor != null ? "tutorial-editor-scrollbar" : "scrollbar";
        if ((announcementEditor != null || tutorialEditor != null) && SingleComposer?.GetElement(editorScrollbar) is GuiElementScrollbar bar
            && bar.Focusable && bar.IsPositionInside(args.X, args.Y) && args.Button == EnumMouseButton.Left)
        {
            bar.OnMouseDown(capi, args);
            if (args.Handled) { draggedAnnouncementScrollbar = bar; return; }
        }
        base.OnMouseDown(args);
    }

    public override void OnMouseMove(MouseEvent args)
    {
        if (SidePickerComposer is { } picker)
        {
            picker.OnMouseMove(args);
            if (args.Handled || pickerPointerPressed || picker.Bounds.PointInside(args.X, args.Y))
            { (content as EconomyGuiContainer)?.ClearHover(); args.Handled = true; return; }
        }
        if (draggedAnnouncementScrollbar != null)
        {
            draggedAnnouncementScrollbar.OnMouseMove(capi, args);
            args.Handled = true;
            return;
        }
        base.OnMouseMove(args);
    }

    public override void OnMouseWheel(MouseWheelEventArgs args)
    {
        if (SidePickerComposer is { } picker && picker.Bounds.PointInside(capi.Input.MouseX, capi.Input.MouseY))
        { picker.OnMouseWheel(args); args.SetHandled(); return; }
        if (ScrollTribeFooter(args)) return;
        base.OnMouseWheel(args);
    }

    public override void OnMouseUp(MouseEvent args)
    {
        if (pickerPointerPressed)
        {
            SidePickerComposer?.OnMouseUp(args);
            pointerPressed = pickerPointerPressed = false;
            args.Handled = true; return;
        }
        if (draggedAnnouncementScrollbar != null)
        {
            draggedAnnouncementScrollbar.OnMouseUp(capi, args);
            draggedAnnouncementScrollbar = null;
            pointerPressed = false;
            args.Handled = true;
            return;
        }
        base.OnMouseUp(args);
        pointerPressed = false;
        var bounds = SingleComposer?.Bounds;
        if (IsOpened() && bounds != null)
            titleBarMenuOpen = args.X >= bounds.renderX + bounds.OuterWidth - 70 * guiScale &&
                args.X <= bounds.renderX + bounds.OuterWidth &&
                args.Y >= bounds.renderY && args.Y <= bounds.renderY + 36 * guiScale;
    }

    public override void OnKeyDown(KeyEvent args)
    {
        if (announcementResourcePicker != null && args.KeyCode == (int)GlKeys.Escape)
        { CloseAnnouncementResource(); args.Handled = true; return; }
        if (announcementResourcePicker is { } activeResource && activeResource.Composer.GetTextInput("search").HasFocus)
        { activeResource.Composer.OnKeyDown(args, Focused); if (args.Handled) return; }
        if (PickerComposer is { } picker && (picker.GetTextInput("economy-picker-search").HasFocus || economyPicker?.HasFocus == true))
        { picker.OnKeyDown(args, Focused); if (args.Handled) return; }
        if (tutorialEditor?.HasFocus == true)
        {
            tutorialEditor.OnKeyDown(capi, args);
            args.Handled = true;
            return;
        }
        if (args.KeyCode == (int)GlKeys.Escape) titleBarMenuOpen = false;
        base.OnKeyDown(args);
    }

    public override void OnKeyPress(KeyEvent args)
    {
        if (tutorialEditor?.HasFocus == true)
        {
            tutorialEditor.OnKeyPress(capi, args);
            args.Handled = true;
            return;
        }
        base.OnKeyPress(args);
    }

    public override bool OnEscapePressed()
    {
        if (announcementResourcePicker != null) { CloseAnnouncementResource(); return true; }
        if (offerEditor?.Picking != null) { offerEditor.ClosePicker(); return true; }
        return base.OnEscapePressed();
    }

    private void ComposeWindow()
    {
        var size = GetWindowSize();
        composedFrameWidth = capi.Render.FrameWidth; composedFrameHeight = capi.Render.FrameHeight;
        composedLocale = Lang.CurrentLocale;
        composedStandardFont = GuiStyle.StandardFontName; composedDecorativeFont = GuiStyle.DecorativeFontName;
        bool restoreFocus = nameInput?.HasFocus == true;
        int caret = nameInput?.CaretPosInLine ?? 0;
        int authorizationFocus = authorizationInputs.FindIndex(i => i.HasFocus);
        int authorizationCaret = authorizationFocus < 0 ? 0 : authorizationInputs[authorizationFocus].CaretPosInLine;
        bool titleFocus = announcementTitleInput?.HasFocus == true;
        int titleCaret = announcementTitleInput?.CaretPosInLine ?? 0;
        bool editorFocus = announcementEditor?.HasFocus == true;
        int editorLine = announcementEditor?.CaretPosLine ?? 0, editorCaret = announcementEditor?.CaretPosInLine ?? 0;
        bool tutorialEditorFocus = tutorialEditor?.HasFocus == true;
        int tutorialEditorLine = tutorialEditor?.CaretPosLine ?? 0,
            tutorialEditorCaret = tutorialEditor?.CaretPosInLine ?? 0;
        var focusedOfferInput = offerEditor?.Inputs.FindIndex(i => i.HasFocus) ?? -1;
        int offerCaret = focusedOfferInput >= 0 ? offerEditor!.Inputs[focusedOfferInput].CaretPosInLine : 0;
        int offerLine = focusedOfferInput >= 0 ? offerEditor!.Inputs[focusedOfferInput].CaretPosLine : 0;
        float scroll = SingleComposer?.GetElement("scrollbar") is GuiElementScrollbar oldScrollbar
            ? oldScrollbar.CurrentYPosition : 0;
        float tutorialScroll = page == TutorialPage && composedPage == TutorialPage && composedTutorialView == TutorialView
            && SingleComposer?.GetElement(tutorialScrollbarKey) is GuiElementScrollbar oldTutorialScrollbar
            ? oldTutorialScrollbar.CurrentYPosition : 0;
        if (page != composedPage) scroll = 0;
        if (page == TribePage && tribePage != composedTribePage) scroll = 0;
        if (page == AnnouncementPage && composedAnnouncementId != announcementId) scroll = 0;
        if (page == EconomyPage && economyPage != composedEconomyPage) scroll = 0;
        if (page == EconomyPage && !ReferenceEquals(offerEditor, composedOfferEditor)) scroll = 0;
        var bounds = ElementBounds.Fixed(0, 0, size.Width, size.Height).WithAlignment(EnumDialogArea.CenterMiddle);
        capi.Gui.SetDialogPosition(WindowId, null);
        var hovered = (SingleComposer?.GetElement("economy-content") as EconomyGuiContainer)?.DetachHover();
        // The helper owns its composer and restores search state after a parent resize/rebuild.
        Composers.Remove("announcement-resource");
        ClearComposers();
        announcementEditor = null; tutorialEditor = null;
        nameInput = null; announcementTitleInput = null;
        content = null;
        tutorialContent = null; tutorialScrollbarKey = "";
        windowWidth = size.Width;
        windowHeight = size.Height;
        guiScale = RuntimeEnv.GUIScale;
        var composer = capi.Gui.CreateCompo(WindowId, bounds)
            // An opaque base also keeps the dialog solid when the game's GUI colors use alpha.
            .AddStaticCustomDraw(ElementBounds.Fill, (ctx, _, current) =>
            {
                ctx.SetSourceRGB(GuiStyle.DialogStrongBgColor[0], GuiStyle.DialogStrongBgColor[1], GuiStyle.DialogStrongBgColor[2]);
                GuiElement.RoundRectangle(ctx, current.drawX, current.drawY, current.InnerWidth, current.InnerHeight, GuiStyle.DialogBGRadius);
                ctx.Fill();
            })
            .AddShadedDialogBG(ElementBounds.Fill, alpha: 1)
            .AddDialogTitleBar(WindowTitle, Close, key: "titlebar");
        SingleComposer = composer;
        var view = ElementBounds.Fixed(20, 56, windowWidth - 40, windowHeight - 76);
        if (page == AnnouncementPage) ComposeAnnouncement(composer, view);
        else if (page == TutorialPage) ComposeTutorial(composer, view);
        else if (page == TribePage) ComposeTribe(composer, view);
        else ComposeEconomy(composer, view);
        composer.Compose(false);
        if (page == TutorialPage)
        {
            UpdateTutorialScroll(composer, tutorialScroll);
            composedTutorialView = TutorialView;
        }
        composedTribePage = tribePage;
        if (page == AnnouncementPage && composer.GetElement("announcement-tabs") is GuiElementVerticalTabs announcementTabs)
            announcementTabs.SetValue(Math.Max(0, AnnouncementEntries.FindIndex(e => e.Id == announcementId)), false);
        composedAnnouncementId = announcementId;
        if (titleFocus && announcementTitleInput != null)
        { composer.FocusElement(announcementTitleInput.TabIndex); announcementTitleInput.SetCaretPos(Math.Min(titleCaret, announcementTitleInput.GetText().Length)); }
        composedEconomyPage = economyPage;
        composedOfferEditor = offerEditor;
        composedPage = page;
        if (page == TribePage && tribePage == 1 && content != null && authorizationFocus >= 0 && authorizationFocus < authorizationInputs.Count)
        {
            var input = authorizationInputs[authorizationFocus];
            composer.FocusElement(content.TabIndex); content.FocusElement(input.TabIndex);
            input.SetCaretPos(Math.Min(authorizationCaret, input.GetText().Length));
        }
        if (content != null)
        {
            var scrollbar = (GuiElementAutoScrollbar)composer.GetScrollbar("scrollbar");
            // Scrollbar positions and heights use screen pixels; element offsets use GUI units.
            double viewportHeight = content.Bounds.ParentBounds.InnerHeight;
            double contentHeight = announcementEditor?.ContentHeight ?? content.Bounds.OuterHeight;
            scrollbar.SetContentHeights((float)viewportHeight, (float)Math.Max(viewportHeight, contentHeight));
            scrollbar.CurrentYPosition = Math.Clamp(scroll, 0, (float)Math.Max(0, contentHeight - viewportHeight));
            scrollbar.TriggerChanged();
            if (restoreFocus && nameInput != null)
            {
                composer.FocusElement(content.TabIndex);
                content.FocusElement(nameInput.TabIndex);
                nameInput.SetCaretPos(Math.Min(caret, nameInput.GetText().Length));
            }
            if (editorFocus && announcementEditor != null)
            {
                composer.FocusElement(content.TabIndex); content.FocusElement(announcementEditor.TabIndex);
                int line = Math.Clamp(editorLine, 0, announcementEditor.GetLines().Count - 1);
                announcementEditor.SetCaretPos(Math.Min(editorCaret, announcementEditor.GetLines()[line].Length), line);
            }
            if (offerEditor != null && focusedOfferInput >= 0 && focusedOfferInput < offerEditor.Inputs.Count)
            {
                var input = offerEditor.Inputs[focusedOfferInput];
                composer.FocusElement(content.TabIndex); content.FocusElement(input.TabIndex);
                int line = Math.Clamp(offerLine, 0, input.GetLines().Count - 1);
                input.SetCaretPos(Math.Min(offerCaret, input.GetLines()[line].Length), line);
            }
        }
        if (tutorialEditorFocus && tutorialEditor != null
            && composer.GetContainer("tutorial-editor-content") is { } focusedTutorialContent)
        {
            composer.FocusElement(focusedTutorialContent.TabIndex);
            focusedTutorialContent.FocusElement(tutorialEditor.TabIndex);
            int line = Math.Clamp(tutorialEditorLine, 0, tutorialEditor.GetLines().Count - 1);
            tutorialEditor.SetCaretPos(
                Math.Min(tutorialEditorCaret, tutorialEditor.GetLines()[line].Length), line);
        }
        if (composer.GetElement("economy-content") is EconomyGuiContainer economyContainer) economyContainer.RestoreHover(hovered);
        else if (hovered != null) capi.Input.TriggerOnMouseLeaveSlot(hovered);
        if (page == EconomyPage && offerEditor?.Picking != null) ComposeEconomyPicker();
        economyPicker?.RefreshScroll();
        if (announcementResourcePicker is { } resourcePicker
            && ((page == AnnouncementPage && announcementEditor != null)
                || (page == TutorialPage && tutorialEditor != null)))
        { resourcePicker.Recompose(AnnouncementResourceBounds()); Composers["announcement-resource"] = resourcePicker.Composer; }
        else if (announcementResourcePicker != null) CloseAnnouncementResource();
        dirty = false;

    }

    private static TextBackground OpaqueTooltipBackground()
    {
        var background = GuiElementHoverText.DefaultBackground.Clone();
        background.FillColor[3] = 1;
        return background;
    }

    public override void Dispose()
    {
        if (disposed) return;
        TryClose();
        // Retain the editor draft while another main window is open.
        disposed = true;
        CloseAnnouncementResource();
        base.Dispose();
    }
}
