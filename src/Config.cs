using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace SeraphCivilizationSimulation;

[JsonConverter(typeof(StringEnumConverter))] public enum ModPolicyMode { Whitelist, Blacklist }
[JsonConverter(typeof(StringEnumConverter))] public enum ClusterAssignmentMode { Sequential, Balanced, Random }

public sealed class VscnConfig
{
    public bool DisableVintageCanvasArtistClass { get; set; } = true;
    public ModPolicyConfig ModPolicy { get; set; } = new();
    public RespawnConfig Respawn { get; set; } = new();
    public SleepInteractionConfig Sleep { get; set; } = new();
    public ClusterConfig Clusters { get; set; } = new();
    public TerritoryConfig Territory { get; set; } = new();
    public BuildingProtectionConfig BuildingProtection { get; set; } = new();
    public TribeConfig Tribes { get; set; } = new();
    public TribeAuthorizationConfig TribeAuthorization { get; set; } = new();
    public TribeRosterConfig Roster { get; set; } = new();
    public PaperMapsConfig PaperMaps { get; set; } = new();

    public void Validate()
    {
        if (ModPolicy == null || Respawn == null || Sleep == null || Clusters == null || Territory == null || Tribes == null) throw new ArgumentException("Configuration sections cannot be null.");
        Territory.Validate();
        (Roster ?? throw new ArgumentException("Missing roster settings.")).Validate();
        (TribeAuthorization ?? throw new ArgumentException("Missing authorization settings.")).Validate();
        (BuildingProtection ?? throw new ArgumentException("Missing building protection settings.")).Validate();
        Respawn.Validate();
        (PaperMaps ?? throw new ArgumentException("Missing paper map settings.")).Validate();
        if (!Enum.IsDefined(ModPolicy.Mode) || !Enum.IsDefined(Clusters.AssignmentMode)) throw new ArgumentException("Invalid mode.");
        if (ModPolicy.ReportTimeoutSeconds is < 5 or > 300 || Respawn.ProtectionSeconds is < 0 or > 86400) throw new ArgumentException("Invalid timeout/protection seconds.");
        if (ModPolicy.Mods == null || Clusters.ManualClusters == null) throw new ArgumentException("Lists cannot be null.");
        foreach (var m in ModPolicy.Mods)
        {
            if (m == null || !Vintagestory.API.Common.ModInfo.IsValidModID(m.ModId) || string.IsNullOrWhiteSpace(m.Version)) throw new ArgumentException("Every mod rule needs a valid ModId and an exact Version or '*'.");
        }
        if (Clusters.TotalClusters is < 1 or > 200 || Clusters.GlobalRadius is < 1 or > 1000000 || Clusters.ClusterRadius is < 1 or > 10000 || Clusters.MaxPlayersPerCluster < 1 || Clusters.MinDistanceBetween < 0 || Clusters.LandValidationRadius is < 0 or > 1000 || Clusters.MinLandRatio < 0 || Clusters.MinLandRatio > 1 || Clusters.MinTemperature > Clusters.MaxTemperature) throw new ArgumentException("Invalid cluster layout settings.");
        if (Clusters.ManualClusters.Any(c => c == null || c.X < 0 || c.Z < 0)) throw new ArgumentException("Manual coordinates must be absolute world coordinates.");
    }
}

public sealed class PaperMapsConfig
{
    public const int DefaultMaxSheets = 49;
    public const int MaximumMaxSheets = 256;
    public int MaxSheets { get; set; } = DefaultMaxSheets;

    public void Validate()
    {
        if (MaxSheets is < 2 or > MaximumMaxSheets) throw new ArgumentException("PaperMaps.MaxSheets must be between 2 and 256.");
    }
}

public sealed class TribeConfig
{
    public bool AllowSoloCreation { get; set; }
}

public sealed class TerritoryConfig
{
    [JsonIgnore] public BuildingTerritoryConfig Buildings { get; set; } = new();
    [JsonIgnore] public RectangularTerritoryConfig Rectangles { get; set; } = new();
    public bool BoundaryDisplayEnabled { get; set; } = true;
    public int ReinforcementPerLivingMember { get; set; } = 10;
    public int ReinforcementMaximum { get; set; } = 200;
    public double ReinforcementRecoveryPerMemberPerMinute { get; set; } = 0.2;
    public double ReinforcementRecoveryMaximumPerMinute { get; set; } = 4;
    public double ReinforcementRecoveryDelaySeconds { get; set; } = 600;
    public int CenterCooldownMinutes { get; set; } = 60;
    public int FirstOutpostMembers { get; set; } = 4;
    public int SecondOutpostMembers { get; set; } = 6;
    public int MaxOutposts { get; set; } = 2;
    public float HealthBonus { get; set; }

