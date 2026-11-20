using System.Globalization;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace SeraphCivilizationSimulation;

public sealed partial class CivilizationMapInventory
{
    public const string SheetIdAttribute = "vscncsSheetId", StitchMaskAttribute = "vscncsStitches";
    private const string LedgerAttribute = "vscncsStitchLedger", RefundAttribute = "vscncsThreadRefund";
    private sealed record Stitch(int A, int B, string SheetA, string SheetB);
    private List<Stitch> stitches = [];
    private int pendingThread;
    public int StitchCount => stitches.Count;

    public void Retire(IPlayer player)
    {
        if (Api.Side != EnumAppSide.Server || player.Entity == null) return;
        pendingThread += stitches.Count;
        stitches.Clear();
        SyncMasks();
        ReturnThread();
        for (int i = 0; i < SlotCount; i++)
        {
            if (slots[i].Empty) continue;
            var stack = slots[i].TakeOutWhole();
            slots[i].MarkDirty();
            player.InventoryManager.TryGiveItemstack(stack);
            if (stack.StackSize > 0) Api.World.SpawnItemEntity(stack, player.Entity.Pos.XYZ);
        }
    }

    private void SaveStitches(ITreeAttribute tree)
    {
        tree.SetString(LedgerAttribute, JsonConvert.SerializeObject(stitches));
        tree.SetInt(RefundAttribute, pendingThread);
    }

    private void LoadStitches(ITreeAttribute tree)
    {
        string json = tree.GetString(LedgerAttribute, "[]");
        if (json.Length > 32768) throw new InvalidDataException("Invalid paper stitching ledger.");
        var loaded = JsonConvert.DeserializeObject<List<Stitch>>(json) ?? [];
        if (loaded.Count > 66 || loaded.Any(s => s.A < 0 || s.B <= s.A || s.B >= MapSlotCount)
            || loaded.DistinctBy(s => (s.A, s.B)).Count() != loaded.Count)
            throw new InvalidDataException("Invalid paper stitching ledger.");
        stitches = loaded;
        pendingThread = Math.Max(0, tree.GetInt(RefundAttribute));
    }

    private void SyncMasks()
    {
        var masks = new int[MapSlotCount];
        foreach (var stitch in stitches) { masks[stitch.A] |= 1 << stitch.B; masks[stitch.B] |= 1 << stitch.A; }
        for (int i = 0; i < MapSlotCount; i++)
            if (slots[i].Itemstack is { } stack && stack.Attributes.GetInt(StitchMaskAttribute) != masks[i])
            {
                stack.Attributes.SetInt(StitchMaskAttribute, masks[i]); slots[i].MarkDirty();
            }
    }

    private void ReturnThread(Vec3d? dropPosition = null)
    {
        if (pendingThread <= 0) return;
        var item = Api.World.GetItem(new AssetLocation(ThreadCode));
        if (item == null) return; // Preserve the debt if a required item is temporarily unavailable.
        var slot = slots[ThreadSlot];
        int capacity = slot.Empty ? Math.Min(32, item.MaxStackSize)
            : Accepts(ThreadSlot, slot.Itemstack) ? Math.Max(0, Math.Min(32, item.MaxStackSize) - slot.StackSize) : 0;
        int added = Math.Min(capacity, pendingThread);
        if (added > 0)
        {
            if (slot.Empty) slot.Itemstack = new ItemStack(item, added); else slot.Itemstack!.StackSize += added;
            pendingThread -= added; slot.MarkDirty();
        }
        var player = Player;
        while (pendingThread > 0 && (dropPosition != null || player?.Entity != null))
        {
            var returned = new ItemStack(item, Math.Min(pendingThread, Math.Min(32, item.MaxStackSize)));
            int quantity = returned.StackSize;
            // DropAll must drop at the death position, not silently move debt into another inventory.
            if (dropPosition == null) player!.InventoryManager.TryGiveItemstack(returned);
            if (returned.StackSize > 0) Api.World.SpawnItemEntity(returned, dropPosition ?? player!.Entity.Pos.XYZ);
            pendingThread -= quantity;
        }
    }

    private void UnstitchAll(Vec3d position)
    {
        if (Api.Side != EnumAppSide.Server) return;
        pendingThread += stitches.Count; stitches.Clear(); SyncMasks(); ReturnThread(position);
    }
}
