using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Cairo;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace SeraphCivilizationSimulation;

public sealed class TribeRosterConfig
{
    public string HeaderSvg { get; set; } = DefaultHeader;
    public const string DefaultHeader = """
        <svg viewBox="0 0 700 120" xmlns="http://www.w3.org/2000/svg" preserveAspectRatio="xMidYMid meet">
          <rect x="32" y="24" width="36" height="36" rx="4" fill="#6b8e23"/>
          <rect x="104" y="24" width="36" height="36" rx="4" fill="#8b5a2b"/>
          <rect x="68" y="60" width="36" height="36" rx="4" fill="#555"/>
          <text x="164" y="78" font-size="50" font-family="Inter, Arial, Helvetica, sans-serif" font-weight="600" textLength="504" lengthAdjust="spacingAndGlyphs" fill="#fff">复古物语中文社区</text>
        </svg>
        """;
    internal const string LegacyDefaultHeader = """
        <svg viewBox="0 0 700 120" xmlns="http://www.w3.org/2000/svg">
          <!-- ===== V 图标 ===== -->
          <rect x="40" y="24" width="36" height="36" rx="4" fill="#6b8e23"/>
          <rect x="112" y="24" width="36" height="36" rx="4" fill="#8b5a2b"/>
          <rect x="76" y="60" width="36" height="36" rx="4" fill="#555"/>
          <!-- ===== 文字（70%高度） ===== -->
          <text x="170" y="78" font-size="50" font-family="Inter, Arial, Helvetica, sans-serif" font-weight="600" letter-spacing="0.5" fill="#fff">复古物语中文社区</text>
        </svg>
        """;
    public void Validate()
    {
        if (HeaderSvg == null || HeaderSvg.Length > 65536) throw new ArgumentException("Invalid roster SVG size.");
        if (HeaderSvg.Length > 0)
        {
            var doc = RosterSvg.Parse(HeaderSvg);
            var legacy = RosterSvg.Parse(LegacyDefaultHeader);
            doc.DescendantNodes().OfType<XComment>().Remove();
            legacy.DescendantNodes().OfType<XComment>().Remove();
            if (XNode.DeepEquals(doc, legacy)) HeaderSvg = DefaultHeader;
        }
    }
}

[ProtoContract]
public sealed class TribeRosterPacket
{
    [ProtoMember(1)] public string TribeId { get; set; } = "";
    [ProtoMember(2)] public string HeaderSvg { get; set; } = "";
    [ProtoMember(3)] public List<TribeRosterMember> Members { get; set; } = [];
}
[ProtoContract]
public sealed class TribeRosterMember
{
    [ProtoMember(1)] public string Uid { get; set; } = "";
    [ProtoMember(2)] public string Name { get; set; } = "";
    [ProtoMember(3)] public int PingMs { get; set; }
}

public sealed partial class VscnCivilizationModSystem
{
    private readonly Dictionary<string, long> rosterRequests = [];
    private TribeRosterPacket BuildTribeRoster(IServerPlayer player)
    {
        var tribe = tribeState.FindMember(player.PlayerUID);
        return new() { TribeId = tribe?.Id ?? "", HeaderSvg = config.Roster.HeaderSvg,
            Members = Online.Where(p => tribe != null && tribe.Members.Contains(p.PlayerUID) && p.ConnectionState == EnumClientState.Playing)
                .Prepend(player).DistinctBy(p => p.PlayerUID).OrderBy(p => p.PlayerUID != player.PlayerUID)
                .ThenBy(p => p.PlayerUID != tribe?.LeaderUid).ThenBy(p => PlayerName(p.PlayerUID), StringComparer.Ordinal)
                .Select(p => new TribeRosterMember { Uid = p.PlayerUID, Name = MemberDisplayName(p.PlayerUID),
                    PingMs = double.IsFinite(p.Ping) ? (int)Math.Clamp(Math.Round(p.Ping * 1000), 0, 999999) : 0 }).ToList() };
    }
    private void SendTribeRoster(IServerPlayer player)
    {
        if (rosterRequests.TryGetValue(player.PlayerUID, out long last) && Environment.TickCount64 - last < 500) return;
        rosterRequests[player.PlayerUID] = Environment.TickCount64;
        channel!.SendPacket(BuildTribeRoster(player), player);
    }
}

