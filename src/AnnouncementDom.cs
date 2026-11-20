using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using System.Globalization;
using System.Net;
using System.Text;

namespace SeraphCivilizationSimulation;

// HTML parsing is local only: no scripting, network loader, external stylesheets or browser.
internal static class AnnouncementDom
{
    internal static string Markup(string html) => AnnouncementHtml.ToNativeMarkup(Prepare(html), int.MaxValue);
    internal static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    internal static string Decode(string? value)
    { try { return Encoding.UTF8.GetString(Convert.FromBase64String(value ?? "")); } catch (FormatException) { return ""; } }
    internal static IElement Parse(string html) => new HtmlParser().ParseDocument(html).Body!;
    internal static Dictionary<string, string> Styles(IElement node)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in (node.GetAttribute("style") ?? "").Split(';'))
        { int colon = part.IndexOf(':'); if (colon > 0) result[part[..colon].Trim()] = part[(colon + 1)..].Trim(); }
        return result;
    }
    internal static string Value(IReadOnlyDictionary<string, string> values, string key, string fallback = "")
        => values.TryGetValue(key, out var value) ? value : fallback;
    internal static double Number(string? text, double fallback = 0, double max = 2048)
        => double.TryParse(text?.Trim().Replace("px", "", StringComparison.OrdinalIgnoreCase), NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
            && double.IsFinite(n) ? Math.Clamp(n, 0, max) : fallback;
    internal static double Dimension(string? text, double available, double fallback)
        => text?.EndsWith('%') == true ? available * Number(text[..^1], 100, 100) / 100
            : string.IsNullOrWhiteSpace(text) || text == "auto" ? fallback : Vintagestory.API.Client.GuiElement.scaled(Number(text));
    internal static double[]? Color(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "transparent") return null;
        try
        {
            if (value.Length == 4 && value[0] == '#') value = "#" + string.Concat(value[1..].Select(c => new string(c, 2)));
            var color = System.Drawing.ColorTranslator.FromHtml(value);
            return [color.R / 255d, color.G / 255d, color.B / 255d, color.A / 255d];
        }
        catch (Exception e) when (e is ArgumentException or FormatException) { return null; }
    }
    internal static bool SafeLink(string? href) => href?.StartsWith('#') == true
        || href?.StartsWith("handbook://", StringComparison.OrdinalIgnoreCase) == true && href.Length > 11 && !href.Any(char.IsControl)
        || Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    internal static string Prepare(string html)
    {
        var body = Parse(html);
        // Discard user-supplied internal tokens before producing our own.
        foreach (var node in body.QuerySelectorAll("script,style,iframe,object,embed,link,meta,base").ToArray()) node.Remove();
        foreach (var node in body.QuerySelectorAll("*").Where(n => n.LocalName.StartsWith("vscn-", StringComparison.Ordinal)).ToArray())
            node.Replace(body.Owner!.CreateTextNode(node.TextContent));
        var headings = body.QuerySelectorAll("h1,h2,h3,h4,h5,h6");
        int headingIndex = 0;
        foreach (var heading in headings)
            if (!heading.HasAttribute("id")) heading.Id = "vscn-heading-" + ++headingIndex;
        foreach (var toc in body.QuerySelectorAll("[data-toc]").ToArray())
            toc.InnerHtml = "<ul>" + string.Concat(headings.Select(h => "<li><a href=\"#" + WebUtility.HtmlEncode(h.Id) + "\">" + WebUtility.HtmlEncode(h.TextContent) + "</a></li>")) + "</ul>";
        int interaction = 0;
        Transform(body, 0);
        return body.InnerHtml;

        void Transform(IElement parent, int depth)
        {
            if (depth > 32) { parent.TextContent = parent.TextContent; return; }
            foreach (var node in parent.Children.ToArray())
            {
                string tag = node.LocalName;
                var styles = Styles(node);
                if (styles.GetValueOrDefault("display") == "none" || node.HasAttribute("hidden")) { node.Remove(); continue; }
                foreach (var attribute in node.Attributes.ToArray())
                    if (attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase)) node.RemoveAttribute(attribute.Name);
                // These controls have no native game implementation. Keep an explicit fallback.
                if (tag is "video" or "audio" or "svg" or "math" or "select" or "textarea" or "button")
                { node.Replace(body.Owner!.CreateTextNode("[" + tag + "：原生公告不支持] " + node.TextContent)); continue; }
                if (tag == "input" && node.GetAttribute("type") != "checkbox")
                { node.Replace(body.Owner!.CreateTextNode("[表单输入不支持]")); continue; }
                if (tag == "table") { Opaque(node, "table", node.OuterHtml); continue; }
                if (tag == "details")
                {
                    node.SetAttribute("data-key", (++interaction).ToString(CultureInfo.InvariantCulture));
                    Opaque(node, "details", node.OuterHtml); continue;
                }
                if (tag == "input")
                {
                    node.SetAttribute("data-key", (++interaction).ToString(CultureInfo.InvariantCulture));
                    Opaque(node, "checkbox", node.OuterHtml); continue;
                }
                if (tag == "figure" && node.HasAttribute("data-game-recipe")) { Opaque(node, "recipe", node.OuterHtml); continue; }
                // New embeds use inline spans so AngleSharp keeps them in the
                // paragraph with adjacent text. Keep accepting legacy figures
                // from saved announcements.
                if ((tag == "figure" || tag == "span") && node.HasAttribute("data-game-slideshow")) { Opaque(node, "item", node.OuterHtml); continue; }
                if ((tag == "figure" || tag == "span") && node.HasAttribute("data-game-item")) { Opaque(node, "item", node.OuterHtml); continue; }
                if (tag == "hr") { Opaque(node, "rule", ""); continue; }
                if (tag == "ol")
                {
                    var items = node.Children.Where(n => n.LocalName == "li").ToArray();
                    bool reversed = node.HasAttribute("reversed");
                    int number = int.TryParse(node.GetAttribute("start"), out int start) ? start : reversed ? items.Length : 1;
                    foreach (var item in items)
                    {
                        if (int.TryParse(item.GetAttribute("value"), out int specified)) number = specified;
                        item.SetAttribute("data-marker", ListNumber(number, node.GetAttribute("type")) + ". ");
                        number += reversed ? -1 : 1;
                    }
                }
                if (tag == "ul")
                    foreach (var item in node.Children.Where(n => n.LocalName == "li"))
                        item.SetAttribute("data-marker", styles.GetValueOrDefault("list-style-type") switch { "none" => "", "circle" => "○ ", "square" => "▪ ", _ => "• " });
                if (tag == "q") { node.Prepend(body.Owner!.CreateTextNode("“")); node.Append(body.Owner!.CreateTextNode("”")); }
                if (tag == "dt") node.InnerHtml = "<strong>" + node.InnerHtml + "</strong>";
                if (tag == "dd") node.Prepend(body.Owner!.CreateTextNode("　"));
                bool box = tag is "p" or "div" or "section" or "main" or "header" or "footer" or "aside" or "nav" or "blockquote";
                if (box && styles.Keys.Any(k => k is "text-align" or "padding" or "margin" or "background-color" or "border" or "width" or "height" or "max-width" or "min-height"))
                { Opaque(node, "box", node.OuterHtml); continue; }
                Transform(node, depth + 1);
                if (!string.IsNullOrEmpty(node.Id))
                {
                    var anchor = body.Owner!.CreateElement("vscn-native");
                    anchor.SetAttribute("kind", "anchor"); anchor.SetAttribute("data64", Encode(node.Id)); node.Prepend(anchor);
                }
                string decoration = tag switch { "u" or "ins" => "underline", "s" or "del" => "line-through", "mark" => "mark", "sub" => "sub", "sup" => "sup", _ => styles.GetValueOrDefault("text-decoration", "") };
                if (decoration.Length > 0)
                    node.InnerHtml = "<vscn-format mode=\"" + WebUtility.HtmlEncode(decoration) + "\">" + node.InnerHtml + "</vscn-format>";
            }
        }
        void Opaque(IElement element, string kind, string value)
        {
            var token = body.Owner!.CreateElement("vscn-native");
            token.SetAttribute("kind", kind); token.SetAttribute("data64", Encode(value)); element.Replace(token);
        }
    }

    internal static string ListNumber(int number, string? type)
    {
        if (number < 1 || number > 3999 || type is not ("a" or "A" or "i" or "I")) return number.ToString(CultureInfo.InvariantCulture);
        string result = "";
        if (type is "a" or "A") { while (number > 0) { number--; result = (char)('A' + number % 26) + result; number /= 26; } }
        else foreach (var (amount, digit) in new[] { (1000,"M"),(900,"CM"),(500,"D"),(400,"CD"),(100,"C"),(90,"XC"),(50,"L"),(40,"XL"),(10,"X"),(9,"IX"),(5,"V"),(4,"IV"),(1,"I") })
            while (number >= amount) { result += digit; number -= amount; }
        return type is "a" or "i" ? result.ToLowerInvariant() : result;
    }
}
