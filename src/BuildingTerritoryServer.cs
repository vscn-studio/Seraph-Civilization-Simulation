using Vintagestory.API.MathTools;
namespace SeraphCivilizationSimulation;
public sealed partial class VscnCivilizationModSystem
{
    // The center is persisted independently of buildings and member counts.
    private BlockPos TribeSpawn(TribeRecord tribe) => new(tribe.X, tribe.Y, tribe.Z, tribe.Dimension);
    private bool TrySaveTerritoryChange(TribeRecord tribe, Action change)
    {
        string before = Newtonsoft.Json.JsonConvert.SerializeObject(tribe);
        bool existed = tribeState.Tribes.Contains(tribe);
        try { change(); ReindexTerritories(); SaveState(); return true; }
        catch (Exception error)
        {
            if (!existed) tribeState.Tribes.Remove(tribe);
            Newtonsoft.Json.JsonConvert.PopulateObject(before, tribe, new() { ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace });
            ReindexTerritories();
            sapi!.Logger.Error("Building territory change rolled back: {0}", error);
            try { SaveState(); } catch (Exception rollback) { sapi.Logger.Error("Territory rollback could not be persisted: {0}", rollback); }
            return false;
        }
    }
}
