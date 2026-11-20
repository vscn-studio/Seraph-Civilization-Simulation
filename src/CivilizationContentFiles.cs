using Newtonsoft.Json;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnCivilizationModSystem
{
    private ModDataContentStore? contentFiles;
    private AnnouncementState announcementState = new();
    private TutorialState tutorialState = new();
    private bool announcementContentReady = true;
    private bool tutorialContentReady = true;

    private void LoadCivilizationContent(byte[]? legacyAnnouncements, byte[]? legacyTutorials)
    {
        string identifier = sapi!.World.SavegameIdentifier?.ToString() ?? "";
        contentFiles = new ModDataContentStore(sapi.GetOrCreateDataPath("vscncs-content"), identifier);
        announcementContentReady = LoadContentFile(contentFiles.AnnouncementsPath, legacyAnnouncements,
            new AnnouncementState { Entries = [] }, state => state.Migrate(), value => announcementState = value, "announcements");
        tutorialContentReady = LoadContentFile(contentFiles.TutorialsPath, legacyTutorials,
            new TutorialState(), state => state.Migrate(), value => tutorialState = value, "tutorials");
    }

    private bool LoadContentFile<T>(string path, byte[]? legacy, T fallback, Action<T> validate, Action<T> setState, string label)
    {
        bool migrating = !File.Exists(path);
        try
        {
            T loaded = contentFiles!.LoadOrCreate(path, () => legacy is { Length: > 0 }
                ? JsonConvert.DeserializeObject<T>(System.Text.Encoding.UTF8.GetString(legacy))
                    ?? throw new InvalidDataException("Null legacy " + label + " state.")
                : fallback, validate);
            setState(loaded);
            if (migrating && legacy is { Length: > 0 })
            {
                string key = label == "announcements" ? AnnouncementStateKey : TutorialStateKey;
                sapi!.WorldManager.SaveGame.StoreData(key, []);
            }
            return true;
        }
        catch (Exception ex)
        {
            setState(fallback);
            sapi!.Logger.Error("Seraph Civilization Simulation {0} content could not be loaded from ModData; writes are disabled until fixed: {1}", label, ex);
            return false;
        }
    }

    private bool ReloadContentFile<T>(string path, Action<T> validate, Action<T> setState, string label)
    {
        try
        {
            T loaded = contentFiles!.Load<T>(path, validate, out bool recovered);
            setState(loaded);
            if (recovered) sapi!.Logger.Warning("Seraph Civilization Simulation recovered {0} content from its .bak file.", label);
            return true;
        }
        catch (Exception ex)
        {
            sapi!.Logger.Error("Seraph Civilization Simulation {0} content reload failed; current in-memory content was retained: {1}", label, ex.Message);
            return false;
        }
    }

    private bool ReloadAnnouncementContent()
    {
        bool loaded = ReloadContentFile<AnnouncementState>(contentFiles!.AnnouncementsPath, state => state.Migrate(), value => announcementState = value, "announcements");
        if (loaded) announcementContentReady = true;
        return loaded;
    }

    private bool ReloadTutorialContent()
    {
        bool loaded = ReloadContentFile<TutorialState>(contentFiles!.TutorialsPath, state => state.Migrate(), value => tutorialState = value, "tutorials");
        if (loaded) tutorialContentReady = true;
        return loaded;
    }
}
