using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Server;
using Vintagestory.GameContent;
using SeraphCivilizationSimulation;

internal static class GuidanceChecks
{
    private sealed class PlayerEntity : EntityPlayer
    {
        public int Teleports;
        public Action? Teleported;
        public override void TeleportTo(EntityPos pos, Action? callback = null)
        { Teleports++; Pos.SetFrom(pos); Teleported = callback; }
        public override void Revive() => Alive = true;
    }

    public static void Run(Action<bool, string> check)
    {
        PlacementTrackingChecks.Run(check);
        foreach (string language in new[] { "en", "zh-cn" })
            Vintagestory.API.Config.Lang.AvailableLanguages[language] = Proxy.Make<Vintagestory.API.Config.ITranslationService>((m, a) =>
                m.Name.StartsWith("Get") && a?[0] is string text ? a.Length > 1 && a[1] is object[] { Length: > 0 } values ? string.Format(text, values) : text : null);
        Vintagestory.API.Config.Lang.ChangeLanguage("en");
        var type = typeof(VscnCivilizationModSystem);
        object? Call(object target, string method, params object?[] args) => AccessTools.Method(target.GetType(), method).Invoke(target, args);
        void Set(object target, string field, object value) => AccessTools.Field(target.GetType(), field).SetValue(target, value);
        T Get<T>(object target, string field) => (T)AccessTools.Field(target.GetType(), field).GetValue(target)!;
        var settings = new RespawnConfig();
        var record = new CivilizationIdentity();
        check(record.Remaining(settings, 0) == 3 && record.Remaining(settings, 3) == 0 && record.Remaining(settings, 99) == 0,
            "Civilization life defaults or nonnegative exhaustion failed.");
        record.AdditionalLives = 99;
        check(record.Remaining(settings, 99) == 3, "Added lives must work without resetting lifetime deaths.");
        settings.InitialLives = -1;
        check(record.Remaining(settings, 500) == -1, "Configured unlimited civilization lives failed.");
        foreach (string name in new[] { "赵", "文明", "李小龙", "O'Neil", "阿·布", "Jean-Luc", "𠮷", "Player_123", "王文明2026", "John Smith", "12345", new string('王', 32) })
            check(CivilizationIdentity.ValidName(name), "A valid Unicode name was rejected: " + name);
        foreach (string name in new[] { "", " ", " 王", "王 ", "<font>", "甲\n乙", "甲\t乙", "😀", "--", "___", new string('王', 33) })
            check(!CivilizationIdentity.ValidName(name), "An unsafe or invalid name was accepted.");
        var legacyIdentity = JsonConvert.DeserializeObject<CivilizationIdentity>("{\"Surname\":\"王\",\"GivenName\":\"文明\",\"Accepted\":true}")!;
        check(legacyIdentity.FullName == "王文明" && CivilizationIdentity.ValidName(legacyIdentity.FullName),
            "Existing split names must remain valid without renaming.");
        foreach (Action<VscnConfig> invalid in new Action<VscnConfig>[]
        {
            c => c.Respawn.InitialLives = -2, c => c.Respawn.ReincarnationChance = double.NaN,
            c => c.Respawn.ReincarnationChance = 1.01, c => c.Respawn.ReincarnationLives = 0,
            c => c.Respawn.NewbornMinimumLivingMembers = 0,
            c => c.Respawn.HomecomingMinimumChance = double.NaN, c => c.Respawn.HomecomingMinimumChance = -.01,
            c => c.Respawn.HomecomingMinimumChance = 1.01, c => c.Respawn.HomecomingChancePerExtraLivingMember = double.PositiveInfinity,
            c => c.Respawn.HomecomingChancePerExtraLivingMember = -.01, c => c.Respawn.HomecomingChancePerExtraLivingMember = 1.01,
            c => c.Respawn.ReincarnationOutsideRange = 0, c => c.Respawn.ReincarnationOutsideRange = 1001,
            c => c.Territory.ReinforcementMaximum = -1, c => c.Territory.ReinforcementPerLivingMember = -1
        })
        {
            var config = new VscnConfig(); invalid(config);
            try { config.Validate(); check(false, "Invalid guidance/life settings were accepted."); }
            catch (ArgumentException) { check(true, "Invalid configuration rejected."); }
        }
        var inherited = JsonConvert.DeserializeObject<VscnConfig>("{\"Respawn\":{\"ProtectionSeconds\":10}}")!;
        inherited.Validate();
        check(inherited.Respawn.InitialLives == 3 && inherited.Respawn.NewbornMinimumLivingMembers == 5
            && inherited.Respawn.ReincarnationChance == 1
            && inherited.Respawn.ReincarnationLives == 3 && inherited.Territory.ReinforcementPerLivingMember == 10
            && inherited.Respawn.HomecomingMinimumChance == .5 && inherited.Respawn.HomecomingChancePerExtraLivingMember == .1
            && inherited.Territory.ReinforcementMaximum == 200 && inherited.Respawn.ReincarnationOutsideRange == 10, "Old config must inherit all approved defaults.");
        check(JsonConvert.DeserializeObject<RespawnConfig>("{\"ReincarnationRadius\":80}")!.ReincarnationOutsideRange == 10,
            "The old centre radius must not become the new outside width.");
        var oldTiming = JsonConvert.DeserializeObject<VscnConfig>("{\"Respawn\":{\"ReincarnationIntervalMinutes\":0}}")!;
        oldTiming.Validate();
        var oldWaiting = JsonConvert.DeserializeObject<CivilizationIdentity>("{\"NextReincarnationUtc\":9999999999,\"AdditionalLives\":4}")!;
        check(!JsonConvert.SerializeObject(oldTiming).Contains("ReincarnationIntervalMinutes")
            && !JsonConvert.SerializeObject(oldWaiting).Contains("NextReincarnationUtc") && oldWaiting.AdditionalLives == 4,
            "Legacy timing fields must be ignored without losing identity data or blocking config upgrades.");

        var system = new VscnCivilizationModSystem();
        var entity = new PlayerEntity { Alive = false };
        entity.Stats = new EntityStats(entity);
        var data = new ServerWorldPlayerData { Deaths = 3 };
        var packets = new List<GuidancePacket>();
        var saved = new Dictionary<string, byte[]>();
        bool failSave = false;
        var queued = new Queue<Action>();
        var loaded = new Queue<ChunkLoadOptions>();
        bool safe = false;
        string? disconnected = null;
        int dirty = 0, blockId = 27, sounds = 0, chatMessages = 0;
        var nativeReinforcement = new ModSystemBlockReinforcement();
        var chunkData = new Dictionary<string, byte[]>();
        var chunk = Proxy.Make<IWorldChunk>((m, a) =>
        {
            if (m.Name == "GetModdata") return chunkData.GetValueOrDefault((string)a![0]!);
            if (m.Name == "SetModdata") chunkData[(string)a![0]!] = (byte[])a[1]!;
            return null;
        });
        Block CurrentBlock()
        {
            var block = new Block { BlockId = blockId, Replaceable = 10000 };
            var behavior = new BlockBehaviorReinforcable(block);
            block.BlockBehaviors = [behavior]; block.CollectibleBehaviors = [behavior];
            return block;
        }
        var blocks = Proxy.Make<IBlockAccessor>((m, a) => m.Name switch
        {
            "GetTerrainMapheightAt" => 100, "IsSideSolid" => safe,
            "GetBlock" => CurrentBlock(), "GetChunkAtBlockPos" or "GetChunk" => chunk,
            "MarkBlockDirty" => Dirty(), _ => null
        });
        object? Dirty() { dirty++; return null; }
        var player = Proxy.Make<IServerPlayer>((m, a) => m.Name switch
        {
            "get_PlayerUID" or "get_PlayerName" => "waiting", "get_Entity" => entity,
            "get_WorldData" => data, "get_LanguageCode" => "zh-cn",
            "get_ConnectionState" => EnumClientState.Playing, "Disconnect" => Kick((string)a![0]!),
            "SendMessage" => Message(), _ => null
        });
        object? Message() { chatMessages++; return null; }
        object? Kick(string reason) { disconnected = reason; return null; }
        IPlayer[] online = [player];
        ICoreServerAPI? api = null;
        var world = Proxy.Make<IServerWorldAccessor>((m, a) => m.Name switch
        {
            "get_AllOnlinePlayers" => online, "get_BlockAccessor" => blocks,
            "get_Config" => new TreeAttribute(), "get_Side" => EnumAppSide.Server, "get_Api" => api,
            "PlaySoundAt" => Sound(), _ => null
        });
        object? Sound() { sounds++; return null; }
        var save = Proxy.Make<ISaveGame>((m, a) =>
        {
            if (m.Name == "GetData") return saved.GetValueOrDefault((string)a![0]!);
            if (m.Name == "StoreData")
            {
                if (failSave) throw new IOException("test save failure");
                saved[(string)a![0]!] = (byte[])a[1]!;
            }
            return null;
        });
        var manager = Proxy.Make<IWorldManagerAPI>((m, a) =>
        {
            if (m.Name == "LoadChunkColumnPriority") loaded.Enqueue((ChunkLoadOptions)a![2]!);
            return m.Name switch { "get_SaveGame" => save, "get_MapSizeX" or "get_MapSizeZ" => 10000, "get_MapSizeY" => 384, _ => null };
        });
        var events = Proxy.Make<IServerEventAPI>((m, a) =>
        { if (m.Name == "EnqueueMainThreadTask") queued.Enqueue((Action)a![0]!); return m.Name == "RegisterGameTickListener" ? 1L : null; });
        var logger = Proxy.Make<ILogger>((m, a) => null);
        var loader = Proxy.Make<IModLoader>((m, a) => m.Name == "IsModEnabled" ? false : m.Name == "GetModSystem" ? nativeReinforcement : null);
        var accountData = new Dictionary<string, IServerPlayerData>();
        var playerData = Proxy.Make<IPlayerDataManager>((m, a) => m.Name == "get_PlayerDataByUid" ? accountData : null);
        api = Proxy.Make<ICoreServerAPI>((m, a) => m.Name switch
        { "get_World" => world, "get_WorldManager" => manager, "get_Event" => events, "get_Logger" => logger, "get_ModLoader" => loader, "get_PlayerData" => playerData, "get_Side" => EnumAppSide.Server, _ => null });
        Set(nativeReinforcement, "api", api);
        var channel = Proxy.Make<IServerNetworkChannel>((m, a) =>
        { if (m.Name == "SendPacket" && a?[0] is GuidancePacket packet) packets.Add(packet); return null; });
        entity.World = world;
        AccessTools.Property(typeof(Entity), "Properties").SetValue(entity,
            new EntityProperties { Server = new EntityServerProperties([], new()) });
        Set(system, "sapi", api); Set(system, "channel", channel);
        Get<HashSet<string>>(system, "readyPlayers").Add("waiting");
        var life = Get<CivilizationLifeState>(system, "lifeState");
        var tribe = new TribeRecord { Id = "tribe", Name = "新生", LeaderUid = "a", Members = ["a", "b", "c", "d", "e", "waiting"], X = 1000, Y = 101, Z = 1000 };
        var tribes = Get<TribeState>(system, "tribeState"); tribes.Tribes.Add(tribe);
        tribe.Sites = [FixedTerritoryRules.Create(tribe)]; TerritoryTestWorld.Index(system);
        foreach (var uid in tribe.Members) life.Players[uid] = new() { Accepted = true, Surname = "王", GivenName = "人" };
        life.Players["waiting"].Accepted = false;
        life.Players["waiting"].Living = false;
        check((int)Call(system, "LivingMembers", tribe)! == 5, "Only living tribe members should qualify, including offline survivors.");
        life.Players["e"].Living = false;
        check((int)Call(system, "LivingMembers", tribe)! == 4, "Offline deceased members must not qualify.");
        life.Players["e"].Living = true;
        var destinationA = new TribeRecord { Id = "destination-a", Members = ["other-a"] };
        var destinationB = new TribeRecord { Id = "destination-b", Members = ["other-b"] };
        TribeRecord Select(string uid, List<TribeRecord> choices, double roll) =>
            (TribeRecord)Call(system, "SelectReincarnationTribe", uid, choices, roll)!;
        int[] Distribution(string uid, List<TribeRecord> choices)
        {
            var counts = new int[choices.Count];
            for (int i = 0; i < 1000; i++) counts[choices.IndexOf(Select(uid, choices, (i + .5) / 1000))]++;
            return counts;
        }
        for (int living = 5; living <= 11; living++)
        {
            string[] extra = Enumerable.Range(5, living - 5).Select(i => "extra-" + i).ToArray();
            tribe.Members.AddRange(extra);
            foreach (string uid in extra) life.Players[uid] = new() { Living = true };
            double expected = Math.Min(1, .5 + (living - 5) * .1);
            check(Math.Abs(system.Config.Respawn.HomecomingChance((int)Call(system, "LivingMembers", tribe)!) - expected) < 1e-9,
                "Homecoming chance must use living members beyond the newborn threshold and cap at 100%.");
            var counts = Distribution("waiting", [tribe, destinationA, destinationB]);
            check(counts[0] == (int)Math.Round(expected * 1000) && counts[1] == counts[2] && counts.Sum() == 1000,
                "The home tribe must receive exactly its configured share; only the remainder is shared by other tribes.");
            var effects = (List<TerritoryEffectView>)Call(system, "BuildCivilizationEffects", player, true, false)!;
            check(effects.Any(e => e.Code == "effect-newborn")
                && Math.Abs(effects.Single(e => e.Code == "effect-homecoming").Value - expected * 100) < .001,
                "The tribe page must display the newborn and homecoming buffs with the current population's chance.");
            foreach (string uid in extra) { tribe.Members.Remove(uid); life.Players.Remove(uid); }
        }
        check(Select("waiting", [tribe, destinationA], 0) == tribe
            && Select("waiting", [tribe, destinationA], .499999) == tribe
            && Select("waiting", [tribe, destinationA], .5) == destinationA
            && Select("waiting", [tribe, destinationA], .999999) == destinationA,
            "The minimum homecoming share must switch destinations at exactly 50%, with no extra uniform draw of the home tribe.");
        check(Distribution("no-tribe", [tribe, destinationA]).SequenceEqual(new[] { 500, 500 })
            && Distribution("waiting", [destinationA, destinationB]).SequenceEqual(new[] { 500, 500 }),
            "Unaligned players and players whose tribe does not qualify must select eligible tribes uniformly.");
        check(Select("waiting", [tribe], .99) == tribe && Select("waiting", [destinationA], .99) == destinationA,
            "A sole eligible tribe must be selected regardless of homecoming chance or membership.");
        life.Players["e"].Living = false;
        check(system.Config.Respawn.HomecomingChance((int)Call(system, "LivingMembers", tribe)!) == 0
            && !((List<TerritoryEffectView>)Call(system, "BuildCivilizationEffects", player, true, false)!).Any(e => e.Code is "effect-newborn" or "effect-homecoming"),
            "Both buffs must disappear when living members fall below the newborn threshold; a deceased waiting member must not qualify.");
        life.Players["e"].Living = true;
        system.Config.Respawn.HomecomingMinimumChance = .3;
        system.Config.Respawn.HomecomingChancePerExtraLivingMember = .2;
        system.Config.Respawn.NewbornMinimumLivingMembers = 3;
        check(Math.Abs(system.Config.Respawn.HomecomingChance(5) - .7) < 1e-9
            && Distribution("waiting", [destinationA, tribe, destinationB]).SequenceEqual(new[] { 150, 700, 150 }),
            "Configured thresholds, base chances and increments must drive actual destination selection regardless of tribe ordering.");
        system.Config.Respawn.HomecomingMinimumChance = 1;
        check(Select("waiting", [tribe, destinationA], .999999) == tribe, "A configured 100% home chance must always return home.");
        system.Config.Respawn.HomecomingMinimumChance = 0;
        system.Config.Respawn.HomecomingChancePerExtraLivingMember = 0;
        check(Distribution("waiting", [tribe, destinationA, destinationB]).SequenceEqual(new[] { 0, 500, 500 }),
            "A configured zero home share must leave the whole probability to other eligible tribes.");
        system.Config.Respawn.HomecomingMinimumChance = .5;
        system.Config.Respawn.HomecomingChancePerExtraLivingMember = .1;
        system.Config.Respawn.NewbornMinimumLivingMembers = 5;
        system.Config.Respawn.ReincarnationOutsideRange = 10;
        (int X, int Z)[] Candidates() => ((IEnumerable<(int X, int Z)>)Call(system, "ReincarnationCandidates", tribe)!).ToArray();
        bool Outside((int X, int Z) point) => (bool)Call(system, "IsReincarnationOutside", tribe, point.X, point.Z)!;
        var ring = Candidates().Where(Outside).ToArray();
        check(ring.Length > 0 && ring.All(p => tribe.Sites.SelectMany(s => s.Surface()).Min(f => Math.Sqrt(Math.Pow(p.X-f.X,2)+Math.Pow(p.Z-f.Z,2))) <= 10),
            "Reincarnation must use registered building edges and configured outside distance.");
        tribe.Dimension = 1;
        check(!Outside(ring[0]) && Candidates().Length == 0, "Non-main dimensions must not use main-world landing candidates.");
        tribe.Dimension = 0;
        var foreign = new TribeRecord { Id = "spawn-obstacle", X = ring[0].X, Y = 101, Z = ring[0].Z, Members = ["foreign"] };
        var obstacle = TerritoryTestWorld.Region(foreign); tribes.Tribes.Add(foreign); TerritoryTestWorld.Index(system);
        check(!Outside(ring[0]), "Loaded landing validation must exclude other registered regions.");
        obstacle.Dimension = 1; TerritoryTestWorld.Index(system);
        check(Outside(ring[0]), "Another dimension must not block reincarnation.");
        tribes.Tribes.Remove(foreign); TerritoryTestWorld.Index(system);
        foreach (int width in new[] { 1, 10, 1000 })
        {
            system.Config.Respawn.ReincarnationOutsideRange = width;
            check(Candidates().Any(Outside), "Building edge candidates must support narrow and wide outside ranges.");
        }
        system.Config.Respawn.ReincarnationOutsideRange = 10;
        Get<HashSet<string>>(system, "readyPlayers").Remove("waiting");
        Call(system, "SendGuidance", player, "");
        var offer = packets.Last();
        check(offer.Required && offer.Nonce.Length > 0 && offer.LivesLeft == 0 && offer.RulesHtml.Length == 0 && offer.Settlement.Length == 0,
            "New players need a name challenge and authoritative lives, without rules or settlement content.");
        Call(system, "OnGuidanceDecision", player, new GuidanceDecision { Nonce = "forged", Agree = false });
        check(disconnected == null, "A stale guidance decision must not kick a player.");
        Call(system, "OnGuidanceDecision", player, new GuidanceDecision { Nonce = offer.Nonce, Agree = true, GameName = "<bad>名" });
        check(!life.Players["waiting"].Accepted && packets.Last().Error.Length > 0, "Invalid names must keep onboarding open.");
        Call(system, "OnGuidanceDecision", player, new GuidanceDecision { Nonce = offer.Nonce, Agree = true, GameName = "王人" });
        check(!life.Players["waiting"].Accepted && packets.Last().Error.Contains("已被使用"),
            "A game name must not duplicate an existing split name.");
        life.Players["reserved-name"] = new() { GameName = "Player_123", Accepted = true };
        foreach (bool accepted in new[] { true, false })
        {
            life.Players["reserved-name"].Accepted = accepted;
            foreach (string name in new[] { "Player_123", "player_123", "Ｐｌａｙｅｒ_１２３" })
            {
                Call(system, "OnGuidanceDecision", player, new GuidanceDecision { Nonce = offer.Nonce, Agree = true, GameName = name });
                check(!life.Players["waiting"].Accepted && packets.Last().Error.Contains("已被使用"),
                    "Offline and pending-reset names must reserve exact, case and compatibility variants: " + name);
            }
        }
        life.Players.Remove("reserved-name");
        Call(system, "OnGuidanceDecision", player, new GuidanceDecision { Nonce = offer.Nonce, Agree = false });
        check(disconnected == null && !life.Players["waiting"].Accepted, "Legacy declines must neither disconnect nor accept the player.");
        Call(system, "OnGuidanceDecision", player, new GuidanceDecision { Nonce = offer.Nonce, Agree = true, GameName = "王文明_2026" });
        check(life.Players["waiting"].Accepted && !packets.Last().Required && (string)Call(system, "PlayerName", "waiting")! == "王文明_2026"
            && life.Players["waiting"].GameName == "王文明_2026" && life.Players["waiting"].Surname.Length == 0 && life.Players["waiting"].GivenName.Length == 0,
            "Accept must persist the chosen identity and release the modal.");
        Get<HashSet<string>>(system, "readyPlayers").Add("waiting");
        var acceptedIdentity = life.Players["waiting"];
        life.Players["waiting"] = legacyIdentity;
        Call(system, "SendGuidance", player, "");
        check(!packets.Last().Required, "Accepted legacy names must not trigger another onboarding prompt.");
        life.Players["waiting"] = acceptedIdentity;
        check(JsonConvert.DeserializeObject<CivilizationIdentity>(JsonConvert.SerializeObject(acceptedIdentity))!.FullName == "王文明_2026",
            "A game name must survive save serialization.");
        using (var decisionStream = new MemoryStream())
        {
            Serializer.Serialize(decisionStream, new GuidanceDecision { GameName = "王文明_2026", Nonce = "name-test", Agree = true });
            decisionStream.Position = 0;
            check(Serializer.Deserialize<GuidanceDecision>(decisionStream).GameName == "王文明_2026", "Single game name protocol roundtrip failed.");
        }

        TextCommandResult Command(string command, IServerPlayer? caller = null)
        {
            var parser = Proxy.Make<ICommandArgumentParser>((m, a) => m.Name == "GetValue" ? command : null);
            var commandCaller = new Caller { Type = caller == null ? EnumCallerType.Console : EnumCallerType.Player };
            if (caller != null) commandCaller.Player = caller;
            return (TextCommandResult)Call(system, "HandleCommand", new TextCommandCallingArgs { Caller = commandCaller, Parsers = [parser] })!;
        }
        var admin = Proxy.Make<IServerPlayer>((m, a) => m.Name == "HasPrivilege" && a?[0] is string privilege
            ? privilege == Privilege.controlserver : null);
        string originalIdentity = JsonConvert.SerializeObject(acceptedIdentity);
        string originalTribe = JsonConvert.SerializeObject(tribe);
        int packetCount = packets.Count;
        check(Command("name reset waiting", player).Status == EnumCommandStatus.Error
            && acceptedIdentity.Accepted && packets.Count == packetCount, "Ordinary players must not reset names.");
        foreach (string command in new[] { "name", "name reset", "name other waiting", "name reset unknown", "name reset 王人" })
            check(Command(command).Status == EnumCommandStatus.Error && acceptedIdentity.Accepted && packets.Count == packetCount,
                "Invalid or ambiguous name reset must not mutate identities: " + command);
        failSave = true;
        check(Command("name reset waiting").Status == EnumCommandStatus.Error && acceptedIdentity.Accepted
            && packets.Count == packetCount && !Get<Dictionary<string, string>>(system, "guidanceOffers").ContainsKey("waiting"),
            "A failed save must restore acceptance without opening a new window.");
        failSave = false;
        check(Command("name reset 王文明_2026", admin).Status == EnumCommandStatus.Success && packets.Last().Required
            && JsonConvert.SerializeObject(acceptedIdentity) == originalIdentity.Replace("\"Accepted\":true", "\"Accepted\":false")
            && JsonConvert.SerializeObject(tribe) == originalTribe && data.Deaths == 3,
            "An administrator must reopen guidance by game name without changing lives, membership or spawn data.");
        string resetNonce = packets.Last().Nonce;
        packetCount = packets.Count;
        failSave = true;
        check(Command("name reset waiting").Status == EnumCommandStatus.Error && !acceptedIdentity.Accepted
            && packets.Count == packetCount && Get<Dictionary<string, string>>(system, "guidanceOffers")["waiting"] == resetNonce,
            "A failed repeated reset must preserve the outstanding challenge and pending acceptance.");
        failSave = false;
        check(Command("name reset WAITING").Status == EnumCommandStatus.Success && packets.Last().Nonce != resetNonce,
            "Console resets must match account names without case and rotate the challenge on every reset.");
        Call(system, "OnGuidanceDecision", player, new GuidanceDecision { Nonce = resetNonce, Agree = true, GameName = "过期提交" });
        check(!acceptedIdentity.Accepted && acceptedIdentity.GameName == "王文明_2026", "A stale window must not bypass a repeated administrator reset.");
        Call(system, "OnGuidanceDecision", player, new GuidanceDecision { Nonce = packets.Last().Nonce, Agree = true, GameName = "改名 玩家" });
        check(acceptedIdentity.Accepted && acceptedIdentity.GameName == "改名 玩家" && !packets.Last().Required,
            "A reset player must be able to choose a new game name and close the guidance window by accepting rules.");
        var contender = Proxy.Make<IServerPlayer>((m, a) => m.Name switch
        { "get_PlayerUID" or "get_PlayerName" => "contender", "get_WorldData" => data, "get_LanguageCode" => "zh-cn", _ => null });
        Get<HashSet<string>>(system, "readyPlayers").Add("contender");
        Call(system, "SendGuidance", contender, "");
        Call(system, "OnGuidanceDecision", contender, new GuidanceDecision { Nonce = packets.Last().Nonce, Agree = true, GameName = "改名 玩家" });
        check(!life.Players["contender"].Accepted && packets.Last().Error.Contains("已被使用"),
            "The next submission must reject the previous winner's name immediately.");
        Get<HashSet<string>>(system, "readyPlayers").Remove("contender");
        Get<Dictionary<string, string>>(system, "guidanceOffers").Remove("contender");
        life.Players.Remove("contender");

        var offlineIdentity = new CivilizationIdentity
        {
            GameName = "离线 玩家", Accepted = true, AdditionalLives = 6, LastDeaths = 9, Living = false,
            RebornTribe = "新生", RebornSpawn = new() { X = 101, Y = 102, Z = 103 },
            ReincarnationHome = new() { X = 201, Y = 202, Z = 203 }
        };
        life.Players["offline"] = offlineIdentity;
        accountData["offline"] = Proxy.Make<IServerPlayerData>((m, a) => m.Name switch
        { "get_PlayerUID" => "offline", "get_LastKnownPlayername" => "OfflineAccount", _ => null });
        accountData["other-world"] = Proxy.Make<IServerPlayerData>((m, a) => m.Name switch
        { "get_PlayerUID" => "other-world", "get_LastKnownPlayername" => "OtherWorldAccount", _ => null });
        check(Command("name reset OtherWorldAccount").Status == EnumCommandStatus.Error && !life.Players.ContainsKey("other-world"),
            "Server-wide player data must not create an offline identity from another world.");
        string offlineBefore = JsonConvert.SerializeObject(offlineIdentity);
        packetCount = packets.Count;
        check(Command("name reset 离线 玩家").Status == EnumCommandStatus.Success && packets.Count == packetCount
            && JsonConvert.SerializeObject(offlineIdentity) == offlineBefore.Replace("\"Accepted\":true", "\"Accepted\":false"),
            "Resetting an offline game name with spaces must preserve all life and pending reincarnation fields.");
        offlineIdentity.Accepted = true;
        check(Command("name reset offlineaccount").Status == EnumCommandStatus.Success && !offlineIdentity.Accepted,
            "Offline account names must be matched in the current world.");
        var restoredNames = new VscnCivilizationModSystem(); Set(restoredNames, "sapi", api);
        Call(restoredNames, "LoadCivilizationLives");
        check(!Get<CivilizationLifeState>(restoredNames, "lifeState").Players["offline"].Accepted,
            "An offline name reset must survive reloading world data.");
        var offlinePlayer = Proxy.Make<IServerPlayer>((m, a) => m.Name switch
        { "get_PlayerUID" => "offline", "get_PlayerName" => "OfflineAccount", "get_WorldData" => data, "get_LanguageCode" => "zh-cn", _ => null });
        Get<HashSet<string>>(system, "readyPlayers").Add("offline");
        Call(system, "SendGuidance", offlinePlayer, "");
        check(packets.Last().Required && packets.Last().Title == "欢迎转生 新生 部落", "An offline reset must reopen guidance when the player becomes ready, preserving reincarnation context.");
        // A UID takes precedence over another player's matching display name.
        acceptedIdentity.GameName = "offline";
        offlineIdentity.Accepted = true;
        check(Command("name reset offline").Status == EnumCommandStatus.Success && !offlineIdentity.Accepted && acceptedIdentity.Accepted,
            "An exact UID must select its owner even when another player uses that display name.");
        Get<HashSet<string>>(system, "readyPlayers").Remove("offline");
        Get<Dictionary<string, string>>(system, "guidanceOffers").Remove("offline");
        life.Players.Remove("offline"); accountData.Clear();
        acceptedIdentity.GameName = "王文明_2026";

        var previous = AccessTools.Field(type, "instance").GetValue(null);
        Set(system, "instance", system);
        try
        {
            Call(system, "InstallRespawnIntegration"); Call(system, "StartGuidance");
            foreach (string method in new[] { "OnPlayerRespawn", "HandleSpecialKey", "EventManager_OnPlayerChat" })
                check(Harmony.GetPatchInfo(AccessTools.Method(typeof(ServerSystemEntitySimulation), method))?.Owners.Contains("vsscsmod.respawn") == true,
                    "Native server identity/respawn integration was not installed: " + method);
            ChatIdentityChecks.Run(life, check);
            check(!(bool)Call(system, "BeforeNativeRespawn", player)!, "Exhausted server respawn must be rejected.");
            life.Players["waiting"].AdditionalLives = 3;
            check((bool)Call(system, "BeforeNativeRespawn", player)!, "Restored civilization lives must allow native respawn.");
            life.Players["waiting"].AdditionalLives = 0;

            void Entry() => Call(system, "TryEntryReincarnation", player);
            void Reenter() { Call(system, "EndReincarnationSession", player); Entry(); }
            tribe.Authorizations.Add(new() {Id="old-member-grant",Uid="waiting",ExpiresAt=-1,Permissions=TribePermission.Invite});
            tribe.AuthorizationHistory.Add(new() {Id="old-member-grant",Uid="waiting",ExpiresAt=-1});
            var grantingTribe=new TribeRecord {Id="granting-tribe",LeaderUid="grantor",Members=["grantor"]};
            grantingTribe.Authorizations.Add(new() {Id="pending-old-letter",Uid="waiting",Pending=true,ExpiresAt=0});
            grantingTribe.AuthorizationHistory.Add(new() {Id="pending-old-letter",Uid="waiting",Status="pending",ContractText="[time=30,auth=build,name=Old Name]"});
            grantingTribe.Authorizations.Add(new() {Id="unrelated",Uid="someone-else",ExpiresAt=-1});
            tribes.Tribes.Add(grantingTribe);
            Call(system, "TickCivilizationLives");
            check(loaded.Count == 0, "Online ticks must never initiate reincarnation.");
            entity.Alive = true; Entry(); entity.Alive = false;
            Call(system, "OnCivilizationDeath", player, new DamageSource()); Entry(); Call(system, "TickCivilizationLives");
            check(loaded.Count == 0, "Death after a living entry must wait until the next entry.");
            life.Players["waiting"].AdditionalLives = 3; Reenter();
            check(loaded.Count == 0, "Players with remaining lives must use normal respawn.");
            life.Players["waiting"].AdditionalLives = 0;
            system.Config.Respawn.InitialLives = -1; Reenter();
            check(loaded.Count == 0, "Unlimited lives must never initiate reincarnation.");
            system.Config.Respawn.InitialLives = 3;
            life.Players["e"].Living = false; Reenter();
            life.Players["e"].Living = true; Entry(); Call(system, "TickCivilizationLives");
            check(loaded.Count == 0, "A tribe qualifying after entry must not trigger an online retry.");
            system.Config.Respawn.ReincarnationChance = 0; Reenter();
            check(entity.Teleports == 0 && loaded.Count == 0,
                "Failed entry roll must not teleport or load terrain.");
            system.Config.Respawn.ReincarnationChance = new RespawnConfig().ReincarnationChance;
            Entry(); Call(system, "TickCivilizationLives");
            check(loaded.Count == 0, "Duplicate entry and ticks must not retry a failed roll in the same session.");
            Reenter(); Entry();
            check(loaded.Count == 1 && entity.Teleports == 0 && life.Players["waiting"].AdditionalLives == 0,
                "A reincarnation roll must validate terrain before granting lives.");
            int limit = 100;
            while (loaded.Count > 0 && limit-- > 0) { loaded.Dequeue().OnLoaded(); while (queued.Count > 0) queued.Dequeue()(); }
            check(entity.Teleports == 0 && life.Players["waiting"].AdditionalLives == 0 && tribe.Members.Contains("waiting"),
                "Unsafe terrain must preserve exhaustion and original affiliation.");
            check(tribe.Authorizations.Any(a=>a.Uid=="waiting") && grantingTribe.Authorizations.Any(a=>a.Uid=="waiting"),
                "Failed reincarnation must preserve existing authorizations.");
            Entry(); Call(system, "TickCivilizationLives");
            check(loaded.Count == 0, "Unsafe terrain must not schedule another attempt while online.");
            safe = true; Reenter();
            Get<HashSet<string>>(system, "readyPlayers").Remove("waiting");
            loaded.Dequeue().OnLoaded(); while (queued.Count > 0) queued.Dequeue()();
            check(entity.Teleports == 0 && life.Players["waiting"].AdditionalLives == 0,
                "Disconnecting during terrain validation must cancel reincarnation without granting lives.");
            Get<HashSet<string>>(system, "readyPlayers").Add("waiting");
            Reenter();
            var staleSearch = loaded.Dequeue();
            Reenter();
            staleSearch.OnLoaded(); while (queued.Count > 0) queued.Dequeue()();
            check(loaded.Count == 1 && Get<HashSet<string>>(system, "reincarnating").Contains("waiting")
                && life.Players["waiting"].AdditionalLives == 0,
                "A previous session callback must neither grant lives nor cancel the new entry search.");
            var blockingTribe = new TribeRecord { Id = "late-occupation", X = 980, Y = 101, Z = 980 };
            TerritoryTestWorld.Region(blockingTribe, 50); tribes.Tribes.Add(blockingTribe); TerritoryTestWorld.Index(system);
            loaded.Dequeue().OnLoaded(); while (queued.Count > 0) queued.Dequeue()();
            check(entity.Teleports == 0 && life.Players["waiting"].AdditionalLives == 0,
                "New registered territory during loading must cancel landing without granting lives.");
            while (loaded.Count > 0) { loaded.Dequeue().OnLoaded(); while (queued.Count > 0) queued.Dequeue()(); }
            tribes.Tribes.Remove(blockingTribe); TerritoryTestWorld.Index(system);
            Reenter();
            loaded.Dequeue().OnLoaded(); while (queued.Count > 0) queued.Dequeue()();
            check(entity.Teleports == 1 && life.Players["waiting"].Remaining(system.Config.Respawn, data.Deaths) == 3
                && tribes.FindMember("waiting") == null && life.Players["waiting"].RebornSpawn != null,
                "Safe reincarnation must grant three lives, clear old membership and persist the pending landing.");
            check(tribes.Tribes.All(t=>t.Authorizations.All(a=>a.Uid!="waiting"))
                && tribe.AuthorizationHistory.Single(h=>h.Id=="old-member-grant").Status=="reincarnated"
                && grantingTribe.AuthorizationHistory.Single().Status=="reincarnated"
                && grantingTribe.Authorizations.Single().Uid=="someone-else",
                "Successful reincarnation must cancel all active and pending recipient grants across tribes, preserving other players.");
            var landing = life.Players["waiting"].RebornSpawn!;
            check(Outside((landing.X, landing.Z)) && landing.Y == 101,
                "The actual reincarnation must land safely in the outside annulus, not merely generate outside candidates.");
            check(!life.Players["waiting"].Accepted && life.Players["waiting"].RebornTribe == "新生" && life.Players["waiting"].FullName.Length == 0,
                "Reincarnation must clear both current and legacy names for fresh naming and rule acceptance.");
            Reenter(); Entry(); Call(system, "TickCivilizationLives");
            check(entity.Teleports == 1 && loaded.Count == 0 && life.Players["waiting"].AdditionalLives == 3,
                "A pending saved landing on reentry must not grant lives or search again.");
            entity.Teleported!();
            check(entity.Alive && life.Players["waiting"].RebornSpawn == null && packets.Last().Title == "欢迎转生 新生 部落",
                "Completed reincarnation must revive near the tribe and show the reincarnation modal.");
            Call(system, "TickCivilizationLives");
            check(entity.Teleports == 1 && data.Deaths == 3 && tribes.FindMember("waiting") == null,
                "Reincarnation must not reset lifetime deaths, autojoin or grant twice.");
            Call(system, "SaveCivilizationLives");
            var restarted = new VscnCivilizationModSystem(); Set(restarted, "sapi", api); Call(restarted, "LoadCivilizationLives");
            check(Get<CivilizationLifeState>(restarted, "lifeState").Players["waiting"].ReincarnationHome != null,
                "Reincarnation home must survive restart.");

            var selection = new BlockSelection { Position = new BlockPos(1001, 100, 1001) };
            var nativeBehavior = new BlockBehaviorReinforcable(CurrentBlock());
            Call(system, "RecordBuilding", player, selection.Position);
            string pendingInfo = nativeBehavior.GetPlacedBlockInfo(world, selection.Position, player);
            check(string.IsNullOrEmpty(pendingInfo) || !pendingInfo.Contains("ui.reinforcement-pending") && !pendingInfo.Contains("Strength:"),
                "Outsider construction must have no timer or tribal strength.");
            Call(system, "ClearBuildingState", selection.Position);
            EnumHandling handling = EnumHandling.PassThrough;
            void CallBreak()
            {
                int oldId = blockId;
                handling = EnumHandling.PassThrough;
                nativeBehavior.OnBlockBroken(world, selection.Position, player, 1, ref handling);
                // The server raises DidBreakBlock even when reinforcement keeps the block in place.
                Call(system, "OnTerritoryBroken", player, oldId, selection);
            }
            int messagesBefore = chatMessages;
            check(nativeBehavior.GetMiningSpeedModifier(world, selection.Position, player) == .6f,
                "Tribal protection must use the native reinforced mining speed.");
            check(nativeBehavior.GetPlacedBlockInfo(world, selection.Position, player).Contains("Strength: 50"),
                "Native block info must show the real effective reinforcement strength.");
            check(!BlockBehaviorReinforcable.AllowRightClickPickup(world, selection.Position, player),
                "Tribal reinforcement must use native pickup prevention.");
            CallBreak();
            check(handling == EnumHandling.PreventDefault && dirty == 1 && sounds == 1, "Outsider break must run the actual native reinforcement feedback and consume a point.");
            check(nativeBehavior.GetPlacedBlockInfo(world, selection.Position, player).Contains("Strength: 49"),
                "DidBreakBlock after a blocked mining attempt must preserve the point just consumed.");
            for (int i = 1; i < 50; i++) CallBreak();
            CallBreak();
            check(handling == EnumHandling.PassThrough && dirty == 50 && chatMessages == messagesBefore, $"Five living members must provide fifty native strength with no chat spam: {handling}, dirty={dirty}, chat={chatMessages - messagesBefore}, wear={string.Join(',', life.BlockWear.Values.Select(w => w.Damage))}.");
            check(nativeBehavior.GetMiningSpeedModifier(world, selection.Position, player) == 1f
                && BlockBehaviorReinforcable.AllowRightClickPickup(world, selection.Position, player),
                "Exhausted tribal strength must restore normal native mining and pickup.");
            Call(system, "SaveCivilizationLives"); Call(restarted, "LoadCivilizationLives");
            check(Get<CivilizationLifeState>(restarted, "lifeState").BlockWear.Values.Single().Damage == 50,
                "Reinforcement depletion must survive world save and restart.");
            string wearKey = $"{selection.Position.dimension}:{selection.Position.X}:{selection.Position.Y}:{selection.Position.Z}";
            var fireState = new BuildingFireState { BlockCode = "game:planks-oak", DamageSeconds = 12, ManualConsumedFraction = .25 };
            life.BuildingFires[wearKey] = fireState;
            Call(system, "OnTerritoryBroken", player, blockId, selection);
            check(life.BlockWear.Values.Single().Damage == 50 && ReferenceEquals(life.BuildingFires[wearKey], fireState),
                "A break event with the original block present must retain reserve depletion and permanent fire damage.");
            int originalBlockId = blockId;
            blockId = 0;
            Call(system, "OnTerritoryBroken", player, originalBlockId, selection);
            check(life.BlockWear.Count == 0 && life.BuildingFires.Count == 0,
                "Actual block removal must clear reserve depletion and fire damage.");
            blockId = originalBlockId;
            CallBreak(); life.BuildingFires[wearKey] = fireState;
            blockId++;
            Call(system, "OnTerritoryBroken", player, originalBlockId, selection);
            check(life.BlockWear.Count == 0 && life.BuildingFires.Count == 0,
                "A break that replaces the block must clear the old block's protection and fire state.");
            CallBreak();
            check(handling == EnumHandling.PreventDefault, "A changed block must not inherit an unrelated block's depletion.");
            tribe.Members.Add("waiting"); CallBreak();
            check(handling == EnumHandling.PassThrough, "Own tribe must bypass only its extra territory reinforcement.");
            var overlapping = new TribeRecord { Id = "overlap", X = 1000, Z = 1000, Members = ["outsider"] };
            tribes.Tribes.Add(overlapping); CallBreak();
            check(handling == EnumHandling.PassThrough,
                "Membership must exempt extra reinforcement inside the own tribe range even if old ranges overlap.");
            tribes.Tribes.Remove(overlapping);
            tribe.Members.Remove("waiting"); selection.Position.dimension = 1; CallBreak();
            check(handling == EnumHandling.PassThrough, "Territory reinforcement must respect dimensions.");
            selection.Position.dimension = 0;
            life.BuildingFires[wearKey] = fireState;
            Call(system, "OnTerritoryPlaced", player, 0, selection, new ItemStack());
            check(life.BlockWear.Count == 0 && life.BuildingFires.Count == 0,
                "Placing a block, including the same block type, must reset reinforcement depletion and fire damage.");
            system.Config.Territory.ReinforcementMaximum = 2;
            CallBreak(); CallBreak(); CallBreak();
            check(handling == EnumHandling.PassThrough, "Territory reinforcement cap must be configurable.");
            selection.Position = new BlockPos(2000, 100, 2000); CallBreak();
            check(handling == EnumHandling.PassThrough, "Outside tribe ranges must have no extra reinforcement.");
            tribe.Outposts.Add(new() { X = 2000, Y = 100, Z = 2000 });
            var outpostShape = new TribeRecord { X = 2000, Y = 100, Z = 2000 };
            tribe.Regions.Add(TerritoryTestWorld.Region(outpostShape, site: tribe.Outposts.Last().Id)); TerritoryTestWorld.Index(system); CallBreak();
            check(handling == EnumHandling.PreventDefault,
                "Outpost range must reinforce outsider mining independently of native land claims.");
            // Real plumb-and-square records must retain strength, owner, and lock.
            // This fixture exercises legacy native records; new placement/access rules have dedicated checks.

            system.Config.Territory.ReinforcementMaximum = 200;
            Call(system, "OnTerritoryPlaced", player, 0, selection, new ItemStack());
            check(!nativeReinforcement.TryLock(selection.Position, player, "game:padlock")
                && !nativeReinforcement.StrengthenBlock(selection.Position, player, 7), "Unauthorized outsider manual reinforcement/lock must be denied.");
            tribe.Members.Add(player.PlayerUID);
            Call(system, "RecordBuilding", player, selection.Position);
            check(nativeReinforcement.TryLock(selection.Position, player, "game:padlock")
                && nativeReinforcement.StrengthenBlock(selection.Position, player, 7), "Native manual reinforcement fixture failed.");
            tribe.Members.Remove(player.PlayerUID);
            var manual = nativeReinforcement.GetReinforcment(selection.Position);
            check(manual.Strength == 7 && manual.Locked && manual.PlayerUID == "waiting", "Native reinforcement fixture lost its ownership.");
            check(nativeBehavior.GetPlacedBlockInfo(world, selection.Position, player).Contains("Strength: 57"),
                "An outsider must see manual plus tribal strength in native block info.");
            CallBreak();
            manual = nativeReinforcement.GetReinforcment(selection.Position);
            check(manual.Strength == 7 && manual.Locked && manual.PlayerUID == "waiting" && manual.LockedByItemCode == "game:padlock",
                "Consuming tribal strength must not change manual strength, ownership or locks.");
            tribe.Members.Add("waiting");
            check(nativeBehavior.GetPlacedBlockInfo(world, selection.Position, player).Contains("Strength: 7"),
                "Members must still see manually applied reinforcement.");
            CallBreak();
            check(handling == EnumHandling.PreventDefault && nativeReinforcement.GetReinforcment(selection.Position).Strength == 6,
                "Members must consume actual manual strength while bypassing tribal strength.");
            tribe.Members.Remove("waiting");
            for (int i = 0; i < 49; i++) CallBreak();
            check(nativeReinforcement.GetReinforcment(selection.Position).Strength == 6, "Outsiders must exhaust tribal protection before consuming manual strength.");
            CallBreak();
            check(nativeReinforcement.GetReinforcment(selection.Position).Strength == 5,
                "After tribal strength is exhausted, native manual reinforcement must keep working.");
            using var stream = new MemoryStream(); Serializer.Serialize(stream, packets.Last()); stream.Position = 0;
            check(Serializer.Deserialize<GuidancePacket>(stream).Title == packets.Last().Title, "Guidance protocol roundtrip failed.");
        }
        finally
        {
            Call(system, "StopGuidance");
            (Get<Harmony>(system, "respawnHarmony"))?.UnpatchAll("vsscsmod.respawn");
            AccessTools.Field(type, "instance").SetValue(null, previous);
        }
        var client = new VscnClientModSystem();
        try
        {
            Call(client, "StartGuidanceClient");
            var viewerEntity = new EntityPlayer();
            var selected = new BlockSelection { Position = new BlockPos(2000, 100, 2000) };
            var viewer = Proxy.Make<Vintagestory.API.Client.IClientPlayer>((m, a) => m.Name switch
            { "get_Entity" => viewerEntity, "get_PlayerUID" => "viewer", "get_CurrentBlockSelection" => selected, "get_WorldData" => data, _ => null });
            Vintagestory.API.Client.ICoreClientAPI? clientApi = null;
            var clientWorld = Proxy.Make<Vintagestory.API.Client.IClientWorldAccessor>((m, a) => m.Name switch
            { "get_Player" => viewer, "get_BlockAccessor" => blocks, "get_Side" => EnumAppSide.Client, "get_Api" => clientApi, _ => null });
            clientApi = Proxy.Make<Vintagestory.API.Client.ICoreClientAPI>((m, a) => m.Name switch
            { "get_World" => clientWorld, "get_ModLoader" => loader, _ => null });
            Set(client, "capi", clientApi);
            Set(client, "ready", true);
            var requests = new List<TribeReinforcementRequest>();
            var clientChannel = Proxy.Make<Vintagestory.API.Client.IClientNetworkChannel>((m, a) =>
            { if (m.Name == "SendPacket" && a?[0] is TribeReinforcementRequest request) requests.Add(request); return null; });
            Set(client, "channel", clientChannel);
            Call(client, "RequestTribeReinforcement");
            check(requests.Single().Position.Equals(selected.Position), "Client reinforcement queries must refer to the selected block.");
            var synchronized = new TribeReinforcementPacket { Block = requests[0], BlockId = blockId, Strength = 10, Tribe = "新生" };
            using (var stream = new MemoryStream())
            {
                Serializer.Serialize(stream, synchronized); stream.Position = 0;
                synchronized = Serializer.Deserialize<TribeReinforcementPacket>(stream);
            }
            Call(client, "OnTribeReinforcement", synchronized);
            var clientBehavior = new BlockBehaviorReinforcable(CurrentBlock());
            check(clientBehavior.GetPlacedBlockInfo(clientWorld, selected.Position, viewer).Contains("Strength: 15")
                && clientBehavior.GetMiningSpeedModifier(clientWorld, selected.Position, viewer) == .6f,
                "Client HUD and mining must combine server-approved extra strength with native manual reinforcement.");
            var clientHandling = EnumHandling.PassThrough;
            clientBehavior.OnBlockBroken(clientWorld, selected.Position, viewer, 1, ref clientHandling);
            check(synchronized.Strength == 9 && nativeReinforcement.GetReinforcment(selected.Position).Strength == 5,
                "Client prediction must consume extra strength without modifying manual reinforcement.");
            Call(client, "OnTribeReinforcement", new TribeReinforcementPacket { Block = requests[0], BlockId = blockId + 1, Strength = 99 });
            check(ReferenceEquals(Get<TribeReinforcementPacket>(client, "selectedReinforcement"), synchronized),
                "A stale block ID must not overwrite the selected reinforcement.");
            Set(client, "reinforcementReceived", Environment.TickCount64 - 2000);
            check(clientBehavior.GetPlacedBlockInfo(clientWorld, selected.Position, viewer).Contains("Strength: 5"),
                "Expired client reinforcement must not remain visible after affiliation or terrain changes.");
            Call(client, "OnTribeReinforcement", synchronized);
            Call(client, "LeaveGuidance");
            check(clientBehavior.GetPlacedBlockInfo(clientWorld, selected.Position, viewer).Contains("Strength: 5"),
                "Leaving a world must clear the old tribal strength without clearing native reinforcement.");
            viewerEntity.WatchedAttributes.SetString(VscnCivilizationModSystem.TribeAttribute, "own");
            entity.WatchedAttributes.SetString(VscnCivilizationModSystem.TribeAttribute, "own");
            check((bool)Call(client, "Friendly", entity)!, "Same tribe must select green name labels.");
            var tag = new Vintagestory.GameContent.EntityBehaviorNameTag(entity);
            int redraws = 0;
            Set(tag, "nameTagRenderHandler", (Vintagestory.GameContent.NameTagRendererDelegate)((_, _) => { redraws++; return null!; }));
            Call(client, "BeforeTagFrame", tag); Call(client, "BeforeTagFrame", tag);
            entity.WatchedAttributes.SetString(VscnCivilizationModSystem.TribeAttribute, "foreign");
            Call(client, "BeforeTagFrame", tag);
            check(!(bool)Call(client, "Friendly", entity)! && redraws == 2,
                "Tribe changes must redraw labels once, while stable membership must reuse textures.");
            object?[] rendererArgs = [entity, (Vintagestory.GameContent.NameTagRendererDelegate)((_, _) => { redraws++; return null!; })];
            AccessTools.Method(typeof(VscnClientModSystem), "AfterNameRenderer").Invoke(null, rendererArgs);
            ((Vintagestory.GameContent.NameTagRendererDelegate)rendererArgs[1]!)(clientApi, entity);
            viewerEntity.WatchedAttributes.SetString(VscnCivilizationModSystem.TribeAttribute, "");
            check(redraws == 3 && !(bool)Call(client, "Friendly", viewerEntity)!,
                "Foreign labels must keep the original renderer; unaligned players must not become friendly.");
            var deadType = AccessTools.TypeByName("Vintagestory.Client.NoObf.GuiDialogDead");
            check(Harmony.GetPatchInfo(AccessTools.Method(deadType, "ComposeDialog"))?.Owners.Contains("vsscsmod.guidance.client") == true,
                "Native death-dialog life override must patch the installed game.");
        }
        finally { Set(client, "capi", null!); client.Dispose(); }
    }
}
