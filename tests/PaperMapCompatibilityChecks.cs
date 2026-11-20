using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using SeraphCivilizationSimulation;

// Load the installed mod and run its real redraw method, including integrated-world patch disposal.
internal static class PaperMapCompatibilityChecks
{
    public static void Run(string package, Action<bool, string> check)
    {
        string game = Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? "E:/vintagestory/Vintagestory";
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            foreach (string directory in new[] { game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods") })
            {
                string file = Path.GetFullPath(Path.Combine(directory, name.Name + ".dll"));
                if (File.Exists(file)) return context.LoadFromAssemblyPath(file);
            }
            return null;
        }
        AssemblyLoadContext.Default.Resolving += Resolve;
        try
        {
            Verify(LoadAssembly(package), check);
        }
        finally { AssemblyLoadContext.Default.Resolving -= Resolve; }
    }

    internal static Assembly LoadAssembly(string package)
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "ImmersiveMaps");
        if (loaded != null) return loaded;
        using var zip = ZipFile.OpenRead(package);
        using var source = (zip.GetEntry("ImmersiveMaps.dll") ?? throw new InvalidDataException("Immersive Maps DLL is missing.")).Open();
        using var memory = new MemoryStream();
        source.CopyTo(memory); memory.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(memory);
    }

    private static void Verify(Assembly maps, Action<bool, string> check)
    {
        const string patchId = "vsscsmod.papermaps";
        var mapType = maps.GetType("ImmersiveMaps.Items.ItemMap", true)!;
        var draw = AccessTools.DeclaredMethod(mapType, "DrawStroke");
        var treeReader = AccessTools.Method(typeof(Vintagestory.API.Datastructures.TreeAttribute), "FromBytes", [typeof(BinaryReader)]);
        EnumAppSide side = EnumAppSide.Server;
        int sounds = 0;
        var world = Proxy.Make<IWorldAccessor>((m, a) =>
        {
            if (m.Name == "PlaySoundAt") sounds++;
            return m.Name == "get_Side" ? side : null;
        });
        bool enabled = false;
        INetworkChannel? channel = null;
        channel = Proxy.Make<INetworkChannel>((m, a) => m.Name == "RegisterMessageType" ? channel : null);
        var network = Proxy.Make<INetworkAPI>((m, a) => m.Name == "RegisterChannel" ? channel : null);
        var loader = Proxy.Make<IModLoader>((m, a) => m.Name == "IsModEnabled" ? enabled && (string)a![0]! == "immersivemaps" : null);
        var api = Proxy.Make<ICoreAPI>((m, a) => m.Name switch
        { "get_World" => world, "get_Side" => side, "get_Network" => network, "get_ModLoader" => loader, _ => null });
        var withoutMaps = new VscnCivilizationModSystem();
        try
        {
            withoutMaps.Start(api);
            check(Harmony.GetPatchInfo(draw)?.Owners.Contains(patchId) != true,
                "Disabled Immersive Maps must not install redraw compatibility.");
            check(Harmony.GetPatchInfo(treeReader)?.Owners.Contains(patchId) != true, "Disabled Immersive Maps must not patch native attribute reading.");
        }
        finally { withoutMaps.Dispose(); }
        enabled = true;
        var server = new VscnCivilizationModSystem();
        var client = new VscnCivilizationModSystem();
        try
        {
            server.Start(api); client.Start(api);
            PaperMapStorageChecks.VerifyLegacy(check);
            ImmersiveMapMarkerChecks.Run(maps, server, api, check);
            GroundMapChecks.Run(maps, api, check);
            check(Harmony.GetPatchInfo(draw)?.Postfixes.Count(p => p.owner == patchId) == 1,
                "Integrated server/client must share one paper redraw patch.");
            check(Harmony.GetPatchInfo(treeReader)?.Transpilers.Count(p => p.owner == patchId) == 1,
                "Integrated server/client must share one legacy paper migration reader.");
            var item = (Item)Activator.CreateInstance(mapType)!;
            item.Code = new AssetLocation("immersivemaps:map-papyrus"); item.ItemId = 101;
            AccessTools.Field(mapType, "api").SetValue(item, api);
            var paper = new ItemStack(item);
            paper.Attributes.SetFloat("mapRotation", .5f);
            paper.Attributes.SetBytes("waypoints", [1, 2, 3]);
            paper.Attributes.SetBytes(RecordedPaperMap.Attribute, [4, 5, 6]);
            paper.Attributes.SetString(CivilizationMapInventory.SheetIdAttribute, Guid.NewGuid().ToString("N"));
            paper.Attributes.SetInt(CivilizationMapInventory.StitchMaskAttribute, 2);
            var entity = new EntityAgent { World = world, LeftHandItemSlot = new DummySlot() };
            entity.Pos.SetPos(1234, 100, 5678);
            draw.Invoke(item, [paper, entity]);
            check(!paper.Attributes.HasAttribute(RecordedPaperMap.Attribute), "Server redraw must invalidate frozen terrain.");
            check(!paper.Attributes.HasAttribute(CivilizationMapInventory.SheetIdAttribute)
                && !paper.Attributes.HasAttribute(CivilizationMapInventory.StitchMaskAttribute), "Real map redraw must invalidate the paid sewing identity for an authoritative refund.");
            check(paper.Attributes.GetInt("mapPositionX") == 1234 && paper.Attributes.GetInt("mapPositionZ") == 5678
                && paper.Attributes.GetFloat("mapRotation") == .5f && paper.Attributes.GetBytes("waypoints").SequenceEqual(new byte[] { 1, 2, 3 })
                && sounds == 1, "Redraw compatibility must preserve the original drawing behavior, geometry and markers.");
            var largeType = mapType.Assembly.GetType("ImmersiveMaps.Items.ItemLargeMap")!;
            var largeItem = (Item)Activator.CreateInstance(largeType)!;
            largeItem.Code = new AssetLocation("immersivemaps:map-parchment"); largeItem.ItemId = 102;
            AccessTools.Field(mapType, "api").SetValue(largeItem, api);
            var parchment = new ItemStack(largeItem); parchment.Attributes.SetFloat("mapRotation", 0);
            parchment.Attributes.SetBytes(RecordedPaperMap.Attribute, [1, 2]);
            draw.Invoke(largeItem, [parchment, entity]);
            check(!parchment.Attributes.HasAttribute(RecordedPaperMap.Attribute)
                && RecordedPaperMap.Describe(parchment)?.MapWidth == 96 * ((int)largeType.GetProperty("MapSize")!.GetValue(largeItem)! - 2),
                "The installed parchment map must share redraw invalidation and use its actual visible area.");
            side = EnumAppSide.Client;
            paper.Attributes.SetBytes(RecordedPaperMap.Attribute, [7, 8, 9]);
            draw.Invoke(item, [paper, entity]);
            check(paper.Attributes.HasAttribute(RecordedPaperMap.Attribute), "Client prediction must wait for authoritative server invalidation.");
            client.Dispose(); client.Dispose();
            check(Harmony.GetPatchInfo(draw)?.Owners.Contains(patchId) == true, "Client disposal must preserve the server's paper patch.");
            check(Harmony.GetPatchInfo(treeReader)?.Owners.Contains(patchId) == true, "Client disposal must preserve the server's migration reader.");
            side = EnumAppSide.Server;
            draw.Invoke(item, [paper, entity]);
            check(!paper.Attributes.HasAttribute(RecordedPaperMap.Attribute), "Redraw must still invalidate terrain after client disposal.");
            server.Dispose();
            check(Harmony.GetPatchInfo(draw)?.Owners.Contains(patchId) != true, "Final disposal must release the paper patch.");
            check(Harmony.GetPatchInfo(treeReader)?.Owners.Contains(patchId) != true, "Final disposal must release native attribute migration.");
            var nextWorld = new VscnCivilizationModSystem();
            try
            {
                nextWorld.Start(api);
                check(Harmony.GetPatchInfo(draw)?.Postfixes.Count(p => p.owner == patchId) == 1, "The next world must reinstall the paper patch once.");
            }
            finally { nextWorld.Dispose(); }
            check(!typeof(VscnCivilizationModSystem).Assembly.GetReferencedAssemblies().Any(a => a.Name == "ImmersiveMaps"),
                "Paper compatibility must remain an optional mod integration.");
        }
        finally { client.Dispose(); server.Dispose(); }
    }
}
