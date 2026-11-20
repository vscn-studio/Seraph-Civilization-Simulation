using SeraphCivilizationSimulation;

internal static class AnnouncementMarkdownChecks
{
    internal static void Run(Action<bool, string> check)
    {
        string markup = AnnouncementMarkdown.ToNativeMarkup("# 文明公告\n\n欢迎 **共同建设**，*尊重生存*。\n下一行\n\n- 家园\n- 地图\n\n1. 规则\n2. 交易\n\n> 请阅读\n\n```cs\nif (a < b)\n  Work();\n```\n\n[详情](https://example.org/?a=1&b=2)\n\n![横幅](game:textures/gui/logo.png)");
        check(markup.Contains("size=\"24\"") && markup.Contains("<strong>") && markup.Contains("共同建设</strong>") && markup.Contains("<i>尊重生存</i>"),
            "Markdown headings and emphasis must render as native formatted text.");
        check(markup.Contains("<br>下一行") && markup.Contains("• 家园") && markup.Contains("• 地图") && markup.Contains("1. 规则") && markup.Contains("2. 交易"),
            "Markdown line breaks and both list types must render their content and markers.");
        check(markup.Contains("if (a &lt; b)\n  Work();") && markup.Contains("href=\"https://example.org/?a=1&b=2\"") && markup.Contains("<vscn-image"),
            "Markdown code must keep whitespace and escaping, with usable native links and images.");
        check(!AnnouncementMarkdown.ToNativeMarkup("[bad](javascript:alert%281%29)\n\n<script>hidden</script>").Contains("href=")
            && !AnnouncementMarkdown.ToNativeMarkup("<script>hidden</script>").Contains("hidden"),
            "Markdown and embedded HTML must pass through the existing native content filter.");
        string maximum = new('&', AnnouncementState.MaxLength - 4);
        check(AnnouncementMarkdown.ToNativeMarkup(maximum + "结尾标记").EndsWith("结尾标记"),
            "Generated HTML escaping must not truncate a valid maximum-length Markdown document.");
        string legacy = "<article><h2>旧公告</h2><p>正文</p><img src='game:textures/gui/logo.png'></article>";
        check(AnnouncementMarkdown.ToNativeMarkup(legacy) == AnnouncementHtml.ToNativeMarkup(legacy),
            "Editing and previewing a legacy HTML card as Markdown must preserve its native layout.");

        var bold = AnnouncementMarkdown.Format("甲乙丙", 1, 2, "bold", "文字");
        check(bold.Text == "甲**乙**丙" && bold.Text[bold.SelectionStart..bold.SelectionEnd] == "乙",
            "Formatting a selection must wrap only that text and keep its replacement range.");
        check(AnnouncementMarkdown.Format("甲乙", 1, 1, "italic", "文字").Text == "甲*文字*乙",
            "Formatting without a selection must insert an editable placeholder at the caret.");
        check(AnnouncementMarkdown.Format("甲\n乙\n丙", 0, 4, "list", "文字").Text == "- 甲\n- 乙\n丙",
            "Block formatting must cover selected logical lines without touching the next line.");
        check(AnnouncementMarkdown.Format("甲乙\n丙丁", 1, 1, "h2", "文字").Text == "## 甲乙\n丙丁",
            "A heading button in the middle of a line must prefix the whole logical line.");
        var link = AnnouncementMarkdown.Format("详情", 0, 2, "link", "文字");
        check(link.Text == "[详情](https://example.org)" && link.Text[link.SelectionStart..link.SelectionEnd] == "https://example.org",
            "Link insertion must preserve the selected label and select the URL for replacement.");
        var code = AnnouncementMarkdown.Format("```\n代码\n```", 0, 10, "code", "code");
        check(code.Text.StartsWith("````\n") && AnnouncementMarkdown.ToNativeMarkup(code.Text).Contains("```\n代码\n```"),
            "Code blocks containing backticks must use a longer fence and retain their contents.");
        string full = new('中', AnnouncementState.MaxLength);
        check(AnnouncementMarkdown.Format(full, 0, 1, "bold", "文字").Text == full,
            "Toolbar actions must reject an oversized result without losing the original text.");
    }
}
