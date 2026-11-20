using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace SeraphCivilizationSimulation;

// Local wrapping for notices: preserve native fonts and links without depending on
// the active locale or replacing other mods' global text layout.
internal static partial class AnnouncementTextLayout
{
    private const string Opening = "（［｛〈《「『【〔〖〘〚([{“‘";
    private const string Closing = "，。！？、；：）］｝〉》」』】〕〗〙〛)]},.!?;:%”’…";
    public static RichTextComponentBase[] Components(ICoreClientAPI api, string markup, AnnouncementInteraction? interaction = null)
        => Components(api, VtmlParser.Tokenize(api.Logger, markup), new ImageBudget(), 0, interaction ?? new());

    private sealed class ImageBudget { public int Count, Interactions, Nodes; }
    private static RichTextComponentBase[] Components(ICoreClientAPI api, IEnumerable<VtmlToken> tokens, ImageBudget budget, int depth, AnnouncementInteraction interaction, string? inheritedLink = null)
    {
        // Use the same readable base for text and native break components, so
        // a missing-glyph substitute cannot give a plain br a taller line box.
        var font = AnnouncementFonts.Readable(CairoFont.WhiteSmallText(), "公告 中文 ABC");
        var blocks = new List<RichTextComponentBase>();
        // Keep native font/link parsing intact, without registering a global tag converter.
        // A fresh placeholder cannot collide with user text or appear in the rendered notice.
        string prefix = "\uFFFC" + Guid.NewGuid().ToString("N") + ":";
        var rewritten = new StringBuilder();
        Rewrite(tokens, inheritedLink);
        void Rewrite(IEnumerable<VtmlToken> children, string? href)
        {
            foreach (var token in children)
            {
                if (token is VtmlTextToken text)
                {
                    // Native <a> flattens all children into text. Put links on text leaves so
                    // headings, line breaks and visual blocks retain their own formatting.
                    if (href != null) rewritten.Append("<a href=\"").Append(href.Replace("\"", "%22")).Append("\">");
                    rewritten.Append(text.Text.Replace("<", "&lt;").Replace(">", "&gt;"));
                    if (href != null) rewritten.Append("</a>");
                    continue;
                }
                var tag = (VtmlTagToken)token;
                if (tag.Name == "a")
                {
                    string? target = tag.Attributes.GetValueOrDefault("href");
                    Rewrite(tag.ChildElements, AnnouncementDom.SafeLink(target) ? target : href);
                    continue;
                }
                RichTextComponentBase? block = null;
                if (tag.Name == "vscn-format")
                {
                    blocks.Add(new DecorationMarker(api, tag.Attributes.GetValueOrDefault("mode", "")));
                    rewritten.Append(prefix).Append(blocks.Count - 1).Append('\uFFFC');
                    Rewrite(tag.ChildElements, href);
                    blocks.Add(new DecorationMarker(api, null));
                    rewritten.Append(prefix).Append(blocks.Count - 1).Append('\uFFFC'); continue;
                }
                if (tag.Name == "vscn-native" && depth < 8 && ++budget.Nodes <= 1024)
                {
                    string data = AnnouncementDom.Decode(tag.Attributes.GetValueOrDefault("data64"));
                    string kind = tag.Attributes.GetValueOrDefault("kind", "");
                    RichTextComponentBase[] Parse(string html) => Components(api, VtmlParser.Tokenize(api.Logger, AnnouncementDom.Markup(html)), budget, depth + 1, interaction, href);
                    var element = kind is "anchor" or "rule" ? null : AnnouncementDom.Parse(data).FirstElementChild;
                    if (kind == "anchor") block = new AnnouncementAnchor(api, data);
                    else if (kind == "rule") block = new AnnouncementRule(api);
                    else if (kind == "table" && element != null) block = new AnnouncementTable(api, element, Parse);
                    else if (kind == "box" && element != null)
                    {
                        var styles = AnnouncementDom.Styles(element);
                        string textStyles = string.Join(';', styles.Where(p => p.Key is "color" or "font-size" or "font-family" or "font-weight" or "line-height").Select(p => p.Key + ":" + p.Value));
                        block = new AnnouncementBox(api, Parse("<span style=\"" + System.Net.WebUtility.HtmlEncode(textStyles) + "\">" + element.InnerHtml + "</span>"), styles, element.Id ?? "");
                    }
                    else if (kind is "checkbox" or "details" && element != null)
                    {
                        int key = ++budget.Interactions;
                        bool open = interaction.Toggles.GetValueOrDefault(key, element.HasAttribute(kind == "details" ? "open" : "checked"));
                        if (kind == "checkbox") block = new AnnouncementSwitch(api, "", interaction, key, open, true);
                        else
                        {
                            var summary = element.Children.FirstOrDefault(c => c.LocalName == "summary");
                            string label = summary?.TextContent ?? "详情"; summary?.Remove();
                            var body = Parse(element.InnerHtml); // Allocate stable interaction keys even when closed.
                            var toggle = new AnnouncementSwitch(api, label, interaction, key, open);
                            block = new AnnouncementDisclosure(api, open ? new RichTextComponentBase[] { toggle, new BlockSpacing(api, 8) }.Concat(body).ToArray() : [toggle], open ? [] : body);
                        }
                    }
                    else if (kind == "recipe" && element != null)
                        block = AnnouncementRecipes.Component(api, element, interaction);
                    else if (kind == "item" && element != null)
                        block = AnnouncementGameResources.Component(api, element, interaction);
                }
                else if (tag.Name == "vscn-gap")
                    block = new BlockSpacing(api, Math.Max(font.UnscaledFontsize * Number(tag, "em"), Number(tag, "px")));
                else if (tag.Name == "vscn-image")
                {
                    if (++budget.Count <= AnnouncementImages.MaxImages) block = new AnnouncementImage(api, tag.Attributes, href, interaction.Navigate);
                    else rewritten.Append("[图片数量超出限制]");
                }
                else if (tag.Name == "vscn-card")
                {
                    if (depth < 8) block = new AnnouncementCard(api, Components(api, tag.ChildElements, budget, depth + 1, interaction, href), tag.Attributes);
                    else { Rewrite(tag.ChildElements, href); continue; }
                }
                if (block != null)
                {
                    blocks.Add(block); rewritten.Append(prefix).Append(blocks.Count - 1).Append('\uFFFC'); continue;
                }
                rewritten.Append('<').Append(tag.Name);
                foreach (var attribute in tag.Attributes ?? [])
                    rewritten.Append(' ').Append(attribute.Key).Append("=\"").Append(attribute.Value.Replace("\"", "%22")).Append('"');
                rewritten.Append('>'); Rewrite(tag.ChildElements, href);
                if (tag.Name != "br") rewritten.Append("</").Append(tag.Name).Append('>');
            }
        }
        var placeholder = new Regex(Regex.Escape(prefix) + "([0-9]+)\uFFFC");
        var components = new List<RichTextComponentBase>();
        var decorations = new Stack<string>();
        foreach (var component in VtmlUtil.Richtextify(api, rewritten.ToString(), font))
        {
            if (component is not RichTextComponent text) { components.Add(component); continue; }
            int start = 0;
            foreach (Match match in placeholder.Matches(text.DisplayText))
            {
                AddText(start, match.Index);
                var block = blocks[int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)];
                if (block is DecorationMarker marker) { if (marker.Mode == null) { if (decorations.Count > 0) decorations.Pop(); } else decorations.Push(marker.Mode); }
                else components.Add(block);
                start = match.Index + match.Length;
            }
            AddText(start, text.DisplayText.Length);
            text.Dispose();

            void AddText(int from, int to)
            {
                if (from == to) return;
                string value = text.DisplayText[from..to];
                int offset = from;
                foreach (var run in AnnouncementFonts.Runs(text.Font, value))
                {
                    RichTextComponent replacement = text is LinkTextComponent link
                        ? new NoticeLink(api, link, run.Text, run.Font, interaction) : new NoticeText(api, run.Text, run.Font, string.Join(" ", decorations));
                    if (offset == 0) replacement.PaddingLeft += text.PaddingLeft;
                    offset += run.Text.Length;
                    if (offset == text.DisplayText.Length) replacement.PaddingRight += text.PaddingRight;
                    replacement.VerticalAlign = text.VerticalAlign;
                    replacement.UnscaledMarginTop = text.UnscaledMarginTop;
                    components.Add(replacement);
                }
            }
        }
        return components.ToArray();
    }

    private static double Number(VtmlTagToken tag, string key)
        => double.TryParse(tag.Attributes.GetValueOrDefault(key), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number)
            ? Math.Clamp(number, 0, 2048) : 0;

    // The document and every card share the same line/baseline and block layout.
    internal static double Measure(RichTextComponentBase[] components, double width)
    {
        TextFlowPath[] path = [new(0, 0, Math.Max(1, width), double.MaxValue)];
        double x = 0, y = 0, lineHeight = 0, ascent = 0;
        var currentLine = new List<RichTextComponentBase>();
        foreach (var component in components)
        {
            if (component is AnnouncementAnchor)
            { ((AnnouncementAnchor)component).Position(x, y); continue; }
            if (component is BlockSpacing spacing)
            {
                AlignCurrentLine(); currentLine.Clear();
                y += (x > 0 ? lineHeight : 0) + GuiElement.scaled(spacing.UnscaledGap);
                x = lineHeight = ascent = 0; continue;
            }
            if (component is AnnouncementBlock && component is not AnnouncementGameResources.AnnouncementInlineItemBox)
            {
                AlignCurrentLine(); currentLine.Clear();
                y += x > 0 ? lineHeight : 0;
                component.CalcBounds(path, 0, 0, y, out _);
                var block = component.BoundsPerLine[0]; y = block.Y + block.Height;
                x = lineHeight = ascent = 0; continue;
            }
            var result = component.CalcBounds(path, lineHeight, x, y, out double nextX);
            x = nextX;
            if (component.BoundsPerLine.Length == 0) continue;
            var first = component.BoundsPerLine[0];
            if (result != EnumCalcBoundsResult.Continue)
            {
                ascent = Math.Max(ascent, first.AscentOrHeight); AlignCurrentLine();
                if (result == EnumCalcBoundsResult.Multiline) Align(component, first, ascent);
                currentLine.Clear();
                var last = component.BoundsPerLine[^1]; y = last.Y;
                lineHeight = last.Width > 0 ? last.Height : 0;
                ascent = last.Width > 0 ? last.AscentOrHeight : 0;
            }
            else { lineHeight = Math.Max(lineHeight, first.Height); ascent = Math.Max(ascent, first.AscentOrHeight); }
            currentLine.Add(component);
        }
        AlignCurrentLine(); return y + (x > 0 ? lineHeight : 0) + 1;
        void AlignCurrentLine()
        {
            foreach (var component in currentLine) Align(component, component.BoundsPerLine[^1], ascent);
        }
        static void Align(RichTextComponentBase component, LineRectangled line, double lineAscent)
        {
            if (component.VerticalAlign == EnumVerticalAlign.Bottom) line.Y = Math.Ceiling(line.Y + lineAscent - line.AscentOrHeight);
            else if (component.VerticalAlign == EnumVerticalAlign.Middle) line.Y = Math.Ceiling(line.Y + (lineAscent - line.AscentOrHeight) / 2);
            else if (component.VerticalAlign == EnumVerticalAlign.FixedOffset) line.Y += component.UnscaledMarginTop;
        }
    }

    internal sealed class BlockSpacing : RichTextComponentBase
    {
        public double UnscaledGap { get; }
        public BlockSpacing(ICoreClientAPI api, double gap) : base(api)
        { UnscaledGap = gap; BoundsPerLine = []; }
    }

    private static EnumCalcBoundsResult Layout(RichTextComponent text, TextFlowPath[] path, double currentLineHeight, double x, double y,
        out LineRectangled[] bounds, out double nextX)
    {
        text.Lines = Wrap(text.Font, text.DisplayText, path[0].X2, x + GuiElement.scaled(text.PaddingLeft), y,
            firstLineHeight: currentLineHeight);
        bounds = text.Lines.Select(line => line.Bounds).ToArray();
        if (bounds.Length > 0) bounds[^1].Width += GuiElement.scaled(text.PaddingRight);
        nextX = bounds.Length == 0 ? x : bounds[^1].X + bounds[^1].Width;
        return bounds.Length > 1 ? EnumCalcBoundsResult.Multiline : EnumCalcBoundsResult.Continue;
    }

    private sealed class DecorationMarker(ICoreClientAPI api, string? mode) : RichTextComponentBase(api) { internal string? Mode => mode; }

    private sealed class NoticeText(ICoreClientAPI api, string text, CairoFont font, string decoration) : RichTextComponent(api, text, font)
    {
        public override void ComposeElements(Context ctx, ImageSurface surface)
        {
            ctx.Save();
            if (decoration.Contains("mark"))
            { ctx.SetSourceRGBA(.5,.4,0,.65); foreach (var line in BoundsPerLine) { ctx.Rectangle(line.X,line.Y,line.Width,line.Height); ctx.Fill(); } }
            double shift = decoration.Contains("sup") ? -GuiElement.scaled(4) : decoration.Contains("sub") ? GuiElement.scaled(3) : 0;
            ctx.Translate(0, shift); base.ComposeElements(ctx,surface);
            if (decoration.Contains("underline") || decoration.Contains("line-through"))
            {
                ctx.SetSourceRGBA(Font.Color); ctx.LineWidth = Math.Max(1,GuiElement.scaled(1));
                foreach (var line in BoundsPerLine)
                {
                    double y = line.Y + line.Height * (decoration.Contains("line-through") ? .45 : .87);
                    ctx.MoveTo(line.X,y); ctx.LineTo(line.X+line.Width,y); ctx.Stroke();
                }
            }
            ctx.Restore();
        }
        public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] path, double height, double x, double y, out double nextX)
        {
            var result = Layout(this, path, height, x, y, out var bounds, out nextX);
            BoundsPerLine = bounds;
            return result;
        }
    }

    private sealed class NoticeLink : LinkTextComponent
    {
        public NoticeLink(ICoreClientAPI api, LinkTextComponent source, string text, CairoFont font, AnnouncementInteraction interaction) : base(api, text, font, source.Href.StartsWith('#') ? link => interaction.Navigate?.Invoke(link.Href) : source.Href.StartsWith("handbook://", StringComparison.OrdinalIgnoreCase) ? link => AnnouncementGameResources.Open(api, link.Href) : null)
        { Font = font; Href = source.Href; Clickable = source.Clickable; }
        public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] path, double height, double x, double y, out double nextX)
        {
            var result = Layout(this, path, height, x, y, out var bounds, out nextX);
            BoundsPerLine = bounds;
            return result;
        }
    }

    public static TextLine[] Wrap(CairoFont font, string text, double width, double startX, double startY, bool keepNewlines = false,
        double firstLineHeight = 0)
    {
        if (text.Length == 0) return [];
        using var surface = new ImageSurface(Format.Argb32, 1, 1);
        using var ctx = new Context(surface);
        font.SetupContext(ctx);
        double height = ctx.FontExtents.Height * font.LineHeightMultiplier;
        double x = startX, y = startY;
        int[] positions = [.. StringInfo.ParseCombiningCharacters(text), text.Length];
        var result = new List<TextLine>();
        int start = 0;
        while (start < positions.Length - 1)
        {
            int end = start, lastBreak = -1;
            bool newline = false;
            while (end < positions.Length - 1)
            {
                char current = text[positions[end]];
                if (current is '\r' or '\n') { newline = true; break; }
                double length = ctx.TextExtents(text[positions[start]..positions[end + 1]]).XAdvance;
                if (length > width - x && (end > start || x > 0)) break;
                end++;
                if (end < positions.Length - 1 && CanBreak(text, positions[end - 1], positions[end])) lastBreak = end;
                if (length > width - x) break; // Always advance even in a very narrow viewport.
            }
            bool overflow = !newline && end < positions.Length - 1;
            if (overflow)
            {
                if (lastBreak > start) end = lastBreak;
                else if (x > 0)
                {
                    Add(""); NextLine();
                    continue; // An unbroken word may fit on the next full line.
                }
            }
            int newlineEnd = end;
            if (newline)
            {
                newlineEnd++;
                // A Windows line ending is one logical break, including in the editor.
                if (text[positions[end]] == '\r' && newlineEnd < positions.Length - 1 && text[positions[newlineEnd]] == '\n') newlineEnd++;
            }
            Add(text[positions[start]..positions[end]] + (newline && keepNewlines ? text[positions[end]..positions[newlineEnd]] : ""));
            start = end;
            if (newline) start = newlineEnd;
            if (start < positions.Length - 1 || newline) NextLine();
            if (newline && start == positions.Length - 1) Add("");
        }
        return result.ToArray();

        void NextLine()
        {
            x = 0;
            y += Math.Max(height, firstLineHeight);
            firstLineHeight = 0;
        }
        void Add(string value)
        {
            double advance = ctx.TextExtents(value).XAdvance;
            result.Add(new TextLine
            {
                Text = value, NextOffsetX = x, RightSpace = width - x - advance,
                Bounds = new LineRectangled(x, y, advance, height) { Ascent = ctx.FontExtents.Ascent }
            });
        }
    }

    private static bool CanBreak(string text, int leftIndex, int rightIndex)
    {
        char left = text[leftIndex], right = text[rightIndex];
        return left is not ('\u00a0' or '\u202f' or '\u2060') && right is not ('\u00a0' or '\u202f' or '\u2060')
            && !Opening.Contains(left) && !Closing.Contains(right)
            && (char.IsWhiteSpace(left) || char.IsWhiteSpace(right) || left is '-' or '/'
                || Cjk(Rune.TryGetRuneAt(text, leftIndex, out var l) ? l.Value : left)
                || Cjk(Rune.TryGetRuneAt(text, rightIndex, out var r) ? r.Value : right));
    }

    private static bool Cjk(int c) => c is >= 0x2e80 and <= 0x9fff or >= 0xf900 and <= 0xfaff
        or >= 0xff00 and <= 0xffef or >= 0x20000 and <= 0x323af;
}
