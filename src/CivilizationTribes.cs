using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnCivilizationModSystem
{
    private sealed record PendingInvite(string TribeId, string FounderUid, string Name, int X, int Z, string TargetUid, long ExpiresAt);
    private sealed record PendingFounding(string Id, string FounderUid, string Name, int X, int Y, int Z, int Dimension);
    private readonly List<PendingInvite> pendingInvites = [];
    private readonly List<PendingFounding> pendingFoundings = [];
    private const long RequestLifetimeMs = 120_000;

    private IEnumerable<IServerPlayer> Online => sapi!.World.AllOnlinePlayers.OfType<IServerPlayer>();
    private IServerPlayer? ByUid(string uid) => Online.FirstOrDefault(p => p.PlayerUID == uid);
    private string PlayerName(string uid) => lifeState.Players.TryGetValue(uid, out var identity) && identity.Accepted ? identity.FullName : ByUid(uid)?.PlayerName ?? sapi!.PlayerData.PlayerDataByUid.GetValueOrDefault(uid)?.LastKnownPlayername ?? uid;
    private string MemberDisplayName(string uid)
    {
        string? account = ByUid(uid)?.PlayerName;
        if (string.IsNullOrWhiteSpace(account)) account = sapi?.PlayerData?.PlayerDataByUid?.GetValueOrDefault(uid)?.LastKnownPlayername;
        if (string.IsNullOrWhiteSpace(account)) account = uid;
        return lifeState.Players.TryGetValue(uid, out var identity) && identity.Accepted && CivilizationIdentity.ValidName(identity.FullName)
            ? $"{account}({identity.FullName})" : account;
    }
    private string Ui(IServerPlayer player, string key, params object[] args) =>
        Lang.GetL(player.LanguageCode, "vsscsmod:ui." + key, args);

    private static double DistanceTo(IServerPlayer a, IServerPlayer b)
    {
        var p = a.Entity.Pos; var q = b.Entity.Pos;
        if (p.Dimension != q.Dimension) return double.PositiveInfinity;
        double dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private void RemovePendingTribeActions(string uid)
    {
        pendingInvites.RemoveAll(i => i.TargetUid == uid || i.FounderUid == uid && tribeState.Find(i.TribeId) == null);
        PruneFoundings();
    }

    private void PruneFoundings() => pendingFoundings.RemoveAll(f => !pendingInvites.Any(i => i.TribeId == f.Id));

    private void PruneMembershipRequests()
    {
        pendingInvites.RemoveAll(i => tribeState.Find(i.TribeId) is { } tribe && !HasTribePermission(tribe, i.FounderUid, TribePermission.Invite));
    }

    private void PruneRequests()
    {
        long now = Environment.TickCount64;
        pendingInvites.RemoveAll(i => i.ExpiresAt <= now);
        PruneFoundings();
    }

    private TextCommandResult HandleTribeCommand(CmdArgs words)
    {
        string action = words.PopWord("").ToLowerInvariant();
        if (action == "solo") return HandleSoloTribeCommand(words);
        if (action == "list")
        {
            if (tribeState.Tribes.Count == 0) return TextCommandResult.Success("当前没有部落。");
            var spawn = sapi!.World.DefaultSpawnPosition?.AsBlockPos;
            if (spawn == null) return TextCommandResult.Error("世界出生点尚未就绪，无法计算相对坐标。");
            return TextCommandResult.Success("部落列表（相对坐标以世界出生点为原点）：\n" + string.Join("\n",
                tribeState.Tribes.OrderBy(t => t.Name, StringComparer.Ordinal).Select((tribe, index) =>
                    $"{index + 1}. {tribe.Name} | 首领={PlayerName(tribe.LeaderUid)} | 人数={tribe.Members.Count} | "
                    + $"相对 X={tribe.X - spawn.X}, Z={tribe.Z - spawn.Z} | Y={tribe.Y} | 维度={tribe.Dimension} | 领地面积={TerritoryArea(tribe)}")));
        }
        if (action == "delete")
        {
            string name = words.PopAll().Trim();
            if (name.Length == 0) return TextCommandResult.Error("用法: /vsscsmod tribe delete <部落名>");
            var tribe = tribeState.Tribes.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (tribe == null) return TextCommandResult.Error($"未找到部落“{name}”。");
            tribeState.Tribes.Remove(tribe);
            pendingInvites.RemoveAll(i => i.TribeId == tribe.Id);
            territoryDebugMembers.Remove(tribe.LeaderUid);
            SaveState();
            RefreshCivilizationViews();
            return TextCommandResult.Success($"已解散部落“{tribe.Name}”（{tribe.Members.Count} 人）。");
        }
        return TextCommandResult.Error("用法: /vsscsmod tribe list|delete <部落名>|solo on|off|toggle|status");
    }

    private TextCommandResult HandleSoloTribeCommand(CmdArgs words)
    {
        string action = words.PopWord("status").ToLowerInvariant();
        bool? value = action switch
        {
            "on" or "enable" => true,
            "off" or "disable" => false,
            "toggle" => !config.Tribes.AllowSoloCreation,
            _ => null
        };
        if (words.PopAll().Trim().Length > 0 || value == null && action != "status")
            return TextCommandResult.Error("用法: /vsscsmod tribe solo on|off|toggle|status");
        if (value == null)
            return TextCommandResult.Success($"单人创建部落当前为{(config.Tribes.AllowSoloCreation ? "开启" : "关闭")}。");

        bool previous = config.Tribes.AllowSoloCreation;
        config.Tribes.AllowSoloCreation = value.Value;
        try { sapi!.StoreModConfig(config, ConfigFile); }
        catch (Exception ex)
        {
            config.Tribes.AllowSoloCreation = previous;
            sapi!.Logger.Error("Seraph Civilization Simulation: failed to save solo tribe configuration: {0}", ex);
            return TextCommandResult.Error("保存配置失败，保留原开关状态: " + ex.Message);
        }
        RefreshCivilizationViews();
        return TextCommandResult.Success($"单人创建部落已{(value.Value ? "开启" : "关闭")}并保存；已允许单人存在的部落会继续保留。");
    }

    private void OnCivilizationRequest(IServerPlayer player, CivilizationRequestPacket request)
    {
        if (disposed || player.Entity == null) return;
        if (request.Action is "announcement-save" or "announcement-create" or "announcement-delete") { SaveAnnouncement(player, request); return; }
        if (request.Action is "tutorial-save" or "tutorial-create" or "tutorial-delete") { SaveTutorial(player, request); return; }
        if (request.Action == "announcement-seen") { AcknowledgeAnnouncement(player, request); return; }
        if (request.Action == "close") return;
        if (request.Action == "roster") { SendTribeRoster(player); return; }
        if (request.Action == "territory-clear-preview") { sapi!.World.HighlightBlocks(player, 9041, []); return; }
        PruneRequests();
        if (request.Action == "open")
        {
            SendCivilizationSnapshot(player,true);return;
        }
        string result = request.Action switch
        {
            "create" => CreateTribe(player, request.Name),
            "rename" => RenameTribe(player, request.Name),
            "invite" => InviteNearby(player),
            "accept" => RespondInvite(player, request.Target, true),
            "decline" => RespondInvite(player, request.Target, false),
            "leave" => LeaveTribe(player),
            "kick" => KickMember(player, request.Target),
            "outpost" => "部落范围固定为 16×16 格。",
            "remove-outpost" => "附庸地管理已移除，已有领地保留。",
            "transfer-leader" => TransferLeader(player, request.Target),
            "adopt-building" => AdoptBuilding(player),
            "move-center" => MoveCenter(player),
            "territory-main" => RequestTerritoryDetection(player, "main", "", "room"),
            "territory-room" => RequestTerritoryDetection(player, "expand", "", "room"),
            "territory-migrate" => RequestTerritoryDetection(player, "migrate", request.Target, "room"),
            "territory-site-preview" => PreviewTerritory(player, request.Target),
            "territory-update" => RequestTerritoryDetection(player, "update", request.Target, "room"),
            "territory-legacy-outpost" => "部落范围固定为 16×16 格。",
            "territory-confirm" => ConfirmTerritory(player, request.Target),
            "territory-preview" => PreviewTerritory(player, request.Target),
            "territory-forget" => "部落范围不随人数变化。",
            "territory-convert" => "部落范围固定为 16×16 格。",
            "authorize" => AuthorizePlayer(player, request),
            "revoke-authorization" => RevokeAuthorization(player, request.Target),
            _ => Ui(player, "invalid-action")
        };
        SendCivilizationSnapshot(player, false, result);
    }

    private string CreateTribe(IServerPlayer founder, string name)
    {
        name = (name ?? "").Trim();
        if (name.Length is < 3 or > 10) return Ui(founder, "invalid-length-name");
        if (!name.All(IsChineseCharacter)) return Ui(founder, "invalid-name");
        if (tribeState.FindMember(founder.PlayerUID) != null) return Ui(founder, "already-member");
        bool solo = config.Tribes.AllowSoloCreation;
        if (!solo && pendingFoundings.Any(f => f.FounderUid == founder.PlayerUID)) return Ui(founder, "founding-pending");
        if (tribeState.Tribes.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            || pendingInvites.Any(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && (!solo || i.FounderUid != founder.PlayerUID))) return Ui(founder, "name-used");
        var candidates = solo ? new List<IServerPlayer>() : Online.Where(p => p.PlayerUID != founder.PlayerUID && p.Entity != null && tribeState.FindMember(p.PlayerUID) == null && DistanceTo(founder, p) <= 10).ToList();
        if (!solo && candidates.Count == 0) return Ui(founder, "no-nearby");
        string id = Guid.NewGuid().ToString("N");
        int x = (int)Math.Floor(founder.Entity.Pos.X), z = (int)Math.Floor(founder.Entity.Pos.Z);
        if (solo)
        {
            var created = new TribeRecord
            {
                Id = id, Name = name, LeaderUid = founder.PlayerUID, Members = [founder.PlayerUID], AllowsSolo = true,
                X = x, Y = (int)founder.Entity.Pos.Y, Z = z, Dimension = founder.Entity.Pos.Dimension,
                CenterChangedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            return BeginFounding(founder, created, () =>
            {
                RemovePendingTribeActions(founder.PlayerUID);
                RefreshCivilizationViews();
                SendCivilizationSnapshot(founder, false, Ui(founder, "solo-founded"));
            });
        }
        var proposed = new TribeRecord { Id = id, LeaderUid = founder.PlayerUID, Members = [founder.PlayerUID], X = x,
            Y = (int)founder.Entity.Pos.Y, Z = z, Dimension = founder.Entity.Pos.Dimension };
        if (CheckFoundingTerritory(proposed) is { } foundingFailure) return foundingFailure;
        pendingFoundings.Add(new(id, founder.PlayerUID, name, x, (int)founder.Entity.Pos.Y, z, founder.Entity.Pos.Dimension));
        foreach (var candidate in candidates) AddInvite(id, founder, name, x, z, candidate);
        return Ui(founder, "founding-invited", candidates.Count);
    }

    private string InviteNearby(IServerPlayer leader)
    {
        var tribe = tribeState.FindMember(leader.PlayerUID);
        if (tribe == null || !HasTribePermission(tribe, leader.PlayerUID, TribePermission.Invite)) return Ui(leader, "leader-only");
        var candidates = Online.Where(p => p.PlayerUID != leader.PlayerUID && p.Entity != null && tribeState.FindMember(p.PlayerUID) == null && DistanceTo(leader, p) <= 10).ToList();
        foreach (var candidate in candidates) AddInvite(tribe.Id, leader, tribe.Name, tribe.X, tribe.Z, candidate);
        return candidates.Count == 0 ? Ui(leader, "no-nearby") : Ui(leader, "invited-count", candidates.Count);
    }

    private string RenameTribe(IServerPlayer leader, string name)
    {
        var tribe = tribeState.FindMember(leader.PlayerUID);
        if (tribe == null || tribe.LeaderUid != leader.PlayerUID) return Ui(leader, "leader-only");
        name = (name ?? "").Trim();
        if (name.Length is < 3 or > 10) return Ui(leader, "invalid-length-name");
        if (!name.All(IsChineseCharacter)) return Ui(leader, "invalid-name");
        if (tribeState.Tribes.Any(t => t.Id != tribe.Id && t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            || pendingInvites.Any(i => i.TribeId != tribe.Id && i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return Ui(leader, "name-used");
        tribe.Name = name;
        for (int i = 0; i < pendingInvites.Count; i++)
            if (pendingInvites[i].TribeId == tribe.Id) pendingInvites[i] = pendingInvites[i] with { Name = name };
        SaveState(); RefreshCivilizationViews();
        return Ui(leader, "renamed", name);
    }

    private static bool IsChineseCharacter(char value) =>
        value is >= '\u3400' and <= '\u4DBF'
            or >= '\u4E00' and <= '\u9FFF'
            or >= '\uF900' and <= '\uFAFF';

    private void AddInvite(string id, IServerPlayer founder, string name, int x, int z, IServerPlayer target)
    {
        pendingInvites.RemoveAll(i => i.TribeId == id && i.TargetUid == target.PlayerUID);
        pendingInvites.Add(new(id, founder.PlayerUID, name, x, z, target.PlayerUID, Environment.TickCount64 + RequestLifetimeMs));
        target.SendMessage(GlobalConstants.GeneralChatGroup, Ui(target, "invite-notice", founder.PlayerName, name), EnumChatType.Notification);
        SendCivilizationSnapshot(target, false);
    }

    private string RespondInvite(IServerPlayer target, string id, bool accept)
    {
        var invite = pendingInvites.FirstOrDefault(i => i.TargetUid == target.PlayerUID && i.TribeId == id);
        if (invite == null) return Ui(target, "invite-expired");
        pendingInvites.Remove(invite);
        if (!accept) { PruneFoundings(); return Ui(target, "invite-declined"); }
        if (tribeState.FindMember(target.PlayerUID) != null)
        {
            PruneFoundings();
            return Ui(target, "already-member");
        }
        var tribe = tribeState.Find(id);
        if (tribe == null && pendingFoundings.FirstOrDefault(f => f.Id == id) is { } founding)
        {
            var founder = ByUid(founding.FounderUid);
            if (founder == null || tribeState.FindMember(founding.FounderUid) != null
                || tribeState.Tribes.Any(t => t.Name.Equals(founding.Name, StringComparison.OrdinalIgnoreCase)))
            {
                pendingInvites.RemoveAll(i => i.TribeId == id);
                pendingFoundings.Remove(founding);
                return Ui(target, "invite-expired");
            }
            tribe = new TribeRecord { Id = id, Name = founding.Name, LeaderUid = founding.FounderUid,
                Members = [founding.FounderUid, target.PlayerUID], X = founding.X, Y = founding.Y, Z = founding.Z,
                Dimension = founding.Dimension, CenterChangedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            return BeginFounding(founder, tribe, () =>
            {
                pendingFoundings.Remove(founding); pendingInvites.RemoveAll(i => i.TargetUid == target.PlayerUID);
                RefreshCivilizationViews();
                founder.SendMessage(GlobalConstants.GeneralChatGroup, Ui(founder, "founded"), EnumChatType.Notification);
                SendCivilizationSnapshot(target, false, Ui(target, "joined", tribe.Name));
            });
        }
        if (tribe == null || !HasTribePermission(tribe, invite.FounderUid, TribePermission.Invite)) return Ui(target, "invite-expired");
        if (!tribe.Members.Contains(target.PlayerUID)) tribe.Members.Add(target.PlayerUID);
        pendingInvites.RemoveAll(i => i.TargetUid == target.PlayerUID);
        SaveState(); RefreshCivilizationViews();
        return Ui(target, "joined", tribe.Name);
    }

    private string LeaveTribe(IServerPlayer player)
    {
        var tribe = tribeState.FindMember(player.PlayerUID);
        if (tribe == null) return Ui(player, "no-tribe");
        tribeState.RemoveMember(tribe, player.PlayerUID, config.Tribes.AllowSoloCreation);
        if (tribeState.Find(tribe.Id) == null) pendingInvites.RemoveAll(i => i.TribeId == tribe.Id);
        RemovePendingTribeActions(player.PlayerUID);
        PruneMembershipRequests();
        SaveState(); RefreshCivilizationViews();
        return Ui(player, tribeState.Find(tribe.Id) == null ? "left-dissolved" : "left");
    }

    private string KickMember(IServerPlayer leader, string uid)
    {
        var tribe = tribeState.FindMember(leader.PlayerUID);
        if (tribe == null || !HasTribePermission(tribe, leader.PlayerUID, TribePermission.Kick)) return Ui(leader, "leader-only");
        if (uid == leader.PlayerUID || uid == tribe.LeaderUid || !tribe.Members.Contains(uid)) return Ui(leader, "invalid-action");
        string name = PlayerName(uid);
        tribeState.RemoveMember(tribe, uid, config.Tribes.AllowSoloCreation);
        if (tribeState.Find(tribe.Id) == null) pendingInvites.RemoveAll(i => i.TribeId == tribe.Id);
        RemovePendingTribeActions(uid);
        PruneMembershipRequests();
        SaveState(); RefreshCivilizationViews();
        return Ui(leader, "kicked", name);
    }

    private void RefreshCivilizationViews()
    {
        InvalidateEconomyViews();
        ReindexTerritories();
        UpdateTerritoryEffects();
        foreach (var player in Online) SendCivilizationSnapshot(player, false);
    }

    private List<TribeTerritoryPoint> BuildTerritoryPoints(TribeRecord? tribe) => tribe == null ? [] :
        new[] { new TribeTerritoryPoint { Name = tribe.Name, X = tribe.X, Y = tribe.Y, Z = tribe.Z, Dimension = tribe.Dimension } }
        .ToList();

    private List<TribeBoundaryView> BuildTerritoryBoundaries(IServerPlayer player) => !config.Territory.BoundaryDisplayEnabled ? [] :
        RectangleBoundaries(player)
            .OrderBy(b => Math.Pow(b.X - player.Entity.Pos.X, 2) + Math.Pow(b.Y - player.Entity.Pos.Y, 2) + Math.Pow(b.Z - player.Entity.Pos.Z, 2)).Take(128).ToList();

    private string CreateOutpost(IServerPlayer leader) => RequestTerritoryDetection(leader, "outpost", "", "room");
    private string MoveCenter(IServerPlayer leader) => RequestTerritoryDetection(leader, "move", "", "room");

    private void SendCivilizationSnapshot(IServerPlayer player, bool open, string message = "", string announcementRequestId = "", bool announcementSaved = false)
        => channel!.SendPacket(BuildCivilizationSnapshot(player, open, message, announcementRequestId, announcementSaved), player);

    private void SendTutorialSnapshot(IServerPlayer player, bool open, string message, string requestId, bool saved)
        => channel!.SendPacket(BuildCivilizationSnapshotInternal(player, open, message, "", false, requestId, saved), player);

    private CivilizationSnapshotPacket BuildCivilizationSnapshot(IServerPlayer player, bool open, string message = "", string announcementRequestId = "", bool announcementSaved = false)
        => BuildCivilizationSnapshotInternal(player, open, message, announcementRequestId, announcementSaved, "", false);

    private CivilizationSnapshotPacket BuildCivilizationSnapshotInternal(IServerPlayer player, bool open, string message, string announcementRequestId, bool announcementSaved,
        string tutorialRequestId = "", bool tutorialSaved = false)
    {
        var mine = tribeState.FindMember(player.PlayerUID);
        var (member, home) = TerritoryStatus(player);
        var snapshot = new CivilizationSnapshotPacket
        {
            Open = open, Message = message,
            ServerTime = AuthorizationNow,
            AuthorizationEnabled = config.TribeAuthorization.Enabled,
            ConstructionAuthorizationEnabled = config.TribeAuthorization.ConstructionEnabled,
            BenefitAuthorizationEnabled = config.TribeAuthorization.BenefitsEnabled,
            Authorizations = mine != null && HasTribePermission(mine, player.PlayerUID, TribePermission.Authorize) ? mine.Authorizations.Where(a => a.Pending || a.Active(AuthorizationNow)).Select(a => AuthorizationView(mine, a)).ToList() : [],
            ReceivedAuthorizations = tribeState.Tribes.SelectMany(t => t.Authorizations.Where(a => a.Uid == player.PlayerUID && a.Active(AuthorizationNow)).Select(a => AuthorizationView(t, a))).ToList(),
            AuthorizationHistory = tribeState.Tribes.SelectMany(t => t.AuthorizationHistory
                .Where(h => HasTribePermission(t, player.PlayerUID, TribePermission.Authorize) || h.Uid == player.PlayerUID).Select(h => AuthorizationHistoryView(t, h)))
                .OrderByDescending(h => h.IssuedAt).Take(100).ToList(),
            AnnouncementCollection = true,
            AnnouncementHtml = announcementState.Html, AnnouncementRevision = announcementState.Revision,
            Announcements = announcementState.Entries?.Select(e => new AnnouncementEntry { Id = e.Id, Title = e.Title,
                Html = e.Html, IsMarkdown = e.IsMarkdown, Notify = e.Notify, NotificationRevision = e.NotificationRevision }).ToList() ?? [],
            CanEditAnnouncement = player.HasPrivilege(Privilege.controlserver),
            Tutorials = tutorialState.Entries.Where(e => e.Published || e.AuthorUid == player.PlayerUID || player.HasPrivilege(Privilege.controlserver))
                .Select(e => new AnnouncementEntry { Id = e.Id, Title = e.Title, Html = e.Html, IsMarkdown = e.IsMarkdown,
                    AuthorUid = e.AuthorUid, AuthorName = e.AuthorName, Published = e.Published }).ToList(),
            TutorialRevision = tutorialState.Revision,
            CanEditTutorials = true,
            CanApproveTutorials = player.HasPrivilege(Privilege.controlserver),
            AnnouncementSaveRequestId = announcementRequestId, AnnouncementSaved = announcementSaved,
            TutorialSaveRequestId = tutorialRequestId, TutorialSaved = tutorialSaved,
            AllowSoloCreation = config.Tribes.AllowSoloCreation,
            MyTribeId = mine?.Id, MyTribeName = mine?.Name,
            IsLeader = mine?.LeaderUid == player.PlayerUID,
            Permissions = mine == null ? TribePermission.None : PermissionsFor(mine, player.PlayerUID),
            OutpostCooldownRemainingSeconds = 0,
            TerritoryRadius = 0,
            BoundaryDisplayEnabled = config.Territory.BoundaryDisplayEnabled,
            OutpostLimit = 0,
            MyOutpostCount = 0,
            IsInTerritory = home,
            TerritoryEffects = BuildCivilizationEffects(player, member, home),
            CenterCooldownRemainingSeconds = mine == null ? 0 : Math.Max(0, (mine.CenterChangedAt + config.Territory.CenterCooldownMinutes * 60_000L - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 999) / 1000),
            Territories = BuildTerritoryPoints(mine),
            Boundaries = BuildTerritoryBoundaries(player),
            Members = mine?.Members.Select(uid => new TribePerson { Uid = uid, Name = MemberDisplayName(uid), IsLeader = uid == mine.LeaderUid }).ToList() ?? [],
            Invitations = pendingInvites.Where(i => i.TargetUid == player.PlayerUID).Select(i => new TribeInviteView { TribeId = i.TribeId, TribeName = i.Name, InviterName = PlayerName(i.FounderUid) }).ToList(),
        };
        FillBuildingTerritorySnapshot(player, snapshot, mine);
        return snapshot;
    }
}
