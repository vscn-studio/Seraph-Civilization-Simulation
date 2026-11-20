using Vintagestory.API.Client;

namespace SeraphCivilizationSimulation;

// Native text buttons normally replace the requested height with the font's line height.
internal sealed class GuiElementFixedButton(ICoreClientAPI api, string text, Action click, ElementBounds bounds)
    : GuiElementTextButton(api, text, CairoFont.SmallButtonText(EnumButtonStyle.Small),
        CairoFont.SmallButtonText(EnumButtonStyle.Small).WithColor(GuiStyle.ActiveButtonTextColor),
        () => { click(); return true; }, bounds, EnumButtonStyle.Small)
{
    public override void BeforeCalcBounds() { }
}
