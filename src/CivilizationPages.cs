using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.Server;
using Vintagestory.Common;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnCivilizationModSystem
{
    private const string AnnouncementStateKey = "vscncsAnnouncement";
    private const string TutorialStateKey = "vscncsTutorial";
    private PaperMapDrawingCompatibility? paperMapCompatibility;
    private ImmersiveMapCompatibility? immersiveMapCompatibility;

    // Register legacy inventory classes so saved items can be returned on login.
    private static void ReturnLegacyMaps(IServerPlayer player)
    {
        for (int page = 1; page <= CivilizationMapInventory.MaxPages; page++)
            CivilizationMapInventory.GetPage(player.InventoryManager, page)?.Retire(player);
    }

    private bool PersistAnnouncements(AnnouncementState next)
    {
        try
        {
            if (!announcementContentReady || contentFiles == null) return false;
            contentFiles.Save(contentFiles.AnnouncementsPath, next, state => state.Migrate());
            announcementState = next;
            return true;
        }
        catch (Exception ex)
        {
            sapi!.Logger.Error("Seraph Civilization Simulation announcement save failed: {0}", ex);
            return false;
        }
    }

    private void SaveAnnouncement(IServerPlayer player, CivilizationRequestPacket request)
    {
        announcementState.Migrate();
        string? error = announcementState.ValidateSave(player.HasPrivilege(Privilege.controlserver), request.Revision, request.Html);
        var next = JsonConvert.DeserializeObject<AnnouncementState>(JsonConvert.SerializeObject(announcementState))!;
        bool create = request.Action == "announcement-create"
            || (request.Action == "announcement-save" && request.Target.Length == 0 && next.Entries!.Count == 0);
        bool delete = request.Action == "announcement-delete";
        var entry = request.Target.Length == 0 && !create ? next.Entries!.FirstOrDefault()
            : next.Entries!.FirstOrDefault(e => e.Id == request.Target);
        string title = request.Name.Trim();
        // Preserve the legacy single-notice protocol while upgrading its stored representation.
        if (title.Length == 0 && request.Target.Length == 0 && request.Action == "announcement-save") title = entry?.Title ?? "公告";
        if (error == null && !delete && (title.Length == 0 || title.Length > AnnouncementState.MaxTitleLength)) error = "announcement-title-invalid";
        if (error == null && create && next.Entries!.Count >= AnnouncementState.MaxAnnouncements) error = "announcement-limit";
        if (error == null && !create && entry == null) error = "announcement-missing";
        if (error == null)
        {
            next.Revision++;
            if (create) { entry = new() { Id = Guid.NewGuid().ToString("N") }; next.Entries!.Add(entry); }
            if (delete) next.Entries!.Remove(entry!);
            else
            {
                bool notifyChanged = entry!.Notify != request.Notify || (request.Notify && (entry.Html != request.Html || entry.IsMarkdown != request.IsMarkdown));
                entry.Title = title; entry.Html = request.Html; entry.Notify = request.Notify;
                entry.IsMarkdown = request.IsMarkdown;
                if (notifyChanged) entry.NotificationRevision = next.Revision;
                if (entry.Notify)
                    foreach (var other in next.Entries!.Where(e => e != entry)) other.Notify = false;
            }
            next.Html = next.Entries!.FirstOrDefault()?.Html ?? "";
            if (!PersistAnnouncements(next)) error = "announcement-save-failed";
            if (error == null)
                try { RefreshCivilizationViews(); }
                catch (Exception ex) { sapi!.Logger.Error("Seraph Civilization Simulation announcement broadcast failed: {0}", ex); }
        }
        SendCivilizationSnapshot(player, false, Ui(player, error ?? "announcement-saved"), request.RequestId, error == null);
    }

    private void SaveTutorial(IServerPlayer player, CivilizationRequestPacket request)
    {
        tutorialState.Migrate();
        bool admin = player.HasPrivilege(Privilege.controlserver);
        bool create = request.Action == "tutorial-create", delete = request.Action == "tutorial-delete";
        var next = JsonConvert.DeserializeObject<TutorialState>(JsonConvert.SerializeObject(tutorialState))!;
        var entry = next.Entries.FirstOrDefault(e => e.Id == request.Target);
        string title = request.Name.Trim();
        string? error = request.Revision != tutorialState.Revision ? "tutorial-conflict" : null;
        if (error == null && !delete && (request.Html == null || request.Html.Length > AnnouncementState.MaxLength)) error = "announcement-too-long";
        if (error == null && !delete && (title.Length == 0 || title.Length > AnnouncementState.MaxTitleLength)) error = "tutorial-title-invalid";
        if (error == null && create && next.Entries.Count >= TutorialState.MaxTutorials) error = "tutorial-limit";
        if (error == null && !create && entry == null) error = "tutorial-missing";
        if (error == null && !create && !admin && entry!.AuthorUid != player.PlayerUID) error = "tutorial-owner-only";
        if (error == null)
        {
            next.Revision++;
            if (create)
            {
                entry = new AnnouncementEntry { Id = Guid.NewGuid().ToString("N"), AuthorUid = player.PlayerUID, AuthorName = player.PlayerName };
                next.Entries.Add(entry);
            }
            if (delete) next.Entries.Remove(entry!);
            else
            {
                entry!.Title = title; entry.Html = request.Html ?? ""; entry.IsMarkdown = request.IsMarkdown;
                entry.AuthorUid = string.IsNullOrEmpty(entry.AuthorUid) ? player.PlayerUID : entry.AuthorUid;
                entry.AuthorName = string.IsNullOrEmpty(entry.AuthorName) ? player.PlayerName : entry.AuthorName;
                // Ordinary authors always enter moderation. Admins control public visibility.
                entry.Published = admin ? request.Notify : false;
            }
            if (!PersistTutorials(next)) error = "tutorial-save-failed";
            if (error == null) try { RefreshCivilizationViews(); } catch (Exception ex) { sapi!.Logger.Error("Seraph Civilization Simulation tutorial broadcast failed: {0}", ex); }
        }
        SendTutorialSnapshot(player, false, Ui(player, error ?? (delete ? "tutorial-deleted" : "tutorial-saved")), request.RequestId, error == null);
    }

    private bool PersistTutorials(TutorialState next)
    {
        try
        {
            if (!tutorialContentReady || contentFiles == null) return false;
            contentFiles.Save(contentFiles.TutorialsPath, next, state => state.Migrate());
            tutorialState = next;
            return true;
        }
        catch (Exception ex) { sapi!.Logger.Error("Seraph Civilization Simulation tutorial save failed: {0}", ex); return false; }
    }

    private void SendLoginAnnouncement(IServerPlayer player)
    {
        announcementState.Migrate();
        if (announcementState.PendingNotification(player.PlayerUID) is not { } entry) return;
        var snapshot = BuildCivilizationSnapshot(player, true);
        snapshot.NotificationId = entry.Id; snapshot.NotificationRevision = entry.NotificationRevision;
        channel!.SendPacket(snapshot, player);
    }

    private void AcknowledgeAnnouncement(IServerPlayer player, CivilizationRequestPacket request)
    {
        var entry = announcementState.PendingNotification(player.PlayerUID);
        if (entry == null || entry.Id != request.Target || entry.NotificationRevision != request.Revision) return;
        var next = JsonConvert.DeserializeObject<AnnouncementState>(JsonConvert.SerializeObject(announcementState))!;
        next.SeenNotifications[player.PlayerUID] = entry.NotificationRevision;
        PersistAnnouncements(next);
    }

}
