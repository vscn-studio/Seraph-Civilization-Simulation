using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.Server;
using SeraphCivilizationSimulation;

internal static class ChatIdentityChecks
{
    public static void Run(CivilizationLifeState life, Action<bool, string> check)
    {
        const string uid = "chat-test";
        const string account = "AccountName";
        const string body = "AccountName: hello &lt;world&gt;";
        var identity = new CivilizationIdentity { GameName = "游戏 玩家", Accepted = true };
        life.Players[uid] = identity;
        bool staff = false;
        var entitlements = new List<Entitlement>();
        var player = Proxy.Make<IServerPlayer>((m, a) => m.Name switch
        {
            "get_PlayerUID" => uid, "get_PlayerName" => account, "get_Entitlements" => entitlements,
            "HasPrivilege" => staff && (string)a![0]! == "staffentitlement", _ => null
        });
        var method = AccessTools.Method(typeof(ServerSystemEntitySimulation), "EventManager_OnPlayerChat");
        var simulation = RuntimeHelpers.GetUninitializedObject(typeof(ServerSystemEntitySimulation));
        string Chat()
        {
            var consumed = new BoolRef();
            object?[] args = [player, 7, body, "original-data", consumed];
            method.Invoke(simulation, args);
            check((string)args[3]! == "original-data" && !consumed.value,
                "Chat identity must preserve metadata and native delivery.");
            check(((string)args[2]!).EndsWith(" " + body, StringComparison.Ordinal),
                "Chat identity must leave mentions, escaping and the message body untouched.");
            return (string)args[2]!;
        }
        try
        {
            check(Chat() == "<strong>游戏 玩家:</strong> " + body, "Ordinary native chat must display the configured game name.");
            staff = true;
            string styled = Chat();
            check(styled.Contains(" 游戏 玩家:</strong>") && styled.StartsWith("<font "),
                "Staff chat must keep native styling while displaying the game name.");
            staff = false;
            entitlements.Add(new Entitlement { Code = GlobalConstants.playerColorByEntitlement.Keys.First() });
            styled = Chat();
            check(styled.Contains("<strong>游戏 玩家:</strong>") && styled.StartsWith("<font "),
                "Entitlement chat must keep native styling while displaying the game name.");
            entitlements.Clear();
            identity.GameName = "即时 改名";
            check(Chat().StartsWith("<strong>即时 改名:</strong>"), "Chat must use an updated game name immediately.");
            identity.GameName = ""; identity.Surname = "王"; identity.GivenName = "文明";
            check(Chat().StartsWith("<strong>王文明:</strong>"), "Legacy split names must work in native chat.");
            identity.Accepted = false;
            check(Chat().StartsWith("<strong>AccountName:</strong>"), "Unconfirmed identities need the account fallback.");
            identity.Accepted = true; identity.GameName = "<bad>";
            check(Chat().StartsWith("<strong>AccountName:</strong>"), "Invalid saved names must not inject chat markup.");
            life.Players.Remove(uid);
            check(Chat().StartsWith("<strong>AccountName:</strong>") && !life.Players.ContainsKey(uid),
                "Unknown players must fall back without creating an identity.");
            check(player.PlayerName == account, "Chat must preserve the actual account identifier.");
        }
        finally { life.Players.Remove(uid); }
    }
}
