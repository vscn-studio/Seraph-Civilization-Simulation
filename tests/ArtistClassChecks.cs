using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using SeraphCivilizationSimulation;

internal static class ArtistClassChecks
{
    public static void Run(Action<bool, string> check)
    {
        string game = Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? "E:/vintagestory/Vintagestory";
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            foreach (string dir in new[] { game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods") })
                if (File.Exists(Path.Combine(dir, name.Name + ".dll")))
                    return context.LoadFromAssemblyPath(Path.GetFullPath(Path.Combine(dir, name.Name + ".dll")));
            return null;
        }
        AssemblyLoadContext.Default.Resolving += Resolve;
        string directory = Path.Combine(Path.GetTempPath(), "vsscsmod-artist-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        ConfigMigration.ConfigDirectoryOverride = directory;
        try { CheckNativeClasses(game, check); }
        finally { ConfigMigration.ConfigDirectoryOverride = null; Directory.Delete(directory, true); AssemblyLoadContext.Default.Resolving -= Resolve; }
    }

    private static void CheckNativeClasses(string game, Action<bool, string> check)
    {
        JToken painter = JArray.Parse("[{enabled:true,code:'canvaspainter',traits:['fleetfooted'],gear:[]}]");
        if (Environment.GetEnvironmentVariable("VSCN_VINTAGECANVAS_ZIP") is { } zipPath)
        {
            using var zip = ZipFile.OpenRead(zipPath);
            using var reader = new StreamReader(zip.GetEntry("assets/vintagecanvas/config/characterclasses.json")!.Open());
            painter = JToken.Parse(reader.ReadToEnd());
        }
        var classes = new Dictionary<AssetLocation, JToken>
        {
            [new("game:config/characterclasses.json")] = JToken.Parse(File.ReadAllText(Path.Combine(game, "assets/survival/config/characterclasses.json"))),
            [new("vintagecanvas:config/characterclasses.json")] = painter
        };
        var logger = Proxy.Make<ILogger>((m, a) => null);
        var assets = Proxy.Make<IAssetManager>((m, a) => m.Name == "GetMany" ? classes : null);
        var serverConfig = new TreeAttribute();
        var clientConfig = new TreeAttribute();
        var serverWorld = Proxy.Make<IServerWorldAccessor>((m, a) => m.Name switch
        { "get_Config" => serverConfig, "get_Logger" => logger, "get_Side" => EnumAppSide.Server, _ => null });
        var clientWorld = Proxy.Make<IWorldAccessor>((m, a) => m.Name switch
        { "get_Config" => clientConfig, "get_Logger" => logger, "get_Side" => EnumAppSide.Client, _ => null });
        var channel = Proxy.Make<IServerNetworkChannel>((m, a) => null);
        var network = Proxy.Make<IServerNetworkAPI>((m, a) => m.Name == "GetChannel" ? channel : null);
        var config = new VscnConfig();
        var serverApi = Proxy.Make<ICoreServerAPI>((m, a) => m.Name switch
        {
            "get_World" => serverWorld, "get_Assets" => assets, "get_Logger" => logger,
            "get_Side" => EnumAppSide.Server, "get_Network" => network, "LoadModConfig" => config, _ => null
        });
        var clientApi = Proxy.Make<ICoreAPI>((m, a) => m.Name switch
        {
            "get_World" => clientWorld, "get_Assets" => assets, "get_Logger" => logger,
            "get_Side" => EnumAppSide.Client,
            "LoadModConfig" => throw new Exception("Client must use server-owned policy."), _ => null
        });
        CharacterSystem Native(ICoreAPI api)
        {
            var native = new CharacterSystem { PrintLoadWarnings = false };
            AccessTools.Field(typeof(CharacterSystem), "api").SetValue(native, api);
            AccessTools.Field(typeof(CharacterSystem), "sapi").SetValue(native, api as ICoreServerAPI);
            return native;
        }
        var load = AccessTools.Method(typeof(CharacterSystem), "LoadClasses");
        var join = AccessTools.Method(typeof(CharacterSystem), "Event_PlayerJoinServer");
        var nativeServer = Native(serverApi);
        var nativeClient = Native(clientApi);
        var server = new ArtistClassRestrictionSystem();
        var client = new ArtistClassRestrictionSystem();
        try
        {
            check(config.DisableVintageCanvasArtistClass, "Artist restriction must default on for existing configurations.");
            server.Start(serverApi);
            server.AssetsFinalize(serverApi);
            client.Start(clientApi);
            client.AssetsFinalize(clientApi);
            check(!clientConfig.HasAttribute(ArtistClassRestrictionSystem.WorldKey), "Client must not overwrite server policy.");
            clientConfig.SetBool(ArtistClassRestrictionSystem.WorldKey, serverConfig.GetBool(ArtistClassRestrictionSystem.WorldKey));
            nativeServer.characterClassesByCode["canvaspainter"] = new() { Code = "canvaspainter" };
            load.Invoke(nativeServer, null);
            load.Invoke(nativeClient, null);
            check(nativeServer.characterClasses.All(c => c.Code != "canvaspainter") && !nativeServer.characterClassesByCode.ContainsKey("canvaspainter"),
                "Server must remove painter from both selection list and stale lookup.");
            check(nativeClient.characterClasses.Select(c => c.Code).SequenceEqual(nativeServer.characterClasses.Select(c => c.Code)),
                "Client and server class options must match.");
            check(nativeServer.characterClasses.Count == 6 && nativeServer.characterClasses.Any(c => c.Code == "commoner"),
                "Artist restriction must preserve all enabled vanilla professions.");

            var entity = new EntityPlayer { World = serverWorld, Api = serverApi };
            entity.Stats = new EntityStats(entity);
            AccessTools.Property(typeof(Entity), "Properties").SetValue(entity,
                new EntityProperties { Server = new EntityServerProperties([], new()) });
            entity.WatchedAttributes.SetString("characterClass", "canvaspainter");
            entity.WatchedAttributes.SetString("skinConfigTest", "unchanged");
            entity.WatchedAttributes.SetStringArray("extraTraits", ["test-extra"]);
            nativeServer.TraitsByCode["test-extra"] = new() { Code = "test-extra", Attributes = new() { ["miningSpeedMul"] = .1 } };
            entity.Stats.Set("walkspeed", "trait", .1f);
            entity.Stats.Set("walkspeed", "other-mod", .2f);
            var player = Proxy.Make<IServerPlayer>((m, a) => m.Name switch
            {
                "get_Entity" => entity, "get_PlayerUID" => "former-artist",
                "GetModdata" => SerializerUtil.Serialize(true), _ => null
            });
            join.Invoke(nativeServer, [player]);
            check(entity.WatchedAttributes.GetString("characterClass") == "commoner", "Existing Artist must migrate on native player join.");
            check(!entity.Stats["walkspeed"].ValuesByKey.ContainsKey("trait") && entity.Stats["walkspeed"].ValuesByKey.ContainsKey("other-mod"),
                "Migration must remove old profession stat sources and preserve other mods' sources.");
            check(entity.WatchedAttributes.GetString("skinConfigTest") == "unchanged" && entity.WatchedAttributes.GetStringArray("extraTraits").SequenceEqual(new[] { "test-extra" })
                && entity.Stats["miningSpeedMul"].ValuesByKey.ContainsKey("trait"), "Appearance and independent extra traits must survive migration.");
            // No player inventory behavior is installed: migration would throw if it attempted to replace clothes.
            join.Invoke(nativeServer, [player]);
            check(entity.WatchedAttributes.GetString("characterClass") == "commoner", "Migration must be safe on repeated joins.");
            nativeServer.setCharacterClass(entity, "canvaspainter", initializeGear: false);
            check(entity.WatchedAttributes.GetString("characterClass") == "commoner", "Direct class requests must not restore banned Artist.");

            server.Dispose();
            server.Dispose();
            load.Invoke(nativeClient, null);
            check(nativeClient.characterClasses.All(c => c.Code != "canvaspainter"), "Disposing local server must leave the client patch installed.");
            // A new server startup publishes false; a different world's policy must remain independent.
            config.DisableVintageCanvasArtistClass = false;
            File.WriteAllText(Path.Combine(ConfigMigration.ConfigDirectoryOverride!, ConfigMigration.Main), Newtonsoft.Json.JsonConvert.SerializeObject(config));
            server = new ArtistClassRestrictionSystem();
            server.Start(serverApi);
            server.AssetsFinalize(serverApi);
            load.Invoke(nativeServer, null);
            check(nativeServer.characterClasses.Any(c => c.Code == "canvaspainter"), "Disabling restriction must preserve third-party Artist definition.");
            nativeServer.setCharacterClass(entity, "canvaspainter", initializeGear: false);
            check(entity.WatchedAttributes.GetString("characterClass") == "canvaspainter", "Artist must work when switch is off.");
            join.Invoke(nativeServer, [player]);
            check(entity.WatchedAttributes.GetString("characterClass") == "canvaspainter", "Switch off must suppress saved-character migration.");
            load.Invoke(nativeClient, null);
            check(nativeClient.characterClasses.All(c => c.Code != "canvaspainter"), "Policies must not leak between server/client worlds.");
        }
        finally { server.Dispose(); client.Dispose(); }
        load.Invoke(nativeClient, null);
        check(nativeClient.characterClasses.Any(c => c.Code == "canvaspainter"), "Last dispose must remove the native class patch.");
    }
}
