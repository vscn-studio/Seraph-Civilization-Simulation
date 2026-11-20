using Vintagestory.API.Client;
using Vintagestory.API.Config;
using SeraphCivilizationSimulation;

internal static class MapHudChecks
{
    internal static void Run(ICoreClientAPI api, Action<bool, string> check)
    {
        float previousScale = RuntimeEnv.GUIScale;
        try
        {
            var owned = new List<LoadedTexture>();
            using (var client = new GroundAtlasClient(api))
            {
                int width = 0, height = 0;
                foreach (float scale in new[] { 1f, 1.5f, 2f })
                {
                    RuntimeEnv.GUIScale = scale;
                    var label = client.HudLabel("MMMMM", 100); owned.Add(label);
                    check(label.TextureId > 0 && label.Width > 30 * scale && label.Height > 10 * scale && label.Height < 32 * scale,
                        $"Pinned HUD names must rasterize at the original compact 14-point size, rather than paper-label size: scale {scale}, {label.Width}x{label.Height}.");
                    if (scale == 1) { width = label.Width; height = label.Height; }
                    check(label.Width <= width * scale + 4 && label.Height <= height * scale + 4
                        && (scale == 1 || label.Width > width && label.Height > height),
                        $"HUD labels must follow the GUI scale once, without the paper glyph cache's extra scaling: {scale}, {label.Width}x{label.Height}, baseline {width}x{height}.");
                    check(ReferenceEquals(label, client.HudLabel("MMMMM", 200)),
                        "HUD names must reuse their own texture without requesting the native paper glyph cache.");
                }
                check(owned.Distinct().Count() == 3, "Changing GUI scale must refresh cached HUD label dimensions.");
                owned.Add(client.HudLabel("聚落", 100));
            }
            check(owned.All(t => t.TextureId == 0), "Disposing the map HUD must release its own label textures.");
        }
        finally { RuntimeEnv.GUIScale = previousScale; }
    }
}
