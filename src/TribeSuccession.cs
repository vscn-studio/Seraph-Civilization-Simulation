using Vintagestory.API.Server;

namespace SeraphCivilizationSimulation;

internal static class TribeSuccession
{
    internal const long Day = 86_400_000;
    internal const long Absence = 7 * Day;

    internal static void Record(TribeMemberActivity activity, long now, long elapsed)
    {
        activity.LastSeenUtc = now;
        long start = now - Math.Max(0, elapsed);
        if (start < now)
        {
            var last=activity.OnlinePeriods.LastOrDefault();
            if (last!=null && start<=last.EndUtc+1000)
            { last.StartUtc=Math.Min(last.StartUtc,start); last.EndUtc=Math.Max(last.EndUtc,now); }
            else activity.OnlinePeriods.Add(new() { StartUtc=start, EndUtc=now });
        }
        activity.OnlinePeriods.RemoveAll(period=>period.EndUtc<=now-Absence);
    }

    internal static string? Successor(TribeRecord tribe, long now, ISet<string> online, Func<string, bool> eligible)
    {
        if (online.Contains(tribe.LeaderUid) || !tribe.MemberActivity.TryGetValue(tribe.LeaderUid, out var leader)
            || now - leader.LastSeenUtc <= Absence) return null;
        return tribe.Members.Where(uid => uid != tribe.LeaderUid && eligible(uid)
                && tribe.MemberActivity.TryGetValue(uid, out var a) && now - a.LastSeenUtc < Absence)
            .Select(uid => new { Uid=uid, Activity=tribe.MemberActivity[uid],
                Score=tribe.MemberActivity[uid].OnlinePeriods.Sum(p => Math.Max(0,Math.Min(now,p.EndUtc)-Math.Max(now-Absence,p.StartUtc))) })
            .Where(p => p.Score > 0).OrderByDescending(p => p.Score).ThenByDescending(p => p.Activity.LastSeenUtc)
            .ThenBy(p => p.Uid, StringComparer.Ordinal).Select(p => p.Uid).FirstOrDefault();
    }
}

public sealed partial class VscnCivilizationModSystem
{
    private readonly Dictionary<string, long> activityTicks = [];
    private long successionListener;

    private void StartTribeSuccession()
        => successionListener = sapi!.Event.RegisterGameTickListener(_ => UpdateTribeSuccession(), 60_000);

    private void RecordTribeActivity(IServerPlayer player, bool disconnect = false)
    {
        long tick = Environment.TickCount64, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long elapsed = activityTicks.TryGetValue(player.PlayerUID, out long previous) ? Math.Clamp(tick - previous, 0, 120_000) : 0;
        if (disconnect) activityTicks.Remove(player.PlayerUID); else activityTicks[player.PlayerUID] = tick;
        if (tribeState.FindMember(player.PlayerUID) is not { } tribe) return;
        if (!tribe.MemberActivity.TryGetValue(player.PlayerUID, out var activity)) tribe.MemberActivity[player.PlayerUID] = activity = new();
        TribeSuccession.Record(activity, now, elapsed);
    }

    private void UpdateTribeSuccession()
    {
        if (disposed) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var online = Online.Select(p => p.PlayerUID).ToHashSet(StringComparer.Ordinal);
        foreach (var player in Online.Where(p => readyPlayers.Contains(p.PlayerUID))) RecordTribeActivity(player);
        bool changed = false;
        foreach (var tribe in tribeState.Tribes)
        {
            tribe.MemberActivity ??= [];
            // Old worlds have no reliable logout timestamp. Start their seven-day grace period here.
            foreach (string uid in tribe.Members)
                if (!tribe.MemberActivity.ContainsKey(uid)) tribe.MemberActivity[uid] = new() { LastSeenUtc=now };
            foreach (string uid in tribe.MemberActivity.Keys.Where(uid => !tribe.Members.Contains(uid)).ToArray()) tribe.MemberActivity.Remove(uid);
            string? next = TribeSuccession.Successor(tribe, now, online,
                uid => !lifeState.Players.TryGetValue(uid, out var identity) || identity.Living);
            if (next == null) continue;
            string previous = tribe.LeaderUid;
            ChangeTribeLeader(tribe, next);
            changed = true;
            sapi!.Logger.Notification("Seraph Civilization Simulation: tribe {0} automatically transferred leadership from {1} to {2} after seven days offline.", tribe.Id, previous, next);
            foreach (var player in Online.Where(p => tribe.Members.Contains(p.PlayerUID)))
                sapi.SendMessage(player, Vintagestory.API.Config.GlobalConstants.GeneralChatGroup,
                    $"{tribe.Name}：首领离线超过 7 天，已自动转让给近期最活跃成员 {PlayerName(next)}。", Vintagestory.API.Common.EnumChatType.Notification);
        }
        if (changed) { PruneMembershipRequests(); SaveState(); RefreshCivilizationViews(); }
    }

    private static void ChangeTribeLeader(TribeRecord tribe, string target)
    {
        tribe.LeaderUid = target;
        foreach (var grant in tribe.Authorizations) grant.Permissions = TribePermission.None;
    }
}
