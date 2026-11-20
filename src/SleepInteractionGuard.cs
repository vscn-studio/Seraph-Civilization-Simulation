using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace SeraphCivilizationSimulation;

internal static class SleepInteractionGuard
{
    public const string EnabledAttribute = "vscncsSleepInteractionRestricted";

    public const string Claimant = "custommessage-vsscsmod-sleep-build-restricted";

    // The mount is established before the bed's queued IsSleeping update.
    public static bool IsSleeping(EntityAgent? entity) => entity != null
        && (entity.MountedOn is BlockEntityBed
            || entity.WatchedAttributes.GetTreeAttribute("tiredness")?.GetInt("isSleeping") > 0);
}