// NanoSVG renders paths/shapes; Cairo supplies SVG text, which NanoSVG omits.
internal static class RosterSvg
{
    internal static XDocument Parse(string source)
    {
        using var reader = XmlReader.Create(new StringReader(source), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 });
        var document = XDocument.Load(reader);
        if (document.Root?.Name.LocalName != "svg") throw new ArgumentException("Roster header must be SVG.");
        return document;
    }
    internal static void Draw(ICoreClientAPI api, string source, Context context, ImageSurface surface, ElementBounds bounds)
    {
        if (string.IsNullOrWhiteSpace(source)) return;
        var doc = Parse(source); var root = doc.Root!;
        double Number(XElement e, string key, double fallback) => double.TryParse((string?)e.Attribute(key), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && double.IsFinite(n) ? n : fallback;
        var view = ((string?)root.Attribute("viewBox") ?? "0 0 700 120").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.TryParse(s, CultureInfo.InvariantCulture, out double n) && double.IsFinite(n) ? n : 0).ToArray();
        if (view.Length != 4 || view[2] <= 0 || view[3] <= 0) return;
        var texts = root.Descendants().Where(e => e.Name.LocalName == "text").ToList();
        foreach (var text in texts) text.Remove();
        root.SetAttributeValue("width", view[2].ToString(CultureInfo.InvariantCulture)); root.SetAttributeValue("height", view[3].ToString(CultureInfo.InvariantCulture));
        var loader = new SvgLoader(api);
        var asset = new Asset(Encoding.UTF8.GetBytes(doc.ToString()), new AssetLocation("vsscsmod:roster.svg"), null!);
        double factor = Math.Min(bounds.InnerWidth / view[2], bounds.InnerHeight / view[3]);
        int width = Math.Max(1, (int)Math.Round(view[2] * factor)), height = Math.Max(1, (int)Math.Round(view[3] * factor));
        double left = bounds.drawX + (bounds.InnerWidth - width) / 2, top = bounds.drawY + (bounds.InnerHeight - height) / 2;
        loader.DrawSvg(asset, surface, (int)left, (int)top, width, height);
        context.Save();
        context.Translate(left, top);
        context.Scale(factor, factor); context.Translate(-view[0], -view[1]);
        foreach (var text in texts)
        {
            context.SelectFontFace(GuiStyle.StandardFontName, FontSlant.Normal, Number(text, "font-weight", 400) >= 600 ? FontWeight.Bold : FontWeight.Normal);
            context.SetFontSize(Math.Clamp(Number(text, "font-size", 16), 1, 512));
            string fill = ((string?)text.Attribute("fill") ?? "#fff").TrimStart('#');
            if (fill.Length == 3) fill = string.Concat(fill.Select(c => new string(c, 2)));
            uint color = uint.TryParse(fill, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : 0xffffff;
            context.SetSourceRGB(((color >> 16) & 255) / 255d, ((color >> 8) & 255) / 255d, (color & 255) / 255d);
            double x = Number(text, "x", 0), y = Number(text, "y", 0), spacing = Number(text, "letter-spacing", 0);
            var characters = text.Value.Trim().EnumerateRunes().Select(r => r.ToString()).ToArray();
            double advance = characters.Sum(c => context.TextExtents(c).XAdvance) + Math.Max(0, characters.Length - 1) * spacing;
            double desired = Number(text, "textLength", advance);
            double horizontal = advance > 0 && desired > 0 ? desired / advance : 1;
            string? anchor = (string?)text.Attribute("text-anchor");
            if (anchor == "middle") x -= desired / 2; else if (anchor == "end") x -= desired;
            context.Save(); context.Translate(x, y); context.Scale(horizontal, 1); x = y = 0;
            foreach (string character in characters)
            { context.MoveTo(x, y); context.ShowText(character); x += context.TextExtents(character).XAdvance + spacing; }
            context.Restore();
        }
        context.Restore();
    }
}

