using System.Globalization;
using System.Text;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace SeraphCivilizationSimulation;

internal abstract class AnnouncementBlock(ICoreClientAPI api) : RichTextComponentBase(api)
{
    internal virtual bool HasNewImages => false;
    internal virtual double? Anchor(string id) => null;
    internal static double? FindAnchor(IEnumerable<RichTextComponentBase> children, string id)
    {
        foreach (var child in children)
        {
            if (child is AnnouncementAnchor marker && marker.Id == id) return marker.BoundsPerLine[0].Y;
            if (child is AnnouncementBlock block && block.Anchor(id) is { } y) return y;
        }
        return null;
    }
}

internal sealed class AnnouncementCard(ICoreClientAPI api, RichTextComponentBase[] children, Dictionary<string, string> attributes)
    : AnnouncementBlock(api)
{
    internal RichTextComponentBase[] Children => children;
    internal override bool HasNewImages => children.OfType<AnnouncementBlock>().Any(c => c.HasNewImages);
    internal override double? Anchor(string id) => FindAnchor(children, id) is { } y ? BoundsPerLine[0].Y + Padding + y : null;
    private double Padding => GuiElement.scaled(14);
    private readonly double[] background = ParseColor(attributes.GetValueOrDefault("bg", "#30291f"));
    private readonly double[] border = ParseColor(attributes.GetValueOrDefault("border", "#88714d"));

    public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] path, double height, double x, double y, out double nextX)
    {
        double width = Math.Max(1, path[0].X2);
        double contentHeight = AnnouncementTextLayout.Measure(children, Math.Max(1, width - 2 * Padding));
        BoundsPerLine = [new(0, y, width, Math.Max(GuiElement.scaled(20), contentHeight) + 2 * Padding)];
        nextX = width; return EnumCalcBoundsResult.Continue;
    }
    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        var box = BoundsPerLine[0];
        ctx.Save();
        ctx.Rectangle(box.X + .5, box.Y + .5, Math.Max(1, box.Width - 1), Math.Max(1, box.Height - 1));
        ctx.SetSourceRGB(background[0], background[1], background[2]); ctx.FillPreserve();
        ctx.SetSourceRGB(border[0], border[1], border[2]); ctx.LineWidth = Math.Max(1, GuiElement.scaled(1)); ctx.Stroke();
        ctx.Translate(box.X + Padding, box.Y + Padding);
        foreach (var child in children) child.ComposeElements(ctx, surface);
        ctx.Restore();
    }
    public override void RenderInteractiveElements(float dt, double x, double y, double z)
    {
        MouseOverCursor = null;
        var box = BoundsPerLine[0];
        foreach (var child in children)
        {
            child.RenderInteractiveElements(dt, x + box.X + Padding, y + box.Y + Padding, z);
            if (child.MouseOverCursor != null && child.UseMouseOverCursor(ElementBounds.Empty)) MouseOverCursor = child.MouseOverCursor;
        }
    }
    public override bool UseMouseOverCursor(ElementBounds bounds) => MouseOverCursor != null;
    public override void OnMouseMove(MouseEvent args) => Dispatch(args, c => c.OnMouseMove);
    public override void OnMouseDown(MouseEvent args) => Dispatch(args, c => c.OnMouseDown);
    public override void OnMouseUp(MouseEvent args) => Dispatch(args, c => c.OnMouseUp);
    private void Dispatch(MouseEvent args, System.Func<RichTextComponentBase, Action<MouseEvent>> action)
    {
        var box = BoundsPerLine[0];
        var local = new MouseEvent((int)(args.X - box.X - Padding), (int)(args.Y - box.Y - Padding), args.Button, 0);
        foreach (var child in children)
        {
            action(child)(local);
            if (local.Handled) break;
        }
        args.Handled |= local.Handled;
    }
    public override void Dispose() { foreach (var child in children) child.Dispose(); }
    private static double[] ParseColor(string color) => [Convert.ToInt32(color.Substring(1, 2), 16) / 255.0,
        Convert.ToInt32(color.Substring(3, 2), 16) / 255.0, Convert.ToInt32(color.Substring(5, 2), 16) / 255.0];
}

internal sealed class AnnouncementImage : AnnouncementBlock
{
    private readonly ImageSurface? image;
    private readonly Task<byte[]?>? download;
    private readonly string alt, width, height;
    private readonly LinkTextComponent? link;
    private readonly Dictionary<string, string> style;
    private bool mouseDown, hovered;
    internal bool Loaded => image != null;
    internal override bool HasNewImages => download?.IsCompleted == true;

