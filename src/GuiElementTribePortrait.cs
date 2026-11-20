using Cairo;
using Vintagestory.API.Client;

namespace SeraphCivilizationSimulation;

// Use the game's third-person renderer, including installed player models and clothing.
// Members outside the client's loaded entities retain a framed neutral portrait.
internal sealed class GuiElementTribePortrait(ICoreClientAPI api, string uid, ElementBounds bounds) : GuiElement(api, bounds)
{
    private int background, fallback;
    // Native slot borders extend 2.25 logical pixels into the frame.
    private readonly ElementBounds portraitClip = ElementBounds.Fixed(3, 3,
        Math.Max(0, bounds.fixedWidth - 6), Math.Max(0, bounds.fixedHeight - 6)).WithParent(bounds);
    public override void RenderInteractiveElements(float dt)
    {
        if (background == 0)
        {
            background = GuiElementItemSlotGridBase.DrawSlotBackground(api, Bounds.InnerWidth, Bounds.InnerHeight,
                GuiStyle.DialogSlotBackColor, GuiStyle.DialogSlotFrontColor, null);
            fallback = GuiElementItemSlotGridBase.DrawSlotBackground(api, Bounds.InnerWidth, Bounds.InnerHeight,
                GuiStyle.DialogSlotBackColor, GuiStyle.DialogSlotFrontColor, ctx =>
                {
                    double w = Bounds.InnerWidth, h = Bounds.InnerHeight;
                    ctx.SetSourceRGBA(.62, .53, .39, 1);
                    ctx.Arc(w / 2, h * .35, w * .15, 0, Math.PI * 2); ctx.Fill();
                    ctx.Save(); ctx.Translate(w / 2, h * .79); ctx.Scale(w * .28, h * .24);
                    ctx.Arc(0, 0, 1, Math.PI, Math.PI * 2); ctx.ClosePath(); ctx.Fill(); ctx.Restore();
                });
        }
        var entity = api.World.PlayerByUid(uid)?.Entity;
        bool loaded = entity?.Properties?.Client?.Renderer != null;
        api.Render.Render2DTexturePremultipliedAlpha(loaded ? background : fallback, Bounds.renderX, Bounds.renderY,
            Bounds.InnerWidth, Bounds.InnerHeight);
        if (!loaded) return;
        float size = (float)(Bounds.InnerWidth * 1.25);
        double eye = entity!.Properties.EyeHeight * entity.Properties.Client.Size;
        portraitClip.CalcWorldBounds();
        api.Render.PushScissor(portraitClip, stacking: true);
        try
        {
            api.Render.RenderEntityToGui(dt, entity, Bounds.renderX + Bounds.InnerWidth / 2 - size,
                Bounds.renderY + Bounds.InnerHeight * .46 - (2 - eye) * size, 150, -1.5707963f, size, -1);
        }
        finally { api.Render.PopScissor(); }
    }
    public override void Dispose()
    {
        if (background != 0) api.Gui.DeleteTexture(background);
        if (fallback != 0) api.Gui.DeleteTexture(fallback);
        background = fallback = 0; base.Dispose();
    }
}
