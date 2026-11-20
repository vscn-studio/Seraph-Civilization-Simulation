using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnClientModSystem
{
    private GuidanceWindow? guidance;
    private GuidancePacket? pendingGuidance;
    private bool guidanceAccepted;
    private (CharacterSystem System, IClientPlayer Player)? deferredCharacterJoin;
    private Harmony? guidanceHarmony;
    private static VscnClientModSystem? guidanceClient;
    private int? civilizationLives;
    private TribeReinforcementCompatibility? tribeReinforcement;
    private TribeReinforcementPacket? selectedReinforcement;
    private long reinforcementReceived, reinforcementListener;
    private sealed class TagState { public bool Friendly; }
    private static readonly ConditionalWeakTable<EntityBehaviorNameTag, TagState> TagStates = new();

    private void StartGuidanceClient()
    {
        guidanceClient = this;
        tribeReinforcement ??= new();
        if (capi != null)
        {
            capi.Event.PauseResume += OnGuidancePauseResume;
            reinforcementListener = capi.Event.RegisterGameTickListener(_ =>
            { TryShowGuidance(); RequestTribeReinforcement(); }, 250);
        }
        guidanceHarmony = new Harmony("vsscsmod.guidance.client");
        guidanceHarmony.Patch(AccessTools.Method(typeof(CharacterSystem), "Event_PlayerJoin"),
            prefix: new HarmonyMethod(typeof(VscnClientModSystem), nameof(BeforeCharacterJoin)));
        var dead = AccessTools.TypeByName("Vintagestory.Client.NoObf.GuiDialogDead");
        guidanceHarmony.Patch(AccessTools.Method(dead, "ComposeDialog"),
            prefix: new HarmonyMethod(typeof(VscnClientModSystem), nameof(BeforeDeathDialog)),
            transpiler: new HarmonyMethod(typeof(VscnClientModSystem), nameof(DeathDialogText)));
        guidanceHarmony.Patch(AccessTools.Method(typeof(EntityNameTagRendererRegistry), "GetNameTagRenderer"),
            postfix: new HarmonyMethod(typeof(VscnClientModSystem), nameof(AfterNameRenderer)));
        guidanceHarmony.Patch(AccessTools.Method(typeof(EntityBehaviorNameTag), "OnRenderFrame"),
            prefix: new HarmonyMethod(typeof(VscnClientModSystem), nameof(BeforeTagFrame)));
    }

    private void OnGuidance(GuidancePacket packet)
    {
        guidanceAccepted = !packet.Required;
        pendingGuidance = packet;
        civilizationLives = packet.LivesLeft;
        // Packets can arrive on the connection screen, before native HUDs have
        // initialized. Their IsOpened overrides are not safe to query for logging.
        capi?.Logger.Notification("Seraph Civilization Simulation guidance received: required={0}, ready={1}, playerReady={2}, paused={3}",
            packet.Required, ready, capi.PlayerReadyFired, capi.IsGamePaused);
        if (!packet.Required)
        {
            if (ready) pendingGuidance = null;
            guidance?.Release(); guidance = null;
            if (ready && capi != null) OnLivesReset(new() { LivesLeft = packet.LivesLeft });
            ResumeCharacterJoin();
            return;
        }
        TryShowGuidance();
    }

    private void OnGuidancePauseResume(bool paused)
    {
        if (paused) guidance?.Suspend();
        // Reopen on the next tick, after the native dialog finishes closing.
    }

    private void TryShowGuidance()
    {
        ResumeCharacterJoin();
        if (!ready || capi == null || (pendingGuidance == null && guidance == null)) return;
        if (pendingGuidance is { Required: false } accepted)
        {
            pendingGuidance = null;
            OnLivesReset(new() { LivesLeft = accepted.LivesLeft });
            return;
        }
        // CharacterSystem's join callback is deferred before it can pause the local
        // server. Name confirmation therefore works before native character creation.
        if (capi.IsGamePaused
            || capi.Gui.LoadedGuis.Any(g => g is GuiDialogCreateCharacter && g.IsOpened()))
        { guidance?.Suspend(); return; }
        if (pendingGuidance is not { } packet)
        {
            if (guidance != null && !guidance.IsOpened()) guidance.TryOpen();
            return;
        }
        pendingGuidance = null;
        OnLivesReset(new() { LivesLeft = packet.LivesLeft });
        if (guidance != null && guidance.Nonce == packet.Nonce) guidance.Update(packet);
        else
        {
            guidance?.Release();
            guidance = new(capi, packet, decision =>
            {
                if (!ready) return;
                capi.Logger.Notification("Seraph Civilization Simulation guidance submitted: agree={0}, playerReady={1}, paused={2}",
                    decision.Agree, capi.PlayerReadyFired, capi.IsGamePaused);
                channel!.SendPacket(decision);
            });
        }
        if (!guidance.IsOpened()) guidance.TryOpen();
    }

    private void LeaveGuidance()
    { guidance?.Release(); guidance = null; pendingGuidance = null; guidanceAccepted = false; deferredCharacterJoin = null; civilizationLives = null; selectedReinforcement = null; }

    private static bool BeforeCharacterJoin(CharacterSystem __instance, IClientPlayer byPlayer, ICoreClientAPI ___capi)
    {
        var client = guidanceClient;
        if (client == null || !ReferenceEquals(client.capi, ___capi)
            || byPlayer.PlayerUID != ___capi.World.Player.PlayerUID || client.guidanceAccepted) return true;
        client.deferredCharacterJoin = (__instance, byPlayer);
        return false;
    }

    private void ResumeCharacterJoin()
    {
        if (!ready || !guidanceAccepted || deferredCharacterJoin is not { } join || capi?.IsGamePaused != false) return;
        deferredCharacterJoin = null;
        AccessTools.Method(typeof(CharacterSystem), "Event_PlayerJoin").Invoke(join.System, [join.Player]);
    }

    private void RequestTribeReinforcement()
    {
        if (!ready || capi?.World.Player?.CurrentBlockSelection?.Position is not { } pos) { selectedReinforcement = null; return; }
        channel!.SendPacket(new TribeReinforcementRequest { X = pos.X, Y = pos.Y, Z = pos.Z, Dimension = pos.dimension });
    }

    private void OnTribeReinforcement(TribeReinforcementPacket packet)
    {
        if (!ready || packet.Block == null || capi?.World.Player?.CurrentBlockSelection?.Position is not { } pos
            || !packet.Block.Position.Equals(pos) || capi.World.BlockAccessor.GetBlock(pos).Id != packet.BlockId) return;
        selectedReinforcement = packet;
        reinforcementReceived = Environment.TickCount64;
    }

    internal static TribeReinforcementPacket? NativeTribeReinforcement(IWorldAccessor world, IPlayer player, BlockPos pos)
    {
        var client = guidanceClient;
        return client is { ready: true, selectedReinforcement: { } extra } && ReferenceEquals(client.capi?.World, world)
            && client.capi.World.Player.PlayerUID == player.PlayerUID && extra.Block.Position.Equals(pos)
            && Environment.TickCount64 - client.reinforcementReceived < 1000 && world.BlockAccessor.GetBlock(pos).Id == extra.BlockId
            ? extra : null;
    }

    private static void BeforeDeathDialog(object __instance)
    {
        if (guidanceClient?.civilizationLives is { } lives)
            AccessTools.Field(__instance.GetType(), "livesLeft").SetValue(__instance, lives);
    }

    private static IEnumerable<CodeInstruction> DeathDialogText(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Ldstr && instruction.operand as string == "Congratulations, you died! Forever!")
                instruction.operand = "重生次数已用尽，每次进入服务器时尝试部落新生转生；需有合格部落和安全落点。";
            yield return instruction;
        }
    }

    private static bool Friendly(Entity entity)
    {
        string? own = guidanceClient?.capi?.World.Player?.Entity?.WatchedAttributes.GetString(VscnCivilizationModSystem.TribeAttribute, "");
        return entity is EntityPlayer && !string.IsNullOrEmpty(own) && entity.WatchedAttributes.GetString(VscnCivilizationModSystem.TribeAttribute, "") == own;
    }

    private static void AfterNameRenderer(Entity entity, ref NameTagRendererDelegate __result)
    {
        if (entity is not EntityPlayer) return;
        var original = __result;
        __result = (api, target) => Friendly(target)
            ? api.Gui.TextTexture.GenUnscaledTextTexture(target.GetBehavior<EntityBehaviorNameTag>()?.DisplayName ?? "",
                CairoFont.WhiteMediumText().WithColor([.35, .85, .35, 1]), new TextBackground
                {
                    FillColor = GuiStyle.DialogLightBgColor, Padding = 3, Radius = GuiStyle.ElementBGRadius,
                    Shade = true, BorderColor = GuiStyle.DialogBorderColor, BorderWidth = 3
                })
            : original(api, target);
    }

    private static void BeforeTagFrame(EntityBehaviorNameTag __instance)
    {
        if (__instance.entity is not EntityPlayer) return;
        bool friendly = Friendly(__instance.entity);
        var state = TagStates.GetOrCreateValue(__instance);
        if (state.Friendly == friendly) return;
        state.Friendly = friendly;
        AccessTools.Method(typeof(EntityBehaviorNameTag), "OnNameChanged").Invoke(__instance, null);
    }
}
