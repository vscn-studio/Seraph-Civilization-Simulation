using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace SeraphCivilizationSimulation;

// Draw toolbar symbols as paths so they do not depend on installed symbol fonts.
internal sealed class GuiElementAnnouncementTool(ICoreClientAPI api, string tool, Action click, ElementBounds bounds)
    : GuiElementTextButton(api, "", CairoFont.SmallButtonText(EnumButtonStyle.Small),
        CairoFont.SmallButtonText(EnumButtonStyle.Small).WithColor(GuiStyle.ActiveButtonTextColor),
        () => { click(); return true; }, bounds, EnumButtonStyle.Small)
{
    private int texture;
    // Empty button text must not resize the square icon bounds to the font's line height.
    public override void BeforeCalcBounds() { }
    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        base.ComposeElements(ctx, surface);
        using var image = new ImageSurface(Format.Argb32, Math.Max(1, (int)Math.Ceiling(Bounds.OuterWidth)), Math.Max(1, (int)Math.Ceiling(Bounds.OuterHeight)));
        using var draw = new Context(image);
        double iconSize = Math.Min(scaled(20), Math.Min(image.Width, image.Height) - scaled(3));
        draw.Translate((image.Width - iconSize) / 2, (image.Height - iconSize) / 2);
        draw.Scale(iconSize / 20, iconSize / 20); draw.LineWidth = 1.7;
        draw.LineCap = LineCap.Round; draw.LineJoin = LineJoin.Round;
        draw.SetSourceRGBA(.95, .9, .78, 1);
        DrawIcon(draw, tool); generateTexture(image, ref texture);
    }
    internal static void DrawIcon(Context c, string tool)
    {
        void Line(double x, double y, double x2, double y2) { c.MoveTo(x, y); c.LineTo(x2, y2); c.Stroke(); }
        switch (tool)
        {
            case "format":
                Line(2, 16, 8, 3); Line(8, 3, 14, 16); Line(5, 11, 11, 11); Line(13, 18, 18, 18); break;
            case "insert":
                Line(3, 9, 15, 9); Line(9, 3, 9, 15); break;
            case "embed":
                c.Rectangle(2, 3, 12, 13); c.Stroke(); c.Rectangle(7, 7, 11, 11); c.Stroke(); break;
            case "bold":
                c.LineWidth = 2.5; c.MoveTo(5, 3); c.LineTo(5, 17); c.LineTo(11, 17);
                c.CurveTo(18, 17, 18, 10, 11, 10); c.LineTo(5, 10); c.LineTo(11, 10);
                c.CurveTo(17, 10, 17, 3, 11, 3); c.ClosePath(); c.Stroke(); break;
            case "italic": Line(8, 3, 16, 3); Line(4, 17, 12, 17); Line(12, 3, 8, 17); break;
            case "link":
                c.Save(); c.Translate(10, 10); c.Rotate(-Math.PI / 4);
                GuiElement.RoundRectangle(c, -8, -3, 10, 6, 3); c.Stroke();
                GuiElement.RoundRectangle(c, -2, -3, 10, 6, 3); c.Stroke(); c.Restore(); break;
            case "image":
                c.Rectangle(2, 3, 16, 14); c.Stroke(); c.Arc(6, 7, 1.4, 0, Math.PI * 2); c.Fill();
                c.MoveTo(3, 15); c.LineTo(8, 10); c.LineTo(11, 13); c.LineTo(14, 9); c.LineTo(18, 14); c.Stroke(); break;
            case "list": case "numbered":
                for (int row = 0; row < 3; row++)
                {
                    double y = 4 + row * 6;
                    if (tool == "list") { c.Arc(3, y, 1.2, 0, Math.PI * 2); c.Fill(); }
                    else if (row == 0) { Line(2, y - 2, 3, y - 2); Line(3, y - 2, 3, y + 2); }
                    else if (row == 1) { Line(2, y - 2, 4, y - 2); Line(4, y - 2, 2, y + 2); Line(2, y + 2, 4, y + 2); }
                    else { Line(2, y - 2, 4, y - 2); Line(4, y - 2, 4, y + 2); Line(2, y, 4, y); Line(2, y + 2, 4, y + 2); }
                    Line(7, y, 18, y);
                }
                break;
            case "table":
                c.Rectangle(2, 2, 16, 16); c.Stroke(); c.Rectangle(2, 2, 16, 5); c.Fill();
                Line(2, 12, 18, 12); Line(7, 7, 7, 18); Line(12, 7, 12, 18); break;
        }
    }
    public override void RenderInteractiveElements(float dt)
    {
        base.RenderInteractiveElements(dt);
        if (!Visible) return;
        api.Render.Render2DTexturePremultipliedAlpha(texture, Bounds.renderX, Bounds.renderY, Bounds.OuterWidth, Bounds.OuterHeight);
    }
    public override void OnKeyDown(ICoreClientAPI api, KeyEvent e)
    { if (Visible && Enabled && HasFocus && e.KeyCode is (int)GlKeys.Enter or (int)GlKeys.Space) { click(); e.Handled = true; } }
    public override void Dispose() { if (texture != 0) api.Gui.DeleteTexture(texture); texture = 0; base.Dispose(); }
}
