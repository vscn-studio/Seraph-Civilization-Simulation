using System.Reflection;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using SeraphCivilizationSimulation;

internal static class UpdatesChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var config = JsonConvert.DeserializeObject<VscnConfig>("{\"Updates\":{\"FetchModDb\":true},\"Guidance\":{\"RulesHtml\":null}}")!;
        config.Validate();
        check(!JsonConvert.SerializeObject(config).Contains("RulesHtml") && !JsonConvert.SerializeObject(config).Contains("FetchModDb"),
            "Retired rules and update settings must be ignored when migrating old configuration.");
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var type = typeof(VscnCivilizationModSystem);
        check(type.GetMethod("InitializeUpdates", flags) == null && type.GetMethod("StartUpdateFetch", flags) == null,
            "Removed update logs must not start background fetching.");
        var system = new VscnCivilizationModSystem();
        var result = (TextCommandResult)type.GetMethod("HandleContentCommand", flags)!.Invoke(system, [new CmdArgs("updates list"), null])!;
        check(result.Status == EnumCommandStatus.Error, "Removed update commands must be rejected.");
        check(!File.Exists("assets/vsscsmod/config/releases.json"), "Removed release logs must not be packaged.");
    }
}
