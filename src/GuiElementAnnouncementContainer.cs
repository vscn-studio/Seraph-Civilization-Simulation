using Cairo;
using Vintagestory.API.Client;

namespace SeraphCivilizationSimulation;

// Retain native focus, links and input dispatch without allocating a full-document texture.
internal sealed class GuiElementAnnouncementContainer(ICoreClientAPI api, ElementBounds bounds, bool cull = false) : GuiElementContainer(api, bounds)
{
    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        CalcTotalHeight(); Bounds.CalcWorldBounds();
        var clip = InsideClipBounds ?? Bounds.ParentBounds;
        using var visible = new ImageSurface(Format.Argb32, Math.Max(1, (int)Bounds.OuterWidth), Math.Max(1, (int)clip.InnerHeight));
        using var draw = new Context(visible);
        foreach (var element in Elements) element.ComposeElements(draw, visible);
    }

    public override void RenderInteractiveElements(float dt)
    {
        MouseOverCursor = null;
        foreach (var element in Elements)
        {
            var clip = InsideClipBounds ?? Bounds.ParentBounds;
            if (cull && (element.Bounds.renderY + element.Bounds.OuterHeight < clip.renderY
                || element.Bounds.renderY > clip.renderY + clip.InnerHeight)) continue;
            element.RenderInteractiveElements(dt);
            if (element.IsPositionInside(api.Input.MouseX, api.Input.MouseY)) MouseOverCursor = element.MouseOverCursor;
        }
    }
}
