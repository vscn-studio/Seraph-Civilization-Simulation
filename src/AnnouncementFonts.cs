using System.Text;
using Cairo;
using SkiaSharp;
using Vintagestory.API.Client;

namespace SeraphCivilizationSimulation;

// Cairo's toy-font API does not provide missing-glyph fallback. Resolve only local
// announcement runs; leave the game's fonts and other mods' rendering untouched.
internal static class AnnouncementFonts
{
    private static readonly Dictionary<(string, string, FontWeight, FontSlant, int), string> families = [];
    internal readonly record struct Run(string Text, CairoFont Font);
    internal static string SingleLine(CairoFont font, string value, double maxWidth)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ');
        if (font.GetTextExtents(value).Width <= maxWidth) return value;
        const string ellipsis = "…";
        var result = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (font.GetTextExtents(result.ToString() + rune + ellipsis).Width > maxWidth) break;
            result.Append(rune);
        }
        return result.ToString() + ellipsis;
    }
    internal static IEnumerable<Run> Runs(CairoFont font, string text)
    {
        if (!font.Fontname.Equals("Consolas", StringComparison.OrdinalIgnoreCase))
        {
            var readable = Readable(font, text);
            if (readable.Fontname != font.Fontname) { yield return new(text, readable); yield break; }
        }
        string? family = null;
        var buffer = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            string next = Family(font, rune);
            if (family != next && buffer.Length > 0)
            { yield return new(buffer.ToString(), WithFamily(font, family!)); buffer.Clear(); }
            family = next; buffer.Append(rune.ToString());
        }
        if (buffer.Length > 0) yield return new(buffer.ToString(), WithFamily(font, family!));
    }
    internal static CairoFont Readable(CairoFont font, string text)
    {
        // Disclosure captions use a single text component.
        // Newlines and formatting controls have no glyph and must not disqualify
        // a font that covers all visible characters of the same component.
        text = string.Concat(text.EnumerateRunes().Where(r => !Rune.IsControl(r)
            && Rune.GetUnicodeCategory(r) != System.Globalization.UnicodeCategory.Format).Select(r => r.ToString()));
        using var face = SKTypeface.FromFamilyName(font.Fontname);
        if (face.ContainsGlyphs(text)) return font;
        var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { font.Fontname };
        foreach (var rune in text.EnumerateRunes())
        {
            string family = Family(font, rune);
            if (!attempted.Add(family)) continue;
            using var candidate = SKTypeface.FromFamilyName(family);
            if (candidate.ContainsGlyphs(text)) return WithFamily(font, family);
        }
        return font;
    }
    private static CairoFont WithFamily(CairoFont font, string family)
    { if (font.Fontname == family) return font; var clone = font.Clone(); clone.Fontname = family; return clone; }
    private static string Family(CairoFont font, Rune rune)
    {
        if (Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) == System.Globalization.UnicodeCategory.Format) return font.Fontname;
        var key = (font.Fontname, GuiStyle.StandardFontName, font.FontWeight, font.Slant, rune.Value);
        if (families.TryGetValue(key, out var cached)) return cached;
        var style = new SKFontStyle(font.FontWeight == FontWeight.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal, font.Slant == FontSlant.Normal ? SKFontStyleSlant.Upright : SKFontStyleSlant.Italic);
        using (style)
        {
            using var preferred = SKTypeface.FromFamilyName(font.Fontname, style);
            string result = font.Fontname;
            if (!preferred.ContainsGlyph(rune.Value))
            {
                using var standard = SKTypeface.FromFamilyName(GuiStyle.StandardFontName, style);
                if (standard.ContainsGlyph(rune.Value)) result = GuiStyle.StandardFontName;
                else
                {
                    using var fallback = SKFontManager.Default.MatchCharacter(font.Fontname, style, null, rune.Value);
                    if (fallback != null) result = fallback.FamilyName;
                }
            }
            if (families.Count >= 8192) families.Clear();
            return families[key] = result;
        }
    }
}
