using Cairo;
using Vintagestory.API.Client;

namespace SeraphCivilizationSimulation;

// Match native measurement to the active language's wrapping rules when drawing.
internal sealed class GuiElementWrappedText(ICoreClientAPI api, string value, ElementBounds bounds, CairoFont font)
    : GuiElementStaticText(api, value, EnumTextOrientation.Left, bounds, font)
{
    public override void ComposeTextElements(Context ctx, ImageSurface surface)
    {
        var lines = api.Gui.Text.Lineize(Font, text, Bounds.InnerWidth, EnumLinebreakBehavior.Default);
        api.Gui.Text.DrawMultilineTextAt(ctx, Font, lines, Bounds.drawX, Bounds.drawY, Bounds.InnerWidth);
    }
}
