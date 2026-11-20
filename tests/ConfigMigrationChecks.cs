using Newtonsoft.Json;
using SeraphCivilizationSimulation;
using Vintagestory.API.Common;

internal static class ConfigMigrationChecks
{
    public static void Run(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "vsscsmod-config-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        ConfigMigration.ConfigDirectoryOverride = directory;
        int writes = 0;
        var api = Proxy.Make<ICoreAPI>((method, args) =>
        {
            if (method.Name == "StoreModConfig")
            {
                File.WriteAllText(Path.Combine(directory, (string)args![1]!), JsonConvert.SerializeObject(args[0]));
                writes++;
            }
            return null;
        });
        string main = Path.Combine(directory, ConfigMigration.Main);
        string old = Path.Combine(directory, ConfigMigration.OldMain);
        string lore = Path.Combine(directory, ConfigMigration.Lore);
        string oldLore = Path.Combine(directory, ConfigMigration.OldLore);
        try
        {
            check(ConfigMigration.LoadMain(api) == null, "Fresh configuration must not invent a migration.");
            string legacy = "{\"DisableVintageCanvasArtistClass\":false,\"ModPolicy\":{\"Mods\":[{\"ModId\":\"vscncivilizationsimulation\",\"Version\":\"1.2.18\"},{\"ModId\":\"example\",\"Version\":\"*\"}]}}";
            File.WriteAllText(old, legacy);
            var migrated = ConfigMigration.LoadMain(api)!;
            check(!migrated.DisableVintageCanvasArtistClass && migrated.ModPolicy.Mods[0].ModId == "vsscsmod"
                && migrated.ModPolicy.Mods[0].Version == "1.0.1" && migrated.ModPolicy.Mods[1].Version == "*", "Legacy configuration migration failed.");
            check(File.ReadAllText(old) == legacy && File.Exists(main) && writes == 1, "Migration changed the source or missed the new file.");
            check(!ConfigMigration.LoadMain(api)!.DisableVintageCanvasArtistClass && writes == 1, "Repeated startup imported twice.");
            File.WriteAllText(main, "{\"DisableVintageCanvasArtistClass\":true}");
            check(ConfigMigration.LoadMain(api)!.DisableVintageCanvasArtistClass && writes == 1, "Current configuration did not win.");
            File.WriteAllText(main, "{");
            try { ConfigMigration.LoadMain(api); check(false, "Invalid current config accepted."); } catch (InvalidDataException) { }
            check(File.ReadAllText(main) == "{" && File.ReadAllText(old) == legacy, "Invalid configuration was overwritten.");
            File.WriteAllText(oldLore, "{\"LivingDeadSpawns\":false}");
            check(!ConfigMigration.LoadLore(api).LivingDeadSpawns && File.Exists(lore), "Lore configuration was not migrated.");
            File.WriteAllText(lore, "{\"LivingDeadSpawns\":true}");
            check(ConfigMigration.LoadLore(api).LivingDeadSpawns, "Current lore configuration did not win.");
            File.WriteAllText(lore, "{\"LivingDeadSpawns\":0}");
            try { ConfigMigration.LoadLore(api); check(false, "Invalid lore config accepted."); } catch (InvalidDataException) { }
            check(File.ReadAllText(oldLore) == "{\"LivingDeadSpawns\":false}", "Legacy lore file was overwritten.");
            File.WriteAllText(main, "{\"ModPolicy\":{\"Mods\":[{\"ModId\":\"vsscmmod\",\"Version\":\"1.0.0\"}]}}");
            int beforeRename = writes;
            check(ConfigMigration.LoadMain(api)!.ModPolicy.Mods[0].ModId == "vsscsmod" && writes == beforeRename + 1, "Previous standalone identity must migrate in the current config.");
            check(ConfigMigration.LoadMain(api)!.ModPolicy.Mods[0].ModId == "vsscsmod" && writes == beforeRename + 1, "Identity migration must not repeat.");
        }
        finally { ConfigMigration.ConfigDirectoryOverride = null; Directory.Delete(directory, true); }
    }
}