internal sealed class TribeRosterHud(ICoreClientAPI api) : HudElement(api)
{
    private TribeRosterPacket packet = new();
    private bool dirty = true;
    private int width, height;
    private double scale;
    private int columnOffset;
    public override bool Focusable => false;
    public override bool ShouldReceiveMouseEvents() => IsOpened();
    public override bool OnEscapePressed() => false;
    internal static (int Column, int Row) Cell(int index) => (index / 5, index % 5);
    internal void Update(TribeRosterPacket next) { packet = next; dirty = true; }
    internal void Show() { if (dirty) Compose(); if (!IsOpened()) TryOpen(false); }
    internal void Hide() { TryClose(); columnOffset = 0; }
    private void Compose()
    {
        ClearComposers(); width = capi.Render.FrameWidth; height = capi.Render.FrameHeight; scale = Math.Max(.25, RuntimeEnv.GUIScale);
        int columns = Math.Max(1, (packet.Members.Count + 4) / 5);
        int visibleColumns = Math.Max(1, Math.Min(columns, (int)((width / scale - 32) / 240)));
        columnOffset = Math.Clamp(columnOffset, 0, Math.Max(0, columns - visibleColumns));
        double w = Math.Min(width / scale - 24, Math.Max(420, visibleColumns * 240));
        double headerHeight = packet.HeaderSvg.Length == 0 ? 0 : Math.Min(100, w * 120 / 700) * 2 / 3;
        double rowHeight = Math.Min(60, Math.Max(28, (height / scale - headerHeight - 55) / 5));
        var bounds = ElementBounds.Fixed(0, 12, w, headerHeight + Math.Min(5, Math.Max(1, packet.Members.Count)) * rowHeight + 24).WithAlignment(EnumDialogArea.CenterTop);
        var composer = capi.Gui.CreateCompo("vsscs-tribe-roster", bounds)
            .AddGameOverlay(ElementBounds.Fill, [.16, .105, .065, .78]);
        double headerWidth = w * 2 / 3;
        if (headerHeight > 0) composer.AddStaticCustomDraw(ElementBounds.Fixed((w - headerWidth) / 2, 0, headerWidth, headerHeight),
            (ctx, surface, b) => { try { RosterSvg.Draw(capi, packet.HeaderSvg, ctx, surface, b); } catch (Exception ex) { capi.Logger.Warning("Roster SVG: {0}", ex.Message); } });
        if (packet.Members.Count == 0) composer.AddStaticText(Lang.Get("vsscsmod:ui.roster-empty"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(16, headerHeight + 8, w - 32, 26));
        for (int i = columnOffset * 5; i < Math.Min(packet.Members.Count, (columnOffset + visibleColumns) * 5); i++)
        {
            var member = packet.Members[i]; var cell = Cell(i);
            double x = (cell.Column - columnOffset) * w / visibleColumns + 8, y = headerHeight + cell.Row * rowHeight + 8;
            double portrait = rowHeight - 8, textWidth = w / visibleColumns - portrait - 24;
            composer.AddInteractiveElement(new GuiElementTribePortrait(capi, member.Uid, ElementBounds.Fixed(x, y, portrait, portrait)), "portrait-" + i);
            var font = CairoFont.WhiteDetailText();
            double measured = font.GetTextExtents(member.Name).Width / scale;
            if (measured > textWidth) font.UnscaledFontsize *= textWidth / measured;
            composer.AddStaticText(member.Name, font, ElementBounds.Fixed(x + portrait + 8, y, textWidth, rowHeight / 2));
            composer.AddStaticText(Lang.Get("vsscsmod:ui.roster-ping", member.PingMs), CairoFont.WhiteDetailText(), ElementBounds.Fixed(x + portrait + 8, y + rowHeight / 2 - 3, textWidth, rowHeight / 2));
        }
        SingleComposer = composer.Compose(); dirty = false;
    }
    public override void OnMouseWheel(MouseWheelEventArgs args)
    { columnOffset = Math.Max(0, columnOffset - Math.Sign(args.delta)); dirty = true; args.SetHandled(); }
    public override void OnRenderGUI(float dt)
    { if (dirty || width != capi.Render.FrameWidth || height != capi.Render.FrameHeight || scale != Math.Max(.25, RuntimeEnv.GUIScale)) Compose(); base.OnRenderGUI(dt); }
}

// Measure actual key events: polling alone can miss a quick tap between ticks.
internal sealed class CoreMenuKeyGesture
{
    internal const int HoldMilliseconds = 250;
    internal int KeyCode { get; private set; } = -1;
    private long pressedAt;
    internal void Begin(int key, long now)
    { if (KeyCode < 0) { KeyCode=key; pressedAt=now; } }
    internal bool Holding(long now) => KeyCode >= 0 && now-pressedAt >= HoldMilliseconds;
    internal bool Release(int key, long now)
    {
        if (key != KeyCode || KeyCode < 0) return false;
        bool tap=!Holding(now); Cancel(); return tap;
    }
    internal void Cancel() => KeyCode=-1;
}

public sealed partial class VscnClientModSystem
{
    private TribeRosterHud? rosterHud;
    private string? rosterTribeId;
    private KeyCombination? rosterPreviousChatMapping;
    private long rosterListener, rosterRequestedAt;
    private readonly CoreMenuKeyGesture coreMenuKey = new();
    private bool CanUseCoreMenuKey => ready && guidance?.IsOpened() != true
        && (capi!.Input.MouseGrabbed || coreHud?.KeyboardMenuOpen == true)
        && !capi.Gui.OpenedGuis.Any(gui => gui.DialogType == EnumDialogType.Dialog && gui.ShouldReceiveKeyboardEvents());
    private void StartRosterClient()
    {
        capi!.Input.RegisterHotKey("vsscs-tribe-roster", Lang.Get("vsscsmod:ui.roster-key"), GlKeys.Tab, HotkeyType.GUIOrOtherControls);
        capi.Input.SetHotKeyHandler("vsscs-tribe-roster", _ =>
        {
            if (!CanUseCoreMenuKey) return false;
            var mapping=capi.Input.GetHotKeyByCode("vsscs-tribe-roster")?.CurrentMapping;
            if (mapping == null) return false;
            coreMenuKey.Begin(mapping.KeyCode,Environment.TickCount64); return true;
        });
        capi.Event.KeyUp += OnCoreMenuKeyUp;
        var chat = capi.Input.GetHotKeyByCode("chatdialog");
        if (chat?.CurrentMapping is { } mapping && mapping.KeyCode == (int)GlKeys.Tab && !mapping.Ctrl && !mapping.Alt && !mapping.Shift)
        {
            rosterPreviousChatMapping = mapping.Clone();
            chat.CurrentMapping = mapping.Clone(); chat.CurrentMapping.Ctrl = true;
        }
        rosterListener = capi.Event.RegisterGameTickListener(_ => TickRoster(), 50);
    }
    private void TickRoster()
    {
        var mapping = capi!.Input.GetHotKeyByCode("vsscs-tribe-roster")?.CurrentMapping;
        var keys = capi.Input.KeyboardKeyStateRaw;
        if (!CanUseCoreMenuKey || !RosterHeld(mapping, keys)) { coreMenuKey.Cancel(); rosterHud?.Hide(); return; }
        if (!capi.Input.MouseGrabbed || !coreMenuKey.Holding(Environment.TickCount64)) { rosterHud?.Hide(); return; }
        (rosterHud ??= new(capi)).Show();
        if (Environment.TickCount64 - rosterRequestedAt >= 1000)
        { rosterRequestedAt = Environment.TickCount64; channel!.SendPacket(new CivilizationRequestPacket { Action = "roster" }); }
    }
    private void OnCoreMenuKeyUp(KeyEvent args)
    {
        if (args.KeyCode != coreMenuKey.KeyCode) return;
        var mapping=capi!.Input.GetHotKeyByCode("vsscs-tribe-roster")?.CurrentMapping;
        bool valid=CanUseCoreMenuKey && mapping != null && mapping.KeyCode == args.KeyCode
            && mapping.Ctrl == args.CtrlPressed && mapping.Alt == args.AltPressed && mapping.Shift == args.ShiftPressed;
        bool tap=coreMenuKey.Release(args.KeyCode,Environment.TickCount64);
        rosterHud?.Hide();
        if (!valid) return;
        args.Handled=true;
        if (tap) (coreHud ??= new CivilizationCoreHud(capi,OpenCivilizationPage)).ToggleKeyboardMenu();
    }
    internal static bool RosterHeld(KeyCombination? mapping, bool[] keys)
    {
        bool Down(int key) => key > 0 && key < keys.Length && keys[key];
        return mapping != null && Down(mapping.KeyCode)
            && (mapping.SecondKeyCode is not > 0 || Down(mapping.SecondKeyCode.Value))
            && mapping.Ctrl == (Down((int)GlKeys.LControl) || Down((int)GlKeys.RControl))
            && mapping.Alt == (Down((int)GlKeys.LAlt) || Down((int)GlKeys.RAlt))
            && mapping.Shift == (Down((int)GlKeys.LShift) || Down((int)GlKeys.RShift));
    }
}
