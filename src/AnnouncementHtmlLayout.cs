using AngleSharp.Dom;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace SeraphCivilizationSimulation;

public sealed class AnnouncementInteraction
{
    internal readonly Dictionary<int, bool> Toggles = [];
    public Action<string>? Navigate;
    public bool Dirty;
}

internal sealed class AnnouncementAnchor(ICoreClientAPI api, string id) : RichTextComponentBase(api)
{
    internal string Id => id;
    internal void Position(double x, double y) => BoundsPerLine = [new(x, y, 0, 0)];
}

// Block layout is shared by styled sections and table cells. Children keep native text/item/link input.
internal class AnnouncementBox : AnnouncementBlock
{
    internal RichTextComponentBase[] Children { get; }
    internal Dictionary<string, string> Style { get; }
    internal double ContentX, ContentY, ContentHeight;
    private readonly string id;
    private double inset, margin, boxWidth, boxHeight;
    internal void Position(double x, double y, double width, double height)
        => BoundsPerLine = [new(x, y, width, height)];
    internal void StretchHeight(double height) => boxHeight = Math.Max(boxHeight, height - margin * 2);
    internal override bool HasNewImages => Children.OfType<AnnouncementBlock>().Any(c => c.HasNewImages);
    internal AnnouncementBox(ICoreClientAPI api, RichTextComponentBase[] children, Dictionary<string, string>? style = null, string id = "") : base(api)
    { Children = children; Style = style ?? []; this.id = id; }
    internal virtual double Measure(double width)
    {
        margin = GuiElement.scaled(AnnouncementDom.Number(Style.GetValueOrDefault("margin"), 0, 128));
        inset = GuiElement.scaled(AnnouncementDom.Number(Style.GetValueOrDefault("padding"), 0, 128));
        double available = Math.Max(1, width - margin * 2);
        boxWidth = Math.Clamp(AnnouncementDom.Dimension(Style.GetValueOrDefault("width"), available, available), 1, available);
        boxWidth = Math.Min(boxWidth, Math.Max(1, AnnouncementDom.Dimension(Style.GetValueOrDefault("max-width"), available, available)));
        inset = Math.Min(inset, Math.Max(0, (boxWidth - 1) / 2));
        ContentX = margin + inset; ContentY = margin + inset;
        double innerWidth = Math.Max(1, boxWidth - 2 * inset);
        ContentHeight = MeasureContent(innerWidth);
        Align(Children, innerWidth, Style.GetValueOrDefault("text-align", "left"));
        boxHeight = Math.Max(ContentHeight + 2 * inset, AnnouncementDom.Dimension(Style.GetValueOrDefault("min-height"), width, 0));
        // A height is a minimum, so content is never hidden behind the following block.
        boxHeight = Math.Max(boxHeight, AnnouncementDom.Dimension(Style.GetValueOrDefault("height"), width, 0));
        return boxHeight + 2 * margin;
    }
    protected virtual double MeasureContent(double width) => AnnouncementTextLayout.Measure(Children, width);
    public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] path, double height, double x, double y, out double nextX)
    {
        double measured = Measure(path[0].X2);
        BoundsPerLine = [new(0, y, boxWidth + 2 * margin, measured)]; nextX = BoundsPerLine[0].Width;
        return EnumCalcBoundsResult.Continue;
    }
    internal static void Align(RichTextComponentBase[] children, double width, string alignment)
    {
        if (alignment is not ("center" or "right")) return;
        foreach (var line in children.SelectMany(c => c.BoundsPerLine).GroupBy(b => Math.Round(b.Y)))
        {
            double spare = Math.Max(0, width - line.Max(b => b.X + b.Width));
            foreach (var bounds in line) bounds.X += alignment == "center" ? spare / 2 : spare;
        }
    }
    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        var b = BoundsPerLine[0]; ctx.Save(); ctx.Translate(b.X, b.Y);
        double radius = GuiElement.scaled(AnnouncementDom.Number(Style.GetValueOrDefault("border-radius"), 0, 64));
        GuiElement.RoundRectangle(ctx, margin, margin, boxWidth, boxHeight, Math.Min(radius, Math.Min(boxWidth, boxHeight) / 2));
        if (AnnouncementDom.Color(Style.GetValueOrDefault("background-color")) is { } bg) { ctx.SetSourceRGBA(bg); ctx.FillPreserve(); }
        string border = Style.GetValueOrDefault("border", "");
        if (border.Length > 0 && border is not ("none" or "0"))
        {
            ctx.SetSourceRGBA(AnnouncementDom.Color(border.Split(' ').Last()) ?? [.53,.44,.30,1]);
            ctx.LineWidth = GuiElement.scaled(AnnouncementDom.Number(border.Split(' ')[0], 1, 8)); ctx.Stroke();
        }
        else ctx.NewPath();
        ctx.Translate(ContentX, ContentY);
        foreach (var child in Children) child.ComposeElements(ctx, surface);
        ctx.Restore();
    }
    public override void RenderInteractiveElements(float dt, double x, double y, double z)
    {
        MouseOverCursor = null; var b = BoundsPerLine[0];
        foreach (var child in Children)
        {
            child.RenderInteractiveElements(dt, x + b.X + ContentX, y + b.Y + ContentY, z);
            if (child.UseMouseOverCursor(ElementBounds.Empty)) MouseOverCursor = child.MouseOverCursor;
        }
    }
    public override bool UseMouseOverCursor(ElementBounds bounds) => MouseOverCursor != null;
    public override void OnMouseDown(MouseEvent args) => Dispatch(args, c => c.OnMouseDown);
    public override void OnMouseUp(MouseEvent args) => Dispatch(args, c => c.OnMouseUp);
    public override void OnMouseMove(MouseEvent args) => Dispatch(args, c => c.OnMouseMove);
    private void Dispatch(MouseEvent args, System.Func<RichTextComponentBase, Action<MouseEvent>> method)
    {
        var b = BoundsPerLine[0];
        var local = new MouseEvent((int)(args.X - b.X - ContentX), (int)(args.Y - b.Y - ContentY), args.Button, 0);
        foreach (var child in Children) { method(child)(local); if (local.Handled) break; }
        args.Handled |= local.Handled;
    }
    internal override double? Anchor(string target)
        => id == target ? BoundsPerLine[0].Y : AnnouncementBlock.FindAnchor(Children, target) is { } child ? BoundsPerLine[0].Y + ContentY + child : null;
    public override void Dispose() { foreach (var child in Children) child.Dispose(); }
}

