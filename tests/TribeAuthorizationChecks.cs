using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using SeraphCivilizationSimulation;

internal static class TribeAuthorizationChecks
{
    public static void Run(Action<bool, string> check)
    {
        if (!Vintagestory.API.Config.Lang.AvailableLanguages.ContainsKey("en"))
            Vintagestory.API.Config.Lang.AvailableLanguages["en"] = Proxy.Make<Vintagestory.API.Config.ITranslationService>((m, a) =>
                m.Name.StartsWith("Get") && a?[0] is string text ? a.Length > 1 && a[1] is object[] { Length: > 0 } values ? string.Format(text, values) : text : null);
        var system = new VscnCivilizationModSystem();
        var type = system.GetType();
        object? Call(string method, params object?[] args) => AccessTools.Method(type, method).Invoke(system, args);
        void Set(string name, object value) => AccessTools.Field(type, name).SetValue(system, value);
        var tribes = (TribeState)AccessTools.Field(type, "tribeState").GetValue(system)!;
        var life = (CivilizationLifeState)AccessTools.Field(type, "lifeState").GetValue(system)!;
        var tribe = new TribeRecord { Id = "home", Name = "测试部落", LeaderUid = "chief", Members = ["chief"], X = 100, Y = 100, Z = 100, AllowsSolo = true };
        tribes.Tribes.Add(tribe);
        TerritoryTestWorld.Region(tribe); TerritoryTestWorld.Index(system);
        foreach (var (uid, name) in new[] { ("chief", "首领"), ("guest", "游戏访客"), ("other", "其他玩家") })
            life.Players[uid] = new() { Accepted = true, GameName = name, Living = true };
        var saved = new Dictionary<string, byte[]>();
        var save = Proxy.Make<ISaveGame>((m, a) => {
            if (m.Name == "StoreData") saved[(string)a![0]!] = (byte[])a[1]!;
            return m.Name == "GetData" ? saved.GetValueOrDefault((string)a![0]!) : null;
        });
        var worldManager = Proxy.Make<IWorldManagerAPI>((m, a) => m.Name == "get_SaveGame" ? save : null);
        var modloader = Proxy.Make<IModLoader>((m, a) => m.Name == "IsModEnabled" ? false : null);
        var channel = Proxy.Make<IServerNetworkChannel>((m, a) => null);
        var block = new Block { BlockId = 12, Code = new("game:planks-oak") };
        var blocks = Proxy.Make<IBlockAccessor>((m, a) => m.Name == "GetBlock" ? block : null);
        IPlayer[] online = [];
        Entity? rayTarget = null;
        BlockSelection? rayBlock = null;
        int discoveries = 0, sounds = 0;
        var world = Proxy.Make<IServerWorldAccessor>((m, a) => {
            if (m.Name == "RayTraceForSelection") { a![4] = rayBlock; a[5] = rayTarget == null ? null : new EntitySelection { Entity = rayTarget }; }
            if (m.Name == "PlaySoundFor") sounds++;
            return m.Name switch { "get_AllOnlinePlayers" => online, "get_BlockAccessor" => blocks, "get_Side" => EnumAppSide.Server, _ => null };
        });
        var accounts = new Dictionary<string, IServerPlayerData>
        { ["guest"] = Proxy.Make<IServerPlayerData>((m,a) => m.Name == "get_LastKnownPlayername" ? "account-guest" : null) };
        var accountManager = Proxy.Make<IPlayerDataManager>((m,a) => m.Name == "get_PlayerDataByUid" ? accounts : null);
        var api = Proxy.Make<ICoreServerAPI>((m, a) => m.Name switch { "SendIngameDiscovery" => ++discoveries > 0 ? null : null, "get_World" => world, "get_WorldManager" => worldManager, "get_ModLoader" => modloader, "get_PlayerData" => accountManager, _ => null });
        Set("sapi", api); Set("channel", channel);
        var chiefEntity = new EntityPlayer { World = world }; chiefEntity.Pos.SetPos(100, 100, 100);
        var guestEntity = new EntityPlayer { World = world }; guestEntity.Pos.SetPos(102, 100, 100); guestEntity.Stats = new EntityStats(guestEntity);
        var selected = new BlockSelection { Position = new BlockPos(101, 100, 100) };
        IServerPlayer Player(string uid, EntityPlayer entity) => Proxy.Make<IServerPlayer>((m, a) => m.Name switch
        { "get_PlayerUID" => uid, "get_PlayerName" => "account-" + uid, "get_Entity" => entity, "get_CurrentBlockSelection" => selected, "get_LanguageCode" => "en", "get_Ping" => .123f, "get_ConnectionState" => EnumClientState.Playing, _ => null });
        var chief = Player("chief", chiefEntity); var guest = Player("guest", guestEntity);
        CivilizationRequestPacket Request(string name = "游戏访客", string minutes = "1") => new()
        { Action = "authorize", Name = name, DurationMinutes = minutes, Construction = true, Benefits = true };
        var previous = AccessTools.Field(type, "instance").GetValue(null);
        Set("instance", system);
        try
        {
            foreach (string value in new[] { "0", "-2", "1.5", "NaN", "9223372036854775807", "", "5256001" })
            { Call("AuthorizePlayer", chief, Request(minutes: value)); check(tribe.Authorizations.Count == 0, "Invalid duration accepted: " + value); }
            foreach (string name in new[] { "guest", "account-guest", "游戏", "missing" })
            { Call("AuthorizePlayer", chief, Request(name)); check(tribe.Authorizations.Count == 0, "Account/UID/partial name accepted: " + name); }
            Call("AuthorizePlayer", guest, Request()); check(tribe.Authorizations.Count == 0, "Nonleader grant spoof succeeded.");
            life.Players["other"].GameName = "游戏访客";
            Call("AuthorizePlayer", chief, Request()); check(tribe.Authorizations.Count == 0, "Ambiguous custom name accepted.");
            life.Players["other"].GameName = "其他玩家";
            Call("OnCivilizationRequest", chief, Request());
            check(tribe.Authorizations.Single().Uid == "guest" && tribe.Authorizations[0].ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "Named timed grant failed.");
            check(saved.Count > 0 && JsonConvert.DeserializeObject<TribeState>(System.Text.Encoding.UTF8.GetString(saved["vscncsTribeState"]))!.Tribes[0].Authorizations.Count == 1, "Grant was not persisted.");
            check(tribes.FindMember("guest") == null && tribe.Members.Count == 1, "Authorization changed membership or population.");
            check(((bool Member, bool Home))Call("TerritoryStatus", guest)! == (false, true), "Guest territory benefits missing.");
            var effects = (List<TerritoryEffectView>)Call("BuildCivilizationEffects", guest, false, true)!;
            check(!effects.Any(e => e.Code is "effect-speed" or "effect-attack" or "effect-roping") && !effects.Any(e => e.Code is "effect-newborn" or "effect-homecoming"), "Guest gained directed benefits or lost ordinary benefits.");
            object?[] manual = [selected.Position, guest, false];
            check((bool)AccessTools.Method(type, "AuthorizationLock").Invoke(null, manual)!, "Authorized guest lock denied.");
            Call("RevokeAuthorization", guest, "guest"); check(tribe.Authorizations.Count == 1, "Nonleader revoked a grant.");
            Call("RevokeAuthorization", chief, "guest");
            check(tribe.Authorizations.Count == 0 && ((bool Member, bool Home))Call("TerritoryStatus", guest)! == (false, false), "Revocation retained guest benefits.");
            manual = [selected.Position, guest, true];
            check(!(bool)AccessTools.Method(type, "AuthorizationLock").Invoke(null, manual)! && !(bool)manual[2]!, "Unauthorized guest lock allowed.");
            Call("RecordBuilding", guest, selected.Position);
            check(((TribeReinforcementPacket)Call("TerritoryReinforcement", null, selected.Position)!).Strength == 0, "Outsider building inherited host protection.");
            Call("AuthorizePlayer", chief, Request(minutes: "-1"));
            check(tribe.Authorizations.Single().ExpiresAt == -1, "Indefinite grant failed.");
            check(((TribeReinforcementPacket)Call("TerritoryReinforcement", null, selected.Position)!).Strength == 0, "Later authorization retroactively adopted outsider construction.");
            Call("RecordBuilding", guest, selected.Position);
            Call("RevokeAuthorization", chief, "guest");
            check(((TribeReinforcementPacket)Call("TerritoryReinforcement", null, selected.Position)!).Strength == 10, "Authorized construction lost its recorded tribe ownership on revocation.");
            Call("RecordBuilding", guest, selected.Position);
            var placed = life.PlacedBuildings.Values.Single();
            var settings = system.Config.TribeAuthorization;
            TribeReinforcementPacket Reserve(IPlayer? viewer = null) => (TribeReinforcementPacket)Call("TerritoryReinforcement", viewer, selected.Position)!;
            long started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            check(placed.TribeId == "" && Reserve().Strength == 0 && Reserve().PendingSeconds == 0,
                "Outsider construction must remain unowned without an automatic countdown.");
            placed.EnrollmentTribeId = tribe.Id; placed.EnrollmentStartedAtUtc = started - 600001;
            Call("UpdateBuildingEnrollment", placed, tribe, started);
            Call("TickBuildingEnrollment");
            check(placed.TribeId == "" && placed.EnrollmentStartedAtUtc == 0 && Reserve().Strength == 0,
                "Legacy countdown granted automatic ownership.");
            var persisted = JsonConvert.DeserializeObject<PlacedBuilding>(JsonConvert.SerializeObject(placed))!;
            Call("UpdateBuildingEnrollment", persisted, tribe, started + 600000000);
            check(persisted.TribeId == "", "Offline time granted outsider ownership.");
            foreach (string code in new[] { "game:ladder-wood-north", "game:crudeladder-north", "game:ropeladder-north", "other:scaffolding-oak", "game:torch-up", "game:lantern-copper", "game:carpet-red", "game:snowlayer-1", "game:loosestones-granite", "game:tallgrass-medium", "game:crop-flax-1" })
            {
                block.Code = new(code); Call("RecordBuilding", chief, selected.Position);
                check(Reserve().Strength == 0, "Member/legacy exclusion failed: " + code);
                Call("RecordBuilding", guest, selected.Position); placed = life.PlacedBuildings.Values.Single();
                placed.EnrollmentStartedAtUtc = started - 600000; placed.EnrollmentTribeId = tribe.Id;
                Call("UpdateBuildingEnrollment", placed, tribe, started);
                check(Reserve().Strength == 0 && Reserve().PendingSeconds == 0 && placed.TribeId == "", "Excluded block enrolled: " + code);
            }
            block.Code = new("game:ladder-wood-north");
            Call("RecordBuilding", chief, selected.Position);
            settings.ReinforcementExclusionsEnabled = false;
            check(Reserve().Strength == 10, "Exclusion switch did not restore member protection.");
            settings.ReinforcementExclusionsEnabled = true;
            settings.ReinforcementExcludedBlocks = ["custom:temporary-*"];
            block.Code = new("custom:temporary-bridge");
            check(Reserve().Strength == 0, "Configured wildcard exclusion failed.");
            block.Code = new("game:planks-oak");
            check(Reserve().Strength == 10, "Exclusions incorrectly removed ordinary structural protection.");
            settings.ReinforcementExcludedBlocks = new TribeAuthorizationConfig().ReinforcementExcludedBlocks;
            var legacyPlaced = JsonConvert.DeserializeObject<PlacedBuilding>("{\"TerritoryId\":\"home\"}")!;
            Call("UpdateBuildingEnrollment", legacyPlaced, tribe, started);
            check(legacyPlaced.EnrollmentStartedAtUtc == 0 && legacyPlaced.TribeId == "", "Legacy external block was instantly protected rather than beginning a new timer.");
            using (var pendingStream = new MemoryStream())
            {
                Serializer.Serialize(pendingStream, new TribeReinforcementPacket { PendingSeconds = 599, Tribe = tribe.Name }); pendingStream.Position = 0;
                check(Serializer.Deserialize<TribeReinforcementPacket>(pendingStream).PendingSeconds == 599, "Countdown network roundtrip failed.");
            }
            check(AccessTools.Method(type, "AdoptBuilding") != null, "Explicit adoption operation is missing.");
            Call("ClearBuildingState", selected.Position);
            check(life.PlacedBuildings.Count == 0 && ((TribeReinforcementPacket)Call("TerritoryReinforcement", null, selected.Position)!).Strength == 10, "Legacy block protection/removal migration failed.");
            tribe.Authorizations.Add(new() { Uid = "guest", ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1 });
            check(((bool Member, bool Home))Call("TerritoryStatus", guest)! == (false, false), "Expired grant still gives benefits before pruning.");
            Call("PruneAuthorizations"); check(tribe.Authorizations.Count == 0, "Expiry pruning failed.");
            Call("AuthorizePlayer", chief, Request());
            tribe.Authorizations[0].Construction = false;
            check(!(bool)Call("CanBuildFor", tribe, "guest")! && ((bool Member, bool Home))Call("TerritoryStatus", guest)! == (false, true), "Independent benefit/construction scopes failed.");
            system.Config.TribeAuthorization.BenefitsEnabled = false;
            check(((bool Member, bool Home))Call("TerritoryStatus", guest)! == (false, false), "Server benefits switch ignored.");
            system.Config.TribeAuthorization.BenefitsEnabled = true;
            tribe.Authorizations.Clear();
            online = [guest]; rayTarget = guestEntity;
            Call("AuthorizePlayer", chief, Request(name: "")); check(tribe.Authorizations.Count == 1, "Server-facing 3-block grant failed.");
            tribe.Authorizations.Clear(); guestEntity.Pos.X = 103.01;
            Call("AuthorizePlayer", chief, Request(name: "")); check(tribe.Authorizations.Count == 0, "Out-of-range facing grant accepted.");
            guestEntity.Pos.X = 102; guestEntity.Pos.Dimension = 1;
            Call("AuthorizePlayer", chief, Request(name: "")); check(tribe.Authorizations.Count == 0, "Cross-dimension facing grant accepted.");
            guestEntity.Pos.Dimension = 0; rayTarget = null;
            Call("AuthorizePlayer", chief, Request(name: "")); check(tribe.Authorizations.Count == 0, "Occluded/absent ray target accepted.");
            online = [];
            check((string)Call("MemberDisplayName", "guest")! == "account-guest(游戏访客)",
                "Offline members must use saved account names alongside character names.");
            system.Config.TribeAuthorization.OutsiderPlacementCooldownEnabled = true;
            Call("RecordBuilding", guest, selected.Position);
            check(Call("OutsiderPlacementFailure", guest, selected.Position) != null, "Placement cooldown bypassed.");
            system.Config.TribeAuthorization.OutsiderPlacementCooldownEnabled = false;
            system.Config.TribeAuthorization.OutsiderBlockQuotaEnabled = true; system.Config.TribeAuthorization.OutsiderBlockQuota = 1;
            check(Call("OutsiderPlacementFailure", guest, selected.Position.UpCopy()) != null, "Outsider quota bypassed.");
            Call("ClearBuildingState", selected.Position);
            check(Call("OutsiderPlacementFailure", guest, selected.Position) == null, "Removing outsider block did not release quota.");
            system.Config.TribeAuthorization.ProtectCriticalAreas = true;
            check(Call("OutsiderPlacementFailure", guest, selected.Position) != null && Call("OutsiderPlacementFailure", chief, selected.Position) == null, "Critical-area placement scope failed.");
            using var stream = new MemoryStream(); Serializer.Serialize(stream, Request(minutes: "-1")); stream.Position = 0;
            check(Serializer.Deserialize<CivilizationRequestPacket>(stream) is { DurationMinutes: "-1", Construction: true, Benefits: true }, "Authorization request wire roundtrip failed.");
            // Adoption is a separate server-verified operation, never a side effect of issuing a grant.
            system.Config.TribeAuthorization.ProtectCriticalAreas = false;
            tribe.Authorizations.Clear();
            Call("RecordBuilding", guest, selected.Position); rayBlock = selected; rayTarget = null;
            placed = life.PlacedBuildings.Values.Single();
            Call("AdoptBuilding", chief); check(placed.TribeId == "", "Chief adopted a block whose placer has no construction rights.");
            Call("AuthorizePlayer", chief, Request());
            Call("AdoptBuilding", guest); check(placed.TribeId == tribe.Id, "Authorized placer could not actively adopt existing construction.");
            Call("RecordBuilding", guest, selected.Position);
            Call("RevokeAuthorization", chief, "guest");
            Call("RecordBuilding", guest, selected.Position); placed = life.PlacedBuildings.Values.Single();
            Call("AdoptBuilding", guest); check(placed.TribeId == "", "Unauthorized actor adopted a block.");
            Call("AuthorizePlayer", chief, Request()); block.Code = new("game:stone-granite");
            Call("AdoptBuilding", chief); check(placed.TribeId == "", "Stale block placement was adopted.");
            block.Code = new("game:planks-oak"); rayBlock = null;
            Call("AdoptBuilding", chief); check(placed.TribeId == "", "Adoption without ray target succeeded.");

            // Delegated rights: members only, bounded scope, expiry and revocation enforced server-side.
            tribe.Authorizations.Clear(); tribe.Members.Add("guest");
            var delegation = Request(); delegation.Construction = delegation.Benefits = false;
            delegation.Permissions = TribePermission.Outposts | TribePermission.Authorize | TribePermission.TransferLeader;
            Call("AuthorizePlayer", chief, delegation);
            check((TribePermission)Call("PermissionsFor", tribe, "guest")! == (delegation.Permissions & ~(TribePermission.Outposts | TribePermission.Territory)), "Chief failed to delegate member governance.");
            var privilege = Request(); privilege.Permissions = TribePermission.All;
            Call("AuthorizePlayer", guest, privilege);
            check(tribe.Authorizations.Single().Permissions == (delegation.Permissions & ~(TribePermission.Outposts | TribePermission.Territory)), "Delegate escalated their own management rights.");
            privilege.Name = life.Players["other"].FullName;
            Call("AuthorizePlayer", guest, privilege);
            check(!tribe.Authorizations.Any(a => a.Uid == "other"), "Delegate granted governance to an outsider.");
            privilege.Permissions = TribePermission.None;
            Call("AuthorizePlayer", guest, privilege);
            check(tribe.Authorizations.Any(a => a.Uid == "other"), "Delegate could not authorize a visitor.");
            Call("RevokeAuthorization", guest, "guest");
            check((TribePermission)Call("PermissionsFor", tribe, "guest")! != TribePermission.None, "Delegate revoked member governance.");
            tribe.Authorizations.First(a => a.Uid == "guest").ExpiresAt = 1;
            check((TribePermission)Call("PermissionsFor", tribe, "guest")! == TribePermission.None, "Expired delegation remained usable.");
            Call("AuthorizePlayer", chief, delegation);
            var post = new TribeOutpost { X = 1000, Y = 100, Z = 1000 }; tribe.Outposts.Add(post);
            tribe.OutpostsChangedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Call("RemoveOutpost", guest, post.Id); check(tribe.Outposts.Contains(post), "Outpost deletion bypassed cooldown.");
            tribe.OutpostsChangedAt = 0; Call("RemoveOutpost", guest, "missing");
            check(tribe.Outposts.Contains(post), "Stale outpost ID removed another outpost.");
            Call("RemoveOutpost", guest, post.Id);
            check(tribe.Outposts.Contains(post), "Removed outpost action must preserve legacy territory.");
            guestEntity.Pos.SetPos(1000,100,1000);
            Call("CreateOutpost", guest); check(tribe.Outposts.Count == 1, "Recreation bypassed shared outpost cooldown.");
            guestEntity.Pos.SetPos(102,100,100);
            Call("TransferLeader", guest, "other"); check(tribe.LeaderUid == "chief", "Leadership transferred to a nonmember.");
            Call("TransferLeader", guest, "guest");
            check(tribe.LeaderUid == "guest" && tribe.Authorizations.All(a => a.Permissions == TribePermission.None), "Leadership transfer retained old delegated rights.");
            tribe.LeaderUid = "chief"; Call("AuthorizePlayer", chief, delegation);
            tribes.RemoveMember(tribe, "guest"); tribe.Members.Add("guest");
            check((TribePermission)Call("PermissionsFor", tribe, "guest")! == TribePermission.None, "Leaving and rejoining restored old governance.");

            // Online-only roster remains scoped to membership, even with visitor authorization.
            online = [chief, guest, Player("other", new EntityPlayer { World = world })];
            var roster = (TribeRosterPacket)Call("BuildTribeRoster", chief)!;
            check(roster.Members.Single(m => m.Uid == "guest").Name == "account-guest(游戏访客)",
                "Roster must show account name followed by current character name.");
            var memberSnapshot = (CivilizationSnapshotPacket)Call("BuildCivilizationSnapshot", chief, false, "", "", false)!;
            check(memberSnapshot.Members.Single(m => m.Uid == "guest").Name == "account-guest(游戏访客)",
                "Tribe member list must show both names without changing authorization matching.");
            check(roster.Members.Count == 2 && roster.Members.All(m => m.Uid != "other") && roster.Members.All(m => m.PingMs == 123),
                "Roster leaked outsiders or did not convert ping seconds to ms.");
            check(((TribeRosterPacket)Call("BuildTribeRoster", (IServerPlayer)online[2])!).Members.Single().Uid == online[2].PlayerUID, "An unaffiliated player must see only themself.");
            online = [];
            check(((TribeRosterPacket)Call("BuildTribeRoster", chief)!).Members.Single().Uid == chief.PlayerUID,
                "The requesting player must be included even before the online list is populated.");
            Call("AuthorizePlayer", chief, Request());
            var notice = tribe.AuthorizationHistory.Last();
            check(!notice.Notified && discoveries == 0, "Offline authorization was marked delivered.");
            online = [guest];
            ((HashSet<string>)AccessTools.Field(type, "readyPlayers").GetValue(system)!).Add("guest");
            Call("NotifyPendingAuthorizations", guest);
            check(notice.Notified && discoveries == 1 && sounds == 1, "Recipient did not get native discovery and sound.");
            Call("NotifyPendingAuthorizations", guest);
            check(discoveries == 1 && sounds == 1, "Authorization notification repeated on reconnect.");
            online = []; Call("AuthorizePlayer", chief, Request()); var revoked = tribe.AuthorizationHistory.Last();
            Call("RevokeAuthorization", chief, "guest"); online = [guest];
            Call("NotifyPendingAuthorizations", guest);
            check(!revoked.Notified && discoveries == 1, "Revoked offline authorization generated a notification.");
            online = [];
            var reloaded = JsonConvert.DeserializeObject<CivilizationLifeState>(JsonConvert.SerializeObject(life))!;
            check(reloaded.PlacedBuildings.Count == life.PlacedBuildings.Count, "Building ownership persistence failed.");
        }
        finally { AccessTools.Field(type, "instance").SetValue(null, previous); }
        check(!JsonConvert.SerializeObject(new TribeAuthorizationConfig { ParchmentContractsEnabled=true }).Contains("ParchmentContractsEnabled"),
            "Retired parchment authorization must not appear in saved or administrator configuration.");
    }
}
