using System.Collections;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using SeraphCivilizationSimulation;

internal static class SoloTribeChecks
{
    public static void Run(Action<bool, string> check)
    {
        object? Invoke(object target, string method, params object?[] args) =>
            target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
        void Set(object target, string field, object value) =>
            target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
        object Get(object target, string field) =>
            target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;

        var oldConfig = JsonConvert.DeserializeObject<VscnConfig>("{\"Sleep\":{\"Enabled\":false}}")!;
        oldConfig.Validate();
        check(!oldConfig.Tribes.AllowSoloCreation && !new VscnConfig().Tribes.AllowSoloCreation,
            "New and old configurations must disable solo creation by default.");
        try
        {
            JsonConvert.DeserializeObject<VscnConfig>("{\"Tribes\":null}")!.Validate();
            check(false, "A null tribes configuration section was accepted.");
        }
        catch (ArgumentException) { check(true, "Null tribes section rejected."); }

        var saved = new Dictionary<string, byte[]>();
        var snapshots = new Dictionary<string, CivilizationSnapshotPacket>();
        var messages = new Dictionary<string, string>();
        VscnConfig? storedConfig = null;
        int configWrites = 0;
        bool failStore = false;
        var founderEntity = new EntityPlayer();
        founderEntity.Pos.SetPos(100.8, 110.9, 100.7);
        founderEntity.Pos.Dimension = 1;
        founderEntity.Stats = new EntityStats(founderEntity);
        var guestEntity = new EntityPlayer();
        guestEntity.Pos.SetPos(600, 110, 600);
        guestEntity.Pos.Dimension = 1;
        guestEntity.Stats = new EntityStats(guestEntity);
        IServerPlayer Player(string uid, EntityPlayer entity, bool admin = false) => Proxy.Make<IServerPlayer>((m, a) => m.Name switch
        {
            "get_PlayerUID" or "get_PlayerName" => uid, "get_Entity" => entity,
            "get_LanguageCode" => "en", "HasPrivilege" => admin, _ => null
        });
        var founder = Player("solo-founder", founderEntity, true);
        var guest = Player("guest", guestEntity);
        IPlayer[] online = [founder];
        string contentDataPath = Path.Combine(Path.GetTempPath(), "vscncs-solo-content-" + Guid.NewGuid().ToString("N"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { if (Directory.Exists(contentDataPath)) Directory.Delete(contentDataPath, recursive: true); };
        var world = Proxy.Make<IServerWorldAccessor>((m, a) => m.Name switch
        { "get_AllOnlinePlayers" => online, "get_SavegameIdentifier" => "solo-check-save", _ => null });
        var saveApi = Proxy.Make<ISaveGame>((m, a) =>
        {
            if (m.Name == "GetData") return saved.GetValueOrDefault((string)a![0]!);
            if (m.Name == "StoreData") saved[(string)a![0]!] = (byte[])a[1]!;
            return null;
        });
        var manager = Proxy.Make<IWorldManagerAPI>((m, a) => m.Name == "get_SaveGame" ? saveApi : null);
        var loader = Proxy.Make<IModLoader>((m, a) => m.Name == "IsModEnabled" ? false : null);
        var logger = Proxy.Make<ILogger>((m, a) => null);
        var api = Proxy.Make<ICoreServerAPI>((m, a) =>
        {
            if (m.Name == "StoreModConfig")
            {
                if (failStore) throw new IOException("test config write failure");
                check((string)a![1]! == "SeraphCivilizationSimulation.json", "Solo toggle saved to the wrong config file.");
                storedConfig = JsonConvert.DeserializeObject<VscnConfig>(JsonConvert.SerializeObject(a[0]));
                configWrites++;
                return null;
            }
            if (m.Name == "LoadModConfig") return storedConfig;
            return m.Name switch
            {
                "get_World" => world, "get_WorldManager" => manager,
                "get_ModLoader" => loader, "get_Logger" => logger,
                "GetOrCreateDataPath" => contentDataPath, _ => null
            };
        });
        var channel = Proxy.Make<IServerNetworkChannel>((m, a) =>
        {
            if (m.Name == "SendPacket" && a?[0] is CivilizationSnapshotPacket packet && a[1] is IEnumerable<IServerPlayer> recipients)
                foreach (var recipient in recipients)
                {
                    snapshots[recipient.PlayerUID] = packet;
                    if (packet.Message.Length > 0) messages[recipient.PlayerUID] = packet.Message;
                }
            return null;
        });
        VscnCivilizationModSystem Server(bool enabled = false)
        {
            var server = new VscnCivilizationModSystem();
            Set(server, "sapi", api); Set(server, "channel", channel);
            server.Config.Tribes.AllowSoloCreation = enabled;
            return server;
        }
        var roomWorld = new TerritoryTestWorld(api); roomWorld.Box(100, 110, 100, 4, 4, dimension: 1);
        roomWorld.Box(700, 110, 700, 4, 4, dimension: 1); api = roomWorld.Api;
        var system = Server();
        TribeState State(VscnCivilizationModSystem server) => (TribeState)Get(server, "tribeState");
        TribeState Disk() => JsonConvert.DeserializeObject<TribeState>(Encoding.UTF8.GetString(saved["vscncsTribeState"]))!;
        TextCommandResult Command(string arguments, IServerPlayer? caller = null)
        {
            var parser = Proxy.Make<ICommandArgumentParser>((m, a) => m.Name == "GetValue" ? "tribe solo " + arguments : null);
            return (TextCommandResult)Invoke(system, "HandleCommand", new TextCommandCallingArgs
            {
                Caller = caller == null ? new Caller { Type = EnumCallerType.Console } : new Caller { Player = caller },
                Parsers = [parser]
            })!;
        }
        void Request(IServerPlayer player, string action, string name = "", string target = "")
        {
            Invoke(system, "OnCivilizationRequest", player, new CivilizationRequestPacket { Action = action, Name = name, Target = target });
        }
        void Result(IServerPlayer player, string key) => check(messages.GetValueOrDefault(player.PlayerUID) ==
            Lang.GetL("en", "vsscsmod:ui." + key), "Unexpected tribe action response: " + key);

        Request(founder, "create", "单人部落");
        check(State(system).Tribes.Count == 0 && !snapshots[founder.PlayerUID].AllowSoloCreation,
            "Disabled solo creation must still require a nearby player.");
        Result(founder, "no-nearby");
        check(Command("on", guest).Status == EnumCommandStatus.Error && configWrites == 0,
            "An ordinary player enabled solo creation.");
        check(Command("status").Status == EnumCommandStatus.Success && Command("").Status == EnumCommandStatus.Success && configWrites == 0,
            "Solo status must work in the console without rewriting configuration.");
        foreach (string invalid in new[] { "invalid", "on extra", "status extra" })
            check(Command(invalid).Status == EnumCommandStatus.Error && !system.Config.Tribes.AllowSoloCreation,
                "An invalid solo command changed configuration.");
        check(Command("ON", founder).Status == EnumCommandStatus.Success && storedConfig!.Tribes.AllowSoloCreation
            && snapshots[founder.PlayerUID].AllowSoloCreation, "Administrator solo command did not persist and synchronize its setting.");

        Request(founder, "create", "短名"); Result(founder, "invalid-length-name");
        Request(founder, "create", "Invalid"); Result(founder, "invalid-name");
        check(State(system).Tribes.Count == 0, "Solo creation bypassed name validation.");
        Request(founder, "create", " 单人部落 "); Result(founder, "solo-founded");
        var tribe = State(system).Tribes.Single();
        check(tribe.Name == "单人部落" && tribe.LeaderUid == founder.PlayerUID && tribe.Members.SequenceEqual([founder.PlayerUID])
            && tribe.AllowsSolo && tribe.X == 100 && tribe.Y == 110 && tribe.Z == 100 && tribe.Dimension == 1 && tribe.CenterChangedAt > 0,
            "Solo founding did not use the founder's location, dimension and membership.");
        check(Disk().Tribes.Single().AllowsSolo && snapshots[founder.PlayerUID].IsLeader
            && snapshots[founder.PlayerUID].MyTribeId == tribe.Id && snapshots[founder.PlayerUID].Territories.Count == 1,
            "Solo tribe membership and marker were not saved or synchronized.");
        check(((IList)Get(system, "pendingInvites")).Count == 0 && ((IList)Get(system, "pendingFoundings")).Count == 0,
            "Solo founding must not require invitations.");
        Request(founder, "create", "第二部落"); Result(founder, "already-member");
        online = [founder, guest];
        Request(guest, "create", "单人部落"); Result(guest, "name-used");
        guestEntity.Pos.SetPos(101, 110, 100);
        Request(guest, "create", "重叠部落");
        check(snapshots[guest.PlayerUID].Message.Contains("重叠") || snapshots[guest.PlayerUID].Message.Contains("冲突"), "Overlapping native room was not rejected.");
        check(State(system).Tribes.Count == 1, "Solo founding bypassed membership, uniqueness or territory validation.");
        Request(founder, "invite");
        var invitation = snapshots[guest.PlayerUID].Invitations.Single();
        Request(guest, "accept", target: invitation.TribeId);
        check(tribe.Members.Count == 2, "A solo tribe could not recruit through normal invitations.");
        check(Command("off").Status == EnumCommandStatus.Success && !storedConfig!.Tribes.AllowSoloCreation
            && !snapshots[guest.PlayerUID].AllowSoloCreation && State(system).Tribes.Count == 1,
            "Disabling solo creation deleted a permitted tribe or left stale client settings.");
        Request(founder, "leave"); Result(founder, "left");
        check(tribe.Members.SequenceEqual([guest.PlayerUID]) && tribe.LeaderUid == guest.PlayerUID && Disk().Tribes.Single().Members.Count == 1,
            "A permitted solo tribe dissolved instead of promoting its remaining member.");
        var restarted = Server(); Invoke(restarted, "LoadState");
        check(State(restarted).Tribes.Single().LeaderUid == guest.PlayerUID && State(restarted).Tribes.Single().AllowsSolo,
            "Restarting with solo creation disabled lost a previously permitted solo tribe.");
        system = restarted;
        founderEntity.Pos.SetPos(700, 110, 700); guestEntity.Pos.SetPos(100, 110, 100);
        Request(founder, "create", "关闭测试"); Result(founder, "no-nearby");
        Request(guest, "leave"); Result(guest, "left-dissolved");
        check(State(system).Tribes.Count == 0 && Disk().Tribes.Count == 0, "The last member leaving must dissolve and persist a solo tribe.");

        check(Command("toggle").Status == EnumCommandStatus.Success && storedConfig!.Tribes.AllowSoloCreation,
            "Solo toggle did not enable the setting.");
        Request(founder, "create", "踢出测试");
        guestEntity.Pos.SetPos(701, 110, 700);
        Request(founder, "invite"); Request(guest, "accept", target: snapshots[guest.PlayerUID].Invitations.Single().TribeId);
        Request(founder, "kick", target: guest.PlayerUID);
        check(State(system).Tribes.Single().Members.SequenceEqual([founder.PlayerUID]) && Disk().Tribes.Single().Members.Count == 1,
            "Kicking a member from a solo tribe did not retain its founder.");
        failStore = true;
        int writesBeforeFailure = configWrites;
        check(Command("off").Status == EnumCommandStatus.Error && system.Config.Tribes.AllowSoloCreation
            && storedConfig!.Tribes.AllowSoloCreation && configWrites == writesBeforeFailure
            && snapshots[founder.PlayerUID].AllowSoloCreation, "A failed config write changed the active solo setting.");
        failStore = false;
        Request(founder, "leave");

        // Tribes formed normally may shrink to one while the setting is enabled.
        var normal = new TribeRecord { Name = "正常部落", LeaderUid = founder.PlayerUID, Members = [founder.PlayerUID, guest.PlayerUID] };
        State(system).Tribes.Add(normal);
        Request(guest, "leave");
        check(State(system).Tribes.Single().AllowsSolo && Disk().Tribes.Single().Members.Count == 1,
            "Enabled solo policy did not preserve a normal tribe shrinking to one member.");
        Request(founder, "leave");

        // An already pending founding can complete immediately after an administrator enables solo creation.
        Command("off");
        Request(founder, "create", "等待部落");
        check(State(system).Tribes.Count == 0 && snapshots[guest.PlayerUID].Invitations.Count == 1, "Pending founding setup failed.");
        Command("on"); Request(founder, "create", "等待部落"); Result(founder, "solo-founded");
        check(State(system).Tribes.Count == 1 && snapshots[guest.PlayerUID].Invitations.Count == 0
            && ((IList)Get(system, "pendingFoundings")).Count == 0,
            "Enabling solo creation left the founder blocked by or carrying stale invitations.");

        var legacy = new TribeState { Tribes = [new TribeRecord { Name = "旧单人部落", LeaderUid = founder.PlayerUID, Members = [founder.PlayerUID] }] };
        saved["vscncsTribeState"] = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(legacy));
        var enabledRestart = Server(true); Invoke(enabledRestart, "LoadState");
        check(State(enabledRestart).Tribes.Single().AllowsSolo && Disk().Tribes.Single().AllowsSolo,
            "Loading an existing solo tribe with solo enabled did not persist its permission.");
        storedConfig = JsonConvert.DeserializeObject<VscnConfig>(JsonConvert.SerializeObject(system.Config));
        storedConfig!.Tribes.AllowSoloCreation = false;
        Directory.CreateDirectory(contentDataPath);
        ConfigMigration.ConfigDirectoryOverride = contentDataPath;
        File.WriteAllText(Path.Combine(contentDataPath, ConfigMigration.Main), JsonConvert.SerializeObject(storedConfig));
        VscnConfig loadedConfig;
        try { loadedConfig = (VscnConfig)Invoke(enabledRestart, "ReadConfig", false)!; }
        finally { ConfigMigration.ConfigDirectoryOverride = null; }
        check(!loadedConfig.Tribes.AllowSoloCreation, "Config reload ignored the solo option.");
        var enabledConfig = JsonConvert.DeserializeObject<VscnConfig>("{\"Tribes\":{\"AllowSoloCreation\":true}}")!;
        enabledConfig.Validate();
        check(enabledConfig.Tribes.AllowSoloCreation, "Configuration file could not enable solo creation.");
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, new CivilizationSnapshotPacket { AllowSoloCreation = true }); stream.Position = 0;
        check(Serializer.Deserialize<CivilizationSnapshotPacket>(stream).AllowSoloCreation, "Solo UI setting did not survive protocol serialization.");
    }
}
