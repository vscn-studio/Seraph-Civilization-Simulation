using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace SeraphCivilizationSimulation;

// A handbook-style text row: the title is clickable without drawing a button
// frame, leaving the native action buttons at the far right of the row.
internal sealed class GuiElementTutorialLink : GuiElement
{
    private readonly string label;
    private readonly string? status;
    private readonly bool statusPublished;
    private readonly Action activate;
    private readonly ElementBounds[] exclusions;
    private readonly double textWidth;
    private readonly CairoFont font = AnnouncementFonts.Readable(CairoFont.WhiteSmallText(), "教程");
    private int texture;
    private bool textureDirty = true, hovered;

    internal GuiElementTutorialLink(ICoreClientAPI api, string label, ElementBounds bounds,
        IEnumerable<ElementBounds> exclusions, Action activate, string? status = null, bool statusPublished = false, double? textWidth = null)
        : base(api, bounds)
    {
        this.label = label;
        this.status = status;
        this.statusPublished = statusPublished;
        this.exclusions = exclusions.ToArray();
        this.activate = activate;
        this.textWidth = textWidth ?? bounds.fixedWidth;
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        textureDirty = true;
        EnsureTexture();
    }

    private void EnsureTexture()
    {
        Bounds.CalcWorldBounds();
        if (!textureDirty && texture != 0) return;
        using var image = new ImageSurface(Format.Argb32,
            Math.Max(1, (int)Math.Ceiling(Bounds.InnerWidth)),
            Math.Max(1, (int)Math.Ceiling(Bounds.InnerHeight)));
        using var draw = new Context(image);
        draw.SetSourceRGBA(0, 0, 0, 0);
        draw.Paint();
        if (hovered)
        {
            draw.SetSourceRGBA(.76, .63, .35, .25);
            draw.Rectangle(0, 0, image.Width, image.Height);
            draw.Fill();
        }
        // Keep title glyphs out of the action controls; the hover/click row
        // still spans the full list width.
        double labelWidth = GuiElement.scaled(textWidth);
        draw.Rectangle(0, 0, labelWidth, image.Height); draw.Clip();
        double titleX = GuiElement.scaled(8);
        // A tutorial row has two distinct lines: the title/status line and the
        // author line supplied by the page. Centering the title in the whole
        // 48px row made its glyphs descend into the author line.
        double titleY = GuiElement.scaled(3);
        if (!string.IsNullOrEmpty(status))
        {
            var statusFont = font.Clone().WithColor(statusPublished
                ? new[] { .35, .95, .4, 1.0 }
                : new[] { 1.0, .3, .3, 1.0 });
            double statusWidth = statusFont.GetTextExtents(status).Width;
            var statusLine = new TextLine
            {
                Text = status,
                Bounds = new LineRectangled(0, 0, statusWidth, image.Height)
            };
            api.Gui.Text.DrawMultilineTextAt(draw, statusFont, [statusLine], titleX,
                titleY, image.Width);
            titleX += statusWidth + GuiElement.scaled(6);
        }
        var line = new TextLine
        {
            Text = AnnouncementFonts.SingleLine(font, label, Math.Max(1, labelWidth - titleX - GuiElement.scaled(8))),
            Bounds = new LineRectangled(0, 0, Math.Max(1, labelWidth - titleX), image.Height)
        };
        api.Gui.Text.DrawMultilineTextAt(draw, font, [line], titleX,
            titleY, labelWidth);
        generateTexture(image, ref texture);
        textureDirty = false;
    }

    public override void RenderInteractiveElements(float dt)
    {
        EnsureTexture();
        if (texture != 0)
            api.Render.Render2DTexturePremultipliedAlpha(texture, Bounds.renderX, Bounds.renderY,
                Bounds.OuterWidth, Bounds.OuterHeight);
        MouseOverCursor = "linkselect";
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        if (args.Button != EnumMouseButton.Left || !IsPositionInside(args.X, args.Y)
            || IsExcluded(args.X, args.Y)) return;
        activate();
        args.Handled = true;
    }

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        bool next = IsPositionInside(args.X, args.Y)
            && !IsExcluded(args.X, args.Y);
        if (next != hovered) { hovered = next; textureDirty = true; }
    }

    private bool IsExcluded(double x, double y)
    {
        // Exclusions are relative to this row, in logical GUI units.
        // MouseEvent coordinates are absolute screen pixels.
        double scale = Math.Max(.25, RuntimeEnv.GUIScale);
        double localX = (x - Bounds.renderX) / scale;
        double localY = (y - Bounds.renderY) / scale;
        return exclusions.Any(bounds => localX >= bounds.fixedX && localX <= bounds.fixedX + bounds.fixedWidth
            && localY >= bounds.fixedY && localY <= bounds.fixedY + bounds.fixedHeight);
    }

    public override void Dispose()
    {
        if (texture != 0) api.Gui.DeleteTexture(texture);
        texture = 0;
        base.Dispose();
    }
}
