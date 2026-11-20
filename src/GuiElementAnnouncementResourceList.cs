using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace SeraphCivilizationSimulation;

// A handbook-style flat list.  Rows are painted directly instead of using
// button widgets, which keeps the picker visually close to the game's native
// handbook overview and leaves the whole row as the hit target.
internal sealed class GuiElementAnnouncementResourceList : GuiElement
{
    private readonly Action<AnnouncementGameResources.Resource> choose;
    private readonly CairoFont font = AnnouncementFonts.Readable(CairoFont.WhiteSmallText(), "资源");
    private List<AnnouncementGameResources.Resource> items = [];
    private double scroll;
    private int hover = -1;
    private int texture;
    private bool textureDirty = true;
    private GuiElementAutoScrollbar? scrollbar;
    internal GuiElementAutoScrollbar? Scrollbar
    {
        get => scrollbar;
        set { scrollbar = value; RefreshScroll(); }
    }
    private double RowHeight => 32;

    internal GuiElementAnnouncementResourceList(ICoreClientAPI api, ElementBounds bounds,
        Action<AnnouncementGameResources.Resource> choose) : base(api, bounds) => this.choose = choose;

    internal void SetItems(IEnumerable<AnnouncementGameResources.Resource> value, bool preserveScroll = false)
    {
        items = value.ToList();
        if (!preserveScroll) scroll = 0;
        textureDirty = true;
        RefreshScroll();
    }

    internal void RefreshScroll()
    {
        if (scrollbar == null) return;
        Bounds.CalcWorldBounds();
        double document = items.Count * RowHeight;
        scrollbar.SetContentHeights((float)Bounds.InnerHeight, (float)GuiElement.scaled(Math.Max(RowHeight, document)));
        scroll = Math.Clamp(scroll, 0, Math.Max(0, document - Bounds.fixedHeight));
        scrollbar.CurrentYPosition = (float)GuiElement.scaled(scroll);
        scrollbar.TriggerChanged();
        textureDirty = true;
    }

    internal void ScrollTo(float value)
    {
        scroll = value / Math.Max(.25, RuntimeEnv.GUIScale);
        scroll = Math.Clamp(scroll, 0, Math.Max(0, items.Count * RowHeight - Bounds.fixedHeight));
        textureDirty = true;
        hover = -1;
    }

    private void EnsureTexture()
    {
        Bounds.CalcWorldBounds();
        if (!textureDirty && texture != 0) return;
        using var surface = new ImageSurface(Format.Argb32,
            Math.Max(1, (int)Math.Ceiling(Bounds.InnerWidth)), Math.Max(1, (int)Math.Ceiling(Bounds.InnerHeight)));
        using var ctx = new Context(surface);
        ctx.SetSourceRGBA(0, 0, 0, 0); ctx.Paint();
        font.SetupContext(ctx);
        int first = Math.Max(0, (int)(scroll / RowHeight) - 1);
        int last = Math.Min(items.Count, (int)Math.Ceiling((scroll + Bounds.fixedHeight) / RowHeight) + 1);
        for (int i = first; i < last; i++)
        {
            // Keep row edges on device pixels. Fractional Cairo rectangles
            // leave a one-pixel seam beside the scrollbar at non-100% scales.
            double y = Math.Floor(GuiElement.scaled(i * RowHeight - scroll));
            double bottom = Math.Ceiling(GuiElement.scaled((i + 1) * RowHeight - scroll));
            double h = Math.Max(1, bottom - y);
            if (i == hover)
            {
                ctx.SetSourceRGBA(.76, .63, .35, .25); ctx.Rectangle(0, y, surface.Width, h); ctx.Fill();
            }
            string label = AnnouncementFonts.SingleLine(font, items[i].Name, surface.Width - GuiElement.scaled(24));
            double textHeight = font.GetFontExtents().Height;
            double textY = y + Math.Floor(Math.Max(0, h - textHeight) / 2);
            var line = new TextLine
            {
                Text = label,
                // DrawMultilineTextAt already translates to textY. Line bounds
                // must stay local or successive labels advance twice as far.
                Bounds = new LineRectangled(0, 0,
                    surface.Width - GuiElement.scaled(24), textHeight)
            };
            api.Gui.Text.DrawMultilineTextAt(ctx, font, [line], Math.Floor(GuiElement.scaled(12)), textY,
                surface.Width - GuiElement.scaled(24));
        }
        generateTexture(surface, ref texture);
        textureDirty = false;
    }

    public override void ComposeElements(Context ctx, ImageSurface surface) => EnsureTexture();

    public override void RenderInteractiveElements(float dt)
    {
        EnsureTexture();
        if (texture != 0) api.Render.Render2DTexturePremultipliedAlpha(texture, Bounds.renderX, Bounds.renderY,
            Bounds.OuterWidth, Bounds.OuterHeight);
    }

    private int At(double x, double y)
    {
        if (!IsPositionInside((int)x, (int)y)) return -1;
        double scale = Math.Max(.25, RuntimeEnv.GUIScale);
        int row = (int)(((y - Bounds.renderY) / scale + scroll) / RowHeight);
        return row >= 0 && row < items.Count ? row : -1;
    }

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        int next = At(args.X, args.Y);
        if (next != hover) { hover = next; textureDirty = true; }
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        if (args.Button != EnumMouseButton.Left) return;
        int index = At(args.X, args.Y);
        if (index >= 0) { args.Handled = true; choose(items[index]); }
    }

    public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
    {
        if (!IsPositionInside(api.Input.MouseX, api.Input.MouseY) || scrollbar == null) return;
        scrollbar.CurrentYPosition = (float)Math.Clamp(scrollbar.CurrentYPosition - args.deltaPrecise * GuiElement.scaled(32), 0,
            Math.Max(0, GuiElement.scaled(items.Count * RowHeight) - Bounds.InnerHeight));
        scrollbar.TriggerChanged(); args.SetHandled();
    }

    public override void Dispose()
    {
        if (texture != 0) api.Gui.DeleteTexture(texture);
        texture = 0; base.Dispose();
    }
}

// Compatibility hit target for older layout callers that addressed the first
// resource row by name.  It forwards input without painting a second control.
internal sealed class GuiElementAnnouncementResourceAlias(ICoreClientAPI api, GuiElement target, ElementBounds bounds)
    : GuiElement(api, bounds)
{
    private Action? click;
    internal GuiElementAnnouncementResourceAlias(ICoreClientAPI api, ElementBounds bounds, Action click)
        : this(api, null!, bounds) => this.click = click;
    public override void RenderInteractiveElements(float dt) { }
    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    { if (click != null) { click(); args.Handled = true; } else target.OnMouseDownOnElement(api, args); }
    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    { if (target != null) target.OnMouseMove(api, args); }
}
