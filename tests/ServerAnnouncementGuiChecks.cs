using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using SeraphCivilizationSimulation;

internal static class ServerAnnouncementGuiChecks
{
    internal static void Run(ICoreClientAPI api, Action<bool, string> check, Action<GuiDialog, string> capture,
        Action<GuiDialog, GuiElement> click, Action<GuiDialog, double, double> clickAt, List<string> links)
    {
        using var hud = new ServerAnnouncementHud(api);
        hud.Show(ServerAnnouncementChecks.Example); hud.OnRenderGUI(0);
        check(hud.IsOpened() && !hud.Focusable && !hud.PrefersUngrabbedMouse && !hud.CaptureAllInputs()
            && !hud.OnEscapePressed(), "Server notices must remain a HUD without stealing gameplay or Escape.");
        var bounds = hud.SingleComposer.Bounds;
        check(Math.Abs(bounds.renderX) < .5
            && Math.Abs(bounds.renderY - (Math.Min(72, api.Render.FrameHeight / RuntimeEnv.GUIScale * .08) + 32) * RuntimeEnv.GUIScale) < 1
            && bounds.renderX + bounds.OuterWidth <= api.Render.FrameWidth + 1
            && bounds.renderY + bounds.OuterHeight <= api.Render.FrameHeight + 1,
            "The notification must sit flush against the left edge, 32 logical pixels lower, and fit small/scaled viewports.");
        var elements = new[] { "staticElements", "interactiveElements" }.SelectMany(name =>
            ((Dictionary<string, GuiElement>)typeof(GuiComposer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(hud.SingleComposer)!).Values).Distinct().ToArray();
        var overlay = elements.OfType<GuiElementGameOverlay>().Single();
        check(ReferenceEquals(typeof(GuiElementGameOverlay).GetField("bgcolor", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(overlay), GuiStyle.DialogLightBgColor) && GuiStyle.DialogLightBgColor[3] is > 0 and < 1,
            "Notifications must use the native tutorial overlay and its translucent background color.");
        check(!elements.Any(e => e is GuiElementStaticText or GuiElementTextButton or GuiElementScrollbar or GuiElementDialogBackground)
            && hud.SingleComposer.GetElement("dismiss") == null && hud.SingleComposer.GetElement("scroll") == null,
            "The notification HUD must contain only its content, without a title, buttons, footer, visible scrollbar or dialog background.");
        var reader = hud.SingleComposer.GetContainer("content").Elements.OfType<GuiElementAnnouncement>().Single();
        var texts = reader.Components.OfType<RichTextComponent>().ToArray();
        check(texts.All(t => string.Concat(t.Lines.Select(l => l.Text)) == t.DisplayText)
            && texts.All(t => t.BoundsPerLine.All(b => b.X + b.Width <= reader.Bounds.InnerWidth + .5)),
            "Mixed Chinese and English notice text and links must wrap without overflow or lost characters.");
        var link = texts.OfType<LinkTextComponent>().Single();
        check(link.Lines.Length > 0 && link.Href == "https://vintagestory.top/t/topic/897",
            "The supplied voice chat notice must retain a real native link.");
        var line = link.BoundsPerLine[0];
        clickAt(hud, reader.Bounds.renderX + line.X + 2, reader.Bounds.renderY + line.Y + line.Height / 2);
        check(links.LastOrDefault() == link.Href, "The notice link must open its original URL on click.");
        capture(hud, "server-announcement");
        hud.OnRenderGUI(9);
        check(hud.IsOpened(), "A notification must remain visible before its ten-second deadline.");
        hud.Show("<h2>后续通知</h2>" + string.Concat(Enumerable.Repeat("<p>中文长公告，保留全部内容并滚动阅读。</p>", 40)));
        hud.OnRenderGUI(0);
        reader = hud.SingleComposer.GetContainer("content").Elements.OfType<GuiElementAnnouncement>().Single();
        check(reader.Components.OfType<RichTextComponent>().Any(c => c.DisplayText.Contains("后续通知"))
            && !reader.Components.OfType<LinkTextComponent>().Any(),
            "A new notification must replace the visible announcement immediately instead of waiting in a queue.");
        var body = hud.SingleComposer.GetContainer("content").Bounds;
        var clip = hud.SingleComposer.GetContainer("content").InsideClipBounds;
        check(body.fixedY == 0 && clip.InnerHeight < body.OuterHeight,
            "Long announcements must scroll from the top within a bounded HUD.");
        var outsideWheel = new MouseWheelEventArgs { delta = -1 };
        hud.OnMouseWheel(outsideWheel);
        check(!outsideWheel.IsHandled && body.fixedY == 0, "Scrolling outside a notice must remain available to gameplay.");
        clickAt(hud, clip.renderX + 4, clip.renderY + 4);
        hud.OnRenderGUI(1);
        check(hud.IsOpened(), "Replacing a notification must reset its ten-second display deadline.");
        for (int i = 0; i < 5; i++) hud.OnMouseWheel(new MouseWheelEventArgs { delta = -1, deltaPrecise = -1 });
        check(body.fixedY == -200, "Long notices must remain readable with the mouse wheel and no visible scrollbar.");
        capture(hud, "server-announcement-long");
        for (int i = 0; i < 25; i++) hud.Show("<p>最新通知 " + i + "</p>");
        hud.OnRenderGUI(0);
        reader = hud.SingleComposer.GetContainer("content").Elements.OfType<GuiElementAnnouncement>().Single();
        check(string.Concat(reader.Components.OfType<RichTextComponent>().Select(c => c.DisplayText)) == "最新通知 24"
            && hud.SingleComposer.GetContainer("content").Bounds.fixedY == 0,
            "Notice floods must display only the newest content and reset its scroll position.");
        clip = hud.SingleComposer.GetContainer("content").InsideClipBounds;
        clickAt(hud, clip.renderX + 4, clip.renderY + 4);
        hud.OnRenderGUI(9);
        check(hud.IsOpened(), "The latest notification must receive its own full ten-second display.");
        hud.Show(" ");
        hud.Show(new string('x', HtmlDocumentApi.MaxSourceLength + 1));
        hud.OnRenderGUI(1);
        check(!hud.IsOpened(), "Notifications must expire at ten seconds even while hovered; invalid input must not extend the deadline.");
        hud.OnRenderGUI(20);
        check(!hud.IsOpened(), "Old announcements must never reappear after the latest one expires.");
        hud.Show("再次通知"); hud.OnRenderGUI(0);
        check(hud.IsOpened(), "A new notice must reopen the HUD after expiration.");
        string detailsHtml = "<table><tr><td>表格内容</td></tr></table><details><summary>展开通知详情</summary>"
            + string.Concat(Enumerable.Repeat("<p>折叠正文：中文与 HTML 均可阅读。</p>", 8)) + "</details>";
        hud.Show(detailsHtml); hud.OnRenderGUI(0);
        reader = hud.SingleComposer.GetContainer("content").Elements.OfType<GuiElementAnnouncement>().Single();
        var disclosure = reader.Components.OfType<AnnouncementDisclosure>().Single();
        check(reader.Components.OfType<AnnouncementTable>().Any(), "The notification reader must retain native HTML tables.");
        double collapsedHeight = reader.Bounds.OuterHeight;
        var toggle = disclosure.Children.OfType<AnnouncementSwitch>().Single().BoundsPerLine[0];
        clickAt(hud, reader.Bounds.renderX + disclosure.BoundsPerLine[0].X + disclosure.ContentX + toggle.X + 4 * RuntimeEnv.GUIScale,
            reader.Bounds.renderY + disclosure.BoundsPerLine[0].Y + disclosure.ContentY + toggle.Y + toggle.Height / 2);
        hud.OnRenderGUI(0);
        reader = hud.SingleComposer.GetContainer("content").Elements.OfType<GuiElementAnnouncement>().Single();
        check(reader.Bounds.OuterHeight > collapsedHeight && !reader.HasNewImages,
            "Expanding HTML details in a HUD notice must reflow its content and wheel scrolling range.");
        capture(hud, "server-announcement-html");
        hud.Show(detailsHtml); hud.OnRenderGUI(0);
        reader = hud.SingleComposer.GetContainer("content").Elements.OfType<GuiElementAnnouncement>().Single();
        check(Math.Abs(reader.Bounds.OuterHeight - collapsedHeight) < .5,
            "Even identical new announcements must reset expanded details rather than inherit old interaction state.");

        var font = AnnouncementFonts.Readable(CairoFont.WhiteSmallText(), "中文 ABC");
        var windows = AnnouncementTextLayout.Wrap(font, "第一行\r\n第二行", 500, 0, 0);
        check(windows.Length == 2, "Windows line endings must create one break instead of blank lines.");
        var edited = AnnouncementTextLayout.Wrap(font, "甲\r\n乙", 500, 0, 0, keepNewlines: true);
        check(string.Concat(edited.Select(l => l.Text)) == "甲\r\n乙" && edited.Length == 2,
            "Editor wrapping must retain both CRLF characters while displaying a single line break.");
        string rare = "𠀀𠀁𠀂𠀃𠀄𠀅";
        var rareLines = AnnouncementTextLayout.Wrap(font, rare, font.GetTextExtents("𠀀𠀁").XAdvance + 1,
            font.GetTextExtents("𠀀").XAdvance, 0);
        check(rareLines[0].Text.Length > 0 && string.Concat(rareLines.Select(l => l.Text)) == rare,
            "Supplementary Chinese characters must wrap at grapheme boundaries and use remaining line space.");
    }
}
