using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace SeraphCivilizationSimulation;

public sealed class BearBlockBreakingBehavior(Entity entity) : EntityBehavior(entity)
{
    private const float TickInterval = 0.25f;
    private const float MaximumResistance = 2.5f;
    private const double PursuitScanDistance = 4;
    private const double BreakingReach = 1;
    private const float PursuitMemorySeconds = 5;
    private float tickAccumulator;
    private float breakProgress;
    private float targetLostSeconds;
    private BlockPos? currentTarget;
    private int currentBlockId;
    private Entity? pursuitTarget;
    private float pursuitMemory;

    public override void AfterInitialized(bool onFirstSpawn)
    {
        // A failed native path can finish the seek task before the next scan. Remember the
        // actual combat target briefly; do not acquire players or prey independently of AI.
        if (entity.Api.Side == EnumAppSide.Server && entity.GetBehavior<EntityBehaviorTaskAI>() is { } ai)
            ai.TaskManager.OnTaskStarted += RememberCombatTarget;
    }

    private void RememberCombatTarget(IAiTask task)
    {
        if (task is not (AiTaskSeekEntity or AiTaskMeleeAttack)) return;
        if (((AiTaskBaseTargetable)task).TargetEntity is not { Alive: true } target) return;
        pursuitTarget = target;
        pursuitMemory = PursuitMemorySeconds;
    }

    public override void OnGameTick(float deltaTime)
    {
        if (!CanScan(entity.Api.Side, entity.Alive, entity is EntityAgent))
        {
            ResetProgress();
            return;
        }

        tickAccumulator += deltaTime;
        if (tickAccumulator < TickInterval) return;
        tickAccumulator %= TickInterval;

        IBlockAccessor blocks = entity.World.BlockAccessor;
        EntityBehaviorTaskAI? ai = entity.GetBehavior<EntityBehaviorTaskAI>();
        pursuitMemory -= TickInterval;
        if (ai != null)
            foreach (IAiTask? task in ai.TaskManager.ActiveTasksBySlot)
                if (task != null) RememberCombatTarget(task);
        if (pursuitMemory <= 0 || pursuitTarget?.Alive != true
            || pursuitTarget.Pos.Dimension != entity.Pos.Dimension
            || entity.Pos.SquareDistanceTo(pursuitTarget.Pos) > 64)
            pursuitTarget = null;

        // Keep the old, short body scan for ordinary movement. Combat needs a swept scan
        // towards the prey: native A* may give up or circle away before physical contact.
        IEnumerable<BlockPos> candidates = pursuitTarget == null ? FindForwardBlocks()
            : GetPursuitBlockPositions(entity.Pos.X, entity.Pos.Y + entity.CollisionBox.Y1,
                entity.Pos.Z, entity.CollisionBox.XSize, entity.CollisionBox.YSize,
                pursuitTarget.Pos.X, pursuitTarget.Pos.Y, pursuitTarget.Pos.Z, entity.Pos.Dimension);
        BlockPos? target = null;
        float resistance = 0;
        foreach (BlockPos candidate in candidates)
        {
            Block candidateBlock = blocks.GetBlock(candidate);
            float candidateResistance = candidateBlock.GetResistance(blocks, candidate);
            if (!CanBreak(candidateBlock.Code?.Path, candidateBlock.GetBlockMaterial(blocks, candidate), candidateResistance)) continue;
            if ((target == null || currentTarget?.Equals(candidate) == true) && HasUnbreakableBarrier(blocks, candidate)) continue;

            if (target == null)
            {
                target = candidate;
                resistance = candidateResistance;
            }
            // Tiny changes in yaw or position must not switch between adjacent wall blocks
            // and discard several seconds of digging on each native AI turn.
            if (currentTarget?.Equals(candidate) == true)
            {
                target = candidate;
                resistance = candidateResistance;
                break;
            }
        }

        if (target == null)
        {
            targetLostSeconds += TickInterval;
            if (targetLostSeconds > 1) ResetProgress();
            return;
        }

        if (pursuitTarget != null && !IsWithinBreakingReach(entity.Pos.X, entity.Pos.Z,
            entity.CollisionBox.XSize, target))
        {
            // Reuse native walking/physics only while a cheap obstacle is actually ahead.
            // Without this fallback a no-path result leaves the bear outside digging range.
            ai?.PathTraverser?.WalkTowards(new Vec3d(target.X + 0.5, entity.Pos.Y, target.Z + 0.5),
                0.04f, 0.1f, () => { }, () => { });
            return;
        }

        targetLostSeconds = 0;
        int blockId = blocks.GetBlock(target).Id;
        if (currentTarget == null || !currentTarget.Equals(target) || currentBlockId != blockId)
        {
            currentTarget = target.Copy();
            currentBlockId = blockId;
            breakProgress = 0;
        }

        // Stay with the obstacle while digging instead of letting failed pathfinding send
        // the bear around the pillar and repeatedly abandon its own target. Keep an
        // ongoing dig alive across native seek cooldowns, including the final top block.
        if (pursuitTarget != null)
        {
            pursuitMemory = PursuitMemorySeconds;
            ai?.PathTraverser?.Stop();
        }
        breakProgress += TickInterval;
        blocks.DamageBlock(target, BlockFacing.UP, Math.Min(1, breakProgress / Math.Max(resistance, TickInterval)));
        if (breakProgress >= resistance)
        {
            // Destroy the obstructing block directly and let native block breaking handle normal drops.
            blocks.BreakBlock(target, null, 1);
            ResetProgress();
        }
    }