internal sealed class AnnouncementSwitch : RichTextComponent
{
    private readonly AnnouncementInteraction state;
    private readonly int key;
    private readonly bool current, checkbox;
    private bool pressed, hovered;
    internal AnnouncementSwitch(ICoreClientAPI api, string label, AnnouncementInteraction state, int key, bool current, bool checkbox = false)
        : base(api, label.Length == 0 ? " " : label, AnnouncementFonts.Readable(CairoFont.WhiteSmallText(), label)) { this.state = state; this.key = key; this.current = current; this.checkbox = checkbox; MouseOverCursor = "linkselect"; }
    public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] path, double height, double x, double y, out double nextX)
    {
        // RichTextComponent trims a whitespace-only caption; the drawn checkbox
        // still needs a line box and a hit target when its label is a sibling.
        Lines = AnnouncementTextLayout.Wrap(Font, checkbox ? " " : DisplayText, path[0].X2, x + GuiElement.scaled(22), y, firstLineHeight: height);
        BoundsPerLine = Lines.Select(l => new LineRectangled(l.Bounds.X,l.Bounds.Y,l.Bounds.Width,l.Bounds.Height) { Ascent = l.Bounds.Ascent }).ToArray();
        if (BoundsPerLine.Length > 0) { BoundsPerLine[0].X -= GuiElement.scaled(22); BoundsPerLine[0].Width += GuiElement.scaled(22); }
        nextX = BoundsPerLine.Length == 0 ? x : BoundsPerLine[^1].X + BoundsPerLine[^1].Width;
        return BoundsPerLine.Length > 1 ? EnumCalcBoundsResult.Multiline : EnumCalcBoundsResult.Continue;
    }
    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        base.ComposeElements(ctx, surface);
        if (BoundsPerLine.Length == 0) return;
        var b = BoundsPerLine[0]; ctx.Save();
        ctx.Translate(b.X + GuiElement.scaled(2), b.Y + Math.Max(0,(b.Height-GuiElement.scaled(14))/2));
        ctx.Scale(GuiElement.scaled(1),GuiElement.scaled(1)); ctx.SetSourceRGBA(Font.Color); ctx.LineWidth = 1.5;
        if (checkbox)
        {
            ctx.Rectangle(0,0,14,14); ctx.Stroke();
            if (current) { ctx.MoveTo(3,7); ctx.LineTo(6,10); ctx.LineTo(12,3); ctx.Stroke(); }
        }
        else
        {
            if (current) { ctx.MoveTo(1,4); ctx.LineTo(13,4); ctx.LineTo(7,11); }
            else { ctx.MoveTo(4,1); ctx.LineTo(11,7); ctx.LineTo(4,13); }
            ctx.ClosePath(); ctx.Fill();
        }
        ctx.Restore();
    }
    public override void RenderInteractiveElements(float dt, double x, double y, double z)
        => hovered = BoundsPerLine.Any(b => b.PointInside(api.Input.MouseX - x, api.Input.MouseY - y));
    public override bool UseMouseOverCursor(ElementBounds bounds) => hovered;
    public override void OnMouseDown(MouseEvent e)
    { pressed = e.Button == EnumMouseButton.Left && BoundsPerLine.Any(b => b.PointInside(e.X,e.Y)); e.Handled |= pressed; }
    public override void OnMouseUp(MouseEvent e)
    {
        if (pressed && e.Button == EnumMouseButton.Left && BoundsPerLine.Any(b => b.PointInside(e.X,e.Y)))
        { state.Toggles[key] = !current; state.Dirty = true; e.Handled = true; }
        pressed = false;
    }
}

