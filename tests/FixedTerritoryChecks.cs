using System.Text;
using HarmonyLib;
using Newtonsoft.Json;
using SeraphCivilizationSimulation;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

internal static class FixedTerritoryChecks
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (int x in new[] { -50, 0, 100 })
        foreach (int members in new[] { 1, 5, 100 })
        {
            var tribe = new TribeRecord { X = x, Y = 101, Z = x, Dimension = 1,
                Members = Enumerable.Range(0, members).Select(i => i.ToString()).ToList() };
            FixedTerritoryRules.Apply(tribe);
            var site = tribe.Sites.Single();
            check(site.Bounds.Area == 256 && site.Bounds.Width == 16 && site.Bounds.Length == 16,
                "Tribe size must remain exactly 16x16 at every population and negative coordinate.");
            var index = new BuildingTerritoryIndex(); index.Rebuild([tribe]);
            check(index.At(new(x - 8, 91, x - 8), 1) == tribe && index.At(new(x + 7, 100000, x + 7), 1) == tribe,
                "Fixed range must include the lower boundary and unlimited upper heights.");
            check(index.At(new(x, 90, x), 1) == null && index.At(new(x - 9, 101, x), 1) == null
                && index.At(new(x + 8, 101, x), 1) == null && index.At(new(x, 101, x), 0) == null,
                "Fixed range crossed a horizontal, depth or dimension boundary.");
            var edges = VscnCivilizationModSystem.FixedTerritoryEdges(tribe, 384);
            check(edges.Count == 4 && edges.All(e => e.BelowY == 91 && e.AboveY == 383 && !e.PendingShrink),
                "Fixed boundary must have four edges and no shrink indicators.");
        }
        var config = JsonConvert.DeserializeObject<VscnConfig>("{\"Territory\":{\"Buildings\":null,\"Rectangles\":{\"AreaPerMember\":999999}}}")!;
        config.Validate();
        string configJson = JsonConvert.SerializeObject(config);
        check(!configJson.Contains("AreaPerMember") && !configJson.Contains("Rectangles") && !configJson.Contains("Buildings"),
            "Retired territory configuration must be ignored and absent from administrator settings.");
        Server(check);
    }
    private static void Server(Action<bool, string> check)
    {
        var saved = new Dictionary<string, byte[]>(); bool failSave = false;
        var save = Proxy.Make<ISaveGame>((m, a) =>
        {
            if (m.Name == "StoreData")
            {
                if (failSave) throw new IOException("disk unavailable");
                saved[(string)a![0]!] = (byte[])a[1]!;
            }
            return m.Name == "GetData" ? saved.GetValueOrDefault((string)a![0]!) : null;
        });
        var manager = Proxy.Make<IWorldManagerAPI>((m, a) => m.Name == "get_SaveGame" ? save : null);
        var entity = new EntityPlayer(); entity.Pos.SetPos(100, 101, 100); entity.Stats = new EntityStats(entity);
        var player = Proxy.Make<IServerPlayer>((m, a) => m.Name switch
        { "get_PlayerUID" or "get_PlayerName" => "chief", "get_Entity" => entity, "get_LanguageCode" => "en", _ => null });
        var originalWorld = Proxy.Make<IServerWorldAccessor>((m, a) => m.Name == "get_AllOnlinePlayers" ? Array.Empty<IPlayer>() : null);
        var original = Proxy.Make<ICoreServerAPI>((m, a) => m.Name switch
        {
            "get_World" => originalWorld, "get_WorldManager" => manager,
            "get_PlayerData" => Proxy.Make<IPlayerDataManager>((m, a) => null),
            "get_Logger" => Proxy.Make<ILogger>((m, a) => null), _ => null
        });
        var world = new TerritoryTestWorld(original);
        // Only floor blocks: no walls, roof, room or main hall.
        foreach (int x in new[] { 100, 200, 300 }) world.Blocks[(0, x, 100, 100)] = TerritoryTestWorld.Stone;
        entity.World = world.Api.World;
        var server = new VscnCivilizationModSystem(); world.Attach(server); var type = server.GetType();
        object? Call(string method, params object?[] args) => AccessTools.Method(type, method).Invoke(server, args);
        TribeState State() => (TribeState)AccessTools.Field(type, "tribeState").GetValue(server)!;
        AccessTools.Field(type, "channel").SetValue(server, Proxy.Make<IServerNetworkChannel>((m, a) => null));
        server.Config.Tribes.AllowSoloCreation = true;
        Call("CreateTribe", player, "固定部落");
        var tribe = State().Tribes.Single();
        check(tribe.Regions.Count == 0 && tribe.Sites.Single().Bounds.Area == 256 && tribe.PopulationTerritory == null,
            "Outdoor founding must succeed without rooms or population state.");
        string before = JsonConvert.SerializeObject(tribe.Sites);
        tribe.Members.AddRange(["a", "b", "c"]); Call("SaveState"); tribe.Members.Remove("a"); Call("SaveState");
        check(JsonConvert.SerializeObject(tribe.Sites) == before && tribe.PopulationTerritory == null,
            "Member changes must neither resize territory nor schedule delayed shrink.");
        var snapshot = new CivilizationSnapshotPacket(); Call("FillBuildingTerritorySnapshot", player, snapshot, tribe);
        check(snapshot.TerritoryArea == 256 && snapshot.TerritoryBudget == 256 && snapshot.TerritoryShrinkDeadlines.Count == 0
            && snapshot.TerritoryPendingShrinkArea == 0 && !snapshot.HasMainHall, "Snapshot still exposes hall or population growth requirements.");
        entity.Pos.SetPos(200, 101, 100); Call("MoveCenter", player);
        check(tribe.X == 100, "Center migration bypassed cooldown.");
        server.Config.Territory.CenterCooldownMinutes = 0;
        Call("MoveCenter", player);
        check(tribe.X == 200 && tribe.Sites[0].Bounds.MinX == 192 && tribe.Sites[0].Revision == 2,
            "Center migration failed to atomically move the fixed range.");
        var spawn = (BlockPos)Call("TribeSpawn", tribe)!;
        check(spawn.X == 200 && spawn.Y == 101, "Respawn must follow the center without a main hall.");
        var stranger = Proxy.Make<IServerPlayer>((m, a) => m.Name switch
        { "get_PlayerUID" => "b", "get_Entity" => entity, _ => null });
        entity.Pos.SetPos(300, 101, 100); Call("MoveCenter", stranger);
        check(tribe.X == 200, "Member without migration permission moved the territory.");
        var other = new TribeRecord { Id = "other", X = 310, Y = 201, Z = 100 }; FixedTerritoryRules.Apply(other); State().Tribes.Add(other);
        Call("MoveCenter", player);
        check(tribe.X == 200, "Vertically stacked tribe ranges must still conflict.");
        State().Tribes.Remove(other);
        world.Claims.Add(new LandClaim { OwnedByPlayerUid = "stranger", Areas = [new Cuboidi(295, 250, 95, 305, 260, 105)] });
        Call("MoveCenter", player); check(tribe.X == 200, "High vanilla land claim must block an unlimited-height territory.");
        world.Claims.Clear();
        failSave = true; Call("MoveCenter", player); failSave = false;
        check(tribe.X == 200 && (bool)Call("WithinTerritory", tribe, 200d, 101d, 100d, 0)!, "Failed save must roll back center and protection index.");

        var legacy = new TribeRecord { Id = "legacy", X = 500, Y = 110, Z = 500, TerritoryVersion = 4,
            Members = ["legacy"], LeaderUid = "legacy", AllowsSolo = true, PopulationTerritory = new() { LastMemberCount = 20, TierDeadlinesUtc = new() { [20] = long.MaxValue } },
            Regions = [new() { Root = true }], Outposts = [new() { X = 800 }] };
        State().Tribes.Add(legacy);
        byte[] old = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(State()));
        Call("MigrateBuildingTerritories", old);
        check(saved.Single(p => p.Key.StartsWith("vsscsFixedTerritoryBackup-")).Value.SequenceEqual(old), "Migration must back up the exact old state.");
        check(legacy.TerritoryVersion == 5 && legacy.Sites.Single().Bounds.Area == 256 && legacy.PopulationTerritory == null
            && legacy.Regions.Count == 0 && legacy.Outposts.Count == 0 && legacy.X == 500 && legacy.Y == 110,
            "Migration must preserve the center and remove old halls, growth, shrink and outposts.");
        string migrated = JsonConvert.SerializeObject(State()); Call("MigrateBuildingTerritories", old);
        check(JsonConvert.SerializeObject(State()) == migrated && saved.Keys.Count(k => k.StartsWith("vsscsFixedTerritoryBackup-")) == 1,
            "Restart must not repeat migration or change fixed boundaries.");
        legacy.TerritoryVersion = 4; failSave = true;
        bool rejected = false; try { Call("MigrateBuildingTerritories", old); } catch { rejected = true; }
        failSave = false;
        check(rejected && legacy.TerritoryVersion == 4, "Backup failure must stop destructive migration.");
    }
}
