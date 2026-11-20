using Vintagestory.API.Common;

namespace SeraphCivilizationSimulation;

// True means restriction enabled. Each content switch is independent of the others.
public sealed class LoreRestrictionsConfig
{
    public bool Enabled { get; set; } = true;
    public bool OnlyWhenLoreDisabled { get; set; } = true;
    public bool RemoveExistingRestrictedEntities { get; set; } = true;
    public bool LivingDeadSpawns { get; set; } = true;
    public bool GreenWillowispSpawns { get; set; } = true;
    public bool WhiteWillowispSpawns { get; set; } = true;
    public bool YellowWillowispSpawns { get; set; } = true;
    public bool LivingDeadTemporalGearDrops { get; set; } = true;
    public bool SkullOfTheDead { get; set; } = true;
    public bool HandOfTheDead { get; set; } = true;
    public bool BoneSoilTemporalGearDrops { get; set; } = true;
    public bool BoneSoilLoreBookDrops { get; set; } = true;
    public bool BoneSoilLoreCylinderDrops { get; set; } = true;
    public bool TemporalAmuletTeleport { get; set; } = true;
    public bool TemporalWinterSkip { get; set; } = true;
    public bool EquusRevival { get; set; } = true;
    public bool ImmersiveTemporalMaps { get; set; } = true;
    public bool TemporalTemperatureForecaster { get; set; } = true;
    public bool ScribeClockmakerNotebookRecipe { get; set; } = true;
    public bool VinconomyCatalogRecipe { get; set; } = true;
    public bool CaravanTemporalGearSign { get; set; } = true;
    public bool ExpandedMatterTemporalMaterials { get; set; } = true;
    public bool ShipwrightLoreFigureheads { get; set; } = true;
    public bool HeraldryDrifterPattern { get; set; } = true;
    public bool HeraldryEidolonPattern { get; set; } = true;

    internal bool Active(bool loreEnabled) => Enabled && (!OnlyWhenLoreDisabled || !loreEnabled);

    internal bool EntityRestricted(AssetLocation? code)
    {
        if (code?.Domain != "primitivesurvival") return false;
        string p = code.Path;
        return LivingDeadSpawns && p.StartsWith("livingdead-", StringComparison.Ordinal)
            || GreenWillowispSpawns && p == "willowisp-green"
            || WhiteWillowispSpawns && p == "willowisp-white"
            || YellowWillowispSpawns && p == "willowisp-yellow"
            || SkullOfTheDead && p.StartsWith("skullofthedead-", StringComparison.Ordinal);
    }

    internal bool CollectibleRestricted(AssetLocation? code)
    {
        if (code == null) return false;
        string p = code.Path;
        return code.Domain switch
        {
            "primitivesurvival" => SkullOfTheDead && p.StartsWith("skullofthedead-", StringComparison.Ordinal)
                || HandOfTheDead && p.StartsWith("handofthedead-", StringComparison.Ordinal),
            "game" => TemporalAmuletTeleport && p == "clothes-neck-gear-amulet-temporal",
            "immersivemaps" => ImmersiveTemporalMaps && p.StartsWith("map-", StringComparison.Ordinal) && p.EndsWith("-temporal", StringComparison.Ordinal),
            "dynamicseasonaltemperature" => TemporalTemperatureForecaster && p == "temporaltempforecaster",
            "scribe" => ScribeClockmakerNotebookRecipe && p == "scribeclockmakernotebook",
            "vinconomy" => VinconomyCatalogRecipe && p == "catalog",
            "cartwrightscaravan" => CaravanTemporalGearSign && p.StartsWith("cartsign-", StringComparison.Ordinal) && p.EndsWith("-temporalgear", StringComparison.Ordinal),
            "em" => ExpandedMatterTemporalMaterials && p.Split('-').Contains("temporal"),
            "shipwright" => ShipwrightLoreFigureheads && p.Contains("figurehead-", StringComparison.Ordinal)
                && new[] { "eidolon", "erel", "seamonster", "siren", "thunderlord" }.Any(s => p.EndsWith("-" + s, StringComparison.Ordinal)),
            _ => false
        };
    }

    internal bool LivingDeadDropRestricted(AssetLocation? code) => code != null &&
        (LivingDeadTemporalGearDrops && code.Domain == "game" && code.Path == "gear-temporal"
         || code.Domain == "primitivesurvival" && (SkullOfTheDead && code.Path.StartsWith("skullofthedead-", StringComparison.Ordinal)
             || HandOfTheDead && code.Path.StartsWith("handofthedead-", StringComparison.Ordinal)));

    internal bool PanDropRestricted(string source, AssetLocation? code) => source.Contains("bonysoil", StringComparison.Ordinal)
        && code?.Domain == "game" && (BoneSoilTemporalGearDrops && code.Path == "gear-temporal"
        || BoneSoilLoreBookDrops && code.Path.StartsWith("lore-book-", StringComparison.Ordinal)
        || BoneSoilLoreCylinderDrops && code.Path is "tuningcylinder-5" or "tuningcylinder-8");

    internal bool PatternRestricted(string pattern) => HeraldryDrifterPattern && pattern == "drifter"
        || HeraldryEidolonPattern && pattern == "eidolon";
}
