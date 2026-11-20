using Cairo;
using Vintagestory.API.Client;

namespace SeraphCivilizationSimulation;

// Keep native dropdown keyboard/mouse handling and popup sizing, with a compact face.
internal sealed class GuiElementAnnouncementMenu(ICoreClientAPI api, string symbol, bool iconOnly,
    string[] values, string[] labels, SelectionChangedDelegate changed, ElementBounds bounds, CairoFont font)
    : GuiElementDropDown(api, values, labels, 0, changed, bounds, font, false)
{
    private int icon;
    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        base.ComposeElements(ctx, surface);
        if (!iconOnly) return;
        using var image = new ImageSurface(Format.Argb32, Math.Max(1, (int)Math.Ceiling(Bounds.OuterWidth)), Math.Max(1, (int)Math.Ceiling(Bounds.OuterHeight)));
        using var draw = new Context(image);
        draw.SetSourceRGBA(GuiStyle.ButtonBackColor); draw.Paint();
        draw.SetSourceRGBA(.7,.62,.46,1); draw.LineWidth = scaled(1);
        draw.Rectangle(.5,.5,image.Width-1,image.Height-1); draw.Stroke();
        draw.Save();
        draw.Translate(Math.Max(scaled(2),(image.Width-scaled(24))/2), (image.Height-scaled(18))/2); draw.Scale(scaled(.9),scaled(.9));
        draw.SetSourceRGBA(.95,.9,.78,1); draw.LineWidth=1.7;
        GuiElementAnnouncementTool.DrawIcon(draw,symbol);
        draw.Restore();
        double arrowX=image.Width-scaled(5), arrowY=image.Height/2d;
        draw.MoveTo(arrowX-scaled(2),arrowY-scaled(1)); draw.LineTo(arrowX+scaled(2),arrowY-scaled(1));
        draw.LineTo(arrowX,arrowY+scaled(2)); draw.ClosePath(); draw.Fill();
        generateTexture(image,ref icon);
    }
    public override void RenderInteractiveElements(float dt)
    {
        if (!iconOnly) { base.RenderInteractiveElements(dt); return; }
        api.Render.Render2DTexturePremultipliedAlpha(icon,Bounds.renderX,Bounds.renderY,Bounds.OuterWidth,Bounds.OuterHeight);
        listMenu.RenderInteractiveElements(dt);
    }
    public override void Dispose()
    { if(icon!=0) api.Gui.DeleteTexture(icon); icon=0; base.Dispose(); }
}
