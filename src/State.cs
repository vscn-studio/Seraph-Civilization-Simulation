using ProtoBuf;

namespace SeraphCivilizationSimulation;

public sealed class VscnState
{
    public List<ClusterPoint> Clusters { get; set; } = [];
    // Zero for legacy/completed worlds. Nonzero lets startup resume a partially published layout.
    public int PendingClusterTarget { get; set; }
    public int CurrentActiveIndex { get; set; }
    public int CurrentClusterPlayerCount { get; set; }
    public Dictionary<int, int> ClusterPlayerCounts { get; set; } = [];
    public Dictionary<string, int> PlayerAssignments { get; set; } = [];
}

public sealed class ResetState
{
    public bool JoinEnabled { get; set; }
    public int ResetsPerPlayer { get; set; }
    public Dictionary<string, int> UsedByPlayer { get; set; } = [];
    public long AllGeneration { get; set; }
    public Dictionary<string, long> AllApplied { get; set; } = [];
    public HashSet<string> PendingPlayers { get; set; } = [];

    public void Start(int n) { JoinEnabled = true; ResetsPerPlayer = n; UsedByPlayer.Clear(); }
    public void Stop() { JoinEnabled = false; }
    public bool NeedsReset(string uid) => PendingPlayers.Contains(uid) || AllApplied.GetValueOrDefault(uid) < AllGeneration || (JoinEnabled && UsedByPlayer.GetValueOrDefault(uid) < ResetsPerPlayer);
    public void Applied(string uid, bool joining)
    {
        PendingPlayers.Remove(uid);
        AllApplied[uid] = AllGeneration;
        if (joining && JoinEnabled && UsedByPlayer.GetValueOrDefault(uid) < ResetsPerPlayer) UsedByPlayer[uid] = UsedByPlayer.GetValueOrDefault(uid) + 1;
    }
}

public sealed class TribeState
{
    public List<TribeRecord> Tribes { get; set; } = [];
    public TribeRecord? FindMember(string uid) => Tribes.FirstOrDefault(t => t.Members.Contains(uid));
    public TribeRecord? Find(string id) => Tribes.FirstOrDefault(t => t.Id == id);

    public void RemoveMember(TribeRecord tribe, string uid, bool allowSolo = false)
    {
        tribe.Members.Remove(uid);
        tribe.MemberActivity.Remove(uid);
        foreach (var grant in tribe.Authorizations.Where(a => a.Uid == uid)) grant.Permissions = TribePermission.None;
        if (tribe.Members.Count == 0 || tribe.Members.Count == 1 && !tribe.AllowsSolo && !allowSolo)
        { Tribes.Remove(tribe); return; }
        if (tribe.Members.Count == 1 && allowSolo) tribe.AllowsSolo = true;
        if (tribe.LeaderUid == uid)
        {
            tribe.LeaderUid = tribe.Members[0];
            foreach (var grant in tribe.Authorizations) grant.Permissions = TribePermission.None;
        }
    }
}

public sealed class TribeRecord
{
    public Dictionary<string, TribeMemberActivity> MemberActivity { get; set; } = [];
    public PopulationTerritoryData? PopulationTerritory { get; set; }
    public int TerritoryVersion { get; set; }
    public List<BuildingTerritoryRegion> Regions { get; set; } = [];
    public List<RectangularTerritorySite> Sites { get; set; } = [];
    public List<TerritoryRoomObservation> RoomObservations { get; set; } = [];
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string LeaderUid { get; set; } = "";
    public List<string> Members { get; set; } = [];
    // Once permitted, a tribe may retain one member even if solo creation is later disabled.
    public bool AllowsSolo { get; set; }
    public int X { get; set; }
    public int Z { get; set; }
    public int Y { get; set; }
    public int Dimension { get; set; }
    public long OutpostsChangedAt { get; set; }
    public long LandExpandedAt { get; set; }
    public long CenterChangedAt { get; set; }
    public List<TribeOutpost> Outposts { get; set; } = [];
    public List<TribeAuthorization> Authorizations { get; set; } = [];
    public List<TribeAuthorizationHistory> AuthorizationHistory { get; set; } = [];
}

public sealed class TribeMemberActivity
{
    public long LastSeenUtc { get; set; }
    public List<TribeOnlinePeriod> OnlinePeriods { get; set; } = [];
}

public sealed class TribeOnlinePeriod
{
    public long StartUtc { get; set; }
    public long EndUtc { get; set; }
}

