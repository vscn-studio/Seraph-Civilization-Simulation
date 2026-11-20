using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace SeraphCivilizationSimulation;

public sealed class ModDataContentStore
{
    private readonly string worldDirectory;

    public string AnnouncementsPath => Path.Combine(worldDirectory, "announcements.json");
    public string TutorialsPath => Path.Combine(worldDirectory, "tutorials.json");

    public ModDataContentStore(string contentPath, string savegameIdentifier)
    {
        if (string.IsNullOrWhiteSpace(savegameIdentifier)) throw new ArgumentException("Missing savegame identifier.", nameof(savegameIdentifier));
        string root = contentPath;
        worldDirectory = Path.Combine(root, "worlds", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(savegameIdentifier))).ToLowerInvariant());
        Directory.CreateDirectory(worldDirectory);
    }

    public T LoadOrCreate<T>(string path, Func<T> create, Action<T> validate)
    {
        if (File.Exists(path)) return Load<T>(path, validate, out _);
        string backup = path + ".bak";
        if (File.Exists(backup)) return Load<T>(path, validate, out _);
        T value = create();
        validate(value);
        Save(path, value, validate);
        return value;
    }

    public T Load<T>(string path, Action<T> validate, out bool recoveredFromBackup)
    {
        recoveredFromBackup = false;
        try { return Read<T>(path, validate); }
        catch (Exception original) when (File.Exists(path + ".bak"))
        {
            try
            {
                T recovered = Read<T>(path + ".bak", validate);
                RestoreBackup(path);
                recoveredFromBackup = true;
                return recovered;
            }
            catch (Exception backupError)
            {
                throw new InvalidDataException("Could not recover content file from its backup: " + path,
                    new AggregateException(original, backupError));
            }
        }
    }

    public void Save<T>(string path, T value, Action<T>? validate = null)
    {
        validate?.Invoke(value);
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory)) throw new InvalidOperationException("Content path has no directory.");
        Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonConvert.SerializeObject(value, Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(path))
            {
                bool primaryValid;
                try { Read<T>(path, validate ?? (_ => { })); primaryValid = true; }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException or ArgumentException)
                { primaryValid = false; }
                // Keep the last known-good backup when replacing an invalid primary file.
                if (primaryValid) File.Copy(path, path + ".bak", true);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void RestoreBackup(string path)
    {
        string temporary = path + ".tmp";
        try
        {
            File.Copy(path + ".bak", temporary, true);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static T Read<T>(string path, Action<T> validate)
    {
        T value = JsonConvert.DeserializeObject<T>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Null content document: " + path);
        validate(value);
        return value;
    }
}
