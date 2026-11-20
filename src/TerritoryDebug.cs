using System.Globalization;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnCivilizationModSystem
{
    private readonly Dictionary<string, string> territoryDebugModes = [];
    private readonly Dictionary<string, int> territoryDebugMembers = [];

    private (bool Member, bool Home) TerritoryStatus(IServerPlayer player)
    {
        if (territoryDebugModes.TryGetValue(player.PlayerUID, out string? mode))
            return mode switch { "home" => (true, true), "away" => (true, false), _ => (false, false) };
        var tribe = tribeState.FindMember(player.PlayerUID);
        var pos = player.Entity?.Pos;
        return (tribe != null, pos != null && (tribe != null && WithinTerritory(tribe, pos.X, pos.Y, pos.Z, pos.Dimension)
            || tribeState.Tribes.Any(t => Authorized(t, player.PlayerUID, true) && WithinTerritory(t, pos.X, pos.Y, pos.Z, pos.Dimension))));
    }

    private int TerritoryOutpostLimit(TribeRecord tribe) => config.Territory.OutpostLimit(
        territoryDebugMembers.GetValueOrDefault(tribe.LeaderUid, tribe.Members.Count));

    private TextCommandResult HandleTerritoryDebug(CmdArgs words, IServerPlayer? player)
    {
        const string help = "/vsscsmod territory debug info|home|away|none|auto|reset|create <名称>|cooldown|move|set <配置字段> <值>";
        if (words.PopWord("") != "debug") return TextCommandResult.Error(help);
        if (player?.Entity == null) return TextCommandResult.Error("请由游戏内管理员执行领地调试指令。");
        string action = words.PopWord("info").ToLowerInvariant();
        switch (action)
        {
            case "home": case "away": case "none":
                territoryDebugModes[player.PlayerUID] = action;
                break;
            case "auto":
                territoryDebugModes.Remove(player.PlayerUID);
                break;
            case "reset":
                territoryDebugModes.Remove(player.PlayerUID);
                territoryDebugMembers.Remove(player.PlayerUID);
                break;
            case "create":
                return TextCommandResult.Success(CreateTribe(player, words.PopAll()));
            case "members": case "outpost":
                return TextCommandResult.Error("附庸地名额和调试操作已移除。");
            case "move":
                return TextCommandResult.Success(MoveCenter(player));
            case "cooldown":
                var tribe = tribeState.FindMember(player.PlayerUID);
                if (tribe?.LeaderUid != player.PlayerUID) return TextCommandResult.Error("需要先创建部落并成为首领。");
                tribe.CenterChangedAt = 0; tribe.LandExpandedAt=0;
                SaveState();
                break;
            case "set":
                string field = words.PopWord("");
                var property = typeof(TerritoryConfig).GetProperties().FirstOrDefault(p => p.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
                if (property == null || !property.CanWrite) return TextCommandResult.Error("未知 Territory 配置字段。");
                try
                {
                    var next = JsonConvert.DeserializeObject<TerritoryConfig>(JsonConvert.SerializeObject(config.Territory))!;
                    property.SetValue(next, Convert.ChangeType(words.PopWord(""), property.PropertyType, CultureInfo.InvariantCulture));
                    next.Validate();
                    config.Territory = next;
                }
                catch (Exception ex) { return TextCommandResult.Error("未修改配置: " + ex.Message); }
                break;
            case "info": break;
            default: return TextCommandResult.Error(help);
        }
        RefreshCivilizationViews();
        return TextCommandResult.Success(TerritoryDebugInfo(player));
    }

    private string TerritoryDebugInfo(IServerPlayer player)
    {
        var (member, home) = TerritoryStatus(player);
        var entity = player.Entity;
        var tribe = tribeState.FindMember(player.PlayerUID);
        return $"模式={territoryDebugModes.GetValueOrDefault(player.PlayerUID, "auto")}; 成员={member}; 领地内={home}; 部落={tribe?.Name ?? "无"}\n"
            + $"最终移动速度={entity.Stats.GetBlended("walkspeed"):P1}; 最大生命额外属性={entity.Stats.GetBlended("maxhealthExtraPoints"):0.##}\n"
            + $"套索接入={false}; 攻击倍率={1:0.##}; 套索概率增量={0:P0}\n"
            + $"领地面积={(tribe==null?0:TerritoryArea(tribe))}/{(tribe==null?0:TerritoryBudget(tribe))}";
    }
}
