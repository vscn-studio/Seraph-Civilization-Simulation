using Cairo;
using Vintagestory.API.Client;

namespace SeraphCivilizationSimulation;

// Native handle and interaction; show the track only when there is content to scroll.
internal sealed class GuiElementAutoScrollbar(ICoreClientAPI api, Action<float> changed, ElementBounds bounds)
    : GuiElementScrollbar(api, changed, bounds)
{
    private int trackTexture;
    public override bool Focusable => totalHeight > visibleHeight + .5f && base.Focusable;
    public void SetContentHeights(float viewportHeight, float documentHeight)
    {
        Bounds.CalcWorldBounds();
        float position = CurrentYPosition;
        if (!float.IsFinite(position)) position = 0;
        visibleHeight = Math.Max(1, viewportHeight);
        totalHeight = Math.Max(visibleHeight, documentHeight);
        // At fractional GUI scales the native float handle can be slightly shorter
        // than the double track. Equal content heights then produce a zero scroll
        // conversion factor and 0 / 0 when restoring the position.
        currentHandleHeight = totalHeight <= visibleHeight + .5f
            ? (float)Math.Ceiling(Bounds.InnerHeight)
            : Math.Max(10, (float)(visibleHeight / totalHeight * Bounds.InnerHeight));
        CurrentYPosition = Math.Clamp(position, 0, totalHeight - visibleHeight);
        TriggerChanged();
        RecomposeHandle();
    }
    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        using var track = new ImageSurface(Format.Argb32, Math.Max(1, (int)Bounds.OuterWidth), Math.Max(1, (int)Bounds.OuterHeight));
        using var draw = new Context(track);
        draw.Translate(-Bounds.drawX, -Bounds.drawY);
        base.ComposeElements(draw, track);
        generateTexture(track, ref trackTexture);
    }
    public override void RenderInteractiveElements(float dt)
    {
        if (totalHeight <= visibleHeight + .5f) return;
        api.Render.Render2DTexturePremultipliedAlpha(trackTexture, Bounds);
        base.RenderInteractiveElements(dt);
    }
    public override void Dispose()
    {
        if (trackTexture != 0) api.Gui.DeleteTexture(trackTexture);
        trackTexture = 0; base.Dispose();
    }
}
