using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace SeraphCivilizationSimulation;

// Requirements describe kinds, never the changing condition of the offered stack.
// Unknown mod attributes stay significant: discarding them could erase contents or variants.
public static class EconomyItemMatching
{
    private static readonly HashSet<string> PreviousConditionAttributes = new(StringComparer.Ordinal)
        { "durability", "temperature", "transitionstate", "displayName", "customName" };
    // Verified against Toolsmith 1.2.19 and SmithingPlus 1.10.6. Parts, maximum
    // stats, quality and unknown attributes still define distinct goods.
    private static readonly HashSet<string> ConditionAttributes = new(PreviousConditionAttributes, StringComparer.Ordinal)
    {
        "toolMode", "sp:flipToolModeIndex", "toolSharpnessCurrent", "totalPercentHoned",
        "tinkeredToolHeadDurability", "tinkeredToolHandleDurability", "tinkeredToolBindingDurability",
        "toolPartCurrentDurability", "toolsmithGrindstoneInUse", "toolsmithWhetstoneInUse", "toolsmithWhetstoneDoneSharpen"
    };
    private static readonly HashSet<string> NaturalAttributes = new(StringComparer.Ordinal)
        { "temperature", "transitionstate" };
    public static bool Modern(string key) => Current(key) || key.StartsWith("kind2:", StringComparison.Ordinal);
    internal static bool Current(string key) => key.StartsWith("kind3:", StringComparison.Ordinal);
    public static TreeAttribute Attributes(ItemStack stack) => Attributes(stack, false);
    internal static TreeAttribute Attributes(ItemStack stack, bool previous)
    {
        var attributes = stack.Attributes.Clone();
        Clean(attributes, previous ? PreviousConditionAttributes : ConditionAttributes);
        return (TreeAttribute)attributes;
    }
    private static void Clean(ITreeAttribute attributes, HashSet<string> ignored)
    {
        foreach (string name in ignored) attributes.RemoveAttribute(name);
        // Containers may embed stack trees. Keep contents/counts/recipes, allow their ageing.
        foreach (var entry in attributes.ToList())
        {
            if (entry.Value is ItemstackAttribute item && item.value != null) Clean(item.value.Attributes, ignored);
            else if (entry.Value is ITreeAttribute tree) Clean(tree, ignored);
            else if (entry.Value is TreeArrayAttribute array) foreach (var child in array.value) Clean(child, ignored);
        }
    }
    public static string Key(ItemStack stack) => Key(stack.Class, stack.Collectible.Code.ToString(), Attributes(stack));
    private static string MatchingKey(ItemStack stack, bool previous) => Key(stack.Class, stack.Collectible.Code.ToString(), Attributes(stack, previous), previous);
    internal static string Key(EnumItemClass type, string code, ITreeAttribute attributes, bool previous = false)
    {
        string identity = type + ":" + code;
        string json = Fingerprint(attributes).ToString(Formatting.None);
        // Plain goods retain a readable stable code; attributed variants have a bounded suffix.
        return (previous ? "kind2:" : "kind3:") + identity + (json == "{}" ? "" : ":" + Hash(json));
    }
    public static EconomyItemRequirement Requirement(ItemStack stack)
    {
        var attributes = Attributes(stack);
        var mappings = new List<EconomyNestedItemMapping>();
        Visit(attributes, attribute =>
        {
            if (attribute.value?.Collectible?.Code is not { } code) return;
            if (!mappings.Any(m => m.Class == attribute.value.Class && m.Id == attribute.value.Id))
                mappings.Add(new() { Class = attribute.value.Class, Id = attribute.value.Id, Code = code.ToString() });
        });
        return new() { Class = stack.Class, Code = stack.Collectible.Code.ToString(), Attributes = attributes.ToBytes(), Mappings = mappings };
    }
    // Resolving an outer ItemStack does not resolve its embedded Toolsmith parts.
    // Populate collectible references before hashing or collecting stable-code mappings.
    internal static bool ResolveParts(ItemStack stack, IWorldAccessor world)
    {
        bool resolved = true;
        Visit(stack.Attributes, item =>
        {
            if (item.value != null && item.value.Collectible == null && !item.value.ResolveBlockOrItem(world)) resolved = false;
        });
        return resolved;
    }
    // Native ItemstackAttribute.ToJsonToken omits StackSize. Include nested quantities explicitly.
    // Include primitive attribute types as well so exact inventory checks cover all persisted data.
    private static JObject Fingerprint(ITreeAttribute attributes) => new(attributes.OrderBy(e => e.Key, StringComparer.Ordinal)
        .Select(e => new JProperty(e.Key, Value(e.Value))));
    private static JToken Value(IAttribute attribute)
    {
        try
        {
            int id = attribute.GetAttributeId();
            JToken value = attribute switch
            {
                ItemstackAttribute item => item.value == null ? JValue.CreateNull() : new JObject
                {
                    ["class"] = item.value.Class.ToString(), ["code"] = item.value.Collectible?.Code.ToString() ?? "unresolved:" + item.value.Id,
                    ["quantity"] = item.value.StackSize, ["attributes"] = Fingerprint(item.value.Attributes)
                },
                ITreeAttribute tree => Fingerprint(tree),
                TreeArrayAttribute array => new JArray(array.value.Select(Fingerprint)),
                _ => Canonical(JToken.Parse(attribute.ToJsonToken()))
            };
            return new JArray(id, value);
        }
        catch
        {
            // Some mods return a non-JSON token from ToJsonToken(). Preserve it as
            // opaque data instead of allowing an inventory event to disconnect a player.
            return OpaqueValue(attribute);
        }
    }
    private static JToken OpaqueValue(IAttribute attribute)
    {
        int id;
        try { id = attribute.GetAttributeId(); } catch { id = 0; }
        var value = new JObject { ["type"] = attribute.GetType().AssemblyQualifiedName ?? attribute.GetType().FullName ?? "unknown" };
        try
        {
            value["raw"] = attribute.ToJsonToken();
        }
        catch
        {
            try
            {
                using var stream = new MemoryStream();
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true)) attribute.ToBytes(writer);
                value["bytes"] = Convert.ToBase64String(stream.ToArray());
            }
            catch
            {
                try { value["value"] = attribute.GetValue()?.ToString() ?? ""; }
                catch { value["value"] = "unreadable"; }
            }
        }
        return new JArray(id, value);
    }
    public static string ExactKey(ItemStack stack) => Hash(stack.Class + ":" + stack.Collectible.Code + ":" + Fingerprint(stack.Attributes).ToString(Formatting.None));
    // Refresh/preview checks include nested amounts and condition, without reacting to natural ageing.
    public static string StateKey(ItemStack stack)
    {
        var attributes = stack.Attributes.Clone(); Clean(attributes, NaturalAttributes);
        return Hash(stack.Class + ":" + stack.Collectible.Code + ":" + Fingerprint(attributes).ToString(Formatting.None));
    }
    internal static void Visit(ITreeAttribute attributes, Action<ItemstackAttribute> visit)
    {
        foreach (var entry in attributes)
        {
            if (entry.Value is ItemstackAttribute item)
            { visit(item); if (item.value != null) Visit(item.value.Attributes, visit); }
            else if (entry.Value is ITreeAttribute tree) Visit(tree, visit);
            else if (entry.Value is TreeArrayAttribute array) foreach (var child in array.value) Visit(child, visit);
        }
    }
    public static string LegacyKey(ItemStack stack, bool exact = false)
    {
        var attributes = stack.Attributes.Clone();
        if (!exact) { attributes.RemoveAttribute("transitionstate"); attributes.RemoveAttribute("temperature"); }
        JToken canonical;
        try { canonical = Canonical(JToken.Parse(attributes.ToJsonToken())); }
        catch { canonical = Fingerprint(attributes); }
        return Hash(stack.Class + ":" + stack.Collectible.Code + ":" + canonical.ToString(Formatting.None));
    }
    public static bool Matches(ItemStack stack, string key, EconomyItemRequirement? requirement = null, IWorldAccessor? world = null)
    {
        if (!Modern(key)) return LegacyKey(stack) == key; // Existing signed task terms stay exact.
        if (MatchingKey(stack, !Current(key)) != key) return false;
        return MatchesKind(stack, key, requirement, world);
    }
    // The caller may already have grouped inventory by kind; do not hash each candidate again.
    internal static bool MatchesKind(ItemStack stack, string key, EconomyItemRequirement? requirement, IWorldAccessor? world)
    {
        if (requirement == null) return true;
        if (stack.Class != requirement.Class || stack.Collectible.Code.ToString() != requirement.Code) return false;
        bool previous = !Current(key);
        var template = requirement.Template(stack.Collectible, world, previous);
        if (template == null) return false;
        var actual = stack.Clone(); actual.Attributes = Attributes(stack, previous);
        return requirement.TemplateKey == key && template.Satisfies(actual);
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static JToken Canonical(JToken token) => token switch
    {
        JObject obj => new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new JProperty(p.Name, Canonical(p.Value)))),
        JArray array => new JArray(array.Select(Canonical)), _ => token
    };
}