internal sealed class AnnouncementDisclosure(ICoreClientAPI api, RichTextComponentBase[] visible, RichTextComponentBase[] hidden)
    : AnnouncementBox(api, visible, new() { ["padding"] = "8", ["border"] = "1px solid #88714d" })
{
    public override void Dispose() { base.Dispose(); foreach (var child in hidden) child.Dispose(); }
}

internal sealed class AnnouncementRule(ICoreClientAPI api) : AnnouncementBlock(api)
{
    public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] path, double height, double x, double y, out double nextX)
    { BoundsPerLine = [new(0,y,path[0].X2,GuiElement.scaled(12))]; nextX = path[0].X2; return EnumCalcBoundsResult.Continue; }
    public override void ComposeElements(Context ctx, ImageSurface surface)
    { var b = BoundsPerLine[0]; ctx.Save(); ctx.SetSourceRGBA(.6,.55,.45,1); ctx.LineWidth = GuiElement.scaled(1); ctx.MoveTo(b.X,b.Y+b.Height/2); ctx.LineTo(b.X+b.Width,b.Y+b.Height/2); ctx.Stroke(); ctx.Restore(); }
}

internal sealed class AnnouncementTable : AnnouncementBox
{
    internal sealed record Cell(int Row, int Column, int Rows, int Columns, AnnouncementBox Box, string Vertical);
    internal List<Cell> Cells { get; } = [];
    private readonly List<string?> columnWidths = [];
    private readonly AnnouncementBox? caption;
    private readonly double spacing;
    private readonly int rows, columns;
    private double measuredHeight, measuredWidth;
    internal override bool HasNewImages => Cells.Any(c => c.Box.HasNewImages) || caption?.HasNewImages == true;
    internal AnnouncementTable(ICoreClientAPI api, IElement table, System.Func<string, RichTextComponentBase[]> parse) : base(api, [], AnnouncementDom.Styles(table))
    {
        spacing = AnnouncementDom.Number(AnnouncementDom.Value(Style, "border-spacing", table.GetAttribute("cellspacing") ?? ""), 0, 24);
        var colElements = table.QuerySelectorAll("colgroup > col, colgroup:not(:has(col))").Where(c => c.Closest("table") == table);
        foreach (var col in colElements)
            for (int i = 0; i < Math.Clamp((int)AnnouncementDom.Number(col.GetAttribute("span"), 1, 16), 1, 16) && columnWidths.Count < 16; i++)
                columnWidths.Add(AnnouncementDom.Value(AnnouncementDom.Styles(col), "width", col.GetAttribute("width") ?? ""));
        var rowElements = table.QuerySelectorAll("tr").Where(r => r.Closest("table") == table).Take(64).ToArray();
        rows = rowElements.Length;
        bool[,] occupied = new bool[Math.Max(1, rows), 16];
        for (int r = 0; r < rows; r++)
        {
            int column = 0;
            foreach (var cell in rowElements[r].Children.Where(c => c.LocalName is "td" or "th"))
            {
                while (column < 16 && occupied[r, column]) column++;
                if (column == 16 || Cells.Count >= 512) break;
                int rs = Math.Clamp((int)AnnouncementDom.Number(cell.GetAttribute("rowspan"), 1, 64), 1, rows - r);
                int cs = Math.Clamp((int)AnnouncementDom.Number(cell.GetAttribute("colspan"), 1, 16), 1, 16 - column);
                while (cs > 1 && Enumerable.Range(column, cs).Any(c => occupied[r, c])) cs--;
                for (int y = r; y < r + rs; y++) for (int x = column; x < column + cs; x++) occupied[y, x] = true;
                var style = AnnouncementDom.Styles(cell);
                style.TryAdd("padding", table.GetAttribute("cellpadding") ?? "6");
                style.TryAdd("border", Style.GetValueOrDefault("border", "1px solid #88714d"));
                style.TryAdd("text-align", cell.GetAttribute("align") ?? (cell.LocalName == "th" ? "center" : "left"));
                string inner = (cell.LocalName == "th" ? "<strong>" + cell.InnerHtml + "</strong>" : cell.InnerHtml);
                // Text style belongs to each cell; layout styles remain on the box.
                inner = "<span style=\"" + System.Net.WebUtility.HtmlEncode(string.Join(';', style.Where(p => p.Key is "color" or "font-size" or "font-family" or "line-height").Select(p => p.Key + ":" + p.Value))) + "\">" + inner + "</span>";
                while (columnWidths.Count < column + cs) columnWidths.Add(null);
                if (cs == 1 && columnWidths[column] == null) columnWidths[column] = AnnouncementDom.Value(style, "width", cell.GetAttribute("width") ?? "");
                style.Remove("width"); style.Remove("max-width"); style.Remove("margin");
                Cells.Add(new(r, column, rs, cs, new(api, parse(inner), style, cell.Id ?? ""), style.GetValueOrDefault("vertical-align", cell.GetAttribute("valign") ?? "top")));
                column += cs; columns = Math.Max(columns, column);
            }
        }
        if (table.Children.FirstOrDefault(c => c.LocalName == "caption") is { } title) caption = new(api, parse(title.InnerHtml), new() { ["padding"] = "6", ["text-align"] = "center" });
    }
    internal override double Measure(double width)
    {
        measuredWidth = Math.Clamp(AnnouncementDom.Dimension(Style.GetValueOrDefault("width"), width, width), 1, width);
        double gap = Math.Min(GuiElement.scaled(spacing), measuredWidth / Math.Max(1, columns + 1));
        double available = Math.Max(1, measuredWidth - gap * (columns + 1));
        var widths = Enumerable.Range(0, columns).Select(i => AnnouncementDom.Dimension(columnWidths.ElementAtOrDefault(i), available, 0)).ToArray();
        int automatic = widths.Count(w => w == 0);
        double assigned = widths.Sum();
        for (int i = 0; i < columns; i++) if (widths[i] == 0) widths[i] = Math.Max(available / Math.Max(1, columns) / 4, (available - assigned) / Math.Max(1, automatic));
        double total = widths.Sum();
        if (total > 0) for (int i = 0; i < columns; i++) widths[i] = Math.Max(.1, widths[i] * available / total);
        double captionHeight = 0;
        if (caption != null) { captionHeight = caption.Measure(measuredWidth); caption.Position(0, 0, measuredWidth, captionHeight); }
        var heights = new double[rows];
        var natural = new Dictionary<Cell, double>();
        foreach (var cell in Cells)
        {
            double w = widths.Skip(cell.Column).Take(cell.Columns).Sum() + gap * (cell.Columns - 1);
            natural[cell] = cell.Box.Measure(w);
            if (cell.Rows == 1) heights[cell.Row] = Math.Max(heights[cell.Row], natural[cell]);
        }
        foreach (var cell in Cells.Where(c => c.Rows > 1))
        {
            double missing = natural[cell] - heights.Skip(cell.Row).Take(cell.Rows).Sum() - gap * (cell.Rows - 1);
            if (missing > 0) for (int row = cell.Row; row < cell.Row + cell.Rows; row++) heights[row] += missing / cell.Rows;
        }
        foreach (var cell in Cells)
        {
            double x = gap + widths.Take(cell.Column).Sum() + gap * cell.Column;
            double y = captionHeight + gap + heights.Take(cell.Row).Sum() + gap * cell.Row;
            double w = widths.Skip(cell.Column).Take(cell.Columns).Sum() + gap * (cell.Columns - 1);
            double h = heights.Skip(cell.Row).Take(cell.Rows).Sum() + gap * (cell.Rows - 1);
            cell.Box.Position(x, y, w, h);
            cell.Box.StretchHeight(h);
            if (cell.Vertical is "middle" or "bottom") cell.Box.ContentY += Math.Max(0, h - natural[cell]) * (cell.Vertical == "middle" ? .5 : 1);
        }
        measuredHeight = captionHeight + heights.Sum() + gap * (rows + 1); return measuredHeight;
    }
    public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] path, double height, double x, double y, out double nextX)
    { Measure(path[0].X2); BoundsPerLine = [new(0,y,measuredWidth,measuredHeight)]; nextX = measuredWidth; return EnumCalcBoundsResult.Continue; }
    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        ctx.Save(); var b = BoundsPerLine[0]; ctx.Translate(b.X,b.Y);
        caption?.ComposeElements(ctx,surface); foreach (var cell in Cells) cell.Box.ComposeElements(ctx,surface); ctx.Restore();
    }
    public override void RenderInteractiveElements(float dt, double x, double y, double z)
    {
        MouseOverCursor = null; var b = BoundsPerLine[0];
        foreach (var box in Boxes()) { box.RenderInteractiveElements(dt,x+b.X,y+b.Y,z); if (box.UseMouseOverCursor(ElementBounds.Empty)) MouseOverCursor = box.MouseOverCursor; }
    }
    internal IEnumerable<AnnouncementBox> Boxes() => caption == null ? Cells.Select(c => c.Box) : new[] { caption }.Concat(Cells.Select(c => c.Box));
    private void Dispatch(MouseEvent args, System.Func<AnnouncementBox, Action<MouseEvent>> action)
    {
        var b = BoundsPerLine[0]; var local = new MouseEvent((int)(args.X-b.X),(int)(args.Y-b.Y),args.Button,0);
        foreach (var box in Boxes()) { action(box)(local); if (local.Handled) break; } args.Handled |= local.Handled;
    }
    public override void OnMouseDown(MouseEvent args) => Dispatch(args,c=>c.OnMouseDown);
    public override void OnMouseUp(MouseEvent args) => Dispatch(args,c=>c.OnMouseUp);
    public override void OnMouseMove(MouseEvent args) => Dispatch(args,c=>c.OnMouseMove);
    internal override double? Anchor(string target)
    { foreach (var box in Boxes()) if (box.Anchor(target) is { } y) return BoundsPerLine[0].Y+y; return null; }
    public override void Dispose() { foreach (var box in Boxes()) box.Dispose(); }
}

