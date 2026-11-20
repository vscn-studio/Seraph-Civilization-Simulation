using System.Net;

namespace SeraphCivilizationSimulation;

internal static class AnnouncementHtmlTools
{
    internal static readonly string[] Tools = ["bold", "italic", "link", "image", "list", "numbered", "table"];
    internal static readonly string[] FormatTools = ["h1", "h2", "h3", "h4", "h5", "h6", "p", "br", "hr", "u", "s", "del", "ins", "small", "mark", "sub", "sup", "quote", "q", "code", "kbd", "samp", "dl"];
    internal static readonly string[] InsertTools = ["card", "details", "checkbox", "toc", "anchor", "footnote", "color", "background", "center", "div", "span", "main", "article", "section", "header", "footer", "aside", "nav"];
    internal static AnnouncementMarkdown.Edit Format(string source, int start, int end, string tool, string placeholder)
    {
        start = Math.Clamp(start, 0, source.Length); end = Math.Clamp(end, start, source.Length);
        string selected = source[start..end];
        string value = selected.Length > 0 ? selected : WebUtility.HtmlEncode(placeholder);
        string tag = tool switch { "bold" => "strong", "italic" => "em", "quote" => "blockquote", _ => tool };
        (string left, string right) = tool switch
        {
            "link" => ("<a href=\"https://example.org\" title=\"\">", "</a>"),
            "image" => ("<img src=\"https://example.org/image.png\" alt=\"", "\" style=\"max-width:100%;height:auto\">"),
            "list" => ("<ul>\n<li>", "</li>\n</ul>"),
            "numbered" => ("<ol start=\"1\" type=\"1\">\n<li>", "</li>\n</ol>"),
            "table" => ("<table style=\"width:100%;border:1px solid #88714d\">\n<caption>", "</caption>\n<thead><tr><th>标题</th><th>标题</th></tr></thead>\n<tbody><tr><td>内容</td><td>内容</td></tr></tbody>\n</table>"),
            "code" => ("<pre><code>", "</code></pre>"),
            "dl" => ("<dl><dt>", "</dt><dd>说明</dd></dl>"),
            "card" => ("<div class=\"card\">", "</div>"),
            "details" => ("<details><summary>", "</summary><p>展开内容</p></details>"),
            "checkbox" => ("<label><input type=\"checkbox\"> ", "</label>"),
            "anchor" => ("<span id=\"section\">", "</span>"),
            "footnote" => ("<a href=\"#note-1\" id=\"ref-1\"><sup>[1]</sup></a><p id=\"note-1\">", " <a href=\"#ref-1\">返回正文</a></p>"),
            "color" => ("<span style=\"color:#ffd56a\">", "</span>"),
            "background" => ("<div style=\"background-color:#30291f;padding:12px;border:1px solid #88714d;border-radius:6px\">", "</div>"),
            "center" => ("<div style=\"text-align:center\">", "</div>"),
            _ => ("<" + tag + ">", "</" + tag + ">")
        };
        if (tool is "code" or "image" && selected.Length > 0) value = WebUtility.HtmlEncode(selected);
        string replacement = tool switch { "br" => "<br>", "hr" => "<hr>", "toc" => "<nav data-toc=\"true\"></nav>", _ => left + value + right };
        int from = tool is "br" or "hr" or "toc" ? replacement.Length : left.Length;
        int to = tool is "br" or "hr" or "toc" ? from : from + value.Length;
        if (tool is "link" or "image")
        { from = replacement.IndexOf("https://", StringComparison.Ordinal); to = replacement.IndexOf('"', from); }
        string result = source[..start] + replacement + source[end..];
        return result.Length > AnnouncementState.MaxLength ? new(source, start, end) : new(result, start + from, start + to);
    }

    internal static AnnouncementMarkdown.Edit Insert(string source, int start, int end, string html)
    {
        start = Math.Clamp(start, 0, source.Length); end = Math.Clamp(end, start, source.Length);
        string result = source[..start] + html + source[end..];
        return result.Length > AnnouncementState.MaxLength ? new(source, start, end) : new(result, start + html.Length, start + html.Length);
    }
}
