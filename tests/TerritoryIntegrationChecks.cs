using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.API.Datastructures;
using SeraphCivilizationSimulation;

internal static class TerritoryIntegrationChecks
{
    public static void Run(string ropingDll, Action<bool, string> check)
    {
        string game = Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? "E:/vintagestory/Vintagestory";
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            foreach (string directory in new[] { game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods") })
            {
                string file = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(file)) return context.LoadFromAssemblyPath(Path.GetFullPath(file));
            }
            return null;
        }
        AssemblyLoadContext.Default.Resolving += Resolve;
        var roping = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(ropingDll));
        const string patchId = "vscncs.territory.tests";
        var harmony = new Harmony(patchId);
        var system = new VscnCivilizationModSystem();
        var type = typeof(VscnCivilizationModSystem);
        void Set(string field, object value) => AccessTools.Field(type, field).SetValue(system, value);
        var loader = Proxy.Make<IModLoader>((m, a) => m.Name == "IsModEnabled" ? true : null);
        var events = Proxy.Make<IServerEventAPI>((m, a) => null);
        var logger = Proxy.Make<ILogger>((m, a) => null);
        var sapi = Proxy.Make<ICoreServerAPI>((m, a) => m.Name switch
        { "get_ModLoader" => loader, "get_Event" => events, "get_Logger" => logger, _ => null });
        Set("sapi", sapi); Set("harmony", harmony); Set("instance", system);
        Set("tribeState", new TribeState { Tribes = [new() { Id = "t", LeaderUid = "p", Members = ["p"], X = 100, Z = 100 }] });
        EntityPlayer? entity = null;
        var player = Proxy.Make<IServerPlayer>((m, a) => m.Name switch { "get_PlayerUID" => "p", "get_Entity" => entity, _ => null });
        var world = Proxy.Make<IServerWorldAccessor>((m, a) => m.Name switch
        { "get_Side" => EnumAppSide.Server, "PlayerByUid" => player, _ => null });
        entity = new EntityPlayer { World = world };
        entity.Pos.SetPos(100, 110, 100);
        try
        {
            AccessTools.Method(type, "StartTerritoryEffects").Invoke(system, null);
            entity.Stats = new EntityStats(entity);
            var chance = AccessTools.Method(roping.GetType("VSCN.Roping.RopingSystem"), "CastChance");
            double baseline = (double)chance.Invoke(null, ["rope", false, 1d])!;
            check(AccessTools.Method(type, "BeforeRopingThrow") == null && (double)chance.Invoke(null, ["rope", false, 1d])! == baseline,
                "Retired roping integration changed native probability.");
            var damageTarget = new DamageProbe { World = world };
            damageTarget.ReceiveDamage(new DamageSource { SourceEntity = entity, Type = EnumDamageType.BluntAttack }, 10);
            check(Math.Abs(damageTarget.LastDamage - 10f) < .001f, "Real attack pipeline applied a retired territory damage penalty.");
            damageTarget.ReceiveDamage(new DamageSource { CauseEntity = entity, SourceEntity = new DamageProbe(), Type = EnumDamageType.PiercingAttack }, 10);
            check(Math.Abs(damageTarget.LastDamage - 10f) < .001f, "Projectile damage still applied a retired territory penalty.");
            damageTarget.ReceiveDamage(new DamageSource { SourceEntity = entity, Type = EnumDamageType.Heal }, 10);
            check(damageTarget.LastDamage == 10, "Territory penalty reduced healing.");
        }
        finally
        {
            harmony.UnpatchAll(patchId);
            Set("instance", null!);
            AssemblyLoadContext.Default.Resolving -= Resolve;
        }
    }

    private sealed class DamageProbe : Entity
    {
        public float LastDamage;
        public override bool ShouldReceiveDamage(DamageSource source, float damage) { LastDamage = damage; return false; }
    }
}
