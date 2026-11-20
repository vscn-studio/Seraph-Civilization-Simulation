using SeraphCivilizationSimulation;

internal static class AnnouncementHtmlChecks
{
    internal static void Run(Action<bool, string> check)
    {
        const string html = "<h1>目录标题</h1><p><strong>中文</strong> <a href='#note'>跳转</a></p>"
            + "<table border='1'><caption>数据</caption><thead><tr><th>名称</th><th>图片</th></tr></thead>"
            + "<tbody><tr><td rowspan='2'>物品</td><td><img src='game:textures/gui/logo.png' alt='图标' style='max-width:100%;height:auto'></td></tr>"
            + "<tr><td>第二行</td></tr></tbody></table><p id='note'>脚注</p>";
        string markup = AnnouncementDom.Markup(html);
        check(markup.Contains("size=\"24\"") && markup.Contains("<strong>中文</strong>")
            && markup.Contains("href=\"#note\""),
            "HTML headings, emphasis and internal anchors must use native markup.");
        check(markup.Contains("kind=\"table\"") && markup.Contains("data64="),
            "HTML tables must remain native table blocks with their image layout metadata encoded for the renderer.");
        string cleaned = AnnouncementDom.Prepare("<script>alert(1)</script><p onclick='bad()'>安全</p><iframe src='x'></iframe>");
        check(!cleaned.Contains("script", StringComparison.OrdinalIgnoreCase)
            && !cleaned.Contains("onclick", StringComparison.OrdinalIgnoreCase)
            && !cleaned.Contains("iframe", StringComparison.OrdinalIgnoreCase)
            && cleaned.Contains("安全"),
            "HTML preparation must remove scripts, frames and event handlers while keeping text.");
        string toc = AnnouncementDom.Prepare("<nav data-toc='true'></nav><h2>第一章</h2><h2>第二章</h2>");
        check(toc.Contains("href=\"#vscn-heading-1\"") && toc.Contains("第二章"),
            "HTML preparation must generate a heading table of contents with stable anchors.");
        check(AnnouncementHtmlTools.Format("甲", 0, 1, "table", "文字").Text.Contains("<table"),
            "The HTML table toolbar action must insert a complete table template.");
    }
}
