using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace SeraphCivilizationSimulation;

internal static class ClusterMessages
{
    private const string Prefix = "vsscsmod:cluster.";

    public static string For(string language, string key, params object[] args) =>
        Lang.GetL(language, Prefix + key, args);

    public static void Send(IServerPlayer player, string key, params object[] args) =>
        player.SendMessage(GlobalConstants.GeneralChatGroup, For(player.LanguageCode, key, args), EnumChatType.Notification);
}
