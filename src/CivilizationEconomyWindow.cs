using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnClusterWindow
{
    private Action<EconomyRequest>? sendEconomy;
    private EconomySnapshot economySnapshot = new();
    private int economyPage, composedEconomyPage = -1;
    private string economyFingerprint = "";
    private readonly Dictionary<string, string> rateDrafts = [];
    private readonly List<GuiElementEditableTextBase> economyInputs = [];
    private EconomyOfferEditor? offerEditor;
    private EconomyClientCatalog? economyCatalog;
    private GuiElementEconomyPickerGrid? economyPicker;
    private int pickerCatalogRevision;
    private long pickerRefreshAt;
    private EconomyOfferEditor? composedOfferEditor;
    private bool pickerPointerPressed;
    private GuiComposer? PickerComposer => Composers["economy-picker"];
    private double PickerSideWidth => Math.Min(280, (capi.Render.FrameWidth / Math.Max(.25, RuntimeEnv.GUIScale) - windowWidth) / 2 - 24);
    private bool CompactPicker => PickerSideWidth < 180;
    private double EconomyTabWidth => 0;
    private bool EconomyTyping => offerEditor == null && page == EconomyPage && economyPage == composedEconomyPage && (economyInputs.Any(i => i.HasFocus)
        || content is EconomyGuiContainer body && body.HasOpenMenu);

    public void SetEconomySender(Action<EconomyRequest> action) => sendEconomy = action;
    public void SetEconomyCatalog(EconomyClientCatalog? catalog) => economyCatalog = catalog;
    public void UpdateEconomy(EconomySnapshot value)
    {
        if (disposed) return;
        if (value.Message.Length > 0) capi.ShowChatMessage(value.Message);
        if (value.PolicyRevision != economySnapshot.PolicyRevision) rateDrafts.Clear();
        if (value.TribeId != economySnapshot.TribeId) { rateDrafts.Clear(); offerEditor = null; }
        string current = EconomyGuiFingerprint.Of(value);
        bool changed = current != economyFingerprint; economyFingerprint = current;
        economySnapshot = value;
        if (!value.CanReadLedger && economyPage == 3) economyPage = 0;
        if (page == EconomyPage && changed && offerEditor == null) dirty = true;
        // Drafts stay intact, but their picker must follow inventory snapshots too.
        // Otherwise opening it before stock arrives leaves an empty grid indefinitely.
        if (page == EconomyPage && changed && offerEditor?.Picking != null) RefreshPickerItems(true);
    }
    private void EconomyAction(EconomyRequest request) => sendEconomy?.Invoke(request);
    private void ChangePage(int index)
    {
        if (index < AnnouncementPage || index > EconomyPage) return;
        if (page == EconomyPage && index != EconomyPage) EconomyAction(new() { Action = "close" });
        page = index; dirty = true;
        if (page == EconomyPage) EconomyAction(new());
        else offerEditor = null;
    }
    private string CurrencyName(string key) => economySnapshot.Coins.FirstOrDefault(c => c.Key == key)?.Name
        ?? L(string.IsNullOrEmpty(key) ? "economy-choice-empty" : "economy-unknown-coin");
    private EconomyCoinView? RecordedCurrency(string key) => economySnapshot.Coins.Concat(economySnapshot.ArchivedCoins).FirstOrDefault(c => c.Key == key);
    private EconomyRequest OfferRequest(EconomyOfferView offer, string action = "offer-save", bool? enabled = null) => new()
    { Action = action, Id = offer.Id, Revision = offer.Revision, Item = offer.ItemKey, Coin = offer.Coin, Text = offer.Name, Content = offer.Content,
        Quantity = offer.Quantity, Price = offer.Price, Enabled = enabled ?? offer.Enabled, IsTask = offer.IsTask, Exchange = offer.Exchange };

    private void ComposeEconomy(GuiComposer composer, ElementBounds view)
    {
        economyInputs.Clear();
        economyPicker = null;
        if (offerEditor != null)
        {
            var editorView = view.FlatCopy();
            editorView.fixedHeight -= offerEditor.BuildFooter(composer, view);
            var editorList = new EconomyGuiList(capi, composer, editorView); content = editorList.Container;
            offerEditor.Build(editorList); editorList.Finish();
            return;
        }
        economyPage = 1;
        var listView = view.FlatCopy();
        if (economyPage == 1)
            listView.fixedHeight -= EconomyGuiList.Footer(capi, composer, view,
                ("new-offer", L("economy-new-offer"), () => Edit(null, false)),
                ("new-task", L("economy-new-task"), () => Edit(null, true)));
        var list = new EconomyGuiList(capi, composer, listView); content = list.Container;
        Offers();
        list.Finish();

        void Offers()
        {
            foreach (var offer in economySnapshot.Offers)
            {
                list.OfferRow(offer, economySnapshot.Coins.FirstOrDefault(c => c.Key == offer.Coin),
                    offer.IsTask ? L("economy-task") : "");
                if (offer.NeedsItemSelection) list.RowDetail(L("economy-reselect-item"));
                if (offer.Contract) { list.RowDetail(L("economy-task-assigned")); list.EndRow(); continue; }
                list.RowActions(L("economy-permit"), offer.Enabled, on => EconomyAction(OfferRequest(offer, enabled: on)), null, null,
                    (L("economy-edit"), () => Edit(offer, offer.IsTask)),
                    (L("economy-delete"), () => EconomyAction(OfferRequest(offer, "offer-delete"))));
                if (!offer.Ready) list.RowDetail(L("economy-offer-unavailable-short"));
                list.EndRow();
            }
            if (economySnapshot.Offers.Count == 0) list.Text(L("economy-no-offers"));
        }
    }

    private void Edit(EconomyOfferView? offer, bool task)
    {
        offerEditor = new(capi, economySnapshot, offer, task, EconomyAction, () => dirty = true,
            () => { offerEditor = null; dirty = true; });
        dirty = true;
    }
    private void ComposeEconomyPicker()
    {
        var editor = offerEditor!;
        double scale = Math.Max(.25, RuntimeEnv.GUIScale);
        var main = SingleComposer.Bounds;
        double width = CompactPicker ? windowWidth - 40 : PickerSideWidth;
        double height = CompactPicker ? windowHeight - 40 : windowHeight;
        var bounds = ElementBounds.Fixed(main.renderX / scale + (CompactPicker ? 20 : windowWidth + 12),
            main.renderY / scale + (CompactPicker ? 20 : 0), width, height);
        capi.Gui.SetDialogPosition("vscncs-economy-picker", null);
        var composer = capi.Gui.CreateCompo("vscncs-economy-picker", bounds)
            .AddStaticCustomDraw(ElementBounds.Fill, (ctx, _, current) =>
            {
                ctx.SetSourceRGB(GuiStyle.DialogStrongBgColor[0], GuiStyle.DialogStrongBgColor[1], GuiStyle.DialogStrongBgColor[2]);
                GuiElement.RoundRectangle(ctx, current.drawX, current.drawY, current.InnerWidth, current.InnerHeight, GuiStyle.DialogBGRadius); ctx.Fill();
            }).AddShadedDialogBG(ElementBounds.Fill, alpha: 1)
            .AddDialogTitleBar(L("economy-select-item"), editor.ClosePicker, key: "economy-picker-titlebar");
        Composers["economy-picker"] = composer;
        composer.AddTextInput(ElementBounds.Fixed(12, 50, width - 24, 32),
            text => { editor.Search = text; RefreshPickerItems(); }, key: "economy-picker-search");
        composer.GetTextInput("economy-picker-search").SetPlaceHolderText(L("economy-search"));
        composer.GetTextInput("economy-picker-search").SetValue(editor.Search);
        var clip = ElementBounds.Fixed(12, 94, width - 40, height - 108);
        economyPicker = new(capi, ElementBounds.Fixed(0, 0, clip.fixedWidth, clip.fixedHeight), editor.Select);
        var grid = economyPicker;
        composer.BeginClip(clip).AddInteractiveElement(grid, "economy-picker-grid").EndClip();
        grid.Scrollbar = new(capi, value => grid.ScrollTo(value),
            ElementBounds.Fixed(width - 24, 94, 16, height - 108));
        composer.AddInteractiveElement(grid.Scrollbar, "economy-picker-scrollbar");
        RefreshPickerItems();
        composer.Compose(false);
        SingleComposer.UnfocusOwnElements();
        (content as EconomyGuiContainer)?.ClearHover();
        composer.FocusElement(composer.GetTextInput("economy-picker-search").TabIndex);
    }
    private void RefreshPickerItems(bool preserveScroll = false)
    {
        if (offerEditor?.Picking is not { } index || economyPicker == null) return;
        IEnumerable<EconomyItemView> items = economySnapshot.Items;
        if (index >= 0) items = items.Concat(economySnapshot.Coins.Where(c => c.Accepted)
            .Select(c => EconomyOfferEditor.CoinDisplay(capi, c)).OfType<EconomyItemView>()).Concat(economyCatalog?.Items ?? []);
        string search = offerEditor.Search;
        economyPicker.SetItems(items.DistinctBy(i => i.Key).Where(i => search.Length == 0
            || i.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || i.Code.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList(), preserveScroll);
        pickerCatalogRevision = economyCatalog?.Revision ?? 0;
    }
    private void RefreshPickerCache()
    {
        if (economyPicker == null || pickerCatalogRevision == (economyCatalog?.Revision ?? 0) || Environment.TickCount64 < pickerRefreshAt) return;
        pickerRefreshAt = Environment.TickCount64 + 500; RefreshPickerItems(true);
    }
}
