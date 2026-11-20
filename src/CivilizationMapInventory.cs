using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace SeraphCivilizationSimulation;

// Saved inventory compatibility only: return old maps and materials, reject new deposits.
public sealed partial class CivilizationMapInventory : InventoryBasePlayer
{
    public const string InventoryClass = "vscncsmap";
    public const int MaxPages = 10;
    // Page one retains its original class and inventory ID for existing saves.
    public static string PageClass(int page) => page is >= 1 and <= MaxPages
        ? page == 1 ? InventoryClass : InventoryClass + page
        : throw new ArgumentOutOfRangeException(nameof(page));
    public static CivilizationMapInventory? GetPage(IPlayerInventoryManager? manager, int page)
        => page is >= 1 and <= MaxPages ? manager?.GetOwnInventory(PageClass(page)) as CivilizationMapInventory : null;
    public const int MapSlotCount = 12;
    public const int NeedleSlot = MapSlotCount, ThreadSlot = MapSlotCount + 1, SlotCount = MapSlotCount + 2;
    public const string NeedleCode = "butchering:needle-bone", ThreadCode = "game:flaxtwine";
    private ItemSlot[] slots;

    public CivilizationMapInventory(string inventoryId, ICoreAPI api) : base(inventoryId, api)
        => slots = GenEmptySlots(SlotCount);

    public override int Count => SlotCount;
    public override ItemSlot this[int index] { get => slots[index]; set => slots[index] = value; }
    protected override ItemSlot NewSlot(int index) => new AtlasSlot(this, index);
    public override bool CanContain(ItemSlot sinkSlot, ItemSlot sourceSlot)
        => false;
    public static bool Accepts(int index, ItemStack? stack) => index switch
    {
        >= 0 and < MapSlotCount => RecordedPaperMap.IsPaperMap(stack),
        NeedleSlot => stack?.Collectible?.Code?.ToString() == NeedleCode,
        ThreadSlot => stack?.Collectible?.Code?.ToString() == ThreadCode,
        _ => false
    };
    public override float GetSuitability(ItemSlot sourceSlot, ItemSlot targetSlot, bool isMerge)
        => 0; // Explicit placement only; shift-clicking an unrelated bag must not fill the atlas.
    public override void FromTreeAttributes(ITreeAttribute tree)
    {
        if (tree.GetInt("qslots") is not (9 or SlotCount)) throw new InvalidDataException("Invalid civilization map inventory size.");
        slots = SlotsFromTreeAttributes(tree, slots);
        LoadStitches(tree);
    }
    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        SlotsToTreeAttributes(slots, tree);
        SaveStitches(tree);
    }

    public override void DropAll(Vec3d pos, int maxStackSize = 0)
    {
        // Thread already sewn into sheets is still a physical resource on death.
        UnstitchAll(pos);
        base.DropAll(pos, maxStackSize);
    }

    private sealed class AtlasSlot(InventoryBase inventory, int index) : ItemSlotSurvival(inventory)
    {
        public override int MaxSlotStackSize => index == ThreadSlot ? 32 : 1;
        public override bool CanHold(ItemSlot sourceSlot) => false;
        public override bool CanTakeFrom(ItemSlot sourceSlot, EnumMergePriority priority = EnumMergePriority.AutoMerge)
            => false;
    }
}
