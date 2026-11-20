using System.Text;
using ProtoBuf;

namespace SeraphCivilizationSimulation;

public sealed class CivilizationLifeState
{
    public Dictionary<string, CivilizationIdentity> Players { get; set; } = [];
    public Dictionary<string, TerritoryWear> BlockWear { get; set; } = [];
    public Dictionary<string, PlacedBuilding> PlacedBuildings { get; set; } = [];
    public double ProtectionSeconds { get; set; }
    public Dictionary<string, BuildingFireState> BuildingFires { get; set; } = [];
}

public sealed class CivilizationIdentity
{
    public string GameName { get; set; } = "";
    // Retained for existing world saves; old names continue to display without another name prompt.
    public string Surname { get; set; } = "";
    public string GivenName { get; set; } = "";
    public bool Accepted { get; set; }
    public int AdditionalLives { get; set; }
    public bool Living { get; set; } = true;
    public int LastDeaths { get; set; }
    public string RebornTribe { get; set; } = "";
    public ClusterPoint? RebornSpawn { get; set; }
    public ClusterPoint? ReincarnationHome { get; set; }
    [Newtonsoft.Json.JsonIgnore] public string FullName => string.IsNullOrEmpty(GameName) ? Surname + GivenName : GameName;
    public int Remaining(RespawnConfig config, int deaths) => config.InitialLives < 0 ? -1
        : (int)Math.Clamp((long)config.InitialLives + AdditionalLives - deaths, 0, int.MaxValue);

    public static bool ValidName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 32 || text != text.Trim()) return false;
        bool letterOrDigit = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune)) { letterOrDigit = true; continue; }
            if (rune.Value is 0x2d or 0x27 or 0xb7 or 0x5f or 0x20) continue;
            return false;
        }
        return letterOrDigit;
    }
}

public sealed class TerritoryWear
{
    public int BlockId { get; set; }
    public string TribeId { get; set; } = "";
    public int Damage { get; set; }
    public string BlockCode { get; set; } = "";
    public double FractionalDamage { get; set; }
    public double LastAttackSeconds { get; set; }
    public double LastRecoverySeconds { get; set; }
    public int Capacity { get; set; } = -1;
}

[ProtoContract]
public sealed class GuidancePacket
{
    [ProtoMember(1)] public string Nonce { get; set; } = "";
    [ProtoMember(2)] public string Title { get; set; } = "";
    [ProtoMember(3)] public string Settlement { get; set; } = "";
    [ProtoMember(4)] public string RulesHtml { get; set; } = "";
    [ProtoMember(5)] public string Error { get; set; } = "";
    [ProtoMember(6)] public bool Required { get; set; }
    [ProtoMember(7)] public int LivesLeft { get; set; }
}

[ProtoContract]
public sealed class GuidanceDecision
{
    [ProtoMember(1)] public string Nonce { get; set; } = "";
    [ProtoMember(2)] public bool Agree { get; set; }
    [ProtoMember(3)] public string Surname { get; set; } = "";
    [ProtoMember(4)] public string GivenName { get; set; } = "";
    [ProtoMember(5)] public string GameName { get; set; } = "";
}
