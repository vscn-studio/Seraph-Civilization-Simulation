using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnCivilizationModSystem
{
    private TextCommandResult HandleContentCommand(CmdArgs words, IServerPlayer? caller)
    {
        string type = words.PopWord("").ToLowerInvariant();
        string action = words.PopWord("").ToLowerInvariant();
        return type switch
        {
            "announcements" => HandleAnnouncementCommand(action, words),
            "tutorials" => HandleTutorialCommand(action, words, caller),
            _ => TextCommandResult.Error(ContentCommandUsage)
        };
    }

    private const string ContentCommandUsage = "用法: /vsscsmod content announcements list|add [--notify|--silent] <标题> | <HTML>|edit <ID> [--notify|--silent] <标题> | <HTML>|delete <ID>|clear；tutorials list|add <标题> | <HTML>|edit <ID> <标题> | <HTML>|delete <ID>|clear";

    private TextCommandResult HandleAnnouncementCommand(string action, CmdArgs words)
    {
        if (contentFiles == null || !announcementContentReady) return TextCommandResult.Error("公告文件尚未成功加载，请修复 JSON 后执行 /vsscsmod reload content announcements。");
        if (action == "list" && words.PopAll().Length == 0)
            return TextCommandResult.Success(ListContent("公告", announcementState.Entries!.Select(entry => (entry.Id, entry.Title))));
        if (action == "clear" && words.PopAll().Length == 0)
        {
            var next = Clone(announcementState);
            next.Entries!.Clear(); next.Html = ""; next.Revision++;
            return SaveAnnouncementState(next) ? TextCommandResult.Success("公告已清空并写入 ModData。")
                : TextCommandResult.Error("公告保存失败；保留原内容。");
        }

        string id = action == "edit" || action == "delete" ? words.PopWord("") : "";
        if (action == "delete")
        {
            if (id.Length == 0 || words.PopAll().Length != 0) return TextCommandResult.Error("用法: /vsscsmod content announcements delete <ID>");
            var next = Clone(announcementState);
            if (next.Entries!.RemoveAll(entry => entry.Id.Equals(id, StringComparison.Ordinal)) == 0)
                return TextCommandResult.Error("找不到该公告 ID。");
            next.Revision++; next.Html = next.Entries.FirstOrDefault()?.Html ?? "";
            return SaveAnnouncementState(next) ? TextCommandResult.Success("已删除公告 " + id + "。")
                : TextCommandResult.Error("公告保存失败；保留原内容。");
        }

        bool? notify = null;
        string first = words.PeekWord("");
        if (first.Equals("--notify", StringComparison.OrdinalIgnoreCase) || first.Equals("--silent", StringComparison.OrdinalIgnoreCase))
        {
            words.PopWord("");
            notify = first.Equals("--notify", StringComparison.OrdinalIgnoreCase);
        }
        bool parsed = TryReadContentTitleAndHtml(words, out string title, out string html);
        if (action is not ("add" or "edit") || !parsed || string.IsNullOrWhiteSpace(title) || title.Length > AnnouncementState.MaxTitleLength
            || html.Length > AnnouncementState.MaxLength || string.IsNullOrWhiteSpace(html))
            return TextCommandResult.Error("公告标题最多 40 字，HTML 最多 32768 字且不能为空；标题与 HTML 必须用 | 分隔。" + ContentCommandUsage);

        var state = Clone(announcementState);
        AnnouncementEntry? entry;
        if (action == "add")
        {
            if (state.Entries!.Count >= AnnouncementState.MaxAnnouncements) return TextCommandResult.Error("公告条目已达上限。");
            entry = new AnnouncementEntry { Id = Guid.NewGuid().ToString("N") };
            state.Entries.Add(entry);
        }
        else
        {
            entry = state.Entries!.FirstOrDefault(item => item.Id.Equals(id, StringComparison.Ordinal));
            if (entry == null) return TextCommandResult.Error("找不到该公告 ID。");
        }
        state.Revision++;
        bool nextNotify = notify ?? entry.Notify;
        bool notifyChanged = entry.Notify != nextNotify || (nextNotify && entry.Html != html);
        entry.Title = title; entry.Html = html; entry.Notify = nextNotify; entry.IsMarkdown = false;
        if (notifyChanged) entry.NotificationRevision = state.Revision;
        if (entry.Notify)
            foreach (var other in state.Entries!.Where(other => other != entry)) other.Notify = false;
        state.Html = state.Entries!.FirstOrDefault()?.Html ?? "";
        return SaveAnnouncementState(state) ? TextCommandResult.Success("公告已保存，ID: " + entry.Id)
            : TextCommandResult.Error("公告保存失败；保留原内容。");
    }

    private TextCommandResult HandleTutorialCommand(string action, CmdArgs words, IServerPlayer? caller)
    {
        if (contentFiles == null || !tutorialContentReady) return TextCommandResult.Error("教程文件尚未成功加载，请修复 JSON 后执行 /vsscsmod reload content tutorials。");
        if (action == "list" && words.PopAll().Length == 0)
            return TextCommandResult.Success(ListContent("教程", tutorialState.Entries.Select(entry => (entry.Id, entry.Title + (entry.Published ? "" : "（待审核）")))));
        if (action == "clear" && words.PopAll().Length == 0)
        {
            var next = Clone(tutorialState);
            next.Entries.Clear(); next.Revision++;
            return SaveTutorialState(next) ? TextCommandResult.Success("教程已清空并写入 ModData。")
                : TextCommandResult.Error("教程保存失败；保留原内容。");
        }

        string id = action == "edit" || action == "delete" ? words.PopWord("") : "";
        if (action == "delete")
        {
            if (id.Length == 0 || words.PopAll().Length != 0) return TextCommandResult.Error("用法: /vsscsmod content tutorials delete <ID>");
            var next = Clone(tutorialState);
            if (next.Entries.RemoveAll(entry => entry.Id.Equals(id, StringComparison.Ordinal)) == 0)
                return TextCommandResult.Error("找不到该教程 ID。");
            next.Revision++;
            return SaveTutorialState(next) ? TextCommandResult.Success("已删除教程 " + id + "。")
                : TextCommandResult.Error("教程保存失败；保留原内容。");
        }

        bool parsed = TryReadContentTitleAndHtml(words, out string title, out string html);
        if (action is not ("add" or "edit") || !parsed || string.IsNullOrWhiteSpace(title) || title.Length > AnnouncementState.MaxTitleLength
            || html.Length > AnnouncementState.MaxLength || string.IsNullOrWhiteSpace(html))
            return TextCommandResult.Error("教程标题最多 40 字，HTML 最多 32768 字且不能为空；标题与 HTML 必须用 | 分隔。" + ContentCommandUsage);

        var state = Clone(tutorialState);
        AnnouncementEntry? entry;
        if (action == "add")
        {
            if (state.Entries.Count >= TutorialState.MaxTutorials) return TextCommandResult.Error("教程条目已达上限。");
            entry = new AnnouncementEntry { Id = Guid.NewGuid().ToString("N"), AuthorUid = caller?.PlayerUID ?? "console",
                AuthorName = caller?.PlayerName ?? "Server Console" };
            state.Entries.Add(entry);
        }
        else
        {
            entry = state.Entries.FirstOrDefault(item => item.Id.Equals(id, StringComparison.Ordinal));
            if (entry == null) return TextCommandResult.Error("找不到该教程 ID。");
        }
        state.Revision++;
        entry.Title = title; entry.Html = html; entry.IsMarkdown = false; entry.Published = true;
        entry.AuthorUid = string.IsNullOrEmpty(entry.AuthorUid) ? caller?.PlayerUID ?? "console" : entry.AuthorUid;
        entry.AuthorName = string.IsNullOrEmpty(entry.AuthorName) ? caller?.PlayerName ?? "Server Console" : entry.AuthorName;
        return SaveTutorialState(state) ? TextCommandResult.Success("教程已发布并保存，ID: " + entry.Id)
            : TextCommandResult.Error("教程保存失败；保留原内容。");
    }

    private bool SaveAnnouncementState(AnnouncementState next)
    {
        next.Migrate();
        if (!PersistAnnouncements(next)) return false;
        try { RefreshCivilizationViews(); }
        catch (Exception ex) { sapi!.Logger.Error("Seraph Civilization Simulation announcement broadcast failed: {0}", ex); }
        return true;
    }

    private bool SaveTutorialState(TutorialState next)
    {
        next.Migrate();
        if (!PersistTutorials(next)) return false;
        try { RefreshCivilizationViews(); }
        catch (Exception ex) { sapi!.Logger.Error("Seraph Civilization Simulation tutorial broadcast failed: {0}", ex); }
        return true;
    }

    private TextCommandResult HandleReloadContent(CmdArgs words)
    {
        string target = words.PopWord("all").ToLowerInvariant();
        if (words.PopAll().Length != 0 || target is not ("all" or "announcements" or "tutorials"))
            return TextCommandResult.Error("用法: /vsscsmod reload content [all|announcements|tutorials]");
        if (contentFiles == null) return TextCommandResult.Error("ModData 内容目录尚未初始化。");

        var failures = new List<string>();
        bool civilizationContentChanged = false;
        if (target is "all" or "announcements")
        {
            if (!ReloadAnnouncementContent()) failures.Add("announcements");
            else civilizationContentChanged = true;
        }
        if (target is "all" or "tutorials")
        {
            if (!ReloadTutorialContent()) failures.Add("tutorials");
            else civilizationContentChanged = true;
        }
        if (civilizationContentChanged) NotifyCivilizationContentChanges();
        return failures.Count == 0 ? TextCommandResult.Success("ModData 内容已热重载并同步在线玩家。")
            : TextCommandResult.Error("重载失败: " + string.Join(", ", failures) + "；保留这些类别的内存内容。");
    }

    private void NotifyCivilizationContentChanges()
    {
        foreach (var player in Online)
        {
            try { SendCivilizationSnapshot(player, false); }
            catch (Exception ex) { sapi!.Logger.Warning("Seraph Civilization Simulation could not sync announcements and tutorials to {0}: {1}", player.PlayerUID, ex.Message); }
        }
    }

    private static string ListContent(string heading, IEnumerable<(string Id, string Title)> entries)
    {
        var lines = entries.Select(entry => $"{entry.Id}  {entry.Title}").ToArray();
        return lines.Length == 0 ? heading + "：暂无条目。" : heading + "（" + lines.Length + "）:\n" + string.Join("\n", lines);
    }

    internal static bool TryReadContentTitleAndHtml(CmdArgs words, out string title, out string html)
    {
        string remainder = words.PopAll();
        int separator = remainder.IndexOf('|');
        if (separator < 0)
        {
            title = "";
            html = "";
            return false;
        }
        title = remainder[..separator].Trim();
        html = remainder[(separator + 1)..].Trim();
        return title.Length > 0 && html.Length > 0;
    }

    private static T Clone<T>(T state) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(state))!;
}
