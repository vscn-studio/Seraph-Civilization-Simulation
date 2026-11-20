using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace SeraphCivilizationSimulation;

// Optional integration: no Coinage assembly is copied or referenced by this mod.
public sealed class CoinageEconomyCompatibility : IDisposable
{
    private const string PatchId = "vsscsmod.coinageeconomy";
    private static int references;
    private static Harmony? patches;
    [ThreadStatic] private static string? creatingPlayer;
    public static Action<string>? OpenClientTrade { get; set; }
    private bool disposed;

    public CoinageEconomyCompatibility(ICoreAPI api)
    {
        if (references++ > 0) return;
        patches = new Harmony(PatchId);
        try
        {
            Patch("Coinage.Items.ItemShapableMetalDisc", "OnHeldInteractStart", nameof(HeldInteract));
            Patch("Coinage.BlockEntities.BECoinpress", "SpawnCoinInBestSpot", postfix: nameof(PressedCoin));
            Patch("Coinage.BlockEntities.BEAnvilPile", "OnBlockInteractStep", nameof(BeforeHammer), nameof(AfterHammer));
            Patch("Coinage.CoinageModSystem", "ServerHandleCreateCoinType", nameof(BeforeDesign), finalizer: nameof(AfterDesignRequest));
            Patch("Coinage.CoinageModSystem", "CreateCoinDesign", postfix: nameof(AfterCreatedDesign));
        }
        catch { patches.UnpatchAll(PatchId); patches = null; references--; throw; }
    }
    private static void Patch(string type, string method, string? prefix = null, string? postfix = null, string? finalizer = null)
    {
        var target = AccessTools.Method(AccessTools.TypeByName(type), method) ?? throw new MissingMethodException(type, method);
        HarmonyMethod? Handler(string? name) => name == null ? null : new HarmonyMethod(typeof(CoinageEconomyCompatibility), name);
        patches!.Patch(target, Handler(prefix), Handler(postfix), finalizer: Handler(finalizer));
    }
    private static bool HeldInteract(ItemSlot slot, EntityAgent byEntity, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
    {
        if (EconomyInventory.CoinKey(slot.Itemstack).Length == 0 || entitySel?.Entity is not EntityPlayer target
            || byEntity is not EntityPlayer actor || target.PlayerUID == actor.PlayerUID) return true;
        handling = EnumHandHandling.PreventDefault;
        if (firstEvent && byEntity.World.Side == EnumAppSide.Client && ((ICoreClientAPI)byEntity.Api).World.Player.PlayerUID == actor.PlayerUID)
            OpenClientTrade?.Invoke(target.PlayerUID);
        return false;
    }
    private static VscnCivilizationModSystem? Server(ICoreAPI api) => api.Side == EnumAppSide.Server
        ? api.ModLoader.GetModSystem<VscnCivilizationModSystem>() : null;
    private static void PressedCoin(BlockEntity __instance, IPlayer? player, ItemStack coinStack)
    {
        Observe(__instance.Api, () =>
        {
            var server = Server(__instance.Api); if (server == null) return;
            string uid = player?.PlayerUID ?? server.AutomaticCoinOwner(coinStack);
            server.RegisterCoin(coinStack, uid);
        });
    }
    private sealed record HammerState(ItemSlot Slot, int Count, ItemStack Coin, string Uid);
    private static void BeforeHammer(BlockEntity __instance, IPlayer byPlayer, out HammerState? __state)
    {
        __state = null;
        if (__instance.Api.Side != EnumAppSide.Server || AccessTools.Property(__instance.GetType(), "CanMakeCoin")?.GetValue(__instance) is not true) return;
        var slot = AccessTools.Property(__instance.GetType(), "PlanchetSlot")?.GetValue(__instance) as ItemSlot;
        if (slot?.Itemstack == null) return;
        var item = __instance.Api.World.GetItem(slot.Itemstack.Collectible.CodeWithPart("coin", 0));
        var stamp = byPlayer.Entity.LeftHandItemSlot?.Itemstack;
        if (item == null || stamp == null) return;
        var coin = new ItemStack(item); coin.Attributes.SetLong("coinShape", slot.Itemstack.Attributes.GetLong("coinShape"));
        coin.Attributes.SetLong("coinType", stamp.Attributes.GetLong("coinType"));
        __state = new(slot, slot.StackSize, coin, byPlayer.PlayerUID);
    }
    private static void AfterHammer(BlockEntity __instance, bool __result, HammerState? __state)
    {
        if (__result && __state != null && __state.Count - __state.Slot.StackSize == 1)
            Observe(__instance.Api, () => Server(__instance.Api)?.RegisterCoin(__state.Coin, __state.Uid));
    }
    private static void BeforeDesign(IServerPlayer fromPlayer, out string? __state)
    { __state = creatingPlayer; creatingPlayer = fromPlayer.PlayerUID; }
    private static void AfterDesignRequest(string? __state) => creatingPlayer = __state;
    private static void AfterCreatedDesign(object __instance, object? __result)
    {
        if (__result == null || creatingPlayer == null) return;
        var api = AccessTools.Field(__instance.GetType(), "_api")?.GetValue(__instance) as ICoreAPI;
        if (api != null && __result.GetType().GetProperty("Id")?.GetValue(__result) is long id)
            Observe(api, () => Server(api)?.RegisterCoinDesign(id, creatingPlayer));
    }
    private static void Observe(ICoreAPI api, Action action)
    {
        try { action(); }
        catch (Exception ex) { api.Logger.Error("Seraph Civilization Simulation Coinage record failed: {0}", ex); }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (--references != 0) return;
        patches?.UnpatchAll(PatchId); patches = null;
    }
}
