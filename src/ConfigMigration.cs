using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace SeraphCivilizationSimulation;

internal static class ConfigMigration
{
    internal const string Main = "SeraphCivilizationSimulation.json";
    internal const string OldMain = "VscnCivilizationSimulation.json";
    internal const string Lore = "SeraphCivilizationLoreRestrictions.json";
    internal const string OldLore = "VscnCivilizationLoreRestrictions.json";
    internal static string? ConfigDirectoryOverride { get; set; }

    private static JObject? Read(string name)
    {
        string path = Path.Combine(ConfigDirectoryOverride ?? Path.Combine(GamePaths.DataPath, "ModConfig"), name);
        if (!File.Exists(path)) return null;
        try { return JObject.Parse(File.ReadAllText(path)); }
        catch (Exception ex) { throw new InvalidDataException($"Invalid configuration: {path}", ex); }
    }

    internal static VscnConfig? LoadMain(ICoreAPI api)
    {
        var current = Read(Main);
        var old = current == null ? Read(OldMain) : null;
        var json = current ?? old;
        if (json == null) return null;
        var config = json.ToObject<VscnConfig>() ?? throw new InvalidDataException("Empty civilization configuration.");
        config.Validate();
        bool renamed = false;
        foreach (var rule in config.ModPolicy.Mods.Where(r => r.ModId.Equals("vsscmmod", StringComparison.OrdinalIgnoreCase)))
        {
            rule.ModId = "vsscsmod";
            rule.Version = "1.0.1";
            renamed = true;
        }
        if (old != null)
        {
            foreach (var rule in config.ModPolicy.Mods.Where(r => r.ModId.Equals("vscncivilizationsimulation", StringComparison.OrdinalIgnoreCase)))
            {
                rule.ModId = "vsscsmod";
                rule.Version = "1.0.1";
            }
            api.StoreModConfig(config, Main);
        }
        else if (renamed) api.StoreModConfig(config, Main);
        return config;
    }

    internal static LoreRestrictionsConfig LoadLore(ICoreAPI api)
    {
        var current = Read(Lore);
        var old = current == null ? Read(OldLore) : null;
        var json = current ?? old;
        var config = json != null ? LoreRestrictionSystem.ParseConfig(json) : new LoreRestrictionsConfig();
        if (current == null) api.StoreModConfig(config, Lore);
        return config;
    }
}
