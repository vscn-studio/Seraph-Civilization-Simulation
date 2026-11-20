using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnCivilizationModSystem
{
    private void InstallSleepInteractionRestrictions()
    {
        sapi!.Event.HandInteract += OnSleepingHandInteract;
        sapi.Event.CanUseBlock += CanUseSleepingBlock;
        sapi.Event.OnTestBlockAccess += OnSleepingBlockAccess;
        sapi.Event.OnPlayerInteractEntity += OnSleepingEntityInteract;
    }

    private TextCommandResult HandleSleepCommand(CmdArgs words)
    {
        string action = words.PopWord("status").ToLowerInvariant();
        bool? value = action switch
        {
            "on" or "enable" => true,
            "off" or "disable" => false,
            "toggle" => !config.Sleep.Enabled,
            "status" => null,
            _ => null
        };
        if (value == null && action is not "status")
            return TextCommandResult.Error("用法: /vsscsmod sleep on|off|toggle|status");
        if (value == null) return TextCommandResult.Success($"睡眠交互限制当前为{(config.Sleep.Enabled ? "开启" : "关闭")}。");

        bool previous = config.Sleep.Enabled;
        config.Sleep.Enabled = value.Value;
        try
        {
            sapi!.StoreModConfig(config, ConfigFile);
            SyncSleepInteractionSettings();
            return TextCommandResult.Success($"睡眠交互限制已{(value.Value ? "开启" : "关闭")}，设置已保存。");
        }
        catch (Exception ex)
        {
            config.Sleep.Enabled = previous;
            sapi!.Logger.Error("Seraph Civilization Simulation failed to save sleep interaction setting: {0}", ex);
            return TextCommandResult.Error("设置未能保存，请检查服务器日志。");
        }
    }

    private void SyncSleepInteractionSetting(IServerPlayer player)
    {
        player.Entity?.WatchedAttributes.SetBool(SleepInteractionGuard.EnabledAttribute, config.Sleep.Enabled);
    }

    private void SyncSleepInteractionSettings()
    {
        foreach (var player in sapi!.World.AllOnlinePlayers.OfType<IServerPlayer>())
            SyncSleepInteractionSetting(player);
    }

    private bool IsPlayerSleeping(IServerPlayer player) => config.Sleep.Enabled
        && SleepInteractionGuard.IsSleeping(player.Entity);

    private void OnSleepingHandInteract(IServerPlayer player, EnumHandInteractNw interaction,
        float secondsPassed, ref EnumHandling handling)
    {
        if (!IsPlayerSleeping(player)) return;

        // Keep block-use messages available so the vanilla bed interaction can
        // wake the player; item use is always an extra action while asleep.
        if (interaction is EnumHandInteractNw.StartHeldItemUse
            or EnumHandInteractNw.CancelHeldItemUse
            or EnumHandInteractNw.StopHeldItemUse
            or EnumHandInteractNw.StepHeldItemUse)
            handling = EnumHandling.PreventDefault;
    }

    private bool CanUseSleepingBlock(IServerPlayer player, BlockSelection blockSel)
    {
        if (!IsPlayerSleeping(player) || IsBedBlock(blockSel)) return true;
        return false;
    }

    private EnumWorldAccessResponse OnSleepingBlockAccess(IPlayer player, BlockSelection blockSel,
        EnumBlockAccessFlags accessType, ref string claimant, EnumWorldAccessResponse response)
    {
        if (response != EnumWorldAccessResponse.Granted
            || (accessType & EnumBlockAccessFlags.BuildOrBreak) == 0
            || !config.Sleep.Enabled
            || !SleepInteractionGuard.IsSleeping(player.Entity)) return response;

        // Permission queries are also made without a player action. Deny building
        // without unmounting or changing sleep state; traversal must remain available.
        claimant = SleepInteractionGuard.Claimant;
        return EnumWorldAccessResponse.DeniedByMod;
    }

    private void OnSleepingEntityInteract(Entity entity, IPlayer byPlayer, ItemSlot slot,
        Vintagestory.API.MathTools.Vec3d hitPosition, int mode, ref EnumHandling handling)
    {
        if (byPlayer is IServerPlayer player && IsPlayerSleeping(player))
            handling = EnumHandling.PreventDefault;
    }

    private bool IsBedBlock(BlockSelection? blockSel)
    {
        // Claims.TryAccess constructs a selection with only Position populated.
        // Resolve the block so a repeated bed-use check during mounting stays allowed.
        var block = blockSel?.Block;
        if (block == null && blockSel?.Position != null)
            block = sapi!.World.BlockAccessor.GetBlock(blockSel.Position);
        var path = block?.Code?.Path;
        if (!string.IsNullOrEmpty(path) && path.Contains("bed", StringComparison.OrdinalIgnoreCase)) return true;
        return block?.GetType().Name.Contains("Bed", StringComparison.OrdinalIgnoreCase) == true;
    }

    private void UninstallSleepInteractionRestrictions()
    {
        if (sapi == null) return;
        sapi.Event.HandInteract -= OnSleepingHandInteract;
        sapi.Event.CanUseBlock -= CanUseSleepingBlock;
        sapi.Event.OnTestBlockAccess -= OnSleepingBlockAccess;
        sapi.Event.OnPlayerInteractEntity -= OnSleepingEntityInteract;
    }
}
