using HarmonyLib;
using Vintagestory.Server;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnCivilizationModSystem
{
    private void InstallHandshakePolicy()
    {
        harmony = new Harmony("vsscsmod.handshake");
        var target = AccessTools.Method(typeof(ServerMain), "CreatePacketIdentification", new[] { typeof(bool) });
        if (target == null)
        {
            sapi!.Logger.Error("Seraph Civilization Simulation could not install the native mod loading policy; server handshake restrictions are unavailable.");
            return;
        }
        harmony.Patch(target, postfix: new HarmonyMethod(typeof(VscnCivilizationModSystem), nameof(AddHandshakePolicy)));
    }

    private static void AddHandshakePolicy(ref Packet_Server __result)
    {
        var system = instance;
        var identification = __result?.Identification;
        if (system == null || identification == null || !system.config.ModPolicy.Enabled) return;

        if (system.config.ModPolicy.Mode == ModPolicyMode.Blacklist)
        {
            var blocked = BuildServerBlacklist(system.config.ModPolicy);
            if (blocked.Count == 0) return;
            var current = identification.ServerModIdBlackList ?? Array.Empty<string>();
            identification.SetServerModIdBlackList(current.Concat(blocked)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            return;
        }

        var allowed = BuildServerWhitelist(system.config.ModPolicy, system.serverRules, system.coreVersions);
        if (allowed.Count > 0)
        {
            identification.SetServerModIdWhiteList(allowed.ToArray());
            system.sapi?.Logger.Notification("Seraph Civilization Simulation native whitelist sent: {0} entries (core versions included).", allowed.Count);
        }
    }

    public static List<string> BuildServerBlacklist(ModPolicyConfig policy) =>
        policy.Mods.Select(rule => rule.ModId.Trim().ToLowerInvariant())
            .Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static List<string> BuildServerWhitelist(ModPolicyConfig policy, IEnumerable<ModRule> serverRules,
        IReadOnlyDictionary<string, string>? coreVersions = null)
    {
        var loadedRules = serverRules.ToList();
        var rules = policy.Mods.AsEnumerable();
        if (policy.IncludeServerMods) rules = rules.Concat(loadedRules);
        // The vanilla client uses substring matching, so ID@ allows any version; ID@* does not.
        var allowed = rules.Select(rule => rule.ModId.Trim().ToLowerInvariant() + "@" + (rule.Version.Trim() == "*" ? "" : rule.Version.Trim()))
            .Where(rule => rule.Length > 1 && rule.Contains('@'))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // The vanilla client also applies this whitelist to its core components.
        // They are intentionally kept out of serverRules because they are not
        // ordinary mods, so add the exact server versions explicitly.
        if (coreVersions != null)
        {
            foreach (var id in new[] { "game", "survival", "creative" })
                if (coreVersions.TryGetValue(id, out var version) && !string.IsNullOrWhiteSpace(version))
                    allowed.Add(id + "@" + version.Trim());
        }
        // The policy mod itself must remain enabled so its client handshake can run.
        if (!allowed.Any(rule => rule.StartsWith("vsscsmod@", StringComparison.OrdinalIgnoreCase)))
        {
            var ownVersion = loadedRules.FirstOrDefault(rule => rule.ModId.Equals("vsscsmod", StringComparison.OrdinalIgnoreCase))?.Version
                ?? typeof(VscnCivilizationModSystem).Assembly.GetName().Version?.ToString(3);
            if (!string.IsNullOrWhiteSpace(ownVersion)) allowed.Add("vsscsmod@" + ownVersion.Trim());
        }
        return allowed;
    }
}
