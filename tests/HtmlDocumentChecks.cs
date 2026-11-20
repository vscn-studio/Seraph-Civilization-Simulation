using Cairo;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using SeraphCivilizationSimulation;

internal static class HtmlDocumentChecks
{
    internal static void Run(ICoreClientAPI api, Action<bool, string> check,
        Action<GuiDialog, string> capture, Action<GuiDialog, GuiElement> click, Action<GuiDialog, double, double> clickAt, Action<LoadedTexture, string> textureImage,
        string itemHtml, string slideshowHtml, string recipeHtml)
    {
        check(HtmlDocumentApi.ApiVersion == 1 && HtmlDocumentApi.PlainText("<h1>中文 &amp; 标题</h1><p>正文<br>下一行</p><script>bad()</script>") == "中文 & 标题\n正文\n下一行",
            "The public HTML API must provide plain text without script or markup.");
        string html = "<h2>测试公告</h2><p>中文公告与 <strong>加粗</strong> <a href='https://example.org'>链接</a></p>"
            + "<table><tr><th>标题</th><th>内容</th></tr><tr><td>物品</td><td>" + itemHtml + "</td></tr></table>"
            + "<details><summary>详细说明</summary><p>折叠正文</p></details>"
            + "<img src='vsscsmod:textures/tests/notice.png' width='160' height='80'>";
        int saves = 0, cancels = 0, transcriptions = 0;
        string savedSource = "", savedTitle = ""; bool signed = false;
        void Save(string title, string source, bool sign) { saves++; savedTitle = title; savedSource = source; signed = sign; }
        using (var dialog = HtmlDocumentApi.CreateDialog(api, "纸张标题", html, Save, () => cancels++, null))
        {
            dialog.TryOpen();
            var editor = dialog.SingleComposer.GetContainer("content").Elements.OfType<GuiElementAnnouncementEditor>().Single();
            check(editor.GetText() == html, "Shared editor must load exact HTML, including native item attributes.");
            editor.CaretPosWithoutLineBreaks = 0;
            click(dialog, dialog.SingleComposer.GetElement("tool-bold"));
            check(editor.GetText().Contains("<strong>") && editor.HasFocus, "Shared toolbar must apply HTML and restore native caret focus.");
            string beforeEmbed = editor.GetText();
            var embed = dialog.SingleComposer.GetElement("menu-embed"); click(dialog, embed);
            Tap(embed.Bounds.renderX + 8 * RuntimeEnv.GUIScale,
                embed.Bounds.renderY + embed.Bounds.InnerHeight + 3.5 * 30 * RuntimeEnv.GUIScale);
            dialog.OnRenderGUI(0);
            var resources = dialog.Composers["resources"];
            check(resources != null, "Shared embed menu must open its own resource picker.");
            for (int i = 0; i < 3; i++) dialog.OnRenderGUI(0);
            capture(dialog, "html-api-resource-picker");
            var grid = resources!.GetElement("grid"); Tap(grid.Bounds.renderX + 10, grid.Bounds.renderY + 10); dialog.OnRenderGUI(0);
            check(dialog.Composers["resources"] == null && editor.GetText() != beforeEmbed && editor.GetText().Contains("data-game-item=") && editor.HasFocus,
                "Shared resource selection must insert persistent item HTML and return focus to the saved caret. closed=" + (dialog.Composers["resources"] == null) + " focus=" + editor.HasFocus + " source=" + editor.GetText());
            string draft = editor.GetText();
            capture(dialog, "html-api-editor");
            click(dialog, dialog.SingleComposer.GetButton("preview")); dialog.OnRenderGUI(0);
            check(dialog.SingleComposer.GetContainer("content").Elements.Single() is GuiElementAnnouncement,
                "Shared preview must reuse the announcement reader and its layout.");
            capture(dialog, "html-api-preview");
            click(dialog, dialog.SingleComposer.GetButton("preview")); dialog.OnRenderGUI(0);
            editor = dialog.SingleComposer.GetContainer("content").Elements.OfType<GuiElementAnnouncementEditor>().Single();
            check(editor.GetText() == draft, "Preview toggling must preserve source exactly.");
            dialog.SingleComposer.GetTextInput("title").SetValue("新标题");
            click(dialog, dialog.SingleComposer.GetButton("save"));
            check(saves == 1 && cancels == 0 && savedTitle == "新标题" && savedSource == draft && !signed && !dialog.IsOpened(),
                "Save must submit complete title/source once, close, and never call cancel.");
            void Tap(double x, double y)
                => clickAt(dialog, x, y);
        }
        using (var dialog = HtmlDocumentApi.CreateDialog(api, "取消", html, Save, () => cancels++, null))
        { dialog.TryOpen(); dialog.TryClose(); }
        check(saves == 1 && cancels == 1, "Cancel and dispose must not save or issue duplicate callbacks.");
        using (var dialog = HtmlDocumentApi.CreateDialog(api, "签名测试", html, Save, null, null))
        {
            dialog.TryOpen(); click(dialog, dialog.SingleComposer.GetButton("sign"));
            var confirm = api.Gui.LoadedGuis.OfType<GuiDialogConfirm>().Single(d => d.IsOpened());
            var decision = typeof(GuiDialogConfirm).GetField("DidPressButton", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ((Action<bool>)decision.GetValue(confirm)!)(false); confirm.TryClose(); confirm.Dispose();
            check(saves == 1 && dialog.IsOpened(), "Rejecting signature confirmation must leave the editor open without saving.");
            click(dialog, dialog.SingleComposer.GetButton("sign"));
            confirm = api.Gui.LoadedGuis.OfType<GuiDialogConfirm>().Single(d => d.IsOpened());
            ((Action<bool>)decision.GetValue(confirm)!)(true); confirm.TryClose(); confirm.Dispose();
            check(saves == 2 && signed && savedSource == html && !dialog.IsOpened(), "Confirming signature must save complete HTML exactly once with the sign flag.");
        }
        using (var dialog = HtmlDocumentApi.CreateDialog(api, "只读 · 签名人", html, null, () => cancels++, () => transcriptions++))
        {
            dialog.TryOpen();
            check(dialog.SingleComposer.GetElement("save") == null && dialog.SingleComposer.GetElement("sign") == null
                && !dialog.SingleComposer.GetContainer("content").Tabbable, "Read-only documents must expose no edit/save/sign actions.");
            click(dialog, dialog.SingleComposer.GetButton("transcribe"));
            check(transcriptions == 1 && dialog.IsOpened(), "Transcription must invoke the caller while leaving the reader open.");
            capture(dialog, "html-api-reader"); dialog.TryClose();
        }
        string longHtml = "<p>" + new string('中', 6000) + "末尾完整</p>";
        using (var dialog = HtmlDocumentApi.CreateDialog(api, "长公告", longHtml, Save, null, null))
        {
            dialog.TryOpen(); click(dialog, dialog.SingleComposer.GetButton("save"));
            check(savedSource == longHtml && savedSource.EndsWith("末尾完整</p>"), "Saving a long document must retain content beyond Billposting's old 4096-character cutoff.");
        }
        bool rejected = false;
        try { using var tooLong = HtmlDocumentApi.CreateDialog(api, "", new string('x', HtmlDocumentApi.MaxSourceLength + 1), Save, null, null); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        check(rejected, "Oversized editor source must be rejected instead of silently truncated.");
        string print = HtmlDocumentWorldTexture.PrintHtml(api, slideshowHtml + recipeHtml + "<details><summary>摘要</summary>全文</details>");
        check(!print.Contains("data-game-item") && !print.Contains("data-game-recipe") && !print.Contains("data-game-slideshow")
            && print.Contains("×") && print.Contains("<table ") && print.Contains("摘要") && print.Contains("全文") && !print.Contains("<details")
            && !print.Contains("[物品不可用]"),
            "World layout must expand resources into static quantities, a recipe table, the first slideshow item and open details.");
        var printed = AnnouncementDom.Parse(HtmlDocumentWorldTexture.PrintHtml(api, html + "<p style='color:#ff0000'>红字</p><input type='checkbox' checked><article><p>卡片</p></article>"));
        check(!printed.QuerySelectorAll("a,details,input").Any() && printed.TextContent.Contains("链接") && printed.TextContent.Contains("[✓]")
            && printed.QuerySelector("td")!.GetAttribute("style")!.Contains("color:#201b14")
            && printed.QuerySelector("article p")!.GetAttribute("style")!.Contains("color:#f5ead5")
            && printed.QuerySelectorAll("p").Any(p => p.TextContent == "红字" && p.GetAttribute("style")!.Contains("color:#ff0000")),
            "Printed links and checkboxes must have Cairo captions; table ink and authored colors must survive recursive layout.");
        foreach (float scale in new[] { 1f, 1.5f })
        {
            RuntimeEnv.GUIScale = scale;
            LoadedTexture? texture = null;
            using (HtmlDocumentApi.CreateWorldTexture(api, html + recipeHtml, 800, 500, value => texture = value))
            {
                check(texture is { TextureId: > 0, Width: 800, Height: 500 }, "World document must upload a bounded shared-layout texture at each GUI scale.");
                textureImage(texture!, "html-api-world-" + scale);
            }
            using (HtmlDocumentApi.CreateWorldTexture(api, longHtml, 4096, 4096, value => texture = value))
            { check(texture is { Width: 1024, Height: 1024 }, "Long world documents must obey the texture allocation cap."); textureImage(texture!, "html-api-world-long-" + scale); }
        }
        RuntimeEnv.GUIScale = 1;
    }
}
