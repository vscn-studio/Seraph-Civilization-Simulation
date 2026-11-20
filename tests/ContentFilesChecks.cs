using Newtonsoft.Json;
using Vintagestory.API.Common;
using SeraphCivilizationSimulation;

internal static class ContentFilesChecks
{
    internal static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "vscncs-content-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var firstWorld = new ModDataContentStore(root, "world one");
            var secondWorld = new ModDataContentStore(root, "world two");
            check(firstWorld.AnnouncementsPath != secondWorld.AnnouncementsPath
                && firstWorld.TutorialsPath != secondWorld.TutorialsPath,
                "Announcement and tutorial content must be isolated by savegame identifier.");
            check(firstWorld.AnnouncementsPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && !firstWorld.AnnouncementsPath.Contains("world one", StringComparison.Ordinal),
                "Savegame identifiers must not become raw filesystem path segments.");

            var command = new CmdArgs("tutorials add Tutorial 2026 | <p class='intro'>Multiple words</p>");
            command.PopWord("");
            command.PopWord("");
            bool parsed = VscnCivilizationModSystem.TryReadContentTitleAndHtml(command, out string title, out string html);
            check(title == "Tutorial 2026" && html == "<p class='intro'>Multiple words</p>",
                "Admin commands must preserve multiword titles and HTML attributes using the title/body delimiter.");
            check(parsed, "Admin content commands must require a title/body delimiter.");

            var announcement = new CmdArgs("announcements add --notify Scheduled Maintenance | <p class='notice'>Saturday maintenance</p>");
            announcement.PopWord(""); announcement.PopWord("");
            bool notify = announcement.PopWord("").Equals("--notify", StringComparison.OrdinalIgnoreCase);
            bool announcementParsed = VscnCivilizationModSystem.TryReadContentTitleAndHtml(announcement,
                out string announcementTitle, out string announcementHtml);
            check(notify && announcementParsed && announcementTitle == "Scheduled Maintenance"
                && announcementHtml == "<p class='notice'>Saturday maintenance</p>",
                "Announcement notification flags must coexist with multiword titles and HTML.");

            var malformedCommand = new CmdArgs("Tutorial without delimiter <p>body</p>");
            check(!VscnCivilizationModSystem.TryReadContentTitleAndHtml(malformedCommand, out _, out _),
                "Content commands without a delimiter must be rejected.");

            var initial = new TutorialState();
            initial.Entries.Add(new AnnouncementEntry { Id = "first", Title = "第一条", Html = "<p>内容一</p>" });
            firstWorld.Save(firstWorld.TutorialsPath, initial);
            var changed = new TutorialState { Revision = 1 };
            changed.Entries.Add(new AnnouncementEntry { Id = "second", Title = "第二条", Html = "<p>内容二</p>" });
            firstWorld.Save(firstWorld.TutorialsPath, changed);
            check(File.Exists(firstWorld.TutorialsPath + ".bak")
                && !File.Exists(firstWorld.TutorialsPath + ".tmp"),
                "Replacing content must retain a backup and remove the temporary file.");

            File.WriteAllText(firstWorld.TutorialsPath, "{ broken json");
            var recovered = firstWorld.Load<TutorialState>(firstWorld.TutorialsPath, state => state.Migrate(), out bool fromBackup);
            check(fromBackup && recovered.Entries.Single().Id == "first",
                "A malformed primary document must fall back to its last known good backup.");
            check(JsonConvert.DeserializeObject<TutorialState>(File.ReadAllText(firstWorld.TutorialsPath))!.Entries.Single().Id == "first",
                "Backup recovery must atomically restore the primary file.");

            File.WriteAllText(firstWorld.TutorialsPath, "{ broken json");
            var replacement = new TutorialState { Revision = 2 };
            replacement.Entries.Add(new AnnouncementEntry { Id = "third", Title = "第三条", Html = "<p>内容三</p>" });
            firstWorld.Save(firstWorld.TutorialsPath, replacement, state => state.Migrate());
            check(JsonConvert.DeserializeObject<TutorialState>(File.ReadAllText(firstWorld.TutorialsPath))!.Entries.Single().Id == "third"
                && JsonConvert.DeserializeObject<TutorialState>(File.ReadAllText(firstWorld.TutorialsPath + ".bak"))!.Entries.Single().Id == "first",
                "Replacing an invalid primary must keep the known-good backup intact.");

            File.Delete(firstWorld.TutorialsPath);
            var backupOnly = firstWorld.LoadOrCreate<TutorialState>(firstWorld.TutorialsPath,
                () => throw new Exception("A backup must take precedence over creating empty content."), state => state.Migrate());
            check(backupOnly.Entries.Single().Id == "first",
                "A backup must remain loadable when the primary content file is missing.");
            check(File.Exists(firstWorld.TutorialsPath),
                "A backup-only startup must restore the missing primary document.");

            string migratedPath = firstWorld.AnnouncementsPath;
            var migrated = firstWorld.LoadOrCreate(migratedPath,
                () => JsonConvert.DeserializeObject<AnnouncementState>("{\"Html\":\"<p>legacy</p>\",\"Revision\":1}")!,
                state => state.Migrate());
            check(migrated.Revision == 1 && migrated.Entries!.Single().Html == "<p>legacy</p>"
                && File.Exists(migratedPath),
                "Legacy serialized announcement state must migrate to a validated ModData JSON file.");

            var emptyAnnouncements = new AnnouncementState { Entries = [] };
            emptyAnnouncements.Migrate();
            check(emptyAnnouncements.Entries.Count == 0,
                "A new world must not create a blank legacy announcement entry.");


        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
