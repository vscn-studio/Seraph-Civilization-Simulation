using System.Globalization;
using System.Reflection;
using Newtonsoft.Json.Linq;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using SeraphCivilizationSimulation;

internal static class StatusCopyChecks
{
    public static void Run(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "vscncs-status-checks-" + Guid.NewGuid().ToString("N"));
        string? previousLogPath = GamePaths.CustomLogPath;
        var previousCulture = CultureInfo.CurrentCulture;
        Directory.CreateDirectory(directory);
        try
        {
            GamePaths.CustomLogPath = directory;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Verify(check, directory);
        }
        finally
        {
            GamePaths.CustomLogPath = previousLogPath;
            CultureInfo.CurrentCulture = previousCulture;
            foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private static void Verify(Action<bool, string> check, string directory)
    {
        object? Invoke(object target, string method, params object?[] args) =>
            target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
        void Set(object target, string field, object value) =>
            target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

        var entity = new EntityPlayer { Alive = true };
        entity.Stats = new EntityStats(entity);
        entity.Stats.Set("walkspeed", "carryon:Hands", -.25f);
        entity.Stats.Set("walkspeed", "carryon:Back", -.15f, true);
        var health = new TreeAttribute();
        var injuries = new TreeAttribute();
        injuries.SetString("leftLeg", "fracture: \"重伤\"");
        health["hediffs"] = injuries;
        entity.WatchedAttributes["health"] = health;
        entity.WatchedAttributes.SetInt("downedphase", 2);
        entity.WatchedAttributes.SetBool("vscncsInjuryRespawn", true);
        var carry = new TreeAttribute();
        var hands = new TreeAttribute();
        hands.SetString("privateContainerContents", "must not be exported");
        carry["Hands"] = hands;
        entity.WatchedAttributes["carryon:Carried"] = carry;
        var player = Proxy.Make<IServerPlayer>((m, a) => m.Name switch
        {
            "get_Entity" => entity, "get_PlayerUID" => "self", "get_PlayerName" => "../玩家\"名",
            "HasPrivilege" => false, _ => null
        });
        var config = new TreeAttribute();
        config.SetString("playerlives", "3");
        var world = Proxy.Make<IServerWorldAccessor>((m, a) => m.Name == "get_Config" ? config : null);
        var logger = Proxy.Make<ILogger>((m, a) => null);
        var api = Proxy.Make<ICoreServerAPI>((m, a) => m.Name switch
        { "get_World" => world, "get_Logger" => logger, _ => null });
        var system = new VscnCivilizationModSystem();
        Set(system, "sapi", api);
        Set(system, "tribeState", new TribeState
        { Tribes = [new TribeRecord { Id = "own", Name = "自己的部落", Members = ["self", "chief"] }] });
        StatusCopyPacket? packet = null;
        IServerPlayer[]? recipients = null;
        int sends = 0;
        Set(system, "channel", Proxy.Make<IServerNetworkChannel>((m, a) =>
        {
            if (m.Name == "SendPacket")
            {
                packet = (StatusCopyPacket)a![0]!;
                recipients = (IServerPlayer[])a[1]!;
                sends++;
            }
            return null;
        }));

        TextCommandResult Command(string command, IServerPlayer? caller = null, EnumCallerType type = EnumCallerType.Player)
        {
            var parser = Proxy.Make<ICommandArgumentParser>((m, a) => m.Name == "GetValue" ? command : null);
            var commandCaller = new Caller { Type = type };
            if (caller != null) commandCaller.Player = caller;
            return (TextCommandResult)Invoke(system, "HandleCommand", new TextCommandCallingArgs
            { Caller = commandCaller, Parsers = [parser] })!;
        }
        string before = entity.WatchedAttributes.ToJsonToken();
        var result = Command("statuscopy", player);
        check(result.Status == EnumCommandStatus.Success, "Ordinary players must be able to export their own status.");
        check(sends == 1 && recipients?.Length == 1 && ReferenceEquals(recipients[0], player), "Status must be sent only to its authenticated caller.");
        string serverFile = Directory.GetFiles(directory, "*-server.log").Single();
        check(Path.GetDirectoryName(serverFile) == directory && !Path.GetFileName(serverFile).Contains("玩家"), "Logs must honor CustomLogPath and exclude player names from file names.");
        var report = JObject.Parse(File.ReadAllText(serverFile));
        check((string?)report["player"]?["uid"] == "self" && Math.Abs((float)report["player"]!["walkspeedPercent"]! - 60) < .001,
            "Server log must contain the caller and actual 60% final speed.");
        var contributors = (JArray)report["player"]!["stats"]!["walkspeed"]!["contributors"]!;
        check(contributors.Any(c => (string?)c["key"] == "carryon:Back" && (bool)c["persistent"]!
                && (float)c["weight"]! == 1 && Math.Abs((float)c["value"]! + .15) < .001),
            "Speed contributors must retain their values, weights and persistence.");
        check((string?)report["player"]?["stats"]?["walkspeed"]?["blendType"] == "WeightedSum"
                && (string?)report["player"]?["watchedAttributes"]?["health"]?["hediffs"]?["leftLeg"] == "fracture: \"重伤\"",
            "Nested injuries and blend mode must survive JSON escaping and locale changes.");
        check((int)report["player"]!["watchedAttributes"]!["downedphase"]! == 2
                && (bool)report["player"]!["watchedAttributes"]!["vscncsInjuryRespawn"]!
                && (string?)report["territory"]?["tribeName"] == "自己的部落",
            "Downed, injury respawn and own territory diagnostics must be present.");
        check(!File.ReadAllText(serverFile).Contains("privateContainerContents")
                && (bool)report["player"]!["carryOnSlotsOccupied"]!["Hands"]!, "Carry diagnostics must exclude container contents.");
        check(before == entity.WatchedAttributes.ToJsonToken() && Math.Abs(entity.Stats.GetBlended("walkspeed") - .6) < .001,
            "Export must not change attributes or recompute speed.");
        int fileCount = Directory.GetFiles(directory).Length;
        check(Command("statuscopy other", player).Status == EnumCommandStatus.Error
                && Command("statuscopy", type: EnumCallerType.Console).Status == EnumCommandStatus.Error
                && Command("resetall", player).Status == EnumCommandStatus.Error
                && Directory.GetFiles(directory).Length == fileCount,
            "Targets and console calls must be rejected while administrator commands remain protected.");

        using (var stream = new MemoryStream())
        {
            Serializer.Serialize(stream, packet!);
            stream.Position = 0;
            packet = Serializer.Deserialize<StatusCopyPacket>(stream);
        }
        var clientEntity = new EntityPlayer { Alive = true };
        clientEntity.Stats = new EntityStats(clientEntity);
        clientEntity.Stats.Set("walkspeed", "local", -.1f);
        var clientPlayer = Proxy.Make<IClientPlayer>((m, a) => m.Name switch
        { "get_Entity" => clientEntity, "get_PlayerUID" => "self", _ => null });
        var clientWorld = Proxy.Make<IClientWorldAccessor>((m, a) => m.Name == "get_Player" ? clientPlayer : null);
        var messages = new List<string>();
        var clientApi = Proxy.Make<ICoreClientAPI>((m, a) => m.Name switch
        { "get_World" => clientWorld, "get_Logger" => logger, "ShowChatMessage" => Chat(a!), _ => null });
        object? Chat(object?[] a) { messages.Add((string)a[0]!); return null; }
        var client = new VscnClientModSystem();
        Set(client, "capi", clientApi);
        Invoke(client, "OnStatusCopy", packet);
        string clientFile = Directory.GetFiles(directory, "*-client.log").Single();
        var clientReport = JObject.Parse(File.ReadAllText(clientFile));
        check(JToken.DeepEquals(clientReport["player"], report["player"])
                && (string?)clientReport["reportId"] == (string?)report["reportId"]
                && Math.Abs((float)clientReport["clientPlayer"]!["walkspeed"]! - .9) < .001,
            "Client must retain server snapshot and add its differing local snapshot after protobuf transfer.");
        check(messages.Last().Contains(clientFile), "Client must show its actual saved log path.");

        entity.WatchedAttributes.RemoveAttribute("health");
        check(Command("statuscopy", player).Status == EnumCommandStatus.Success
                && JObject.Parse(packet!.Report)["player"]!["watchedAttributes"]!["health"]!.Type == JTokenType.Null
                && Directory.GetFiles(directory, "*-server.log").Length == 2,
            "Missing health must not prevent another export or overwrite earlier reports.");

        string blocker = Path.Combine(directory, "blocked");
        File.WriteAllText(blocker, "blocks directory creation");
        GamePaths.CustomLogPath = blocker;
        int previousSends = sends;
        check(Command("statuscopy", player).Status == EnumCommandStatus.Error && sends == previousSends + 1,
            "Server write failure must be reported while client delivery is still attempted.");
        Invoke(client, "OnStatusCopy", packet);
        check(messages.Last().Contains("保存失败"), "Client write failure must not report success.");
        GamePaths.CustomLogPath = directory;
        fileCount = Directory.GetFiles(directory).Length;
        Invoke(client, "OnStatusCopy", new StatusCopyPacket { ReportId = "../outside", Report = "{}" });
        check(Directory.GetFiles(directory).Length == fileCount && messages.Last().Contains("失败"),
            "Remote report IDs must not become file paths.");
    }
}
