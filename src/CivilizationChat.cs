using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Common;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnCivilizationModSystem
{
    private static string GameNameKey(string name) => name.Normalize(NormalizationForm.FormKC);

    private static string ChatPlayerName(IPlayer player)
    {
        var system = instance;
        return system != null && !system.disposed
            && system.lifeState.Players.TryGetValue(player.PlayerUID, out var identity)
            && identity.Accepted && CivilizationIdentity.ValidName(identity.FullName)
                ? identity.FullName : player.PlayerName;
    }

    // Change only the native sender-name lookup. Keep native channel delivery, styling,
    // escaping, rate limits and the user's message (including mentions of account names).
    private static IEnumerable<CodeInstruction> UseGameNameInChat(IEnumerable<CodeInstruction> instructions)
    {
        var getter = AccessTools.PropertyGetter(typeof(IPlayer), nameof(IPlayer.PlayerName));
        var replacement = AccessTools.Method(typeof(VscnCivilizationModSystem), nameof(ChatPlayerName));
        int replacements = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(getter))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
                replacements++;
            }
            yield return instruction;
        }
        if (replacements == 0) throw new InvalidOperationException("Native chat sender name lookup was not found.");
    }
}
