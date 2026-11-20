using Vintagestory.API.Client;

namespace SeraphCivilizationSimulation;

/// <summary>Reusable native HTML editing/layout, independent of civilization state and networking.
/// Call on the client thread. Callers own returned controls, dialogs and world texture leases.</summary>
public static class HtmlDocumentApi
{
    public static int ApiVersion => 1;
    public const int MaxSourceLength = 32768;

    public static GuiElementAnnouncementEditor CreateEditor(ICoreClientAPI api, ElementBounds bounds, Action<string> changed, CairoFont font)
        => new(api, bounds, changed, font);

    public static GuiElementAnnouncement CreateReader(ICoreClientAPI api, string html, ElementBounds bounds, AnnouncementInteraction? interaction = null)
        => new(api, AnnouncementDom.Markup(html), bounds, interaction);

    public static void InsertHtml(GuiElementAnnouncementEditor editor, int start, int end, string html)
        => editor.ApplyEdit(AnnouncementHtmlTools.Insert(editor.GetText(), start, end, html));

    /// <summary>save(title, html, sign) runs once on Save/Sign; cancel runs once on other closes.
    /// Null save creates a reader. Transcription and persistence remain the caller's responsibility.</summary>
    public static GuiDialog CreateDialog(ICoreClientAPI api, string title, string html,
        Action<string, string, bool>? save, Action? cancel, Action? transcribe)
        => new HtmlDocumentDialog(api, title, html, save, cancel, transcribe);

    /// <summary>The lease owns its texture. 'updated' supplies a borrowed texture and runs on the client thread.
    /// Reflows after asynchronous images arrive. Dispose when the world object disappears.</summary>
    public static IDisposable CreateWorldTexture(ICoreClientAPI api, string html, int width, int height, Action<LoadedTexture> updated)
        => new HtmlDocumentWorldTexture(api, html, width, height, updated);

    public static string PlainText(string html)
    {
        var body = AnnouncementDom.Parse(html);
        foreach (var hidden in body.QuerySelectorAll("script,style,template")) hidden.Remove();
        foreach (var br in body.QuerySelectorAll("br")) br.Replace(body.Owner!.CreateTextNode("\n"));
        foreach (var block in body.QuerySelectorAll("p,div,h1,h2,h3,h4,h5,h6,li,tr,summary"))
            block.AppendChild(body.Owner!.CreateTextNode("\n"));
        return body.TextContent.Trim();
    }
}
