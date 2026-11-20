using System.Reflection;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using SeraphCivilizationSimulation;
using Path = System.IO.Path;

internal static class AnnouncementGuiRegressionChecks
{
    internal static void Run(ICoreClientAPI api, Dictionary<int, ImageSurface> textures,
        Dictionary<int, bool> textureClamp, Action<bool, string> check, Action<GuiDialog, string> capture,
        Action<GuiDialog, GuiElement> click, Action<GuiDialog, double, double> clickAt,
        string itemHtml, string slideshowHtml, byte[] imageBytes, string output)
    {
        var failures = new List<string>();
        void Verify(bool condition, string message)
        {
            if (condition) check(true, message);
            else failures.Add(message);
        }
        double scale = RuntimeEnv.GUIScale;
        string size = api.Render.FrameWidth.ToString();
        var slots = AnnouncementTextLayout.Components(api, AnnouncementDom.Markup(itemHtml + itemHtml));
        var cells = slots.OfType<AnnouncementGameResources.AnnouncementInlineItemBox>().ToArray();
        using (var image = new ImageSurface(Format.Argb32, (int)(70 * scale), (int)(36 * scale)))
        using (var ctx = new Context(image))
        {
            // Adjacent cells after text can start on fractional device pixels.
            double x = .25;
            foreach (var cell in cells)
            {
                cell.CalcBounds([new(0, 0, image.Width, 100)], 0, x, 0, out x);
                cell.ComposeElements(ctx, image);
            }
            image.Flush();
            Verify(image.Data.Where((_, i) => i % 4 == 3).Max() <= 52,
                "Adjacent fractional item cells must not paint overlapping background strips.");
            WriteOnDark(image, Path.Combine(output, size + "-adjacent-item-backgrounds.png"));
        }
        foreach (var slot in slots) slot.Dispose();

        using (var composer = api.Gui.CreateCompo("handbook-row-regression", ElementBounds.Fixed(0, 0, 340, 210)))
        {
            AnnouncementGameResources.Resource? chosen = null;
            var clip = ElementBounds.Fixed(0, 0, 310, 192);
            var list = new GuiElementAnnouncementResourceList(api, ElementBounds.Fixed(0, 0, 310, 192), r => chosen = r);
            var resources = Enumerable.Range(0, 12).Select(i => new AnnouncementGameResources.Resource(i.ToString(),
                $"第{i + 1}项：" + (i == 2 ? new string('长', 60) : "手册资源与点击行对齐"), () => "")).ToList();
            list.SetItems(resources);
            composer.BeginClip(clip).AddInteractiveElement(list, "list").EndClip();
            var bar = new GuiElementAutoScrollbar(api, list.ScrollTo, ElementBounds.Fixed(320, 0, 16, 192));
            composer.AddInteractiveElement(bar, "bar"); composer.Compose(false); list.Scrollbar = bar;
            foreach (double scroll in new[] { 0d, 11.25 * scale })
            {
                bar.CurrentYPosition = (float)scroll; bar.TriggerChanged();
                list.RenderInteractiveElements(0);
                var image = textures[PrivateInt(list, "texture")]; image.Flush();
                var pixels = image.Data;
                for (int i = 1; i < 6; i++)
                {
                    int glyphs = 0;
                    int top = Math.Max(0, (int)Math.Floor(i * 32 * scale - scroll));
                    int bottom = Math.Min(image.Height, (int)Math.Floor((i + 1) * 32 * scale - scroll));
                    for (int y = top; y < bottom; y++)
                    for (int col = 0; col < image.Width; col++)
                        if (pixels[y * image.Stride + col * 4 + 3] > 120) glyphs++;
                    Verify(glyphs > 20, $"Handbook resource row {i + 1} must contain its own label at scroll {scroll}.");
                    var args = new MouseEvent((int)(list.Bounds.renderX + 12 * scale),
                        (int)(list.Bounds.renderY + (i * 32 + 16) * scale - scroll), EnumMouseButton.Left, 0);
                    list.OnMouseDownOnElement(api, args);
                    Verify(chosen?.Key == i.ToString(), "The label and row hit target must select the same handbook resource.");
                }
                WriteOnDark(image, Path.Combine(output, size + (scroll == 0 ? "-handbook-rows.png" : "-handbook-rows-scrolled.png")));
            }
        }

        string html = "<p>行内物品：" + itemHtml + itemHtml + " 与轮播 " + slideshowHtml + " 右侧正文</p>"
            + "<p>" + itemHtml + "左侧贴边物品格</p>"
            + string.Concat(Enumerable.Range(1, 35).Select(i => $"<p id='section-{i}'>第{i}段教程，滚动长度随正文高度计算。</p>"));
        var snapshot = new CivilizationSnapshotPacket
        {
            Open = true, CanEditAnnouncement = true, TutorialRevision = 1,
            AnnouncementHtml = html,
            Tutorials = Enumerable.Range(0, 16).Select(i => new AnnouncementEntry
            {
                Id = "tutorial-" + i, Title = $"教程{i + 1}：物品排版和长文滚动",
                AuthorName = "测试作者", AuthorUid = "chief", Published = i % 2 == 0, Html = html
            }).ToList()
        };
        using var window = new VscnClusterWindow(api, _ => { });
        window.Update(snapshot); window.OnRenderGUI(0);
        var announcement = window.SingleComposer.GetContainer("announcement-content").Elements.OfType<GuiElementAnnouncement>().Single();
        // Exercise the fractional positioning caused by resizing and GUI scale.
        announcement.Bounds.fixedX += .25 / scale;
        announcement.Bounds.fixedWidth -= .5 / scale;
        announcement.BeforeCalcBounds(); announcement.Bounds.CalcWorldBounds();
        capture(window, "announcement-edge-regression");
        int announcementTexture = ((LoadedTexture)typeof(GuiElementAnnouncement).GetField("visibleTexture", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(announcement)!).TextureId;
        Verify(textureClamp.GetValueOrDefault(announcementTexture),
            "The announcement viewport texture must clamp sampling at its edges instead of repeating left-edge slots.");

        window.SelectWindowForTest(1); window.OnRenderGUI(0);
        capture(window, "tutorial-list-regression");
        var listBody = window.SingleComposer.GetContainer("tutorial-content");
        CheckScrollbar(window.SingleComposer.GetScrollbar("tutorial-scrollbar"), listBody, null, Verify);
        var secondRow = listBody.Elements.OfType<GuiElementTutorialLink>().Skip(1).First();
        var listBar = window.SingleComposer.GetScrollbar("tutorial-scrollbar");
        listBar.CurrentYPosition = (float)GuiElement.scaled(secondRow.Bounds.fixedY);
        listBar.TriggerChanged();
        var secondRowActions = listBody.Elements.OfType<GuiElementTextButton>().Where(button =>
            button.Bounds.renderY >= secondRow.Bounds.renderY &&
            button.Bounds.renderY < secondRow.Bounds.renderY + secondRow.Bounds.OuterHeight);
        foreach (var button in secondRowActions)
        {
            var actionClick = new MouseEvent((int)(button.Bounds.renderX + button.Bounds.OuterWidth / 2),
                (int)(button.Bounds.renderY + button.Bounds.OuterHeight / 2), EnumMouseButton.Left, 0);
            secondRow.OnMouseDownOnElement(api, actionClick);
            Verify(!actionClick.Handled, "Tutorial rows after the first must leave their action buttons out of the whole-row click target.");
        }
        listBar.CurrentYPosition = 0; listBar.TriggerChanged();
        var firstRow = listBody.Elements.OfType<GuiElementTutorialLink>().First();
        clickAt(window, firstRow.Bounds.renderX + 12 * scale, firstRow.Bounds.renderY + 12 * scale);
        window.OnRenderGUI(0);
        capture(window, "tutorial-reader-regression");
        var readerBody = window.SingleComposer.GetContainer("tutorial-reader-content");
        var article = readerBody.Elements.OfType<GuiElementAnnouncement>().Single();
        CheckScrollbar(window.SingleComposer.GetScrollbar("tutorial-reader-scrollbar"), readerBody, null, Verify);
        var readerBar = window.SingleComposer.GetScrollbar("tutorial-reader-scrollbar");
        readerBar.CurrentYPosition = 80; readerBar.TriggerChanged();
        Verify(Math.Abs(readerBody.Bounds.fixedY * scale + readerBar.CurrentYPosition) < 1,
            "Tutorial reader scrolling must move the document by the scrollbar's pixel offset.");
        click(window, window.SingleComposer.GetElement("tutorial-reader-edit")); window.OnRenderGUI(0);
        var editorBody = window.SingleComposer.GetContainer("tutorial-editor-content");
        var editor = editorBody.Elements.OfType<GuiElementAnnouncementEditor>().Single();
        click(window, editor);
        var key = new KeyEvent { KeyChar = '中' }; window.OnKeyPress(key);
        Verify(key.Handled && editor.GetText().Contains('中'), "Tutorial content editing must retain keyboard input.");
        string draft = string.Concat(Enumerable.Range(0, 90).Select(i => $"<p>第{i}行，教程源码与编辑滚动条</p>\n"));
        editor.SetValue(draft);
        CheckScrollbar(window.SingleComposer.GetScrollbar("tutorial-editor-scrollbar"), editorBody, editor.ContentHeight, Verify);
        int lineBeforeScroll = editor.CaretPosLine, caretBeforeScroll = editor.CaretPosInLine;
        click(window, window.SingleComposer.GetScrollbar("tutorial-editor-scrollbar"));
        Verify(editor.HasFocus && editor.CaretPosLine == lineBeforeScroll && editor.CaretPosInLine == caretBeforeScroll,
            "Using the tutorial scrollbar must preserve editor focus and caret.");
        editor.SetCaretPos(2, editor.GetLines().Count - 2);
        var editorBar = window.SingleComposer.GetScrollbar("tutorial-editor-scrollbar");
        Verify(editorBar.CurrentYPosition > 0, "Typing or moving to the last tutorial line must scroll the caret into view.");
        float oldScroll = editorBar.CurrentYPosition;
        capture(window, "tutorial-editor-regression");
        var cancel = window.SingleComposer.GetElement("tutorial-cancel").Bounds;
        var dialogBounds = window.SingleComposer.Bounds;
        Verify(cancel.renderX + cancel.OuterWidth <= dialogBounds.renderX + dialogBounds.InnerWidth,
            "Tutorial footer buttons must fit inside a narrow game window.");
        snapshot.Open = false; window.Update(snapshot); window.OnRenderGUI(0);
        editorBody = window.SingleComposer.GetContainer("tutorial-editor-content");
        editor = editorBody.Elements.OfType<GuiElementAnnouncementEditor>().Single();
        Verify(editor.HasFocus && editor.GetText() == draft &&
            Math.Abs(window.SingleComposer.GetScrollbar("tutorial-editor-scrollbar").CurrentYPosition - oldScroll) < 1,
            "Refreshing a tutorial must preserve its draft, focus, caret and scroll position.");
        click(window, window.SingleComposer.GetElement("tutorial-preview")); window.OnRenderGUI(0);
        editorBody = window.SingleComposer.GetContainer("tutorial-editor-content");
        CheckScrollbar(window.SingleComposer.GetScrollbar("tutorial-editor-scrollbar"), editorBody, null, Verify);
        capture(window, "tutorial-preview-regression");

        click(window, window.SingleComposer.GetElement("tutorial-cancel")); window.OnRenderGUI(0);
        snapshot.Tutorials[0].Html = "<details><summary>展开教程正文</summary>" + html + "</details>";
        window.Update(snapshot); window.OnRenderGUI(0);
        OpenFirstTutorial();
        var disclosure = article.Components.OfType<AnnouncementDisclosure>().Single();
        double collapsedHeight = article.Bounds.OuterHeight;
        Verify(!window.SingleComposer.GetScrollbar("tutorial-reader-scrollbar").Focusable,
            "A short collapsed tutorial must not show an active scrollbar.");
        var toggleBounds = disclosure.Children.OfType<AnnouncementSwitch>().Single().BoundsPerLine[0];
        clickAt(window, article.Bounds.renderX + disclosure.BoundsPerLine[0].X + disclosure.ContentX + toggleBounds.X + 4 * scale,
            article.Bounds.renderY + disclosure.BoundsPerLine[0].Y + disclosure.ContentY + toggleBounds.Y + toggleBounds.Height / 2);
        window.OnRenderGUI(0);
        readerBody = window.SingleComposer.GetContainer("tutorial-reader-content");
        article = readerBody.Elements.OfType<GuiElementAnnouncement>().Single();
        Verify(article.Bounds.OuterHeight > collapsedHeight && !article.HasNewImages,
            "Expanding tutorial details must reflow the article and settle its dirty state.");
        CheckScrollbar(window.SingleComposer.GetScrollbar("tutorial-reader-scrollbar"), readerBody, null, Verify);

        click(window, window.SingleComposer.GetElement("tutorial-back")); window.OnRenderGUI(0);
        string source = $"https://example.org/tutorial-scroll-{size}.png";
        var imageCache = (Dictionary<string, AnnouncementImages.CachedRequest>)typeof(AnnouncementImages)
            .GetField("Cache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var completion = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        imageCache[source] = new(completion.Task);
        try
        {
            snapshot.Tutorials[0].Html = $"<img src='{source}' width='100%'>" + html;
            window.Update(snapshot); window.OnRenderGUI(0); OpenFirstTutorial();
            double pendingHeight = article.Bounds.OuterHeight;
            readerBar = window.SingleComposer.GetScrollbar("tutorial-reader-scrollbar");
            readerBar.CurrentYPosition = 10; readerBar.TriggerChanged();
            completion.SetResult(imageBytes); window.OnRenderGUI(0);
            readerBody = window.SingleComposer.GetContainer("tutorial-reader-content");
            article = readerBody.Elements.OfType<GuiElementAnnouncement>().Single();
            Verify(article.Components.OfType<AnnouncementImage>().Single().Loaded && !article.HasNewImages
                && Math.Abs(article.Bounds.OuterHeight - pendingHeight) > 1
                && Math.Abs(window.SingleComposer.GetScrollbar("tutorial-reader-scrollbar").CurrentYPosition - 10) < 1,
                "Completed tutorial images must update the content height and preserve the scroll position.");
            CheckScrollbar(window.SingleComposer.GetScrollbar("tutorial-reader-scrollbar"), readerBody, null, Verify);
        }
        finally { imageCache.Remove(source); }
        check(failures.Count == 0, string.Join(Environment.NewLine, failures));

        void OpenFirstTutorial()
        {
            var row = window.SingleComposer.GetContainer("tutorial-content").Elements.OfType<GuiElementTutorialLink>().First();
            clickAt(window, row.Bounds.renderX + 12 * scale, row.Bounds.renderY + 12 * scale); window.OnRenderGUI(0);
            readerBody = window.SingleComposer.GetContainer("tutorial-reader-content");
            article = readerBody.Elements.OfType<GuiElementAnnouncement>().Single();
        }
    }

    private static void CheckScrollbar(GuiElementScrollbar bar, GuiElementContainer body, double? editorHeight, Action<bool, string> verify)
    {
        double viewport = body.Bounds.ParentBounds.InnerHeight;
        double total = Math.Max(viewport, editorHeight ?? body.Bounds.OuterHeight);
        double actual = (float)typeof(GuiElementScrollbar).GetField("totalHeight", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bar)!;
        double handle = (float)typeof(GuiElementScrollbar).GetField("currentHandleHeight", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bar)!;
        verify(Math.Abs(actual - total) < 1 && Math.Abs(handle - Math.Max(10, viewport / total * bar.Bounds.InnerHeight)) < 1,
            $"Tutorial scrollbar must use the composed document height and a proportional thumb: total {actual}/{total}, handle {handle}.");
    }

    private static int PrivateInt(object value, string name) => (int)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;

    private static void WriteOnDark(ImageSurface image, string file)
    {
        using var output = new ImageSurface(Format.Argb32, image.Width, image.Height);
        using var ctx = new Context(output);
        ctx.SetSourceRGB(.12, .1, .08); ctx.Paint(); ctx.SetSourceSurface(image, 0, 0); ctx.Paint();
        output.WriteToPng(file);
    }
}
