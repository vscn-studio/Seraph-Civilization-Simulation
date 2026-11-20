using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnCivilizationModSystem
{
    private TextCommandResult ExportPlayerStatus(CmdArgs words, IServerPlayer? player)
    {
        if (player?.Entity == null) return TextCommandResult.Error("请在游戏内执行 /vsscsmod statuscopy，导出自己的状态。");
        if (!string.IsNullOrWhiteSpace(words.PopAll()))
            return TextCommandResult.Error("用法: /vsscsmod statuscopy（仅导出自己，不接受玩家名或其他参数）。");

        Guid reportId = Guid.NewGuid();
        string report;
        try { report = BuildPlayerStatusReport(player, reportId).ToString(Formatting.Indented); }
        catch (Exception ex)
        {
            sapi?.Logger.Error("Seraph Civilization Simulation statuscopy capture failed: {0}", ex);
            return TextCommandResult.Error("读取玩家状态失败: " + ex.Message);
        }

        var messages = new List<string>();
        bool failed = false;
        try { messages.Add("服务端状态日志已保存: " + PlayerStatusLog.Write(reportId, "server", report)); }
        catch (Exception ex)
        {
            failed = true;
            messages.Add("服务端状态日志保存失败: " + ex.Message);
            sapi?.Logger.Error("Seraph Civilization Simulation statuscopy server log failed: {0}", ex);
        }
        try
        {
            if (channel == null) throw new InvalidOperationException("模组网络通道未就绪。");
            channel.SendPacket(new StatusCopyPacket { ReportId = reportId.ToString("N"), Report = report }, player);
            messages.Add("已向你的客户端发送状态报告，客户端保存后会提示本地日志路径。");
        }
        catch (Exception ex)
        {
            failed = true;
            messages.Add("客户端状态报告发送失败: " + ex.Message);
            sapi?.Logger.Error("Seraph Civilization Simulation statuscopy send failed: {0}", ex);
        }
        string message = string.Join("\n", messages);
        return failed ? TextCommandResult.Error(message) : TextCommandResult.Success(message);
    }

    private JObject BuildPlayerStatusReport(IServerPlayer player, Guid reportId)
    {
        var (member, home) = TerritoryStatus(player);
        var tribe = tribeState.FindMember(player.PlayerUID);
        int? deaths = player.WorldData == null ? null : GetDeaths(player);
        string lives = sapi?.World.Config.GetString("playerlives", "-1") ?? "unknown";
        return new JObject
        {
            ["reportId"] = reportId.ToString("N"),
            ["capturedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["source"] = "server",
            ["modVersion"] = typeof(VscnCivilizationModSystem).Assembly.GetName().Version?.ToString(),
            ["player"] = PlayerStatusLog.CapturePlayer(player),
            ["deaths"] = deaths,
            ["playerLivesConfig"] = lives,
            ["remainingLives"] = int.TryParse(lives, out int limit) && limit >= 0 && deaths.HasValue
                ? new JValue(Math.Max(0, limit - deaths.Value)) : JValue.CreateNull(),
            ["territory"] = new JObject
            {
                ["tribeId"] = tribe?.Id,
                ["tribeName"] = tribe?.Name,
                ["member"] = member,
                ["inTerritory"] = home,
                ["debugMode"] = territoryDebugModes.GetValueOrDefault(player.PlayerUID, "auto"),
                ["expectedWalkspeedBonus"] = 0,
                ["config"] = JToken.FromObject(config.Territory)
            },
            ["respawnConfig"] = JToken.FromObject(config.Respawn),
            ["sleepConfig"] = JToken.FromObject(config.Sleep),
            ["integrations"] = new JObject
            {
                ["roping"] = false
            },
            ["loadedMods"] = PlayerStatusLog.CaptureMods(sapi)
        };
    }
}
