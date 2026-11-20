using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace SeraphCivilizationSimulation;

public sealed class LoreRestrictionSystem : ModSystem
{
    public const string ConfigName = ConfigMigration.Lore;
    internal const string WorldKey = "vscncsLoreRestrictions";
    private static readonly ConditionalWeakTable<IWorldAccessor, LoreRestrictionsConfig> Policies = new();
    private LoreRestrictionsConfig config = new();
    private LoreRestrictionCompatibility? compatibility;
    private ICoreServerAPI? server;
    private ICoreClientAPI? client;

    public override double ExecuteOrder() => 10;

    public override void Start(ICoreAPI api)
    {
        if (api.Side == EnumAppSide.Server)
        {
            config = ConfigMigration.LoadLore(api);
        }
        compatibility = new LoreRestrictionCompatibility();
    }

    public override void AssetsFinalize(ICoreAPI api)
    {
        if (api.Side != EnumAppSide.Server) return;
        bool lore = api.World.Config.GetAsBool("loreContent", true);
        // Send an effective, server-owned policy. Clients never read a local config for this system.
        var effective = JsonConvert.DeserializeObject<LoreRestrictionsConfig>(JsonConvert.SerializeObject(config))!;
        effective.Enabled = config.Active(lore);
        Bind(api.World, effective);
        // WorldConfig.loadFromSavegame uses TreeAttribute.ToJsonToken().
        // StringAttribute does not escape quotes, so embedding JSON in a string
        // crashes the singleplayer settings screen even after this mod is removed.
        api.World.Config[WorldKey] = new JsonObject(JObject.FromObject(effective)).ToAttribute();
        ApplyAssets(api, effective);
        api.Logger.Notification("Seraph Civilization Simulation: lore restrictions {0}; config: {1}. Restart required after edits.", effective.Enabled ? "enabled" : "disabled", ConfigName);
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        server = api;
        api.Event.OnTrySpawnEntity += BeforeNaturalSpawn;
        api.Event.OnEntitySpawn += OnEntitySpawn;
        api.Event.OnEntityLoaded += OnEntityLoaded;
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        client = api;
        api.Event.BlockTexturesLoaded += FinalizeClient;
    }

    private void FinalizeClient()
    {
        if (client != null && Policy(client.World) is { } policy) ApplyAssets(client, policy);
    }

    internal static void Bind(IWorldAccessor world, LoreRestrictionsConfig policy)
    {
        Policies.Remove(world);
        Policies.Add(world, policy);
    }

