using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.Common;
using SeraphCivilizationSimulation;

internal static class ServerAnnouncementChecks
{
    internal const string Example = "文字聊天设置修改：近距离16格->6格。需要测试：语音聊天模组。点击<a href=\"https://vintagestory.top/t/topic/897\">简单语音聊天（Voice Chat Mod）</a>了解基础用法。";

    internal static void Run(Action<bool, string> check)
    {
        var api = Proxy.Make<ICoreServerAPI>((m, _) => m.Name == "get_Side" ? EnumAppSide.Server : null);
        var commands = new ChatCommandApi(api);
        var chats = new List<string>();
        bool fail = false;
        commands.Create("announce").RequiresPrivilege(Privilege.announce).WithDescription("vanilla")
            .WithArgs(commands.Parsers.All("message"), commands.Parsers.OptionalInt("range"))
            .HandleWith(args => { chats.Add((string)args[0]); return fail ? TextCommandResult.Error("failed") : TextCommandResult.Success(); });
        commands.Create("announcenear").RequiresPrivilege(Privilege.announce).WithDescription("vanilla")
            .WithArgs(commands.Parsers.OptionalInt("range"), commands.Parsers.All("message"))
            .HandleWith(args => { chats.Add((string)args[1]); return TextCommandResult.Success(); });
        var notices = new List<(string Html, bool Nearby, int Range)>();
        using var relay = new ServerAnnouncementRelay();
        relay.Install(commands, (args, html, nearby) => notices.Add((html, nearby, nearby ? (int)args[0] : 0)));
        TextCommandResult Execute(string command, string text, bool allowed = true)
        {
            TextCommandResult? result = null;
            commands.Get(command).Execute(new TextCommandCallingArgs
            {
                RawArgs = new CmdArgs(text), Caller = new Caller { Type = EnumCallerType.Console,
                    CallerPrivileges = allowed ? [Privilege.announce] : [] }
            }, value => result = value);
            return result!;
        }

        check(Execute("announce", Example).Status == EnumCommandStatus.Success && chats.Single() == Example,
            "The real /announce parser must retain spaces, quotes and the original chat broadcast.");
        check(notices is [{ Nearby: false }] && notices[0].Html.Contains("href=\"https://vintagestory.top/t/topic/897\"")
            && HtmlDocumentApi.PlainText(notices[0].Html).Contains("16格->6格"),
            "Server notices must preserve the supplied HTML link and plain angle-bracket text.");
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, new ServerAnnouncementPacket { Html = notices[0].Html }); stream.Position = 0;
        check(Serializer.Deserialize<ServerAnnouncementPacket>(stream).Html == notices[0].Html,
            "HTML server notices must survive network serialization.");
        check(Execute("announce", Example, false).Status == EnumCommandStatus.Error && chats.Count == 1 && notices.Count == 1,
            "Unauthorized /announce must send neither chat nor HUD packets.");
        check(Execute("announce", new string('字', HtmlDocumentApi.MaxSourceLength + 1)).Status == EnumCommandStatus.Error
            && chats.Count == 1 && notices.Count == 1, "Oversized notices must fail before broadcasting.");
        fail = true;
        Execute("announce", "失败通知");
        check(notices.Count == 1, "A failed vanilla command must not produce a HUD notice.");
        fail = false;
        Execute("announcenear", "16 " + Example);
        check(notices[^1] is { Nearby: true, Range: 16 } && chats[^1] == Example,
            "Nearby notices must retain their native parsed radius and full HTML.");
        Execute("announce", "<script>alert(1)</script><b onclick='bad()'>维护</b><a href='javascript:bad()'>链接</a>");
        string markup = AnnouncementDom.Markup(notices[^1].Html);
        check(!markup.Contains("script", StringComparison.OrdinalIgnoreCase)
            && !markup.Contains("onclick"), "The client reader must sanitize relayed HTML and unsafe links.");
        const string rich = "<table><tr><td>表格</td></tr></table><details><summary>详情</summary><p>正文</p></details>";
        Execute("announce", rich);
        check(notices[^1].Html == rich && AnnouncementDom.Markup(notices[^1].Html).Contains("vscn-native"),
            "Relaying must preserve source HTML so tables and details are converted only once by the reader.");
        int published = notices.Count;
        relay.Dispose(); Execute("announce", "卸载后");
        check(notices.Count == published && chats[^1] == "卸载后", "Disposing the relay must restore vanilla handlers.");
    }
}
