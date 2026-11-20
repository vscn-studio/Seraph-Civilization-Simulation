using AngleSharp.Dom;
using Cairo;
using System.Net;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace SeraphCivilizationSimulation;

internal static class AnnouncementGameResources
{
    // Keep catalog entries cheap. Stack decoding and markup generation belong to selection.
    internal sealed record Resource(string Key, string Name, Func<string> CreateHtml, EconomyItemView? Item = null);
    private static string Esc(string text) => WebUtility.HtmlEncode(text);
    internal static ItemStack? Resolve(ICoreClientAPI api, EconomyItemView item)
    {
        try { using var reader = new BinaryReader(new MemoryStream(item.Stack, false)); var stack = new ItemStack(reader); return stack.ResolveBlockOrItem(api.World) ? stack : null; }
        catch (Exception e) when (e is IOException or ArgumentException or IndexOutOfRangeException) { return null; }
    }
    internal static string ItemHtml(ItemStack stack)
        // Inline resources must remain inside the author's paragraph.  A figure
        // is block-level in HTML and AngleSharp will split surrounding text
        // around it, so use a neutral inline span for newly inserted items.
        => "<span" + ItemAttributes(stack) + "></span>";
    internal const int MaxSlideshowItems = 32;
    private static string ItemAttributes(ItemStack stack)
        => " data-game-item=\"" + Esc(stack.Collectible.Code.ToString()) + "\" data-class=\"" + stack.Class.ToString().ToLowerInvariant()
            + "\" data-attributes64=\"" + AttributeData(stack) + "\" data-count=\"" + Math.Clamp(stack.StackSize, 1, 9999)
            + "\"";
    private static string AttributeData(ItemStack stack)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true)) stack.Attributes.ToBytes(writer);
        return Convert.ToBase64String(bytes.ToArray());
    }
    internal static string SlideshowHtml(IEnumerable<ItemStack> stacks)
        => "<span data-game-slideshow=\"true\">" + string.Concat(stacks.Take(MaxSlideshowItems)
            .Select(stack => "<span" + ItemAttributes(stack) + "></span>")) + "</span>";
    internal static ItemStack? ResolveElement(ICoreClientAPI api, IElement node)
    {
        ItemStack? stack = null;
        try
        {
            var code = new AssetLocation(node.GetAttribute("data-game-item"));
            CollectibleObject? collectible = node.GetAttribute("data-class") == "block" ? api.World.GetBlock(code) : api.World.GetItem(code);
            if (collectible != null)
            {
                stack = new ItemStack(collectible, Math.Clamp((int)AnnouncementDom.Number(node.GetAttribute("data-count"), 1, 9999), 1, 9999));
                if (node.GetAttribute("data-attributes64") is { Length: > 0 and <= AnnouncementState.MaxLength } encoded)
                    stack.Attributes = TreeAttribute.CreateFromBytes(Convert.FromBase64String(encoded));
                else if (node.GetAttribute("data-attributes") is { Length: > 0 and <= 8192 } json && TreeAttribute.FromJson(json) is ITreeAttribute attributes) stack.Attributes = attributes;
            }
        }
        catch (Exception e) when (e is ArgumentException or Newtonsoft.Json.JsonException or InvalidOperationException
            or FormatException or IOException or KeyNotFoundException or IndexOutOfRangeException) { stack = null; }
        return stack;
    }
    internal static RichTextComponentBase Component(ICoreClientAPI api, IElement node, AnnouncementInteraction interaction)
    {
        var nodes = node.HasAttribute("data-game-slideshow") ? node.Children.Where(n => n.HasAttribute("data-game-item")) : [node];
        var stacks = nodes.Take(MaxSlideshowItems).Select(n => ResolveElement(api, n)).OfType<ItemStack>().ToArray();
        if (stacks.Length == 0) return new AnnouncementBox(api, AnnouncementTextLayout.Components(api, AnnouncementHtml.ToNativeMarkup("<p>[物品不可用] " + Esc(node.TextContent) + "</p>"), interaction));
        return new AnnouncementInlineItemBox(api, new ItemCell(api, stacks));
    }
    // The same native cell supports one item and an explicit list of rotating variants.
    internal sealed class ItemCell(ICoreClientAPI api, ItemStack[] stacks)
        : SlideshowItemstackTextComponent(api, stacks, 28, EnumFloat.Left, stack => Open(api, HandbookTarget(api, stack)))
    {
        private bool hovered;
        public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] path, double height, double x, double y, out double nextX)
        {
            Background = ShowStackSize = true;
            double size = Math.Min(GuiElement.scaled(28), Math.Max(1, path[0].X2 - path[0].X1));
            bool wrap = x + size > path[0].X2;
            BoundsPerLine = [new(wrap ? path[0].X1 : x, y + (wrap ? height : 0), size, size)];
            nextX = BoundsPerLine[0].X + size;
            return wrap ? EnumCalcBoundsResult.Nextline : EnumCalcBoundsResult.Continue;
        }
        public override void OnMouseDown(MouseEvent args)
        {
            if (args.Button != EnumMouseButton.Left || !BoundsPerLine.Any(b => b.PointInside(args.X, args.Y))) return;
            base.OnMouseDown(args); args.Handled = true;
        }
        public override void RenderInteractiveElements(float dt, double x, double y, double z)
        {
            hovered = BoundsPerLine.Any(b => b.PointInside(api.Input.MouseX - x, api.Input.MouseY - y));
            MouseOverCursor = "linkselect";
            base.RenderInteractiveElements(dt, x, y, z);
        }
        public override bool UseMouseOverCursor(ElementBounds bounds) => hovered;
    }

    // A figure keeps the compatibility type used by the announcement renderer,
    // while this specialized box opts into normal rich-text flow.  Text can
    // therefore continue on the same line when there is room beside the cell.
    internal sealed class AnnouncementInlineItemBox(ICoreClientAPI api, ItemCell child)
        : AnnouncementBox(api, [child], new() { ["padding"] = "0" })
    {
        private ItemCell Child => (ItemCell)Children[0];
        public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] path, double height, double x, double y, out double nextX)
        {
            var result = Child.CalcBounds(path, height, x, y, out nextX);
            BoundsPerLine = Child.BoundsPerLine.ToArray();
            ContentX = ContentY = 0; ContentHeight = BoundsPerLine.Length == 0 ? 0 : BoundsPerLine[0].Height;
            return result;
        }
        public override void ComposeElements(Context ctx, ImageSurface surface) => Child.ComposeElements(ctx, surface);
        public override void RenderInteractiveElements(float dt, double x, double y, double z)
        {
            Child.RenderInteractiveElements(dt, x, y, z);
            MouseOverCursor = Child.MouseOverCursor;
        }
        public override void OnMouseDown(MouseEvent args) => Child.OnMouseDown(args);
        public override void OnMouseUp(MouseEvent args) => Child.OnMouseUp(args);
        public override void OnMouseMove(MouseEvent args) => Child.OnMouseMove(args);
        public override void Dispose() => Child.Dispose();
    }
    internal static void Open(ICoreClientAPI api, string target)
    {
        // Page codes are opaque and case-sensitive, not URI hosts. Decode our escaped
        // payload once, immediately before calling the game's registered protocol.
        if (target.StartsWith("handbook://", StringComparison.OrdinalIgnoreCase))
        {
            string page = Uri.UnescapeDataString(target[11..]);
            var handbook = api.Gui.LoadedGuis?.OfType<GuiDialogSurvivalHandbook>().FirstOrDefault();
            if (handbook != null && !page.StartsWith("tab-", StringComparison.Ordinal))
            {
                if (OpenHandbookPage(page, handbook.OpenDetailPageFor)) handbook.TryOpen();
                else api.ShowChatMessage(Lang.Get("vsscsmod:ui.html-handbook-unavailable"));
                return;
            }
            target = "handbook://" + page;
        }
        var link = new LinkTextComponent(api,"",CairoFont.WhiteSmallText(),null) { Href = target };
        try { link.Trigger(); } finally { link.Dispose(); }
    }
    internal static bool OpenHandbookPage(string page, System.Func<string, bool> open)
    {
        if (open(page)) return true;
        // Match the native handbook hotkey's fallback for non-handbook stack attributes.
        int attributes = page.IndexOf("-{", StringComparison.Ordinal);
        return attributes > 0 && (page.StartsWith("item-", StringComparison.Ordinal) || page.StartsWith("block-", StringComparison.Ordinal))
            && open(page[..attributes]);
    }
    internal static string HandbookTarget(ICoreClientAPI api, ItemStack stack)
        => HandbookTarget(stack.Collectible.GetCollectibleInterface<IHandBookPageCodeProvider>()?.HandbookPageCodeForStack(api.World, stack)
            ?? GuiHandbookItemStackPage.PageCodeForStack(stack));
    internal static string HandbookTarget(string page) => "handbook://" + Uri.EscapeDataString(page);
    internal static IEnumerable<Resource> Catalog(ICoreClientAPI api, string category, IEnumerable<EconomyItemView> items)
    {
        if (category is "item" or "itemhandbook" or "slideshow")
        {
            foreach (var item in items.DistinctBy(i => i.Key))
            {
                yield return new(item.Key,item.Name,() =>
                {
                    var stack = Resolve(api,item);
                    return stack == null ? "" : category != "itemhandbook" ? ItemHtml(stack)
                        : "<a href=\"" + Esc(HandbookTarget(api,stack)) + "\">" + Esc(stack.GetName()) + "</a>";
                },item);
            }
        }
        else if (category == "handbook")
        {
            foreach (var asset in api.Assets.AllAssets.Where(a => a.Key.Path.StartsWith("config/handbook/", StringComparison.Ordinal) && a.Key.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                GuiHandbookTextPage? page;
                try { page = asset.Value.ToObject<GuiHandbookTextPage>(); }
                catch (Newtonsoft.Json.JsonException) { continue; }
                if (!string.IsNullOrEmpty(page?.pageCode))
                    yield return new(page.pageCode,Lang.Get(page.Title),() => "<a href=\"" + Esc(HandbookTarget(page.pageCode)) + "\">" + Esc(Lang.Get(page.Title)) + "</a>");
            }
        }
        else if (category == "recipe")
        {
            int index = 0;
            foreach (var recipe in api.World.GridRecipes ?? [])
            {
                int recipeIndex = index++;
                var output = recipe.Output?.ResolvedItemStack; if (output == null) continue;
                string key = "recipe-" + recipeIndex;
                var display = EconomyInventory.Display(output); display.Key = key;
                yield return new(key,display.Name,() => AnnouncementRecipes.Html(api, recipe, recipeIndex),display);
            }
        }
    }

}