    internal static LoreRestrictionsConfig ParseConfig(JObject json)
    {
        var names = typeof(LoreRestrictionsConfig).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var property in json.Properties())
            if (!names.Contains(property.Name) || property.Value.Type != JTokenType.Boolean)
                throw new InvalidDataException($"{ConfigName}: unknown switch or non-boolean value: {property.Name}");
        return json.ToObject<LoreRestrictionsConfig>()!;
    }

    internal static LoreRestrictionsConfig? Policy(IWorldAccessor? world)
    {
        if (world == null) return null;
        if (Policies.TryGetValue(world, out var policy)) return policy.Enabled ? policy : null;
        IAttribute? saved = world.Config?[WorldKey];
        if (saved == null) return null;
        policy = saved switch
        {
            ITreeAttribute tree => ParseConfig(JObject.Parse(tree.ToJsonToken())),
            // Read policies from older servers; AssetsFinalize rewrites old saves
            // to the native tree before the next server save.
            StringAttribute { value: { Length: > 0 } json } => ParseConfig(JObject.Parse(json)),
            _ => throw new InvalidDataException("Invalid VSCN lore restriction policy.")
        };
        Bind(world, policy);
        return policy.Enabled ? policy : null;
    }

    private bool BeforeNaturalSpawn(IBlockAccessor accessor, ref EntityProperties properties, Vec3d position, long herdId)
        => Policy(server?.World)?.EntityRestricted(properties.Code) != true;

    private void OnEntitySpawn(Entity entity) => RemoveRestrictedEntity(entity, false);
    private void OnEntityLoaded(Entity entity) => RemoveRestrictedEntity(entity, true);

    private void RemoveRestrictedEntity(Entity entity, bool existing)
    {
        var policy = Policy(server?.World);
        if (policy == null || !policy.EntityRestricted(entity.Code) || existing && !policy.RemoveExistingRestrictedEntities) return;
        // Run after SpawnEntity/LoadEntity has finished registering the entity; no death or loot event.
        server!.Event.EnqueueMainThreadTask(() =>
        {
            if (entity.World == server?.World && server.World.LoadedEntities.ContainsKey(entity.EntityId))
                server.World.DespawnEntity(entity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
        }, "vscncs-remove-restricted-entity");
    }

    internal static void ApplyAssets(ICoreAPI api, LoreRestrictionsConfig policy)
    {
        if (!policy.Enabled) return;
        int recipes = api.World.GridRecipes.RemoveAll(r => policy.CollectibleRestricted(r.Output?.ResolvedItemStack?.Collectible.Code ?? r.Output?.Code)
            || r.RecipeIngredients.Any(i => policy.CollectibleRestricted(i.ResolvedItemStack?.Collectible.Code ?? i.Code)));
        recipes += api.GetSmithingRecipes().RemoveAll(r => policy.CollectibleRestricted(r.Output?.ResolvedItemstack?.Collectible.Code ?? r.Output?.Code));
        foreach (var collectible in api.World.Collectibles)
        {
            var sourceAttrs = collectible.Attributes?.Token as JObject;
            JObject? attrs = null;
            if (sourceAttrs?["HeraldryProperties"]?["AvailablePatterns"] is JArray sourcePatterns
                && sourcePatterns.Any(p => policy.PatternRestricted(p.Value<string>() ?? "")))
            {
                attrs = (JObject)sourceAttrs.DeepClone();
                var patterns = (JArray)attrs["HeraldryProperties"]!["AvailablePatterns"]!;
                foreach (var pattern in patterns.ToArray())
                    if (policy.PatternRestricted(pattern.Value<string>() ?? "")) pattern.Remove();
                collectible.Attributes = new JsonObject(attrs);
            }
            if (policy.CollectibleRestricted(collectible.Code))
            {
                collectible.CreativeInventoryTabs = [];
                collectible.CreativeInventoryStacks = [];
                attrs ??= sourceAttrs?.DeepClone() as JObject ?? new JObject();
                var handbook = attrs["handbook"] as JObject ?? new JObject();
                handbook["exclude"] = true;
                attrs["handbook"] = handbook;
                collectible.Attributes = new JsonObject(attrs);
                // LightHsv is a value type; assigning null invokes the byte[] conversion and throws.
                if (collectible.Code?.Domain == "primitivesurvival") collectible.LightHsv = default;
            }
            if (policy.ExpandedMatterTemporalMaterials && collectible.Code?.ToString() == "game:gear-temporal")
                collectible.CrushingProps = null;
            LoreRestrictionCompatibility.FilterPan(collectible, policy);
        }
        foreach (var properties in api.World.EntityTypes)
        {
            if (!policy.EntityRestricted(properties.Code)) continue;
            var spawn = properties.Server?.SpawnConditions;
            if (spawn?.Runtime != null) spawn.Runtime.Chance = 0;
            if (spawn?.Worldgen?.TriesPerChunk != null) spawn.Worldgen.TriesPerChunk.avg = 0;
        }
        api.Logger.Notification("Seraph Civilization Simulation: removed {0} restricted grid/smithing recipes; entity and drop guards active.", recipes);
    }

    public override void Dispose()
    {
        if (server != null)
        {
            server.Event.OnTrySpawnEntity -= BeforeNaturalSpawn;
            server.Event.OnEntitySpawn -= OnEntitySpawn;
            server.Event.OnEntityLoaded -= OnEntityLoaded;
            Policies.Remove(server.World);
        }
        if (client != null)
        {
            client.Event.BlockTexturesLoaded -= FinalizeClient;
            Policies.Remove(client.World);
        }
        compatibility?.Dispose();
        server = null;
        client = null;
    }
}
