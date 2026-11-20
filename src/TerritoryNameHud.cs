using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace SeraphCivilizationSimulation;

internal sealed class TerritoryNameHud : HudElement
{
    private long expires;
    public override bool Focusable => false;
    public TerritoryNameHud(ICoreClientAPI api) : base(api)
    {
        var font = CairoFont.WhiteMediumText().WithFont(GuiStyle.DecorativeFontName).WithColor(GuiStyle.DiscoveryTextColor)
            .WithStroke(GuiStyle.DialogBorderColor, 2).WithOrientation(EnumTextOrientation.Center);
        SingleComposer = api.Gui.CreateCompo("vsscs-territory-name", ElementBounds.Fixed(0, 100, 700, 50).WithAlignment(EnumDialogArea.CenterTop))
            .AddDynamicText("", font, ElementBounds.Fixed(0, 0, 700, 50), "name").Compose();
    }
    public void Show(string name) { SingleComposer.GetDynamicText("name").SetNewText(name); expires = capi.World.ElapsedMilliseconds + 4000; TryOpen(); }
    public override void OnRenderGUI(float dt) { if (capi.World.ElapsedMilliseconds >= expires) { TryClose(); return; } base.OnRenderGUI(dt); }
    public override bool ShouldReceiveMouseEvents() => false;
    public override bool ShouldReceiveKeyboardEvents() => false;
}