    internal AnnouncementImage(ICoreClientAPI api, Dictionary<string, string> attributes, string? href = null, Action<string>? navigate = null) : base(api)
    {
        if (href != null) link = new LinkTextComponent(api, "", CairoFont.WhiteSmallText(), href.StartsWith('#') ? l => navigate?.Invoke(l.Href) : href.StartsWith("handbook://", StringComparison.OrdinalIgnoreCase) ? l => AnnouncementGameResources.Open(api, l.Href) : null) { Href = href };
        style = [];
        foreach (string declaration in DecodeText(attributes.GetValueOrDefault("style64", "")).Split(';'))
        { int colon = declaration.IndexOf(':'); if (colon > 0) style[declaration[..colon].Trim().ToLowerInvariant()] = declaration[(colon + 1)..].Trim(); }
        alt = DecodeText(attributes.GetValueOrDefault("alt64", ""));
        width = attributes.GetValueOrDefault("width", "0"); height = attributes.GetValueOrDefault("height", "0");
        string? source = AnnouncementImages.NormalizeSource(DecodeText(attributes.GetValueOrDefault("src64", "")));
        if (source == null) return;
        byte[]? bytes;
        if (source.StartsWith("https://", StringComparison.Ordinal))
        {
            var task = AnnouncementImages.Request(source);
            if (!task.IsCompleted) { download = task; return; }
            bytes = task.IsCompletedSuccessfully ? task.Result : null;
        }
        else bytes = api.Assets.TryGet(new AssetLocation(source))?.Data;
        image = AnnouncementImages.Decode(bytes);
    }
    public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] path, double currentHeight, double x, double y, out double nextX)
    {
        double available = Math.Max(1, path[0].X2), ratio = image == null ? 16.0 / 9 : (double)image.Width / image.Height;
        double requestedWidth = Dimension(width, available), requestedHeight = Dimension(height, available);
        double w = requestedWidth > 0 ? requestedWidth : requestedHeight > 0 ? requestedHeight * ratio
            : image == null ? available : GuiElement.scaled(image.Width);
        double h = requestedHeight > 0 ? requestedHeight : w / ratio;
        if (image == null && download == null && requestedHeight == 0) h = Math.Min(h, GuiElement.scaled(72));
        // Scale the entire box to fit. Rendering uses contain, never stretches pixels.
        double maxWidth = Math.Min(available, AnnouncementDom.Dimension(style.GetValueOrDefault("max-width"), available, available));
        double maxHeight = Math.Min(GuiElement.scaled(1024), AnnouncementDom.Dimension(style.GetValueOrDefault("max-height"), available, GuiElement.scaled(1024)));
        if (w > maxWidth) { h *= maxWidth / w; w = maxWidth; }
        if (h > maxHeight) { w *= maxHeight / h; h = maxHeight; }
        double left = style.GetValueOrDefault("margin") is "auto" or "0 auto" || style.GetValueOrDefault("text-align") == "center" ? Math.Max(0,(available-w)/2) : 0;
        BoundsPerLine = [new(left, y, Math.Max(1, w), Math.Max(1, h))];
        nextX = w; return EnumCalcBoundsResult.Continue;
    }
    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        var box = BoundsPerLine[0];
        // Clip half a device pixel inside the calculated bounds. Cairo's texture
        // filtering otherwise samples the transparent edge column and leaves a
        // one pixel vertical seam beside images and item backgrounds.
        ctx.Save(); ctx.Rectangle(box.X + .5, box.Y + .5, Math.Max(1, box.Width - 1), Math.Max(1, box.Height - 1)); ctx.Clip();
        if (image != null)
        {
            double factor = style.GetValueOrDefault("object-fit") == "cover" ? Math.Max(box.Width / image.Width, box.Height / image.Height)
                : Math.Min(box.Width / image.Width, box.Height / image.Height);
            ctx.Translate(box.X + (box.Width - image.Width * factor) / 2, box.Y + (box.Height - image.Height * factor) / 2);
            ctx.Scale(factor, factor); ctx.SetSourceSurface(image, 0, 0); ctx.Paint();
        }
        else
        {
            ctx.SetSourceRGBA(.08, .07, .05, .6); ctx.Paint();
            ctx.SetSourceRGB(.53, .44, .30); ctx.Rectangle(box.X + .5, box.Y + .5, box.Width - 1, box.Height - 1); ctx.Stroke();
            var font = CairoFont.WhiteSmallText();
            string label = Lang.Get("vsscsmod:ui.announcement-image-" + (download == null ? "failed" : "loading"));
            if (alt.Length > 0) label += " · " + alt;
            var lines = AnnouncementTextLayout.Wrap(font, label, Math.Max(1, box.Width - GuiElement.scaled(16)), 0, 0);
            ctx.Translate(box.X + GuiElement.scaled(8), box.Y + GuiElement.scaled(8));
            new TextDrawUtil().DrawMultilineText(ctx, font, lines);
        }
        ctx.Restore();
    }
    public override void RenderInteractiveElements(float dt, double x, double y, double z)
    {
        hovered = link != null && BoundsPerLine[0].PointInside(api.Input.MouseX - x, api.Input.MouseY - y);
        MouseOverCursor = hovered ? "linkselect" : null;
    }
    public override bool UseMouseOverCursor(ElementBounds bounds) => hovered;
    public override void OnMouseDown(MouseEvent args)
    {
        mouseDown = link != null && args.Button == EnumMouseButton.Left && BoundsPerLine[0].PointInside(args.X, args.Y);
        if (mouseDown) args.Handled = true;
    }
    public override void OnMouseUp(MouseEvent args)
    {
        if (mouseDown && args.Button == EnumMouseButton.Left && BoundsPerLine[0].PointInside(args.X, args.Y))
        { args.Handled = true; link?.Trigger(); }
        mouseDown = false;
    }
    public override void Dispose() { image?.Dispose(); link?.Dispose(); }
    private static string DecodeText(string text)
    { try { return Encoding.UTF8.GetString(Convert.FromBase64String(text)); } catch (FormatException) { return ""; } }
    private static double Dimension(string value, double available)
    {
        bool percent = value.EndsWith('%');
        return double.TryParse(percent ? value[..^1] : value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number)
            ? percent ? available * Math.Clamp(number, 0, 100) / 100 : GuiElement.scaled(Math.Clamp(number, 0, 2048)) : 0;
    }
}
