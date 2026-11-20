using System.Text.RegularExpressions;
using Markdig;

namespace SeraphCivilizationSimulation;

internal static class AnnouncementMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseSoftlineBreakAsHardlineBreak().Build();

    public static string ToNativeMarkup(string source)
    {
        source = source[..Math.Min(source.Length, AnnouncementState.MaxLength)];
        // Limit source characters, not the generated HTML: escaping and formatting
        // can expand a valid document well beyond the source limit.
        return AnnouncementHtml.ToNativeMarkup(Markdown.ToHtml(source, Pipeline), int.MaxValue);
    }

    internal static string ToHtml(string source) => Markdown.ToHtml(source, Pipeline);

    internal static readonly string[] Tools = ["h1", "h2", "h3", "bold", "italic", "list", "numbered", "quote", "code", "link", "image"];

    internal readonly record struct Edit(string Text, int SelectionStart, int SelectionEnd);

    internal static Edit Format(string source, int start, int end, string tool, string placeholder)
    {
        start = Math.Clamp(start, 0, source.Length);
        end = Math.Clamp(end, start, source.Length);
        string selected = source[start..end];
        string prefix = tool switch { "h1" => "# ", "h2" => "## ", "h3" => "### ", "list" => "- ", "numbered" => "1. ", "quote" => "> ", _ => "" };
        if (prefix.Length > 0)
        {
            // Block actions operate on complete source lines, including soft wraps.
            start = start == 0 ? 0 : source.LastIndexOf('\n', start - 1) + 1;
            if (end > start && source[end - 1] == '\n') end--;
            int newline = source.IndexOf('\n', end);
            end = newline < 0 ? source.Length : newline;
            selected = source[start..end];
            string body = selected.Length == 0 ? placeholder : selected;
            return Replace(prefix + body.Replace("\n", "\n" + prefix), prefix.Length,
                prefix.Length + body.Length + prefix.Length * body.Count(c => c == '\n'));
        }
        string value = selected.Length == 0 ? placeholder : selected;
        string left, right;
        switch (tool)
        {
            case "bold": left = "**"; right = "**"; break;
            case "italic": left = "*"; right = "*"; break;
            case "link": left = "["; right = "](https://example.org)"; break;
            case "image": left = "!["; right = "](https://example.org/image.png)"; break;
            case "code":
                int longest = Regex.Matches(value, "`+").Select(m => m.Length).DefaultIfEmpty(0).Max();
                string fence = new('`', Math.Max(3, longest + 1));
                left = (start > 0 && source[start - 1] != '\n' ? "\n" : "") + fence + "\n";
                right = "\n" + fence + (end < source.Length && source[end] != '\n' ? "\n" : "");
                break;
            default: throw new ArgumentOutOfRangeException(nameof(tool));
        }
        if (selected.Length > 0 && tool is "link" or "image")
            return Replace(left + value + right, left.Length + value.Length + 2, left.Length + value.Length + right.Length - 1);
        return Replace(left + value + right, left.Length, left.Length + value.Length);

        Edit Replace(string replacement, int from, int to)
        {
            string result = source[..start] + replacement + source[end..];
            return result.Length > AnnouncementState.MaxLength
                ? new(source, start, end) : new(result, start + from, start + to);
        }
    }
}
