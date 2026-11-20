using System.Text;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;

namespace SeraphCivilizationSimulation;

internal static class PlayerStatusLog
{
    // Only diagnostic state is copied; carried containers' contents are excluded.
    private static readonly string[] AttributeNames =
    [
        "health", "hunger", "bodyTemp", "intoxication", "intoxicationTime",
        "tolerance", "detoxicants", "dead", "sleeping", "vscncsInjuryRespawn",
        "downed", "downedphase", "downedphasestartms", "downedpassedoutstartms",
        "downedSecondsLeft", "downedpvp", "downeddied", "downedexecuted",
        "dragged", "dragging", "carried", "stowed", "executing", "beingexecuted"
    ];

    internal static JObject? CapturePlayer(IPlayer? player)
    {
        if (player?.Entity == null) return null;
        var entity = player.Entity;
        var attributes = new JObject();
        foreach (string name in AttributeNames) attributes[name] = CaptureAttribute(entity.WatchedAttributes[name]);

        var stats = new JObject();
        if (entity.Stats != null)
        {
            foreach (var category in entity.Stats)
            {
                var contributors = new JArray();
                foreach (var item in category.Value.ValuesByKey)
                    contributors.Add(new JObject
                    {
                        ["key"] = item.Key,
                        ["value"] = item.Value.Value,
                        ["weight"] = item.Value.Weight,
                        ["persistent"] = item.Value.Persistent
                    });
                stats[category.Key] = new JObject
                {
                    ["blendType"] = category.Value.BlendType.ToString(),
                    ["blended"] = category.Value.GetBlended(),
                    ["contributors"] = contributors
                };
            }
        }

        var carriedSlots = new JObject();
        var carryTree = entity.WatchedAttributes.GetTreeAttribute("carryon:Carried");
        foreach (string slot in new[] { "Hands", "Back" })
            carriedSlots[slot] = carryTree?.GetTreeAttribute(slot) != null;

        return new JObject
        {
            ["name"] = player.PlayerName,
            ["uid"] = player.PlayerUID,
            ["entityId"] = entity.EntityId,
            ["alive"] = entity.Alive,
            ["position"] = new JObject
            {
                ["x"] = entity.Pos.X, ["y"] = entity.Pos.Y, ["z"] = entity.Pos.Z,
                ["dimension"] = entity.Pos.Dimension
            },
            ["walkspeed"] = entity.Stats == null ? JValue.CreateNull() : new JValue(entity.Stats.GetBlended("walkspeed")),
            ["walkspeedPercent"] = entity.Stats == null ? JValue.CreateNull() : new JValue(entity.Stats.GetBlended("walkspeed") * 100),
            ["controls"] = new JObject
            {
                ["sprint"] = entity.Controls.Sprint,
                ["sneak"] = entity.Controls.Sneak,
                ["sitting"] = entity.Controls.FloorSitting,
                ["mounted"] = entity.MountedOn != null
            },
            ["stats"] = stats,
            ["watchedAttributes"] = attributes,
            ["injuryRespawnLocalAttribute"] = CaptureAttribute(entity.Attributes["vscncsInjuryRespawn"]),
            ["carryOnSlotsOccupied"] = carriedSlots
        };
    }

    private static JToken CaptureAttribute(IAttribute? attribute)
    {
        if (attribute == null) return JValue.CreateNull();
        if (attribute is ITreeAttribute tree)
        {
            var result = new JObject();
            foreach (var item in tree) result[item.Key] = CaptureAttribute(item.Value);
            return result;
        }
        // Convert primitive values directly: the engine's JSON token method does
        // not consistently escape strings or format floats across locales.
        object? value = attribute.GetValue();
        if (value == null) return JValue.CreateNull();
        if (value is string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or Array)
            return JToken.FromObject(value);
        return new JObject { ["attributeType"] = attribute.GetType().Name };
    }

    internal static JArray CaptureMods(ICoreAPI? api) => api?.ModLoader == null ? [] :
        new JArray(api.ModLoader.Mods.OrderBy(mod => mod.Info.ModID, StringComparer.OrdinalIgnoreCase)
            .Select(mod => new JObject { ["id"] = mod.Info.ModID, ["version"] = mod.Info.Version }));

    internal static string Write(Guid reportId, string side, string report)
    {
        if (side is not ("server" or "client")) throw new ArgumentException("Invalid log side.", nameof(side));
        string directory = GamePaths.Logs;
        Directory.CreateDirectory(directory);
        // Use only generated values, never player names or remote paths.
        string file = Path.Combine(directory, $"vscncs-status-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{reportId:N}-{side}.log");
        using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(report);
        return file;
    }
}