// The picker owns a cheap, incremental catalog. Only visible slots create GPU resources.
internal sealed class AnnouncementResourcePicker : IDisposable
{
    private readonly ICoreClientAPI api;
    private readonly List<AnnouncementGameResources.Resource> resources = [];
    private readonly HashSet<string> keys = [];
    private IEnumerator<AnnouncementGameResources.Resource>? pending;
    private readonly Action<string> insert;
    internal string Category { get; }
    internal GuiComposer Composer { get; private set; } = null!;
    internal bool CloseRequested { get; private set; }
    internal bool Loading => pending != null;
    internal int LoadedCount => resources.Count;
    private string query = "";
    private bool rebuild, resultsDirty;
    private long nextResultsAt;
    private ElementBounds bounds;
    private GuiElementEconomyPickerGrid? grid;
    private GuiElementAnnouncementResourceList? list;
    private Dictionary<string, AnnouncementGameResources.Resource> filteredByKey = [];
    private readonly Dictionary<string, AnnouncementGameResources.Resource> selected = [];
    internal AnnouncementResourcePicker(ICoreClientAPI api, string category, IEnumerable<AnnouncementGameResources.Resource> resources, ElementBounds bounds, Action<string> insert)
    { this.api = api; Category = category; this.bounds = bounds; this.insert = insert; Refresh(resources); Compose(); }
    private static string L(string key, params object[] args) => Lang.Get("vsscsmod:ui.html-" + key, args);
    internal void Refresh(IEnumerable<AnnouncementGameResources.Resource> updated)
    { pending?.Dispose(); pending = updated.GetEnumerator(); }
    internal void Recompose(ElementBounds updated) { bounds = updated; Compose(); }
    internal void Tick()
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (int count = 0; pending != null && count < 32 && timer.Elapsed.TotalMilliseconds < 2; count++)
        {
            if (!pending.MoveNext()) { pending.Dispose(); pending = null; resultsDirty = true; break; }
            var entry = pending.Current;
            if (keys.Add(entry.Key)) { resources.Add(entry); resultsDirty = true; }
        }
        if (rebuild) Compose();
        else if (resultsDirty && (pending == null || Environment.TickCount64 >= nextResultsAt))
        {
            if (CategoryIsGrid) UpdateGrid(true); else UpdateList(true);
            nextResultsAt = Environment.TickCount64 + 150;
        }
    }
    private List<AnnouncementGameResources.Resource> Filter() => resources.Where(r =>
        r.Name.Contains(query,StringComparison.OrdinalIgnoreCase) || r.Key.Contains(query,StringComparison.OrdinalIgnoreCase)).ToList();
    private void UpdateGrid(bool preserveScroll)
    {
        var filtered = Filter();
        // Catalog growth outside the current search must not recreate visible models.
        if (!preserveScroll || filtered.Count != filteredByKey.Count || filtered.Any(r => !filteredByKey.ContainsKey(r.Key)))
        {
            filteredByKey = filtered.ToDictionary(r => r.Key);
            grid!.SetItems(filtered.Select(r => r.Item).OfType<EconomyItemView>().ToList(), preserveScroll);
        }
        ((GuiElementDynamicText)Composer.GetElement("results")).SetNewText(L("results",filtered.Count) + (Loading ? " · " + L("loading") : ""));
        ((GuiElementDynamicText)Composer.GetElement("empty")).SetNewText(filtered.Count == 0 ? L(Loading ? "loading" : "empty") : "");
        resultsDirty = false;
    }
    private void UpdateList(bool preserveScroll)
    {
        var filtered = Filter();
        filteredByKey = filtered.ToDictionary(r => r.Key);
        list?.SetItems(filtered, preserveScroll);
        ((GuiElementDynamicText)Composer.GetElement("results")).SetNewText(L("results", filtered.Count) + (Loading ? " · " + L("loading") : ""));
        ((GuiElementDynamicText)Composer.GetElement("empty")).SetNewText(filtered.Count == 0 ? L(Loading ? "loading" : "empty") : "");
        resultsDirty = false;
    }
    private void Compose()
    {
        double width = bounds.fixedWidth, height = bounds.fixedHeight;
        int caret = Composer?.GetTextInput("search")?.CaretPosInLine ?? 0;
        bool focus = Composer?.GetTextInput("search")?.HasFocus == true;
        composerReady = false; Composer?.Dispose();
        api.Gui.SetDialogPosition("vscn-announcement-resource", null);
        var composer = api.Gui.CreateCompo("vscn-announcement-resource", bounds)
            .AddStaticCustomDraw(ElementBounds.Fill, (ctx, _, current) =>
            {
                ctx.SetSourceRGB(GuiStyle.DialogStrongBgColor[0], GuiStyle.DialogStrongBgColor[1], GuiStyle.DialogStrongBgColor[2]);
                GuiElement.RoundRectangle(ctx, current.drawX, current.drawY, current.InnerWidth, current.InnerHeight, GuiStyle.DialogBGRadius); ctx.Fill();
            }).AddShadedDialogBG(ElementBounds.Fill, alpha:1)
            .AddDialogTitleBar(L(Category), () => CloseRequested = true, key:"titlebar")
            .AddTextInput(ElementBounds.Fixed(12,46,width-24,30),text =>
            {
                if (text == query) return;
                query=text;
                if (CategoryIsGrid && composerReady) UpdateGrid(false); else rebuild=true;
            },key:"search");
        Composer = composer; composerReady = false;
        composer.GetTextInput("search").SetValue(query); composer.GetTextInput("search").SetPlaceHolderText(L("search"));
        var filtered = Filter();
        var clip = ElementBounds.Fixed(12,88,width-40,Math.Max(36,height-(Category == "slideshow" ? 182 : 140)));
        grid = null;
        list = null;
        if (CategoryIsGrid)
        {
            grid = new(api,ElementBounds.Fixed(0,0,clip.fixedWidth,clip.fixedHeight),item => Choose(filteredByKey[item.Key]));
            if (Category == "slideshow") grid.IsSelected = item => selected.ContainsKey(item.Key);
            composer.BeginClip(clip).AddInteractiveElement(grid,"resource-1").EndClip();
            // Keep the descriptive address used by the current picker tests;
            // both names point at the same native grid.
            composer.AddInteractiveElement(new GuiElementAnnouncementResourceAlias(api, grid,
                clip.FlatCopy()), "grid");
            grid.Scrollbar = new(api,y=>grid.ScrollTo(y),ElementBounds.Fixed(width-24,88,16,clip.fixedHeight));
            composer.AddInteractiveElement(grid.Scrollbar,"scrollbar");
            filteredByKey = filtered.ToDictionary(r => r.Key);
            grid.SetItems(filtered.Select(r => r.Item).OfType<EconomyItemView>().ToList());
        }
        else
        {
            list = new GuiElementAnnouncementResourceList(api, ElementBounds.Fixed(0, 0, clip.fixedWidth, clip.fixedHeight), Choose);
            list.SetItems(filtered);
            composer.BeginClip(clip).AddInteractiveElement(list, "resource-list").EndClip();
            list.Scrollbar = new GuiElementAutoScrollbar(api, value => list.ScrollTo(value),
                ElementBounds.Fixed(width - 24, 88, 16, clip.fixedHeight));
            composer.AddInteractiveElement(list.Scrollbar, "resource-scrollbar");
        }
        var fontDetail = AnnouncementFonts.Readable(CairoFont.WhiteDetailText(), L("loading") + L("empty") + L("results",0));
        composer.AddInteractiveElement(new GuiElementDynamicText(api,filtered.Count == 0 ? L(Loading ? "loading" : "empty") : "",fontDetail,ElementBounds.Fixed(12,94,width-40,60)),"empty");
        composer.AddInteractiveElement(new GuiElementDynamicText(api,L("results",filtered.Count) + (Loading ? " · " + L("loading") : ""),fontDetail,
            ElementBounds.Fixed(CategoryIsGrid?12:88,height-38,width-(CategoryIsGrid?24:100),26)),"results");
        if (Category == "slideshow")
        {
            composer.AddDynamicText(L("selected", selected.Count, AnnouncementGameResources.MaxSlideshowItems), fontDetail.Clone().WithFontSize(12),
                ElementBounds.Fixed(12,height-80,width-124,28), "selected");
            composer.AddSmallButton(L("insert-selected"), () => { InsertSelected(); return true; },
                ElementBounds.Fixed(width-106,height-82,94,28),key:"insert-selected");
            composer.GetButton("insert-selected").Enabled = selected.Count > 0;
        }
        composer.Compose(false); composerReady = true;
        if(focus){composer.FocusElement(composer.GetTextInput("search").TabIndex);composer.GetTextInput("search").SetCaretPos(Math.Min(caret,query.Length));}
        rebuild=resultsDirty=false;
    }
    private bool composerReady;
    private bool CategoryIsGrid => Category is "item" or "itemhandbook" or "recipe" or "slideshow";
    private void Choose(AnnouncementGameResources.Resource resource)
    {
        if (Category != "slideshow") { insert(resource.CreateHtml()); CloseRequested = true; return; }
        if (!selected.Remove(resource.Key))
        {
            if (selected.Count >= AnnouncementGameResources.MaxSlideshowItems) return;
            selected.Add(resource.Key, resource);
        }
        Composer.GetDynamicText("selected").SetNewText(L("selected", selected.Count, AnnouncementGameResources.MaxSlideshowItems));
        Composer.GetButton("insert-selected").Enabled = selected.Count > 0;
    }
    private void InsertSelected()
    {
        var stacks = selected.Values.Select(r => r.Item == null ? null : AnnouncementGameResources.Resolve(api, r.Item)).OfType<ItemStack>().ToArray();
        if (stacks.Length == 0) return;
        insert(AnnouncementGameResources.SlideshowHtml(stacks)); CloseRequested = true;
    }
    public void Dispose() { pending?.Dispose(); pending=null; Composer.Dispose(); }
}
