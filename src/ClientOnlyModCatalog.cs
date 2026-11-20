using System.IO.Compression;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;

namespace SeraphCivilizationSimulation;

public static class ClientOnlyModCatalog
{
    public static List<ModRule> Read(IEnumerable<string> searchPaths, Action<string>? onWarning = null)
        => ReadInfo(searchPaths, onWarning).Select(m => new ModRule { ModId = m.ModId, Version = m.Version }).ToList();

    internal static List<InstalledModRecord> ReadInfo(IEnumerable<string> searchPaths, Action<string>? onWarning = null)
    {
        var rules = new List<InstalledModRecord>();
        foreach (var searchPath in searchPaths.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var source in Directory.EnumerateFileSystemEntries(searchPath))
            {
                if (!Directory.Exists(source) && !source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var info = ReadModInfo(source);
                    if (info == null || !string.Equals((string?)info["side"], "client", StringComparison.OrdinalIgnoreCase)) continue;
                    var id = ((string?)info["modid"])?.Trim();
                    var version = ((string?)info["version"])?.Trim();
                    if (!ModInfo.IsValidModID(id) || string.IsNullOrEmpty(version))
                    {
                        onWarning?.Invoke($"Skipping client mod with invalid ID or version: {source}");
                        continue;
                    }
                    rules.Add(new InstalledModRecord
                    {
                        ModId = id!,
                        Version = version!,
                        Name = (string?)info["name"] ?? id!,
                        Authors = info["authors"] is JArray authors ? string.Join(", ", authors.Values<string>()) : "",
                        Description = (string?)info["description"] ?? "",
                        Side = "Client"
                    });
                }
                catch (Exception ex)
                {
                    onWarning?.Invoke($"Skipping unreadable client mod {source}: {ex.Message}");
                }
            }
        }
        return rules;
    }

    private static JObject? ReadModInfo(string source)
    {
        if (Directory.Exists(source))
        {
            var manifest = Path.Combine(source, "modinfo.json");
            return File.Exists(manifest) ? JObject.Parse(File.ReadAllText(manifest)) : null;
        }
        using var archive = ZipFile.OpenRead(source);
        var entry = archive.GetEntry("modinfo.json");
        if (entry == null) return null;
        using var reader = new StreamReader(entry.Open());
        return JObject.Parse(reader.ReadToEnd());
    }
}
