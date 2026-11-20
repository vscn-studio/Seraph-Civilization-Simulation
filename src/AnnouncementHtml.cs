using System.Net;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SeraphCivilizationSimulation;

public sealed class AnnouncementState
{
    public const int MaxLength = 32768;
    public const int MaxTitleLength = 40, MaxAnnouncements = 20;
    public string Html { get; set; } = "";
    public long Revision { get; set; }
    public List<AnnouncementEntry>? Entries { get; set; }
    public Dictionary<string, long> SeenNotifications { get; set; } = [];

    public void Migrate()
    {
        Entries ??= [new() { Id = "legacy", Title = "公告", Html = Html }];
        if (Html == null || Revision < 0 || Entries.Count > MaxAnnouncements || SeenNotifications == null
            || Entries.Any(e => string.IsNullOrWhiteSpace(e.Id) || string.IsNullOrWhiteSpace(e.Title)
                || e.Title.Length > MaxTitleLength || e.Html == null || e.Html.Length > MaxLength || e.NotificationRevision < 0)
            || Entries.DistinctBy(e => e.Id).Count() != Entries.Count || Entries.Count(e => e.Notify) > 1)
            throw new InvalidDataException("Invalid announcement state.");
    }

    public AnnouncementEntry? PendingNotification(string uid)
        => Entries?.FirstOrDefault(e => e.Notify && e.NotificationRevision > SeenNotifications.GetValueOrDefault(uid));

    public string? ValidateSave(bool administrator, long revision, string? html)
        => !administrator ? "announcement-admin-only" : revision != Revision ? "announcement-conflict"
        : html == null || html.Length > MaxLength ? "announcement-too-long" : null;
}

// Tutorials share the announcement editor and renderer, but have their own
// moderation queue and author metadata.
public sealed class TutorialState
{
    public const int MaxTutorials = 100;
    public long Revision { get; set; }
    public List<AnnouncementEntry> Entries { get; set; } = [];

    public void Migrate()
    {
        Entries ??= [];
        if (Revision < 0 || Entries.Count > MaxTutorials || Entries.Any(e =>
            string.IsNullOrWhiteSpace(e.Id) || string.IsNullOrWhiteSpace(e.Title) || e.Title.Length > AnnouncementState.MaxTitleLength
            || e.Html == null || e.Html.Length > AnnouncementState.MaxLength || e.AuthorUid == null || e.AuthorName == null))
            throw new InvalidDataException("Invalid tutorial state.");
        Entries = Entries.DistinctBy(e => e.Id).ToList();
    }
}

[ProtoBuf.ProtoContract]
public sealed class AnnouncementEntry
{
    [ProtoBuf.ProtoMember(1)] public string Id { get; set; } = "";
    [ProtoBuf.ProtoMember(2)] public string Title { get; set; } = "";
    [ProtoBuf.ProtoMember(3)] public string Html { get; set; } = "";
    [ProtoBuf.ProtoMember(4)] public bool Notify { get; set; }
    [ProtoBuf.ProtoMember(5)] public long NotificationRevision { get; set; }
    // Html is the historical source field; keep its wire number and persisted name.
    [ProtoBuf.ProtoMember(6)] public bool IsMarkdown { get; set; }
    [ProtoBuf.ProtoMember(7)] public string AuthorUid { get; set; } = "";
    [ProtoBuf.ProtoMember(8)] public string AuthorName { get; set; } = "";
    [ProtoBuf.ProtoMember(9)] public bool Published { get; set; } = true;
}

