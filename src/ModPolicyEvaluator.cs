using Vintagestory.API.Common;

namespace SeraphCivilizationSimulation;

public static class ModPolicyEvaluator
{
    public static List<string> Validate(ModPolicyConfig policy, IReadOnlyList<ClientModItem> mods, IEnumerable<ModRule> serverRules, IReadOnlyDictionary<string, string> coreVersions)
    {
        var errors = new List<string>();
        if (mods.Count == 0 || mods.Count > 1024) return ["Missing or oversized mod inventory"];
        if (!mods.Any(m => m?.ModId == "vsscsmod")) errors.Add("Missing vsscsmod");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rules = policy.Mods.AsEnumerable();
        if (policy.Mode == ModPolicyMode.Whitelist && policy.IncludeServerMods) rules = rules.Concat(serverRules);
        var lookup = rules.ToLookup(r => r.ModId, StringComparer.OrdinalIgnoreCase);
        foreach (var m in mods)
        {
            if (m == null || !ModInfo.IsValidModID(m.ModId) || string.IsNullOrWhiteSpace(m.Version) || !seen.Add(m.ModId)) { errors.Add("Invalid/duplicate mod entry"); continue; }
            if (coreVersions.TryGetValue(m.ModId, out var expected))
            {
                if (!string.Equals(expected, m.Version, StringComparison.OrdinalIgnoreCase)) errors.Add($"Core version: {m.ModId}@{m.Version}");
                continue;
            }
            bool match = lookup[m.ModId].Any(r => r.Version.Trim() == "*" || r.Version == m.Version);
            if (policy.Mode == ModPolicyMode.Whitelist && !match) errors.Add($"Not allowed: {m.ModId}@{m.Version}");
            if (policy.Mode == ModPolicyMode.Blacklist && match) errors.Add($"Blocked: {m.ModId}@{m.Version}");
        }
        return errors;
    }
}
