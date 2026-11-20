using Cairo;
using Vintagestory.API.Client;

namespace SeraphCivilizationSimulation;

public enum TribeIcon { Home, Campfire }

// Original vector artwork, drawn with the game's Cairo API at any GUI scale.
public static class TribeIcons
{
    public static LoadedTexture CreateTexture(ICoreClientAPI api, TribeIcon icon)
    {
        using var surface = new ImageSurface(Format.Argb32, 128, 128);
        using var ctx = new Context(surface);
        Draw(ctx, icon, 0, 0, 128);
        return new LoadedTexture(api, api.Gui.LoadCairoTexture(surface, true), 128, 128);
    }

    public static void Draw(Context ctx, TribeIcon icon, double x, double y, double size)
    {
        ctx.Save();
        ctx.Translate(x, y);
        ctx.Scale(size / 64, size / 64);
        ctx.LineJoin = LineJoin.Round;
        ctx.LineCap = LineCap.Round;
        if (icon == TribeIcon.Home) Home(ctx); else Campfire(ctx);
        ctx.Restore();
    }

    private static void Home(Context ctx)
    {
        // Chimney, plaster walls, steep timber roof and a recessed doorway.
        ctx.Rectangle(43, 12, 7, 20);
        FillOutline(ctx, .66, .54, .42);
        ctx.MoveTo(14, 29); ctx.LineTo(32, 14); ctx.LineTo(50, 29);
        ctx.LineTo(50, 54); ctx.LineTo(14, 54); ctx.ClosePath();
        FillOutline(ctx, .88, .81, .73);
        ctx.MoveTo(8, 29); ctx.LineTo(32, 8); ctx.LineTo(56, 29);
        ctx.LineTo(52, 34); ctx.LineTo(32, 17); ctx.LineTo(12, 34); ctx.ClosePath();
        FillOutline(ctx, .66, .54, .42);
        ctx.MoveTo(14, 34); ctx.LineTo(14, 54); ctx.LineTo(50, 54); ctx.LineTo(50, 34);
        Stroke(ctx, .47, .36, .25, 3);
        ctx.Rectangle(27, 36, 11, 18);
        FillOutline(ctx, .32, .25, .19);
        ctx.Rectangle(18, 36, 6, 7);
        FillOutline(ctx, .78, .63, .43);
        ctx.MoveTo(10, 56); ctx.LineTo(54, 56);
        Stroke(ctx, .88, .81, .73, 3);
        ctx.Arc(35, 46, 1, 0, Math.PI * 2);
        ctx.SetSourceRGB(.91, .78, .55); ctx.Fill();
    }

    private static void Campfire(Context ctx)
    {
        // Crossed split logs, a broad flame and a pale inner flame remain readable at 32 px.
        ctx.MoveTo(12, 46); ctx.LineTo(16, 41); ctx.LineTo(53, 53);
        ctx.LineTo(49, 58); ctx.ClosePath();
        FillOutline(ctx, .66, .54, .42);
        ctx.MoveTo(11, 53); ctx.LineTo(48, 41); ctx.LineTo(53, 46);
        ctx.LineTo(16, 58); ctx.ClosePath();
        FillOutline(ctx, .78, .65, .49);
        ctx.MoveTo(18, 39);
        ctx.CurveTo(14, 31, 21, 26, 22, 20);
        ctx.CurveTo(25, 23, 25, 27, 26, 28);
        ctx.CurveTo(34, 21, 29, 13, 35, 6);
        ctx.CurveTo(36, 16, 44, 20, 42, 29);
        ctx.CurveTo(46, 28, 46, 25, 47, 24);
        ctx.CurveTo(54, 38, 45, 46, 32, 46);
        ctx.CurveTo(25, 46, 21, 44, 18, 39); ctx.ClosePath();
        FillOutline(ctx, .88, .58, .30);
        ctx.MoveTo(27, 40);
        ctx.CurveTo(23, 34, 30, 30, 33, 24);
        ctx.CurveTo(32, 32, 41, 35, 37, 41);
        ctx.CurveTo(35, 45, 29, 44, 27, 40); ctx.ClosePath();
        ctx.SetSourceRGB(.95, .83, .60); ctx.Fill();
        ctx.MoveTo(19, 51); ctx.LineTo(43, 44);
        Stroke(ctx, .40, .30, .22, 1.5);
    }

    private static void FillOutline(Context ctx, double r, double g, double b)
    {
        ctx.SetSourceRGB(r, g, b); ctx.FillPreserve();
        Stroke(ctx, .20, .16, .12, 2.5);
    }

    private static void Stroke(Context ctx, double r, double g, double b, double width)
    {
        ctx.SetSourceRGB(r, g, b); ctx.LineWidth = width; ctx.Stroke();
    }
}
