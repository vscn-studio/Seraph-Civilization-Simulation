using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using Cairo;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using SeraphCivilizationSimulation;

internal static class EconomyOverlayChecks
{
    public static void Run(ICoreClientAPI api, Dictionary<int, ImageSurface> textures, string output, Action<bool, string> check)
    {
        var previewType = typeof(EconomyTradeWindow).Assembly.GetType("SeraphCivilizationSimulation.GuiElementEconomyItem")!;
        var item = new Item { Code = new AssetLocation("game:pickaxe-copper"), Durability = 100 };
        var stack = new ItemStack(item); stack.Attributes.SetInt("durability", 27);
        int originalTextures = textures.Count;
        void Preview(bool sharpness, string name, bool durability = true)
        {
            foreach (double size in new[] { 36d, 48d, 64d })
            {
                var bounds = ElementBounds.Fixed(0, 0, size, size).WithEmptyParent(); bounds.CalcWorldBounds();
                using var preview = (GuiElement)Activator.CreateInstance(previewType,
                    [api, null, bounds, null, 1, false, false, null, false, null, null])!;
                var slot = (ItemSlot)previewType.GetField("slot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview)!;
                slot.Itemstack = stack.Clone();
                preview.RenderInteractiveElements(0);
                var overlay = (GuiElementItemSlotGridBase)previewType.GetField("statusOverlay", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview)!;
                check(overlay.slotQuantityTextures[0].TextureId > 0, "Damaged trade previews must draw the native condition overlay.");
                var surface = textures[overlay.slotQuantityTextures[0].TextureId]; surface.Flush();
                byte[] pixels = surface.Data;
                bool HasBar(int bottomOffset)
                {
                    int y = (int)(bounds.InnerHeight - GuiElement.scaled(bottomOffset));
                    return Enumerable.Range((int)GuiElement.scaled(4), (int)(bounds.InnerWidth - GuiElement.scaled(8)))
                        .Count(x => pixels[y * surface.Stride + x * 4 + 3] > 0) > 4;
                }
                check(HasBar(5) == durability, "Native durability must appear in trade, payment and history slot sizes when damaged.");
                check(HasBar(10) == sharpness, "Toolsmith's additional sharpness bar must follow the installed mod's native overlay patch.");
                surface.WriteToPng(System.IO.Path.Combine(output, $"{name}-{size}.png"));
                var retained = overlay.slotQuantityTextures[0];
                preview.RenderInteractiveElements(0);
                check(ReferenceEquals(retained, overlay.slotQuantityTextures[0]), "Rendering an unchanged preview must reuse its status texture.");
            }
            check(textures.Count == originalTextures, "Disposing trade previews must release all state and quantity textures.");
        }
        Preview(false, "native-condition");
        if (Environment.GetEnvironmentVariable("VSCN_TOOLSMITH_PACKAGE") is not { Length: > 0 } package) return;
        using var zip = ZipFile.OpenRead(package);
        using var source = zip.GetEntry("Toolsmith.dll")!.Open(); using var bytes = new MemoryStream();
        source.CopyTo(bytes); bytes.Position = 0;
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Toolsmith")
            ?? AssemblyLoadContext.Default.LoadFromStream(bytes);
        var harmony = new Harmony("vscncs.tests.toolsmith-overlay");
        try
        {
            var system = assembly.GetType("Toolsmith.ToolsmithModSystem")!;
            system.GetField("Config")!.SetValue(null, Activator.CreateInstance(assembly.GetType("Toolsmith.Config.ToolsmithConfigs")!));
            system.GetField("Logger")!.SetValue(null, api.Logger);
            harmony.CreateClassProcessor(assembly.GetType("Toolsmith.ToolTinkering.ToolTinkeringGuiElementPatches")!).Patch();
            item.CollectibleBehaviors = [(CollectibleBehavior)Activator.CreateInstance(
                assembly.GetType("Toolsmith.ToolTinkering.Behaviors.CollectibleBehaviorSmithedTools")!, item)!];
            stack.Attributes.SetInt("toolSharpnessCurrent", 40); stack.Attributes.SetInt("toolSharpnessMax", 100);
            var utility = assembly.GetType("Toolsmith.ToolTinkering.TinkeringUtility")!;
            using (var inventory = (GuiElement)Activator.CreateInstance(previewType,
                [api, null, ElementBounds.Fixed(0, 0, 48, 48).WithEmptyParent(), null, 1, false, false, null, false, null, null])!)
            {
                var hoverSlot = (ItemSlot)previewType.GetField("slot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(inventory)!;
                hoverSlot.Itemstack = stack.Clone();
                check(!(bool)utility.GetMethod("ShouldNotAccessStats")!.Invoke(null, [hoverSlot])!,
                    "Trade preview hover slots must allow Toolsmith to display its real condition details.");
            }
            Preview(true, "toolsmith-condition");
            // A new tool can still have reduced sharpness; Toolsmith must be allowed to draw in that case too.
            stack.Attributes.SetInt("durability", item.GetMaxDurability(stack));
            Preview(true, "toolsmith-sharpness-only", durability: false);
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }
}