    public bool ShouldSerializeFirstOutpostMembers()=>false;
    public bool ShouldSerializeSecondOutpostMembers()=>false;
    public bool ShouldSerializeMaxOutposts()=>false;
    public int OutpostLimit(int members) => Math.Min(MaxOutposts,
        members >= SecondOutpostMembers ? 2 : members >= FirstOutpostMembers ? 1 : 0);

    public void Validate()
    {
        if (ReinforcementPerLivingMember is < 0 or > 10000 || ReinforcementMaximum is < 0 or > 100000) throw new ArgumentException("Invalid territory reinforcement.");
        if (!double.IsFinite(ReinforcementRecoveryPerMemberPerMinute) || ReinforcementRecoveryPerMemberPerMinute is < 0 or > 1000
            || !double.IsFinite(ReinforcementRecoveryMaximumPerMinute) || ReinforcementRecoveryMaximumPerMinute is < 0 or > 10000
            || !double.IsFinite(ReinforcementRecoveryDelaySeconds) || ReinforcementRecoveryDelaySeconds is < 0 or > 86400)
            throw new ArgumentException("Invalid reinforcement recovery.");
        if (CenterCooldownMinutes < 0 || FirstOutpostMembers < 1
            || SecondOutpostMembers <= FirstOutpostMembers || MaxOutposts is < 0 or > 2
            || !float.IsFinite(HealthBonus) || HealthBonus is < -100 or > 1000000)
            throw new ArgumentException("Invalid territory settings.");
    }
}

public sealed class ModPolicyConfig
{
    public bool Enabled { get; set; } = true;
    public ModPolicyMode Mode { get; set; } = ModPolicyMode.Whitelist;
    public bool IncludeServerMods { get; set; } = true;
    public int ReportTimeoutSeconds { get; set; } = 30;
    public List<ModRule> Mods { get; set; } = [];
}

public sealed class ModRule
{
    public string ModId { get; set; } = "";
    public string Version { get; set; } = "";
}

public sealed class RespawnConfig
{
    public bool Enabled { get; set; } = true;
    public int ProtectionSeconds { get; set; } = 10;
    public int InitialLives { get; set; } = 3;
    public int NewbornMinimumLivingMembers { get; set; } = 5;
    public double ReincarnationChance { get; set; } = 1;
    public double HomecomingMinimumChance { get; set; } = .5;
    public double HomecomingChancePerExtraLivingMember { get; set; } = .1;
    public int ReincarnationLives { get; set; } = 3;
    public int ReincarnationOutsideRange { get; set; } = 10;

    public void Validate()
    {
        if (InitialLives is < -1 or > 100000 || NewbornMinimumLivingMembers is < 1 or > 10000
            || !double.IsFinite(ReincarnationChance)
            || ReincarnationChance is < 0 or > 1 || ReincarnationLives is < 1 or > 100000
            || !double.IsFinite(HomecomingMinimumChance) || HomecomingMinimumChance is < 0 or > 1
            || !double.IsFinite(HomecomingChancePerExtraLivingMember) || HomecomingChancePerExtraLivingMember is < 0 or > 1
            || ReincarnationOutsideRange is < 1 or > 1000) throw new ArgumentException("Invalid civilization respawn settings.");
    }

    public double HomecomingChance(int livingMembers) => livingMembers < NewbornMinimumLivingMembers ? 0
        : Math.Min(1, HomecomingMinimumChance + (livingMembers - NewbornMinimumLivingMembers) * HomecomingChancePerExtraLivingMember);
}



public sealed class SleepInteractionConfig
{
    public bool Enabled { get; set; } = true;
}

public sealed class ClusterConfig
{
    public bool Enabled { get; set; } = true;
    public int TotalClusters { get; set; } = 10;
    public int GlobalRadius { get; set; } = 10000;
    public int ClusterRadius { get; set; } = 250;
    public int MinDistanceBetween { get; set; } = 1000;
    public int MaxPlayersPerCluster { get; set; } = 15;
    public int LandValidationRadius { get; set; } = 150;
    public float MinLandRatio { get; set; } = 0.6f;
    public bool AvoidOceans { get; set; } = true;
    public bool AvoidExtremeClimate { get; set; } = true;
    public float MinTemperature { get; set; } = -5;
    public float MaxTemperature { get; set; } = 35;
    public ClusterAssignmentMode AssignmentMode { get; set; } = ClusterAssignmentMode.Sequential;
    public List<ClusterPoint> ManualClusters { get; set; } = [];
}

public sealed class ClusterPoint
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
}
