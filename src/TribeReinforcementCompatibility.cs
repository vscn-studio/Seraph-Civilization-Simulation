using HarmonyLib;
using System.Reflection.Emit;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace SeraphCivilizationSimulation;

[ProtoContract]
public sealed class TribeReinforcementRequest
{
    [ProtoMember(1)] public int X { get; set; }
    [ProtoMember(2)] public int Y { get; set; }
    [ProtoMember(3)] public int Z { get; set; }
    [ProtoMember(4)] public int Dimension { get; set; }
    public BlockPos Position => new(X, Y, Z, Dimension);
}

[ProtoContract]
public sealed class TribeReinforcementPacket
{
    [ProtoMember(1)] public TribeReinforcementRequest Block { get; set; } = new();
    [ProtoMember(2)] public int BlockId { get; set; }
    [ProtoMember(3)] public int Strength { get; set; }
    [ProtoMember(4)] public string Tribe { get; set; } = "";
    [ProtoMember(5)] public int PendingSeconds { get; set; }
    internal string TribeId { get; set; } = "";
}

// Native reinforcement has no player argument on GetReinforcment/ConsumeStrength.
// Scope its queries to the player performing the native behavior, so tribal strength
// can be added without changing a plumb-and-square reinforcement's owner or lock.
internal sealed class TribeReinforcementCompatibility : IDisposable
{
    private const string PatchId = "vsscsmod.tribereinforcement";
    private static readonly object Sync = new();
    private static int users;
    private bool disposed;
    private sealed record Scope(IWorldAccessor World, BlockPos Position, IPlayer Player);
    [ThreadStatic] private static Scope? current;

    public TribeReinforcementCompatibility()
    {
        lock (Sync)
        {
            if (users == 0) Install();
            users++;
        }
    }

    private static void Install()
    {
        var harmony = new Harmony(PatchId);
        try
        {
            foreach (string name in new[] { "OnBlockBroken", "GetMiningSpeedModifier", "GetPlacedBlockInfo", "AllowRightClickPickup" })
                harmony.Patch(AccessTools.DeclaredMethod(typeof(BlockBehaviorReinforcable), name),
                    prefix: new HarmonyMethod(typeof(TribeReinforcementCompatibility), nameof(Begin)),
                    transpiler: new HarmonyMethod(typeof(TribeReinforcementCompatibility), nameof(NativeCalls)),
                    finalizer: new HarmonyMethod(typeof(TribeReinforcementCompatibility), nameof(End)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(BlockBehaviorReinforcable), "GetPlacedBlockInfo"),
                postfix: new HarmonyMethod(typeof(TribeReinforcementCompatibility), nameof(CountdownInfo)));
        }
        catch { harmony.UnpatchAll(PatchId); throw; }
    }

    private static void Begin(IWorldAccessor __0, BlockPos __1, IPlayer? __2, out Scope? __state)
    {
        __state = current;
        current = __2 == null ? null : new(__0, __1.Copy(), __2);
    }

    private static void End(Scope? __state) => current = __state;

    private static void CountdownInfo(BlockPos __1, ref string __result)
    {
        if (Extra(__1) is not { PendingSeconds: > 0 } extra || current == null) return;
        string text = Vintagestory.API.Config.Lang.GetL((current.Player as Vintagestory.API.Server.IServerPlayer)?.LanguageCode
            ?? Vintagestory.API.Config.Lang.CurrentLocale,
            "vsscsmod:ui.reinforcement-pending", extra.Tribe, extra.PendingSeconds / 60, extra.PendingSeconds % 60);
        __result = (__result ?? "") + (string.IsNullOrEmpty(__result) ? "" : "\n") + text;
    }

    private static IEnumerable<CodeInstruction> NativeCalls(IEnumerable<CodeInstruction> instructions)
    {
        var get = AccessTools.Method(typeof(ModSystemBlockReinforcement), "GetReinforcment");
        var consume = AccessTools.Method(typeof(ModSystemBlockReinforcement), "ConsumeStrength");
        bool foundGet = false;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(get) || instruction.Calls(consume))
            {
                foundGet |= instruction.Calls(get);
                string replacement = instruction.Calls(get) ? nameof(GetEffectiveReinforcement) : nameof(ConsumeEffectiveStrength);
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(TribeReinforcementCompatibility), replacement);
            }
            yield return instruction;
        }
        if (!foundGet) throw new InvalidOperationException("Native reinforcement behavior no longer queries reinforcement strength.");
    }

    private static BlockReinforcement? GetEffectiveReinforcement(ModSystemBlockReinforcement system, BlockPos pos)
    {
        var result = system.GetReinforcment(pos);
        AddStrength(pos, ref result);
        return result;
    }

    private static TribeReinforcementPacket? Extra(BlockPos pos)
    {
        if (current == null || !current.Position.Equals(pos)) return null;
        return current.World.Side == EnumAppSide.Server
            ? VscnCivilizationModSystem.NativeTribeReinforcement(current.World, current.Player, pos)
            : VscnClientModSystem.NativeTribeReinforcement(current.World, current.Player, pos);
    }

    private static void AddStrength(BlockPos pos, ref BlockReinforcement? __result)
    {
        if (Extra(pos) is not { Strength: > 0 } extra) return;
        var original = __result;
        // Never mutate the original object or store tribal protection as a personal lock.
        __result = new()
        {
            Strength = (int)Math.Min(int.MaxValue, (long)Math.Max(0, original?.Strength ?? 0) + extra.Strength),
            PlayerUID = original?.PlayerUID,
            LastPlayername = original?.LastPlayername ?? extra.Tribe,
            GroupUid = original?.GroupUid ?? 0,
            LastGroupname = original?.LastGroupname,
            Locked = original?.Locked ?? false,
            LockedByItemCode = original?.LockedByItemCode
        };
    }

    private static void ConsumeEffectiveStrength(ModSystemBlockReinforcement system, BlockPos pos, int byAmount)
    {
        if (byAmount > 0 && Extra(pos) is { Strength: > 0 } extra && current != null)
        {
            int consumed = Math.Min(byAmount, extra.Strength);
            if (current.World.Side == EnumAppSide.Server)
                VscnCivilizationModSystem.ConsumeNativeTribeReinforcement(current.World, current.Player, pos, consumed);
            else extra.Strength -= consumed;
            byAmount -= consumed;
        }
        if (byAmount > 0) system.ConsumeStrength(pos, byAmount);
    }

    public void Dispose()
    {
        lock (Sync)
        {
            if (disposed) return;
            disposed = true;
            if (--users == 0) new Harmony(PatchId).UnpatchAll(PatchId);
        }
    }
}