[Flags]
public enum TribePermission { None = 0, Outposts = 1, Authorize = 2, TransferLeader = 4, Invite = 8, Kick = 16, MoveCenter = 32, Territory = 64, All = 127 }

public sealed class TribeAuthorization
{
    public bool Pending { get; set; }
    public TribePermission Permissions { get; set; }
    public string Id { get; set; } = "";
    public string Uid { get; set; } = "";
    public long ExpiresAt { get; set; } = -1;
    public bool Construction { get; set; } = true;
    public bool Benefits { get; set; } = true;
    public bool Active(long now) => !Pending && (ExpiresAt == -1 || ExpiresAt > now);
}

public sealed class TribeAuthorizationHistory
{
    public long DurationMinutes { get; set; }
    public TribePermission Permissions { get; set; }
    public bool Notified { get; set; }
    public string Id { get; set; } = "";
    public string Uid { get; set; } = "";
    public string Name { get; set; } = "";
    public string SignerUid { get; set; } = "";
    public string SignerName { get; set; } = "";
    public long IssuedAt { get; set; }
    public long ExpiresAt { get; set; }
    public long EndedAt { get; set; }
    public string Status { get; set; } = "active";
    public bool Construction { get; set; }
    public bool Benefits { get; set; }
    // Empty for grants issued from the window. Signed text is authoritative, including revocation.
    public string ContractText { get; set; } = "";
}

[ProtoContract]
public sealed class TribeAuthorizationView
{
    [ProtoMember(13)] public TribePermission Permissions { get; set; }
    [ProtoMember(1)] public string Uid { get; set; } = "";
    [ProtoMember(2)] public string Name { get; set; } = "";
    [ProtoMember(3)] public string TribeName { get; set; } = "";
    [ProtoMember(4)] public long ExpiresAt { get; set; }
    [ProtoMember(5)] public bool Construction { get; set; }
    [ProtoMember(6)] public bool Benefits { get; set; }
    [ProtoMember(7)] public string Id { get; set; } = "";
    [ProtoMember(8)] public string Status { get; set; } = "active";
    [ProtoMember(9)] public long IssuedAt { get; set; }
    [ProtoMember(10)] public long EndedAt { get; set; }
    [ProtoMember(11)] public string SignerName { get; set; } = "";
    [ProtoMember(12)] public bool Parchment { get; set; }
}

public sealed class TribeOutpost
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public int Dimension { get; set; }
}

public sealed class TechnologyState
{
    public Dictionary<string, HashSet<string>> ObservedByPlayer { get; set; } = [];
    public Dictionary<string, HashSet<string>> ActionsByPlayer { get; set; } = [];
    public Dictionary<string, HashSet<string>> CompletedByPlayer { get; set; } = [];
    public Dictionary<string, HashSet<string>> CompletedByTribe { get; set; } = [];
    public Dictionary<string, HashSet<string>> AnnouncedByPlayer { get; set; } = [];
}

[ProtoContract]
public sealed class TechnologyNodeStatus
{
    [ProtoMember(1)] public string Id { get; set; } = "";
    [ProtoMember(2)] public bool Discovered { get; set; }
    [ProtoMember(3)] public bool Complete { get; set; }
    [ProtoMember(4)] public bool OutputReady { get; set; }
    [ProtoMember(5)] public bool ActionsReady { get; set; }
}

[ProtoContract]
public sealed class ModReportRequest
{
    [ProtoMember(1)] public string Nonce { get; set; } = "";
}

[ProtoContract]
public sealed class StatusCopyPacket
{
    [ProtoMember(1)] public string ReportId { get; set; } = "";
    [ProtoMember(2)] public string Report { get; set; } = "";
}

[ProtoContract]
public sealed class LivesResetPacket
{
    [ProtoMember(1)] public int LivesLeft { get; set; }
}

[ProtoContract]
public sealed class ClusterViewPacket
{
    [ProtoMember(1)]
    public List<ClusterViewItem> Clusters { get; set; } = [];
    [ProtoMember(2)]
    public int WorldSpawnX { get; set; }
    [ProtoMember(3)]
    public int WorldSpawnZ { get; set; }
}

