using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace SeraphCivilizationSimulation;

public sealed class VscnRespawnBehavior : EntityBehavior
{
    private VscnCivilizationModSystem? system;
    private long protectedUntil;
    private bool wasAlive;
    public VscnRespawnBehavior(Entity entity) : base(entity) { wasAlive = entity.Alive; }
    public override string PropertyName() => "vscncs.respawn";
    internal void Bind(VscnCivilizationModSystem owner) => system = owner;

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
        system = entity.Api.ModLoader.GetModSystem<VscnCivilizationModSystem>();
    }

    public override void OnEntityRevive()
    {
        base.OnEntityRevive();
        wasAlive = true;
        ProtectAfterRevive();
        system?.OnPlayerRevived(entity);
    }

    public override void OnGameTick(float deltaTime)
    {
        if (!wasAlive && entity.Alive)
        {
            ProtectAfterRevive();
            system?.OnPlayerRevived(entity);
        }
        wasAlive = entity.Alive;
        if (system?.Config.Respawn.Enabled == true && protectedUntil > entity.World.ElapsedMilliseconds)
            entity.SetActivityRunning("invulnerable", (int)Math.Min(int.MaxValue, protectedUntil - entity.World.ElapsedMilliseconds));
    }

    private void ProtectAfterRevive()
    {
        int seconds = system?.Config.Respawn.ProtectionSeconds ?? 0;
        if (system?.Config.Respawn.Enabled != true || seconds <= 0) return;
        protectedUntil = entity.World.ElapsedMilliseconds + seconds * 1000L;
        entity.SetActivityRunning("invulnerable", (int)Math.Min(int.MaxValue, seconds * 1000L));
    }

    public override void OnEntityReceiveDamage(DamageSource source, ref float damage)
    {
        if (source.Type != EnumDamageType.Heal && system?.Config.Respawn.Enabled == true && protectedUntil > entity.World.ElapsedMilliseconds) damage = 0;
    }
}
