using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace SeraphCivilizationSimulation;

// Like the tutorial HUD, this stays visible without taking movement or mouse focus.
internal sealed class ServerAnnouncementHud(ICoreClientAPI api) : HudElement(api)
{
    internal const float DisplaySeconds = 10;
    private string? notice;
    private readonly AnnouncementInteraction interaction = new();
    private GuiElementAnnouncement? reader;
    private bool dirty;
    private ElementBounds? clip, body;
    private double scroll;
    private float remaining = DisplaySeconds;
    private int frameWidth, frameHeight;
    private double scale;
    public override bool Focusable => false;
    public override bool ShouldReceiveMouseEvents() => IsOpened() && !capi.Input.MouseGrabbed;
    public override bool OnEscapePressed() => false;
    public override void OnKeyDown(KeyEvent args) { }
    public override void OnKeyPress(KeyEvent args) { }

    internal void Show(string html)
    {
        if (string.IsNullOrWhiteSpace(html) || html.Length > HtmlDocumentApi.MaxSourceLength) return;
        // Keep only the latest announcement and give it a fresh ten-second display.
        notice = html; remaining = DisplaySeconds; scroll = 0;
        interaction.Toggles.Clear(); dirty = true;
        if (!IsOpened()) { Compose(); TryOpen(false); }
    }

    private void Compose()
    {
        ClearComposers();
        scale = Math.Max(.25, RuntimeEnv.GUIScale);
        frameWidth = capi.Render.FrameWidth; frameHeight = capi.Render.FrameHeight;
        double width = Math.Clamp(frameWidth / scale - 24, 160, 440);
        double top = Math.Min(72, frameHeight / scale * .08) + 32;
        double maxHeight = Math.Max(36, Math.Min(300, frameHeight / scale - top - 100));
        interaction.Dirty = false;
        double padding = GuiStyle.ElementToDialogPadding / 2.0;
        reader = HtmlDocumentApi.CreateReader(capi, notice!, ElementBounds.Fixed(0, 0, width - 2 * padding, 20), interaction);
        double documentHeight = AnnouncementTextLayout.Measure(reader.Components, (width - 2 * padding) * scale) / scale;
        double height = Math.Clamp(documentHeight + 2, 36, maxHeight);
        var bounds = ElementBounds.Fixed(0, top, width, height + 2 * padding).WithAlignment(EnumDialogArea.LeftTop);
        clip = ElementBounds.Fixed(padding, padding, width - 2 * padding, height);
        body = ElementBounds.Fixed(0, 0, clip.fixedWidth, height);
        var composer = capi.Gui.CreateCompo("vscn-server-announcement", bounds)
            // Use the very same overlay and color as the game's tutorial HUD.
            .AddGameOverlay(ElementBounds.Fill, GuiStyle.DialogLightBgColor);
        var container = new GuiElementAnnouncementContainer(capi, body);
        container.Add(reader); container.Tabbable = false;
        composer.BeginClip(clip).AddInteractiveElement(container, "content").EndClip();
        interaction.Navigate = href =>
        {
            if (!href.StartsWith('#') || reader.Anchor(Uri.UnescapeDataString(href[1..])) is not { } y) return;
            SetScroll(y / scale);
        };
        SingleComposer = composer.Compose();
        SetScroll(scroll); dirty = false;
    }

    private void SetScroll(double offset)
    {
        if (body == null || clip == null) return;
        scroll = Math.Clamp(offset, 0, Math.Max(0, (body.OuterHeight - clip.InnerHeight) / scale));
        body.fixedY = -scroll; body.CalcWorldBounds();
    }

    public override void OnMouseWheel(MouseWheelEventArgs args)
    {
        if (args.IsHandled || !ShouldReceiveMouseEvents() || clip == null
            || !clip.PointInside(capi.Input.MouseX, capi.Input.MouseY)) return;
        SetScroll(scroll - (args.deltaPrecise != 0 ? args.deltaPrecise : args.delta) * 40);
        args.SetHandled();
    }

    public override void OnRenderGUI(float dt)
    {
        if (notice == null) return;
        remaining -= Math.Max(0, dt);
        if (remaining <= 0)
        {
            notice = null; interaction.Toggles.Clear(); scroll = 0;
            TryClose(); ClearComposers(); reader = null; clip = body = null; return;
        }
        if (dirty || reader?.HasNewImages == true || frameWidth != capi.Render.FrameWidth
            || frameHeight != capi.Render.FrameHeight || scale != RuntimeEnv.GUIScale) Compose();
        base.OnRenderGUI(dt);
    }
}
