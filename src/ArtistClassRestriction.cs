using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace SeraphCivilizationSimulation;

// Load the startup switch before assets finalize; clients use the server's world config.
public sealed class ArtistClassRestrictionSystem : ModSystem
{
    internal const string WorldKey = "vscncsDisableVintageCanvasArtistClass";
    private bool disabled = true;
    private ArtistClassCompatibility? compatibility;

    public override void Start(ICoreAPI api)
    {
        if (api.Side == EnumAppSide.Server)
            disabled = (ConfigMigration.LoadMain(api) ?? new()).DisableVintageCanvasArtistClass;
        compatibility = new ArtistClassCompatibility();
    }

    public override void AssetsFinalize(ICoreAPI api)
    {
        if (api.Side == EnumAppSide.Server)
            api.World.Config.SetBool(WorldKey, disabled);
    }

    public override void Dispose()
    {
        compatibility?.Dispose();
        compatibility = null;
    }
}

internal sealed class ArtistClassCompatibility : IDisposable
{
    private const string PatchId = "vsscsmod.artistclass";
    private const string Artist = "canvaspainter";
    private const string Commoner = "commoner";
    private static readonly object Gate = new();
    private static int users;
    private bool disposed;

    internal ArtistClassCompatibility()
    {
        lock (Gate)
        {
            if (users == 0)
            {
                var harmony = new Harmony(PatchId);
                try
                {
                    harmony.Patch(AccessTools.Method(typeof(CharacterSystem), "LoadClasses"),
                        postfix: new HarmonyMethod(typeof(ArtistClassCompatibility), nameof(AfterLoadClasses)));
                    harmony.Patch(AccessTools.Method(typeof(CharacterSystem), nameof(CharacterSystem.setCharacterClass)),
                        prefix: new HarmonyMethod(typeof(ArtistClassCompatibility), nameof(BeforeSetClass)));
                    harmony.Patch(AccessTools.Method(typeof(CharacterSystem), "Event_PlayerJoinServer"),
                        prefix: new HarmonyMethod(typeof(ArtistClassCompatibility), nameof(BeforePlayerJoin)));
                }
                catch { harmony.UnpatchAll(PatchId); throw; }
            }
            users++;
        }
    }

    private static bool Restricted(ICoreAPI api)
        => api.World.Config.GetBool(ArtistClassRestrictionSystem.WorldKey, false);

    private static void AfterLoadClasses(CharacterSystem __instance, ICoreAPI ___api)
    {
        if (!Restricted(___api)) return;
        if (!__instance.characterClasses.Any(c => c.Code == Commoner))
            throw new InvalidOperationException("Seraph Civilization Simulation artist restriction requires the commoner class to remain enabled.");
        __instance.characterClasses.RemoveAll(c => c.Code == Artist);
        // Native class loading populates the index after this method; clear any previous load too.
        __instance.characterClassesByCode.Remove(Artist);
    }

    private static void BeforeSetClass(ICoreAPI ___api, ref string classCode)
    {
        if (classCode == Artist && Restricted(___api)) classCode = Commoner;
    }

    private static void BeforePlayerJoin(CharacterSystem __instance, ICoreAPI ___api, IServerPlayer byPlayer)
    {
        EntityPlayer entity = byPlayer.Entity;
        if (!Restricted(___api) || entity.WatchedAttributes.GetString("characterClass") != Artist) return;
        // Recompute native trait stats, keeping inventory, appearance and non-profession stat sources.
        __instance.setCharacterClass(entity, Commoner, initializeGear: false);
        ___api.Logger.Notification("Seraph Civilization Simulation: migrated disabled Artist profession to Commoner for player {0}.", byPlayer.PlayerUID);
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (disposed) return;
            disposed = true;
            if (--users == 0) new Harmony(PatchId).UnpatchAll(PatchId);
        }
    }
}