[ProtoContract]
public sealed class ClusterViewItem
{
    [ProtoMember(1)]
    public int Index { get; set; }
    [ProtoMember(2)]
    public int X { get; set; }
    [ProtoMember(3)]
    public int Y { get; set; }
    [ProtoMember(4)]
    public int Z { get; set; }
    [ProtoMember(5)]
    public int PlayerCount { get; set; }
    [ProtoMember(6)]
    public string AssignmentMode { get; set; } = "";
    [ProtoMember(7)] public int OnlineCount { get; set; }
    [ProtoMember(8)] public int Radius { get; set; }
    [ProtoMember(9)] public int Capacity { get; set; }
    [ProtoMember(10)] public bool Active { get; set; }
    [ProtoMember(11)] public List<string> Players { get; set; } = [];
}

[ProtoContract]
public sealed class CivilizationRequestPacket
{
    [ProtoMember(1)] public string Action { get; set; } = "open";
    [ProtoMember(2)] public string Name { get; set; } = "";
    [ProtoMember(3)] public string Target { get; set; } = "";
    [ProtoMember(4)] public string Html { get; set; } = "";
    [ProtoMember(5)] public long Revision { get; set; }
    [ProtoMember(6)] public string RequestId { get; set; } = "";
    [ProtoMember(7)] public bool Notify { get; set; }
    [ProtoMember(8)] public bool IsMarkdown { get; set; }
    [ProtoMember(9)] public string DurationMinutes { get; set; } = "";
    [ProtoMember(10)] public bool Construction { get; set; }
    [ProtoMember(11)] public bool Benefits { get; set; }
    [ProtoMember(12)] public TribePermission Permissions { get; set; }
}

[ProtoContract]
public sealed class CivilizationSnapshotPacket
{
    // Removed map and membership-request fields retain reserved wire numbers: 1, 6, 8, 9, 10, 21, 28, 29, 30.
    [ProtoMember(12)] public bool Open { get; set; }
    [ProtoMember(2)] public string? MyTribeId { get; set; }
    [ProtoMember(3)] public string? MyTribeName { get; set; }
    [ProtoMember(4)] public bool IsLeader { get; set; }
    [ProtoMember(5)] public List<TribePerson> Members { get; set; } = [];
    [ProtoMember(7)] public List<TribeInviteView> Invitations { get; set; } = [];
    [ProtoMember(11)] public string Message { get; set; } = "";
    [ProtoMember(13)] public List<TechnologyNodeStatus> Technology { get; set; } = [];
    [ProtoMember(14)] public List<TribeTerritoryPoint> Territories { get; set; } = [];
    [ProtoMember(15)] public int TerritoryRadius { get; set; }
    [ProtoMember(16)] public int OutpostLimit { get; set; }
    [ProtoMember(17)] public long CenterCooldownRemainingSeconds { get; set; }
    [ProtoMember(18)] public int MyOutpostCount { get; set; }
    [ProtoMember(19)] public bool IsInTerritory { get; set; }
    [ProtoMember(20)] public List<TerritoryEffectView> TerritoryEffects { get; set; } = [];
    [ProtoMember(22)] public bool AllowSoloCreation { get; set; }
    [ProtoMember(23)] public string AnnouncementHtml { get; set; } = "";
    [ProtoMember(24)] public long AnnouncementRevision { get; set; }
    [ProtoMember(25)] public bool CanEditAnnouncement { get; set; }
    [ProtoMember(26)] public string AnnouncementSaveRequestId { get; set; } = "";
    [ProtoMember(27)] public bool AnnouncementSaved { get; set; }
    [ProtoMember(31)] public List<AnnouncementEntry> Announcements { get; set; } = [];
    [ProtoMember(32)] public string NotificationId { get; set; } = "";
    [ProtoMember(33)] public long NotificationRevision { get; set; }
    [ProtoMember(34)] public bool AnnouncementCollection { get; set; }
    [ProtoMember(35)] public List<AnnouncementEntry> Tutorials { get; set; } = [];
    [ProtoMember(36)] public long TutorialRevision { get; set; }
    [ProtoMember(37)] public bool CanEditTutorials { get; set; }
    [ProtoMember(38)] public bool CanApproveTutorials { get; set; }
    [ProtoMember(39)] public string TutorialSaveRequestId { get; set; } = "";
    [ProtoMember(40)] public bool TutorialSaved { get; set; }
    [ProtoMember(41)] public List<TribeAuthorizationView> Authorizations { get; set; } = [];
    [ProtoMember(42)] public List<TribeAuthorizationView> ReceivedAuthorizations { get; set; } = [];
    [ProtoMember(43)] public long ServerTime { get; set; }
    [ProtoMember(44)] public bool AuthorizationEnabled { get; set; }
    [ProtoMember(45)] public bool ConstructionAuthorizationEnabled { get; set; }
    [ProtoMember(46)] public bool BenefitAuthorizationEnabled { get; set; }
    // Protobuf field 47 is retired; do not reuse it.
    [ProtoMember(48)] public List<TribeAuthorizationView> AuthorizationHistory { get; set; } = [];
    [ProtoMember(49)] public List<TribeBoundaryView> Boundaries { get; set; } = [];
    [ProtoMember(50)] public bool BoundaryDisplayEnabled { get; set; } = true;
    [ProtoMember(51)] public TribePermission Permissions { get; set; }
    [ProtoMember(52)] public long OutpostCooldownRemainingSeconds { get; set; }
    [ProtoMember(53)] public List<TerritoryRegionView> Regions { get; set; } = [];
    [ProtoMember(54)] public List<TerritoryCandidateView> TerritoryCandidates { get; set; } = [];
    [ProtoMember(55)] public int TerritoryArea { get; set; }
    [ProtoMember(56)] public long TerritoryBudget { get; set; }
    [ProtoMember(57)] public bool HasMainHall { get; set; }
    [ProtoMember(58)] public List<TerritorySiteView> TerritorySites { get; set; } = [];
    [ProtoMember(59)] public long LandCooldownSeconds { get; set; }
    [ProtoMember(60)] public string TerritoryHint { get; set; } = "";
    [ProtoMember(61)] public int TerritoryMemberCount { get; set; }
    [ProtoMember(62)] public List<TerritoryShrinkView> TerritoryShrinkDeadlines { get; set; } = [];
    [ProtoMember(63)] public int TerritoryPendingShrinkArea { get; set; }
    [ProtoMember(64)] public long TerritoryRevision { get; set; }
}

