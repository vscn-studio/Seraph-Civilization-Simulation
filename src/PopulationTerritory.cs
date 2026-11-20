using ProtoBuf;

namespace SeraphCivilizationSimulation;

public sealed class PopulationTerritoryData
{
    public int AnchorX { get; set; }
    public int AnchorY { get; set; }
    public int AnchorZ { get; set; }
    public int Dimension { get; set; }
    public int LastMemberCount { get; set; }
    public long Revision { get; set; } = 1;
    // Keep a restored tier's deadline until it expires: leaving/rejoining cannot renew it.
    public Dictionary<int, long> TierDeadlinesUtc { get; set; } = [];
    public string Status { get; set; } = "等待自动扩张";
}

[ProtoContract]
public sealed class TerritoryShrinkView
{
    [ProtoMember(1)] public int MemberTier { get; set; }
    [ProtoMember(2)] public long ExpiresAtUtc { get; set; }
}

[ProtoContract]
public sealed class TerritoryBoundaryEdge
{
    [ProtoMember(1)] public int X1 { get; set; }
    [ProtoMember(2)] public int Z1 { get; set; }
    [ProtoMember(3)] public int X2 { get; set; }
    [ProtoMember(4)] public int Z2 { get; set; }
    [ProtoMember(5)] public int SurfaceY { get; set; }
    [ProtoMember(6)] public int BelowY { get; set; }
    [ProtoMember(7)] public int AboveY { get; set; }
    [ProtoMember(8)] public bool PendingShrink { get; set; }
}
