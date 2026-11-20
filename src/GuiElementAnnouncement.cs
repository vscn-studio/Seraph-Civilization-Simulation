using Cairo;
using System.Runtime.InteropServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace SeraphCivilizationSimulation;

// Native handbook layout and links, rendered in a viewport-sized texture for long announcements.
public sealed class GuiElementAnnouncement(ICoreClientAPI api, string markup, ElementBounds bounds, AnnouncementInteraction? interaction = null)
    : GuiElementRichtext(api, AnnouncementTextLayout.Components(api, markup, interaction), bounds)
{
    private LoadedTexture visibleTexture = new(api);
    private int[] pixels = [];
    private double previousOffset = double.NaN;
    public bool HasNewImages => interaction?.Dirty == true || Components.OfType<AnnouncementBlock>().Any(c => c.HasNewImages);
    public double? Anchor(string id) => AnnouncementBlock.FindAnchor(Components, id);
    public override void BeforeCalcBounds()
    {
        // Use the full logical document height rather than the native 99999px limit.
        Bounds.CalcWorldBounds();
        flowPath = [new TextFlowPath(0, 0, Bounds.InnerWidth, double.MaxValue)];
        Bounds.fixedHeight = AnnouncementTextLayout.Measure(Components, Bounds.InnerWidth) / Math.Max(.25, RuntimeEnv.GUIScale);
    }
    public override void ComposeElements(Context ctx, ImageSurface surface) => Bounds.CalcWorldBounds();
    public override void RenderInteractiveElements(float dt)
    {
        var clip = InsideClipBounds ?? Bounds.ParentBounds;
        double offset = Math.Max(0, clip.renderY - Bounds.renderY);
        int height = Math.Max(1, (int)Math.Ceiling(clip.InnerHeight));
        int width = Math.Max(1, (int)Math.Ceiling(Bounds.InnerWidth));
        if (offset != previousOffset || visibleTexture.Width != width || visibleTexture.Height != height)
        {
            previousOffset = offset;
            using var surface = new ImageSurface(Format.Argb32, width, height);
            using var ctx = new Context(surface);
            ctx.Rectangle(0, 0, Bounds.InnerWidth, height); ctx.Clip();
            ctx.Translate(0, -offset);
            foreach (var component in Components) component.ComposeElements(ctx, surface);
            // Cairo uploads default to GL_REPEAT in the game. At fractional
            // positions linear filtering then samples left-edge slots on the
            // right edge. Explicitly clamp this viewport texture on both axes.
            if (visibleTexture.Width != width || visibleTexture.Height != height)
            {
                visibleTexture.Dispose();
                visibleTexture = new LoadedTexture(api, 0, width, height);
            }
            if (pixels.Length != width * height) pixels = new int[width * height];
            surface.Flush(); Marshal.Copy(surface.DataPtr, pixels, 0, pixels.Length);
            api.Render.LoadOrUpdateTextureFromBgra(pixels, true, 1, ref visibleTexture);
        }
        // Keep document pixels and native item models at the same scale.
        api.Render.Render2DTexturePremultipliedAlpha(visibleTexture.TextureId, Bounds.renderX, Bounds.renderY + offset, width, height);
        MouseOverCursor = null;
        foreach (var component in Components)
        {
            component.RenderInteractiveElements(dt, Bounds.renderX, Bounds.renderY, 50);
            if (component.UseMouseOverCursor(Bounds)) MouseOverCursor = component.MouseOverCursor;
        }
    }
    public override void Dispose()
    {
        visibleTexture.Dispose(); pixels = []; base.Dispose();
    }
}
