using AngleSharp.Dom;
using Cairo;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace SeraphCivilizationSimulation;

// A bounded Cairo page, using exactly the reader's parser, fonts and layout. Native
// GPU models are represented by named quantities in print; the reader keeps models.
internal sealed class HtmlDocumentWorldTexture : IDisposable
{
    private readonly ICoreClientAPI api;
    private readonly string html;
    private readonly int width, height;
    private readonly Action<LoadedTexture> updated;
    private RichTextComponentBase[] components = [];
    private LoadedTexture texture;
    private long listener;
    private bool disposed;
    private double scale;

    internal HtmlDocumentWorldTexture(ICoreClientAPI api, string html, int width, int height, Action<LoadedTexture> updated)
    {
        if (html.Length > 80000) throw new ArgumentOutOfRangeException(nameof(html));
        this.api = api; this.html = html; this.updated = updated;
        this.width = Math.Clamp(width, 64, 1024); this.height = Math.Clamp(height, 64, 1024);
        texture = new LoadedTexture(api);
        try
        {
            Rebuild();
            listener = api.Event.RegisterGameTickListener(_ =>
            {
                if (!disposed && (scale != RuntimeEnv.GUIScale || components.OfType<AnnouncementBlock>().Any(c => c.HasNewImages)))
                {
                    try { Rebuild(); }
                    catch (Exception e)
                    {
                        api.Logger.Warning("HTML world texture refresh stopped: {0}", e.Message);
                        api.Event.UnregisterGameTickListener(listener); listener = 0;
                    }
                }
            }, 250);
        }
        catch { Dispose(); throw; }
    }

    internal static string PrintHtml(ICoreClientAPI api, string html)
    {
        var body = AnnouncementDom.Parse(html);
        string Label(ItemStack? stack) => stack == null ? "[物品不可用]" : stack.GetName() + " × " + stack.StackSize;
        void Replace(IElement node, string replacement)
        {
            var wrapper = body.Owner!.CreateElement("span"); wrapper.InnerHtml = replacement; node.Replace(wrapper);
        }
        foreach (var node in body.QuerySelectorAll("[data-game-recipe]").ToArray())
        {
            var recipe = AnnouncementRecipes.Resolve(api, node);
            if (recipe == null) { Replace(node, "[配方不可用]"); continue; }
            var table = new StringBuilder("<div><p>").Append(WebUtility.HtmlEncode(Label(recipe.Output?.ResolvedItemStack)))
                .Append(recipe.Shapeless ? "（无序配方）" : "（合成配方）").Append("</p><table>");
            for (int row = 0; row < Math.Clamp(recipe.Height, 1, 9); row++)
            {
                table.Append("<tr>");
                for (int col = 0; col < Math.Clamp(recipe.Width, 1, 9); col++)
                {
                    var ingredient = recipe.ResolvedIngredients?.ElementAtOrDefault(row * recipe.Width + col);
                    // Wildcard ingredients have no ResolvedItemStack. Use the same
                    // public matcher as the native recipe slideshow, displaying one variant.
                    var stack = ingredient?.ResolvedItemStack;
                    if (ingredient != null && stack == null)
                        stack = api.World.Collectibles.Select(c => new ItemStack(c, ingredient.Quantity))
                            .FirstOrDefault(s => ingredient.SatisfiesAsIngredient(s, false));
                    string label = ingredient == null ? "—" : Label(stack);
                    if (ingredient?.IsTool == true) label += "（工具）";
                    table.Append("<td>").Append(WebUtility.HtmlEncode(label)).Append("</td>");
                }
                table.Append("</tr>");
            }
            Replace(node, table.Append("</table></div>").ToString());
        }
        foreach (var node in body.QuerySelectorAll("[data-game-slideshow]").ToArray())
        {
            var first = node.Children.FirstOrDefault(c => c.HasAttribute("data-game-item"));
            Replace(node, WebUtility.HtmlEncode(Label(first == null ? null : AnnouncementGameResources.ResolveElement(api, first))));
        }
        foreach (var node in body.QuerySelectorAll("[data-game-item]").ToArray())
            Replace(node, WebUtility.HtmlEncode(Label(AnnouncementGameResources.ResolveElement(api, node))));
        foreach (var toc in body.QuerySelectorAll("[data-toc]"))
        {
            toc.RemoveAttribute("data-toc");
            toc.InnerHtml = "<ul>" + string.Concat(body.QuerySelectorAll("h1,h2,h3,h4,h5,h6")
                .Select(h => "<li>" + WebUtility.HtmlEncode(h.TextContent) + "</li>")) + "</ul>";
        }
        // Interactive components can draw through GPU calls instead of Cairo.
        // Print captions and authored states as ordinary text; readers stay interactive.
        foreach (var node in body.QuerySelectorAll("a,details,summary,input[type=checkbox]").Reverse().ToArray())
        {
            var replacement = body.Owner!.CreateElement(node.LocalName switch { "details" => "div", "summary" => "p", _ => "span" });
            foreach (var attr in node.Attributes.Where(a => a.Name is "style" or "id" or "class")) replacement.SetAttribute(attr.Name, attr.Value);
            replacement.InnerHtml = node.LocalName switch
            {
                "input" => node.HasAttribute("checked") ? "[✓]" : "[ ]",
                "summary" => "<strong>" + node.InnerHtml + "</strong>",
                _ => node.InnerHtml
            };
            node.Replace(replacement);
        }
        // Recursive table/box layout starts fresh native text components. Give
        // each element an explicit inherited ink color across those boundaries.
        Ink(body, "#201b14", 0);
        return body.InnerHtml;

        void Ink(IElement parent, string inherited, int depth)
        {
            if (depth > 32) return;
            var style = AnnouncementDom.Styles(parent);
            string? own = style.GetValueOrDefault("color") ?? parent.GetAttribute("color");
            string color = AnnouncementDom.Color(own) != null ? own! : inherited;
            bool card = parent.LocalName == "article" || parent.LocalName is "div" or "section"
                && parent.ClassList.Any(c => c is "card" or "notice-card" or "announcement-card");
            string? background = style.GetValueOrDefault("background-color") ?? style.GetValueOrDefault("background") ?? (card ? "#30291f" : null);
            if (own == null && AnnouncementDom.Color(background) is { } bg && bg[3] > .5)
                color = bg[0] * .2126 + bg[1] * .7152 + bg[2] * .0722 < .45 ? "#f5ead5" : "#201b14";
            style["color"] = color;
            parent.SetAttribute("style", string.Join(';', style.Select(p => p.Key + ":" + p.Value)));
            foreach (var child in parent.Children) Ink(child, color, depth + 1);
        }
    }