    internal static bool CanBreak(string? codePath, EnumBlockMaterial material, float resistance)
    {
        if (material != EnumBlockMaterial.Soil || string.IsNullOrEmpty(codePath)) return false;
        // Rammed earth (夯土) is a separate vanilla family from packed dirt (夯实泥土).
        if (codePath.StartsWith("rammed-", StringComparison.Ordinal)) return resistance <= 3;
        if (resistance > MaximumResistance) return false;
        return codePath == "packeddirt" || codePath.StartsWith("packeddirt-", StringComparison.Ordinal)
            || codePath == "drypackeddirt" || codePath.StartsWith("soil-", StringComparison.Ordinal)
            || codePath.StartsWith("cob-", StringComparison.Ordinal) || codePath == "strawbedding";
    }

    private bool HasUnbreakableBarrier(IBlockAccessor blocks, BlockPos target)
    {
        // The swept pursuit scan must not reach through a stone/wood wall to dirt behind it.
        // Native ray selection honors block shapes; non-solid decorations do not shield dirt.
        double fromY = Math.Clamp(target.InternalY + 0.5,
            entity.Pos.InternalY + entity.CollisionBox.Y1 + 0.1,
            entity.Pos.InternalY + entity.CollisionBox.Y2 - 0.1);
        BlockSelection? hit = null;
        EntitySelection? entityHit = null;
        entity.World.RayTraceForSelection(new Vec3d(entity.Pos.X, fromY, entity.Pos.Z),
            new Vec3d(target.X + 0.5, target.InternalY + 0.5, target.Z + 0.5), ref hit, ref entityHit,
            (pos, block) => block.GetCollisionBoxes(blocks, pos)?.Length > 0
                && !CanBreak(block.Code?.Path, block.GetBlockMaterial(blocks, pos), block.GetResistance(blocks, pos)),
            _ => false);
        return hit != null;
    }

    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        if (entity.Api.Side == EnumAppSide.Server && entity.GetBehavior<EntityBehaviorTaskAI>() is { } ai)
            ai.TaskManager.OnTaskStarted -= RememberCombatTarget;
        pursuitTarget = null;
    }

    internal static bool CanScan(EnumAppSide side, bool alive, bool isAgent)
    {
        return side == EnumAppSide.Server && alive && isAgent;
    }

    internal static bool IsWithinBreakingReach(double posX, double posZ, float width, BlockPos target)
    {
        double dx = Math.Max(0, Math.Max(target.X - posX, posX - target.X - 1) - width / 2);
        double dz = Math.Max(0, Math.Max(target.Z - posZ, posZ - target.Z - 1) - width / 2);
        return dx * dx + dz * dz <= BreakingReach * BreakingReach;
    }

    internal static IEnumerable<BlockPos> GetPursuitBlockPositions(double posX, double posY, double posZ,
        float width, float height, double targetX, double targetY, double targetZ, int dimension = 0)
    {
        double dx = targetX - posX, dz = targetZ - posZ;
        double distance = Math.Sqrt(dx * dx + dz * dz);
        // For prey above the bear, also remove the cheap platform supporting it. Removing
        // only foot-level dirt leaves a floating, gravity-free top block and the same trap.
        float scanHeight = (float)Math.Max(height, Math.Min(targetY - posY, Math.Max(height, 3.25)));
        if (distance < 0.001)
        {
            // A bear directly under prey must still reach the remaining platform overhead.
            foreach (BlockPos pos in GetFrontBlockPositions(posX, posY, posZ, 0, width, scanHeight, dimension))
                yield return pos;
            yield break;
        }
        double yaw = Math.Atan2(dx, dz);
        double reach = Math.Min(PursuitScanDistance, distance);
        var seen = new HashSet<BlockPos>();
        for (double step = 0; step <= reach; step += 0.25)
            foreach (BlockPos pos in GetFrontBlockPositions(posX + dx / distance * step, posY,
                posZ + dz / distance * step, yaw, width, scanHeight, dimension))
            {
                double projection = (pos.X + 0.5 - posX) * dx / distance + (pos.Z + 0.5 - posZ) * dz / distance;
                if (projection <= 0 || projection > distance + 0.5) continue;
                if (seen.Add(pos)) yield return pos;
            }
    }

    private IEnumerable<BlockPos> FindForwardBlocks()
    {
        return GetFrontBlockPositions(entity.Pos.X, entity.Pos.Y + entity.CollisionBox.Y1,
            entity.Pos.Z, entity.Pos.Yaw, entity.CollisionBox.XSize, entity.CollisionBox.YSize,
            entity.Pos.Dimension);
    }

    internal static IEnumerable<BlockPos> GetFrontBlockPositions(double posX, double posY, double posZ,
        double yaw, float width, float height, int dimension = 0)
    {
        // Agent path traversal moves in +sin(yaw), +cos(yaw). AheadCopy looks the opposite way.
        // Inspect the collision volume shifted slightly forward, including its corners, so a
        // diagonal approach and a one-block opening cannot hide the blocks touching the bear.
        double forwardX = Math.Sin(yaw);
        double forwardZ = Math.Cos(yaw);
        double aheadX = posX + forwardX * 0.35;
        double aheadZ = posZ + forwardZ * 0.35;
        int minX = (int)Math.Floor(aheadX - width / 2 + 0.001);
        int maxX = (int)Math.Floor(aheadX + width / 2 - 0.001);
        int minZ = (int)Math.Floor(aheadZ - width / 2 + 0.001);
        int maxZ = (int)Math.Floor(aheadZ + width / 2 - 0.001);
        int feetY = (int)Math.Floor(posY + 0.05);
        int headY = (int)Math.Floor(posY + height - 0.05);

        for (int x = minX; x <= maxX; x++)
            for (int z = minZ; z <= maxZ; z++)
            {
                // The bounding square includes rear corners on diagonal approaches.
                // Skip blocks whose entire volume lies behind the bear's center.
                double forwardExtent = (x + 0.5 - posX) * forwardX + (z + 0.5 - posZ) * forwardZ
                    + (Math.Abs(forwardX) + Math.Abs(forwardZ)) / 2;
                if (forwardExtent <= 0) continue;
                for (int y = feetY; y <= headY; y++)
                    yield return new BlockPos(x, y, z, dimension);
            }
    }

    private void ResetProgress()
    {
        tickAccumulator = 0;
        breakProgress = 0;
        targetLostSeconds = 0;
        currentTarget = null;
    }

    public override string PropertyName() => "vscncsbearblockbreak";
}