[ProtoContract]
public sealed class TerritoryEffectView
{
    [ProtoMember(1)] public string Code { get; set; } = "";
    [ProtoMember(2)] public float Value { get; set; }
}

[ProtoContract]
public sealed class TribePerson
{
    [ProtoMember(3)] public bool IsLeader { get; set; }
    [ProtoMember(1)] public string Uid { get; set; } = "";
    [ProtoMember(2)] public string Name { get; set; } = "";
}

[ProtoContract]
public sealed class TribeInviteView
{
    [ProtoMember(1)] public string TribeId { get; set; } = "";
    [ProtoMember(2)] public string TribeName { get; set; } = "";
    [ProtoMember(3)] public string InviterName { get; set; } = "";
}

[ProtoContract]
public sealed class TribeTerritoryPoint
{
    [ProtoMember(7)] public string Id { get; set; } = "";
    [ProtoMember(1)] public string Name { get; set; } = "";
    [ProtoMember(2)] public int X { get; set; }
    [ProtoMember(3)] public int Y { get; set; }
    [ProtoMember(4)] public int Z { get; set; }
    [ProtoMember(5)] public int Dimension { get; set; }
    [ProtoMember(6)] public bool IsOutpost { get; set; }
}

[ProtoContract]
public sealed class TribeBoundaryView
{
    [ProtoMember(12)] public List<TerritoryBoundaryEdge> Edges { get; set; } = [];
    [ProtoMember(9)] public string RegionId { get; set; } = "";
    [ProtoMember(10)] public long Revision { get; set; }
    [ProtoMember(11)] public List<TerritoryRun> Cells { get; set; } = [];
    [ProtoMember(1)] public string TribeId { get; set; } = "";
    [ProtoMember(2)] public string Name { get; set; } = "";
    [ProtoMember(3)] public int X { get; set; }
    [ProtoMember(4)] public int Y { get; set; }
    [ProtoMember(5)] public int Z { get; set; }
    [ProtoMember(6)] public int Dimension { get; set; }
    [ProtoMember(7)] public int Radius { get; set; }
    [ProtoMember(8)] public bool IsOutpost { get; set; }
}

[ProtoContract]
public sealed class ClientModReportPacket
{
    [ProtoMember(1)]
    public string PlayerUid { get; set; } = "";
    [ProtoMember(2)]
    public string PlayerName { get; set; } = "";
    [ProtoMember(3)]
    public List<ClientModItem> Mods { get; set; } = [];
    [ProtoMember(4)] public string Nonce { get; set; } = "";
}

[ProtoContract]
public sealed class ClientModItem
{
    [ProtoMember(1)]
    public string ModId { get; set; } = "";
    [ProtoMember(2)]
    public string Version { get; set; } = "";
}
