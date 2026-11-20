using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using SeraphCivilizationSimulation;

internal static class MapTooltipChecks
{
    internal static void Run(ICoreClientAPI api, Action<bool, string> check)
    {
        var original = Lang.AvailableLanguages[Lang.CurrentLocale];
        float originalScale = RuntimeEnv.GUIScale;
        try
        {
            foreach (string locale in new[] { "zh-cn", "en" })
            foreach (var behavior in new[] { EnumLinebreakBehavior.AfterWord, EnumLinebreakBehavior.AfterCharacter })
            {
                var translations = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                    File.ReadAllText($"assets/vsscsmod/lang/{locale}.json"))!;
                Lang.AvailableLanguages[Lang.CurrentLocale] = Proxy.Make<ITranslationService>((m, a) =>
                {
                    if (m.Name == "get_LineBreakBehavior") return behavior;
                    if (!m.Name.StartsWith("Get") || a?[0] is not string key) return null;
                    string text = translations.GetValueOrDefault(key.Replace("vsscsmod:", ""), key);
                    return a.Length > 1 && a[1] is object[] { Length: > 0 } values ? string.Format(text, values) : text;
                });
                foreach (string material in new[] { "papyrus", "parchment" })
                foreach (float scale in new[] { 1f, 1.5f })
                foreach (int width in new[] { 240, 340, 480 })
                {
                    RuntimeEnv.GUIScale = scale;
                    var stack = new ItemStack(new Item { Code = new("immersivemaps:map-" + material) });
                    var atlas = new TreeAttribute(); atlas.SetInt("version", 1); atlas.SetInt("count", 4);
                    stack.Attributes[GroundMapData.AtlasKey] = atlas;
                    var description = new StringBuilder();
                    typeof(GroundMapCompatibility).GetMethod("Info", BindingFlags.NonPublic | BindingFlags.Static)!
                        .Invoke(null, [new DummySlot(stack), description]);
                    string[] expected = [BlockGroundMap.Text("made-" + material, 4), BlockGroundMap.Text("fixed"), BlockGroundMap.Text("atlas-place"), BlockGroundMap.Text("split")];
                    using var element = new GuiElementRichtext(api,
                        VtmlUtil.Richtextify(api, description.ToString(), CairoFont.WhiteSmallText()),
                        ElementBounds.FixedSize(width, 1).WithEmptyParent());
                    element.CalcHeightAndPositions();
                    var lines = element.Components.OfType<RichTextComponent>().SelectMany(c => c.Lines)
                        .Where(l => !string.IsNullOrWhiteSpace(l.Text)).OrderBy(l => l.Bounds.Y).ToArray();
                    string Compact(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
                    string context = $"{locale}/{behavior}/{material}/{width}/{scale}";
                    check(Compact(string.Concat(lines.Select(l => l.Text))) == Compact(string.Concat(expected)),
                        "Native map tooltip wrapping must preserve all description text: " + context + "\nActual: " + string.Concat(lines.Select(l => l.Text)) + "\nExpected: " + string.Concat(expected));
                    check(lines.Zip(lines.Skip(1)).All(pair =>
                        Math.Abs(pair.Second.Bounds.Y - pair.First.Bounds.Y - pair.First.Bounds.Height) <= 1.5),
                        "Native map tooltip must not insert blank rows between wrapped lines or hints: " + context);
                }
            }
        }
        finally
        {
            Lang.AvailableLanguages[Lang.CurrentLocale] = original;
            RuntimeEnv.GUIScale = originalScale;
        }
    }
}