    private void Rebuild()
    {
        foreach (var component in components) component.Dispose();
        components = [];
        scale = RuntimeEnv.GUIScale;
        double guiScale = Math.Max(.25, scale);
        // A dark ink default is scoped to this document, preserving author colors.
        string source = "<div style=\"color:#201b14\">" + PrintHtml(api, html) + "</div>";
        components = AnnouncementTextLayout.Components(api, AnnouncementDom.Markup(source));
        double logicalWidth = width * guiScale / 2;
        double logicalHeight = AnnouncementTextLayout.Measure(components, logicalWidth);
        double factor = Math.Min(2 / guiScale, height / Math.Max(1, logicalHeight));
        bool clipped = factor < .75 / guiScale;
        if (clipped) factor = .75 / guiScale;
        using var surface = new ImageSurface(Format.Argb32, width, height);
        using var ctx = new Context(surface);
        ctx.Save();
        ctx.Rectangle(0, 0, width, clipped ? height - 30 : height); ctx.Clip();
        ctx.Translate((width - logicalWidth * factor) / 2, 0); ctx.Scale(factor, factor);
        foreach (var component in components) component.ComposeElements(ctx, surface);
        ctx.Restore();
        if (clipped)
        {
            ctx.SetSourceRGBA(.95, .9, .78, .94); ctx.Rectangle(0, height - 30, width, 30); ctx.Fill();
            var font = AnnouncementFonts.Readable(new CairoFont(13 / guiScale, GuiStyle.StandardFontName, [.12, .1, .08, 1]), "阅读全文");
            font.SetupContext(ctx); ctx.MoveTo(8, height - 9); ctx.ShowText("… Shift + 右键：阅读全文");
        }
        var pixels = new int[width * height];
        surface.Flush(); Marshal.Copy(surface.DataPtr, pixels, 0, pixels.Length);
        if (texture.Width != width || texture.Height != height)
        { texture.Dispose(); texture = new LoadedTexture(api, 0, width, height); }
        api.Render.LoadOrUpdateTextureFromBgra(pixels, true, 1, ref texture);
        updated(texture);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (listener != 0) api.Event.UnregisterGameTickListener(listener);
        foreach (var component in components) component.Dispose();
        components = []; texture.Dispose();
    }
}
