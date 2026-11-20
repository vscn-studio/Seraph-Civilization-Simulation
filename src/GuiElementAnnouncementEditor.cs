using Cairo;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace SeraphCivilizationSimulation;

// Native HTML editing and selection, with a fixed-size viewport for long documents.
public class GuiElementAnnouncementEditor : GuiElementTextArea
{
    // The game exposes caret positions but keeps the selection anchor private.
    private static readonly FieldInfo SelectionAnchor = typeof(GuiElementEditableTextBase)
        .GetField("selectedTextStart", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private int? savedSelectionAnchor;
    private int visibleTexture, caretTexture;
    private bool redraw = true, previousFocus, selecting;
    private double offset;
    private long caretMovedAt;
    public double ContentHeight => Math.Max(Bounds.InnerHeight, (lines.Count + 1) * Font.GetFontExtents().Height + scaled(16));

    public GuiElementAnnouncementEditor(ICoreClientAPI api, ElementBounds bounds, Action<string> changed, CairoFont font)
        // Native AfterWord wrapping can replace a Chinese character with a space at
        // a narrow line boundary. Let it parse explicit breaks, then wrap locally.
        : base(api, bounds, changed, AnnouncementFonts.Readable(font, "公告编辑 中文 ABC")) { Autoheight = false; WordWrap = false; }

    public void ScrollTo(double value)
    {
        double next = Math.Clamp(value, 0, Math.Max(0, ContentHeight - Bounds.InnerHeight));
        if (next != offset) { offset = next; redraw = true; }
    }

    public override void LoadValue(List<string> newLines)
    {
        int caret = CaretPosWithoutLineBreaks;
        newLines = AnnouncementTextLayout.Wrap(Font, string.Join("", newLines), Math.Max(1, Bounds.InnerWidth - scaled(6)), 0, 0, true)
            .Select(line => line.Text).ToList();
        if (newLines.Count == 0) newLines.Add("");
        base.LoadValue(newLines);
        // Soft wrapping may change visual line lengths; keep the caret at the same
        // character in the document so native typing/deletion can advance it.
        CaretPosWithoutLineBreaks = Math.Min(caret, GetText().Length);
        redraw = true; caretMovedAt = api.ElapsedMilliseconds;
    }

    public override void ComposeTextElements(Context ctx, ImageSurface surface)
    {
        // The native text/highlight textures also remain bounded to the viewport.
        base.ComposeTextElements(ctx, surface);
        using var caret = new ImageSurface(Format.Argb32, 2, Math.Max(1, (int)Math.Ceiling(Font.GetFontExtents().Height)));
        using var draw = new Context(caret);
        draw.SetSourceRGBA(Font.Color); draw.Paint();
        generateTexture(caret, ref caretTexture);
        redraw = true;
    }

    public override void RenderInteractiveElements(float dt)
    {
        if (previousFocus != HasFocus) { previousFocus = HasFocus; redraw = true; }
        double height = Font.GetFontExtents().Height;
        if (redraw)
        {
            redraw = false;
            using var visible = new ImageSurface(Format.Argb32, Math.Max(1, (int)Bounds.InnerWidth), Math.Max(1, (int)Bounds.InnerHeight));
            using var draw = new Context(visible);
            draw.SetSourceRGBA(0, 0, 0, .2); draw.Paint();
            if (HasFocus) { draw.SetSourceRGBA(1, 1, 1, .1); draw.Paint(); }
            int first = Math.Max(0, (int)(offset / height));
            int end = Math.Min(lines.Count, (int)Math.Ceiling((offset + Bounds.InnerHeight) / height) + 1);
            var visibleLines = Enumerable.Range(first, Math.Max(0, end - first)).Select(i => new TextLine
            {
                Text = lines[i].Replace("\r", "").Replace("\n", ""),
                Bounds = new LineRectangled(0, i * height - offset, Bounds.InnerWidth, height)
            }).ToArray();
            api.Gui.Text.DrawMultilineTextAt(draw, Font, visibleLines, 3, 0, Bounds.InnerWidth);
            generateTexture(visible, ref visibleTexture);
        }
        api.Render.Render2DTexturePremultipliedAlpha(visibleTexture, Bounds);
        WithScrolledBounds(RenderTextSelection);
        if (HasFocus && (api.ElapsedMilliseconds - caretMovedAt) % 1800 < 900)
        {
            double x = Font.GetTextExtents(lines[CaretPosLine][..CaretPosInLine]).XAdvance + scaled(1.5);
            api.Render.Render2DTexturePremultipliedAlpha(caretTexture, Bounds.renderX + x,
                Bounds.renderY + CaretPosLine * height - offset, 2, height);
        }
    }

    private void WithScrolledBounds(Action action)
    {
        double y = Bounds.fixedY;
        try { Bounds.fixedY -= offset / Math.Max(.25, RuntimeEnv.GUIScale); Bounds.CalcWorldBounds(); action(); }
        finally { Bounds.fixedY = y; Bounds.CalcWorldBounds(); }
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        if (!IsPositionInside(args.X, args.Y)) return;
        selecting = args.Button == EnumMouseButton.Left;
        WithDocumentMouse(args, () => base.OnMouseDownOnElement(api, args)); caretMovedAt = api.ElapsedMilliseconds;
    }
    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        if (!selecting) return;
        WithDocumentMouse(args, () => base.OnMouseMove(api, args));
        args.Handled = true;
    }
    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        selecting = false;
        base.OnMouseUp(api, args);
    }
    public override void OnFocusLost()
    {
        if (HasFocus) savedSelectionAnchor = (int?)SelectionAnchor.GetValue(this);
        selecting = false;
        base.OnMouseUp(api, new MouseEvent(0, 0, EnumMouseButton.Left, 0));
        base.OnFocusLost();
    }
    private void WithDocumentMouse(MouseEvent args, Action action)
    {
        int y = args.Y;
        try { args.Y += (int)Math.Round(offset); action(); }
        finally { args.Y = y; }
    }
    public override void OnKeyDown(ICoreClientAPI api, KeyEvent args)
    {
        if (HasFocus && args.CtrlPressed && !args.AltPressed && args.KeyCode is (int)GlKeys.B or (int)GlKeys.I or (int)GlKeys.K)
        {
            ApplyHtml(args.KeyCode == (int)GlKeys.B ? "bold" : args.KeyCode == (int)GlKeys.I ? "italic" : "link");
            args.Handled = true;
            return;
        }
        string before = GetText();
        base.OnKeyDown(api, args);
        // Enter changes native lines directly without passing through LoadValue.
        if (GetText() != before) LoadValue([GetText()]);
        caretMovedAt = api.ElapsedMilliseconds;
    }

    public (int Start, int End) Selection()
    {
        int caret = CaretPosWithoutLineBreaks;
        int anchor = (HasFocus ? (int?)SelectionAnchor.GetValue(this) : savedSelectionAnchor) ?? caret;
        return (Math.Min(anchor, caret), Math.Max(anchor, caret));
    }
    public void ApplyHtml(string tool)
    {
        var range = Selection();
        ApplyEdit(AnnouncementHtmlTools.Format(GetText(), range.Start, range.End, tool, Lang.Get("vsscsmod:ui.html-placeholder")));
    }
    internal void ApplyEdit(AnnouncementMarkdown.Edit edit)
    {
        if (edit.Text == GetText()) return;
        SetValue(edit.Text, false);
        CaretPosWithoutLineBreaks = edit.SelectionEnd;
        savedSelectionAnchor = edit.SelectionStart;
        SelectionAnchor.SetValue(this, (int?)edit.SelectionStart);
        redraw = true; caretMovedAt = api.ElapsedMilliseconds;
    }
    public override void Dispose()
    {
        if (visibleTexture != 0) api.Gui.DeleteTexture(visibleTexture);
        if (caretTexture != 0) api.Gui.DeleteTexture(caretTexture);
        visibleTexture = caretTexture = 0; base.Dispose();
    }
}