[ProtoContract]
public sealed class EconomyItemRequirement
{
    [ProtoMember(1)] public EnumItemClass Class { get; set; }
    [ProtoMember(2)] public string Code { get; set; } = "";
    [ProtoMember(3)] public byte[] Attributes { get; set; } = [];
    [ProtoMember(4)] public List<EconomyNestedItemMapping> Mappings { get; set; } = [];
    private ItemStack? template;
    private bool previousTemplate;
    internal string TemplateKey { get; private set; } = "";
    internal ItemStack? Template(CollectibleObject collectible, IWorldAccessor? world, bool previous = false)
    {
        if (template?.Collectible != collectible || previousTemplate != previous)
        {
            var attributes = new TreeAttribute(); attributes.FromBytes(Attributes);
            bool resolved = true;
            EconomyItemMatching.Visit(attributes, item =>
            {
                if (item.value == null) return;
                var mapping = Mappings.FirstOrDefault(m => m.Class == item.value.Class && m.Id == item.value.Id);
                CollectibleObject? content = mapping == null || world == null ? null : mapping.Class == EnumItemClass.Item
                    ? world.GetItem(new AssetLocation(mapping.Code)) : world.GetBlock(new AssetLocation(mapping.Code));
                if (content == null) { resolved = false; return; }
                item.value = new ItemStack(content, item.value.StackSize) { Attributes = item.value.Attributes };
            });
            if (!resolved) return null;
            var source = new ItemStack(collectible) { Attributes = attributes };
            template = new ItemStack(collectible) { Attributes = EconomyItemMatching.Attributes(source, previous) };
            previousTemplate = previous;
            TemplateKey = EconomyItemMatching.Key(template.Class, collectible.Code.ToString(), template.Attributes, previous);
        }
        return template;
    }
}

[ProtoContract]
public sealed class EconomyNestedItemMapping
{
    [ProtoMember(1)] public EnumItemClass Class { get; set; }
    [ProtoMember(2)] public int Id { get; set; }
    [ProtoMember(3)] public string Code { get; set; } = "";
}
