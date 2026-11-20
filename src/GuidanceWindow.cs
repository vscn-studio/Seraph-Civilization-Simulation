using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace SeraphCivilizationSimulation;

internal sealed class GuidanceWindow : GuiDialog
{
    private readonly Action<GuidanceDecision> send;
    private GuidancePacket packet;
    private bool released;
    private bool submitted;
    private int composedFrameWidth, composedFrameHeight;
    private double composedScale;
    internal string Nonce => packet.Nonce;
    public override string ToggleKeyCombinationCode => null!;
    public override bool CaptureAllInputs() => IsOpened();
    public override bool TryClose() => released && base.TryClose();

    internal GuidanceWindow(ICoreClientAPI api, GuidancePacket packet, Action<GuidanceDecision> send) : base(api)
    { this.packet = packet; this.send = send; Compose(""); }

    internal void Update(GuidancePacket next)
    {
        string gameName = SingleComposer.GetTextInput("game-name").GetText();
        packet = next; submitted = false; Compose(gameName);
    }

    private void Compose(string gameName)
    {
        ClearComposers();
        double scale = Math.Max(.25, RuntimeEnv.GUIScale);
        double width = Math.Clamp(capi.Render.FrameWidth / scale - 40, 280, 560);
        var errorFont = CairoFont.WhiteSmallText().WithColor([1, .5, .4, 1]);
        double errorHeight = packet.Error.Length == 0 ? 0
            : Math.Max(24, capi.Gui.Text.GetMultilineTextHeight(errorFont, packet.Error, (width - 40) * scale) / scale + 4);
        double height = 150 + errorHeight;
        composedFrameWidth = capi.Render.FrameWidth; composedFrameHeight = capi.Render.FrameHeight; composedScale = scale;
        var bounds = ElementBounds.Fixed(0, 0, width, height).WithAlignment(EnumDialogArea.CenterMiddle);
        var composer = capi.Gui.CreateCompo("vscncs-guidance", bounds)
            .AddStaticElement(new GuiElementDialogBackground(capi, ElementBounds.Fill, withTitlebar: false, strokeWidth: 5, alpha: .75f), "name-background")
            .AddStaticText("玩家游戏名", CairoFont.WhiteSmallText(), ElementBounds.Fixed(20, 24, width - 40, 24), "game-name-label")
            .AddTextInput(ElementBounds.Fixed(20, 52, width - 40, 30), _ => { }, key: "game-name");
        if (packet.Error.Length > 0)
            composer.AddStaticText(packet.Error, errorFont, ElementBounds.Fixed(20, 86, width - 40, errorHeight));
        composer.AddSmallButton("确认", () => Choose(true), ElementBounds.Fixed((width - 120) / 2, height - 48, 120, 30), key: "agree");
        SingleComposer = composer.Compose();
        SingleComposer.GetTextInput("game-name").SetValue(gameName);
        SingleComposer.GetButton("agree").Enabled = !submitted;
    }

    private bool Choose(bool agree)
    {
        if (submitted || !agree) return true;
        submitted = true;
        send(new() { Nonce = packet.Nonce, Agree = agree,
            GameName = SingleComposer.GetTextInput("game-name").GetText() });
        SingleComposer.GetButton("agree").Enabled = false;
        return true;
    }

    internal void Release() { released = true; TryClose(); Dispose(); }

    // Preserve the name and pending submission while a native pause menu is open.
    internal void Suspend() { if (IsOpened()) base.TryClose(); }

    public override void OnRenderGUI(float deltaTime)
    {
        if (composedFrameWidth != capi.Render.FrameWidth || composedFrameHeight != capi.Render.FrameHeight
            || composedScale != Math.Max(.25, RuntimeEnv.GUIScale))
        {
            Compose(SingleComposer.GetTextInput("game-name").GetText());
        }
        base.OnRenderGUI(deltaTime);
    }
}