// Translate ordinary HTML formatting to the native handbook renderer. No browser or scripts.
public static partial class AnnouncementHtml
{
    [GeneratedRegex(@"<!--[\s\S]*?(?:-->|$)|<\s*(script|style|head|iframe|object)\b[^>]*>[\s\S]*?(?:<\s*/\s*\1\s*>|$)", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenContent();
    [GeneratedRegex(@"<(?:""[^""]*""|'[^']*'|[^'""<>])*>|[^<]+|<")]
    private static partial Regex Tokens();
    [GeneratedRegex(@"^<\s*(/?)\s*([a-zA-Z][\w-]*)")]
    private static partial Regex TagName();
    [GeneratedRegex("([\\w-]+)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))")]
    private static partial Regex Attributes();
    [GeneratedRegex(@"[\t\n\f\r ]+")]
    private static partial Regex Whitespace();

    public static string ToNativeMarkup(string? html, int maxLength = AnnouncementState.MaxLength)
    {
        html = HiddenContent().Replace((html ?? "")[..Math.Min(html?.Length ?? 0, maxLength)], "");
        var result = new StringBuilder();
        var stack = new List<(string Html, string Native)>();
        var lists = new List<(string Html, int Number)>();
        bool preformatted = false;
        bool hasContent = false, lineHasText = false, pendingSpace = false, pendingBreak = false, awaitingItemText = false;
        double pendingEm = 0, pendingPixels = 0;
        string? pendingItem = null;
        foreach (Match token in Tokens().Matches(html))
        {
            var tag = TagName().Match(token.Value);
            if (!tag.Success)
            {
                string text = WebUtility.HtmlDecode(token.Value);
                if (preformatted)
                {
                    if (text.Length > 0) FlushContent();
                    result.Append(Escape(text));
                    if (text.Length > 0)
                    {
                        lineHasText = text[^1] is not '\r' and not '\n';
                        hasContent = true;
                        awaitingItemText = false;
                    }
                }
                else
                {
                    text = Whitespace().Replace(text, " ");
                    bool leadingSpace = text.StartsWith(' '), trailingSpace = text.EndsWith(' ');
                    text = text.Trim(' ');
                    if (text.Length > 0)
                    {
                        FlushContent();
                        if (lineHasText && (pendingSpace || leadingSpace) && !awaitingItemText) result.Append(' ');
                        result.Append(Escape(text));
                        hasContent = lineHasText = true;
                        awaitingItemText = false;
                    }
                    pendingSpace = trailingSpace && lineHasText && !pendingBreak;
                }
                continue;
            }
            string name = tag.Groups[2].Value.ToLowerInvariant();
            bool closing = tag.Groups[1].Length > 0;
            // Resource figures are converted to vscn-native controls before this
            // pass; ordinary figures remain block-level HTML content.
            bool block = name is "p" or "div" or "section" or "article" or "ul" or "ol" or "li" or "blockquote" or "tr" or "pre" or "figure" or "figcaption" or "main" or "header" or "footer" or "aside" or "nav" or "dl" or "dt" or "dd"
                || name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6";
            if (closing)
            {
                if (name == "pre") preformatted = false;
                if (name == "li" && pendingItem != null) FlushContent();
                bool nestedList = false;
                if (name is "ul" or "ol")
                {
                    int listIndex = lists.FindLastIndex(l => l.Html == name);
                    nestedList = listIndex > 0;
                    if (listIndex >= 0) lists.RemoveRange(listIndex, lists.Count - listIndex);
                }
                int index = stack.FindLastIndex(t => t.Html == name);
                bool endedCard = false;
                if (index >= 0)
                    while (stack.Count > index)
                    {
                        endedCard |= stack[^1].Native == "vscn-card";
                        if (stack[^1].Native.Length > 0) result.Append("</").Append(stack[^1].Native).Append('>');
                        stack.RemoveAt(stack.Count - 1);
                    }
                if (endedCard)
                {
                    // The card owns its padding. Discard the last child's trailing margin.
                    pendingBreak = pendingSpace = lineHasText = awaitingItemText = false;
                    pendingEm = pendingPixels = 0;
                    hasContent = true;
                    Break("card", false);
                }
                if (block) Break(name, nestedList, name == "li");
                continue;
            }
            if (name is "br" or "hr")
            {
                FlushContent();
                result.Append("<br>");
                lineHasText = pendingSpace = awaitingItemText = false;
                hasContent = true;
                continue;
            }
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match attribute in Attributes().Matches(token.Value))
                attrs[attribute.Groups[1].Value] = WebUtility.HtmlDecode(attribute.Groups[2].Success ? attribute.Groups[2].Value
                    : attribute.Groups[3].Success ? attribute.Groups[3].Value : attribute.Groups[4].Value);
            var styles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string declaration in attrs.GetValueOrDefault("style", "").Split(';'))
            {
                int colon = declaration.IndexOf(':');
                if (colon > 0) styles[declaration[..colon].Trim()] = declaration[(colon + 1)..].Trim();
            }
            if (name == "pre") preformatted = true;
            if (name is "ul" or "ol" && pendingItem != null) FlushContent();
            if (block) Break(name, lists.Count > 0, name is "li" or "ul" or "ol");
            if (name is "ul" or "ol") lists.Add((name, 0));
            if (name == "li")
            {
                string indent = string.Concat(Enumerable.Repeat("&nbsp;&nbsp;", Math.Clamp(lists.Count - 1, 0, 8)));
                if (lists.Count > 0 && lists[^1].Html == "ol")
                {
                    var list = lists[^1];
                    lists[^1] = (list.Html, list.Number + 1);
                    pendingItem = indent + attrs.GetValueOrDefault("data-marker", (list.Number + 1) + ". ");
                }
                else pendingItem = indent + attrs.GetValueOrDefault("data-marker", "• ");
                pendingSpace = false;
            }
            if (name is "td" or "th") result.Append("  ");
            string? native = name switch { "b" or "strong" => "strong", "em" or "i" => "i", "code" or "kbd" or "samp" => "code", "vscn-format" => "vscn-format", _ => null };
            string opening = native == null ? "" : "<" + native + ">";
            if (name == "vscn-native")
            {
                string kind = attrs.GetValueOrDefault("kind", "");
                bool blockNative = kind is "recipe";
                if (kind is not ("anchor" or "checkbox" or "item" or "recipe")) Break("card", false);
                if (blockNative) Break("figure", false);
                FlushContent(); result.Append(token.Value.TrimEnd('>','/')).Append("/>"); hasContent = true;
                if (blockNative) Break("figure", false);
                continue;
            }
            if (name == "vscn-format") opening = "<vscn-format mode=\"" + attrs.GetValueOrDefault("mode", "") + "\">";
            bool card = name == "article" || (name is "div" or "section" && attrs.GetValueOrDefault("class", "")
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(c => c is "card" or "notice-card" or "announcement-card"));
            if (card && stack.Count < 32)
            {
                Break("card", false); FlushContent();
                native = "vscn-card";
                opening = "<vscn-card bg=\"" + Color(styles.GetValueOrDefault("background-color", styles.GetValueOrDefault("background", "")), "#30291f")
                    + "\" border=\"" + Color(styles.GetValueOrDefault("border-color"), "#88714d") + "\">";
                // The first paragraph in a card starts at its padding, without an outer margin.
                hasContent = lineHasText = pendingSpace = false;
            }
            if (name == "img")
            {
                Break("image", false); FlushContent();
                string alt = attrs.GetValueOrDefault("alt", "");
                if (AnnouncementImages.NormalizeSource(attrs.GetValueOrDefault("src")) is string source)
                    result.Append("<vscn-image src64=\"").Append(Encode(source)).Append("\" alt64=\"").Append(Encode(alt))
                        .Append("\" width=\"").Append(Dimension(attrs.GetValueOrDefault("width", styles.GetValueOrDefault("width", ""))))
                        .Append("\" height=\"").Append(Dimension(attrs.GetValueOrDefault("height", styles.GetValueOrDefault("height", "")))).Append("\" style64=\"").Append(Encode(attrs.GetValueOrDefault("style", ""))).Append("\"/>");
                else result.Append(Escape(alt.Length > 0 ? alt : "[图片地址不支持]"));
                hasContent = true; lineHasText = false; Break("image", false);
                continue;
            }
            if (name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
            {
                native = "font";
                opening = $"<font size=\"{(name == "h1" ? 24 : name == "h2" ? 21 : name == "h3" ? 18 : name == "h4" ? 16 : name == "h5" ? 14 : 12)}\" weight=\"bold\">";
            }
            if (name is "font" or "span" or "small" or "sub" or "sup" || styles.Keys.Any(k => k is "color" or "font-size" or "font-family" or "font-weight" or "line-height"))
            {
                native = "font"; var attributes = new StringBuilder();
                void Attr(string key, string? value) { if (!string.IsNullOrEmpty(value)) attributes.Append(' ').Append(key).Append("=\"").Append(value.Replace("\"", "")).Append('"'); }
                string color = AnnouncementDom.Value(attrs, "color", AnnouncementDom.Value(styles, "color"));
                if (AnnouncementDom.Color(color) != null) Attr("color", color);
                double size = AnnouncementDom.Number(AnnouncementDom.Value(styles, "font-size", AnnouncementDom.Value(attrs, "size")), 0, 64);
                if (size > 0) Attr("size", Math.Max(8, size).ToString(CultureInfo.InvariantCulture));
                if (name is "small" or "sub" or "sup") Attr("scale", "75%");
                Attr("family", AnnouncementDom.Value(styles, "font-family", AnnouncementDom.Value(attrs, "family")));
                string weight = AnnouncementDom.Value(styles, "font-weight", AnnouncementDom.Value(attrs, "weight"));
                if (weight.Length > 0) Attr("weight", weight is "bold" or "600" or "700" or "800" or "900" ? "bold" : "normal");
                double lineHeight = AnnouncementDom.Number(AnnouncementDom.Value(styles, "line-height"), 0, 4);
                if (lineHeight > 0) Attr("lineheight", Math.Max(.8, lineHeight).ToString(CultureInfo.InvariantCulture));
                opening = "<font" + attributes + ">";
            }
            if (name == "a" && AnnouncementDom.SafeLink(attrs.GetValueOrDefault("href")))
            { native = "a"; string href = attrs["href"]; if (!href.StartsWith('#') && !href.StartsWith("handbook://", StringComparison.OrdinalIgnoreCase)) href = new Uri(href).AbsoluteUri; opening = "<a href=\"" + Escape(href.Replace("\"", "%22")) + "\">"; }
            if (native != null && stack.Count < 32)
            {
                FlushContent();
                result.Append(opening);
                stack.Add((name, native));
                if (token.Value.TrimEnd().EndsWith("/>", StringComparison.Ordinal))
                { result.Append("</").Append(native).Append('>'); stack.RemoveAt(stack.Count - 1); }
            }
            else if (name is "div" or "section" or "article" && stack.Count < 32
                && !token.Value.TrimEnd().EndsWith("/>", StringComparison.Ordinal)) stack.Add((name, ""));
        }
        for (int i = stack.Count - 1; i >= 0; i--)
            if (stack[i].Native.Length > 0) result.Append("</").Append(stack[i].Native).Append('>');
        return result.ToString();

        // Collapse adjoining positive block margins, like ordinary HTML. Defer them
        // (and list markers) so indentation adds no rows and page edges have no margin.
        void Break(string name, bool nestedList, bool endItem = false)
        {
            pendingSpace = false;
            if (!hasContent || (awaitingItemText && !endItem)) return;
            pendingBreak = true;
            double em = name is "p" or "pre" or "blockquote" or "card" or "image" or "figure" || (name is "ul" or "ol" && !nestedList) ? 1 : 0;
            double pixels = name switch { "h1" => 24 * .67, "h2" => 21 * .83, "h3" or "h4" or "h5" or "h6" => 18, _ => 0 };
            pendingEm = Math.Max(pendingEm, em);
            pendingPixels = Math.Max(pendingPixels, pixels);
        }
        void FlushBreak()
        {
            if (!pendingBreak) return;
            // An internal boundary, handled locally by the notice layout rather than
            // emulating a margin with blank text lines in a potentially different font.
            result.Append("<vscn-gap em=\"").Append(pendingEm.ToString(CultureInfo.InvariantCulture))
                .Append("\" px=\"").Append(pendingPixels.ToString(CultureInfo.InvariantCulture)).Append("\"/>");
            lineHasText = pendingSpace = pendingBreak = awaitingItemText = false;
            pendingEm = pendingPixels = 0;
        }
        void FlushContent()
        {
            FlushBreak();
            if (pendingItem == null) return;
            result.Append(pendingItem);
            pendingItem = null;
            hasContent = lineHasText = awaitingItemText = true;
        }
    }

    // VTML decodes only angle brackets and nbsp, not XML amp/quote entities.
    private static string Escape(string text) => text.Replace("<", "&lt;").Replace(">", "&gt;");
    private static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    private static string Color(string? value, string fallback)
    {
        if (value?.Length == 4 && value[0] == '#' && value[1..].All(Uri.IsHexDigit))
            value = "#" + string.Concat(value[1..].Select(c => new string(c, 2)));
        return value?.Length == 7 && value[0] == '#' && value[1..].All(Uri.IsHexDigit) ? value : fallback;
    }
    private static string Dimension(string? value)
    {
        value = value?.Trim().Replace("px", "", StringComparison.OrdinalIgnoreCase);
        if (value?.EndsWith('%') == true && double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double percent) && double.IsFinite(percent))
            return Math.Clamp(percent, 1, 100).ToString(CultureInfo.InvariantCulture) + "%";
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double pixels) && double.IsFinite(pixels) && pixels > 0
            ? Math.Clamp(pixels, 1, 2048).ToString(CultureInfo.InvariantCulture) : "0";
    }
}
