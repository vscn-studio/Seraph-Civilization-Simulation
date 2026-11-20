using System.Text;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace SeraphCivilizationSimulation;

[ProtoContract]
public sealed class GroundMapPlaceRequest
{
    [ProtoMember(1)] public int X { get; set; }
    [ProtoMember(2)] public int Y { get; set; }
    [ProtoMember(3)] public int Z { get; set; }
    [ProtoMember(4)] public int Dimension { get; set; }
    [ProtoMember(5)] public int Face { get; set; }
    [ProtoMember(6)] public bool DidOffset { get; set; }
    [ProtoMember(7)] public double HitX { get; set; }
    [ProtoMember(8)] public double HitY { get; set; }
    [ProtoMember(9)] public double HitZ { get; set; }
    [ProtoMember(10)] public int HotbarSlot { get; set; }
    [ProtoMember(11)] public string Fingerprint { get; set; } = "";
    [ProtoMember(12)] public float ViewRotation { get; set; }
}

[ProtoContract]
public sealed class GroundMapTakeRequest
{
    [ProtoMember(1)] public int X { get; set; }
    [ProtoMember(2)] public int Y { get; set; }
    [ProtoMember(3)] public int Z { get; set; }
    [ProtoMember(4)] public int Dimension { get; set; }
    [ProtoMember(5)] public int Index { get; set; }
    [ProtoMember(6)] public string Revision { get; set; } = "";
    [ProtoMember(7)] public string Action { get; set; } = "take";
    [ProtoMember(8)] public double HitX { get; set; }
    [ProtoMember(9)] public double HitY { get; set; }
    [ProtoMember(10)] public double HitZ { get; set; }
}

public sealed class BlockGroundMap : Block
{
    internal static string Text(string key, params object[] args) => Lang.Get("vsscsmod:ui.ground-map-" + key, args);
    internal static void Tell(IWorldAccessor world, IPlayer player, string key, params object[] args)
    {
        if (world.Side == EnumAppSide.Server && player is IServerPlayer server)
            server.SendMessage(GlobalConstants.GeneralChatGroup, Lang.GetL(server.LanguageCode, "vsscsmod:ui.ground-map-" + key, args), EnumChatType.Notification);
    }
    internal static bool Access(IWorldAccessor world, IPlayer player, BlockPos pos, Vec3d? hit = null, Vec3d? outward = null)
        => player.Entity?.Alive == true && player.Entity.Pos.Dimension == pos.dimension
            && player.Entity.Pos.XYZ.Add(player.Entity.LocalEyePos).SquareDistanceTo(hit ?? pos.ToVec3d().Add(.5, .5, .5)) <= 36
            && world.Claims.TryAccess(player, pos, EnumBlockAccessFlags.BuildOrBreak)
            && (hit == null || world.Claims.TryAccess(player, new BlockPos((int)Math.Floor(hit.X - (outward?.X ?? 0) * .01),
                (int)Math.Floor(hit.Y - (pos.InternalY - pos.Y) - (outward?.Y ?? 1) * .01),
                (int)Math.Floor(hit.Z - (outward?.Z ?? 0) * .01), pos.dimension), EnumBlockAccessFlags.BuildOrBreak));
    internal static void Give(IWorldAccessor world, IPlayer player, ItemStack stack)
    { if (!player.InventoryManager.TryGiveItemstack(stack, true)) world.SpawnItemEntity(stack, player.Entity.Pos.XYZ); }

    internal static void PlaceOnWall(IWorldAccessor world, IServerPlayer player, GroundMapPlaceRequest packet)
    {
        if (world.Side != EnumAppSide.Server || player.Entity?.Alive != true || packet.Dimension != player.Entity.Pos.Dimension
            || packet.Face < 0 || packet.Face >= 4 || !float.IsFinite(packet.ViewRotation)
            || !double.IsFinite(packet.HitX) || !double.IsFinite(packet.HitY) || !double.IsFinite(packet.HitZ)
            || packet.HitX < 0 || packet.HitX > 1 || packet.HitY < 0 || packet.HitY > 1 || packet.HitZ < 0 || packet.HitZ > 1
            || packet.HotbarSlot != player.InventoryManager.ActiveHotbarSlotNumber) return;
        var slot = player.InventoryManager.ActiveHotbarSlot;
        if (slot?.Itemstack is not { } stack || !GroundMapData.IsAtlas(stack)
            || packet.Fingerprint != ImmersiveMapCompatibility.Fingerprint(stack)) return;
        var position = new BlockPos(packet.X, packet.Y, packet.Z, packet.Dimension);
        var face = BlockFacing.ALLFACES[packet.Face];
        var support = packet.DidOffset ? position.AddCopy(face.Opposite) : position;
        var hit = new Vec3d(packet.HitX, packet.HitY, packet.HitZ);
        var point = hit.AddCopy(support.X, support.InternalY, support.Z);
        if (!Access(world, player, support, point, face.Normald)) return;
        var eye = player.Entity.Pos.XYZ.Add(player.Entity.LocalEyePos).Sub(support.X, support.InternalY, support.Z);
        if (!GroundMapSurface.Visible(world, support, eye, hit)) return;
        Place(world, player, new BlockSelection { Position = position, Face = face, HitPosition = hit, DidOffset = packet.DidOffset }, slot,
            packet.ViewRotation % MathF.Tau);
    }

    internal static void Place(IWorldAccessor world, IPlayer player, BlockSelection selection, ItemSlot slot, float? readingRotation = null)
    {
        if (world.Side != EnumAppSide.Server || slot.Itemstack == null || (!player.Entity.Controls.CtrlKey && !GroundMapData.IsAtlas(slot.Itemstack))) return;
        if (world.BlockAccessor.GetBlockEntity(selection.Position) is BlockEntityGroundMap existing)
        { existing.Add(player, slot); return; }
        if (selection.Face == null) return;
        var supportPos = selection.DidOffset ? selection.Position.AddCopy(selection.Face.Opposite) : selection.Position;
        BlockPos pos = supportPos.AddCopy(selection.Face);
        var support = world.BlockAccessor.GetBlock(supportPos);
        var block = world.GetBlock(new AssetLocation("vsscsmod:groundmaps"));
        if (block == null || !Access(world, player, pos) || !world.Claims.TryAccess(player, supportPos, EnumBlockAccessFlags.BuildOrBreak)
            || world.BlockAccessor.GetBlock(pos).Id != 0 || !(support.GetCollisionBoxes(world.BlockAccessor, supportPos)?.Length > 0)
            || world.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid).Id != 0 || world.BlockAccessor.GetBlock(supportPos, BlockLayersAccess.Fluid).Id != 0)
        { Tell(world, player, "support"); return; }
        if (!GroundMapData.CanPlace(slot.Itemstack, world)) { Tell(world, player, "invalid"); return; }
        world.BlockAccessor.SetBlock(block.Id, pos);
        if (world.BlockAccessor.GetBlockEntity(pos) is BlockEntityGroundMap maps)
        {
            maps.Placement.Face = selection.Face.Index;
            var hit = maps.Placement.FromWorld(selection.HitPosition.AddCopy(supportPos.X - pos.X, supportPos.Y - pos.Y, supportPos.Z - pos.Z));
            maps.Placement.X = Math.Floor(hit.X * 2) * .5 + .25;
            maps.Placement.Z = Math.Floor(hit.Z * 2) * .5 + .25;
            maps.Placement.Height = hit.Y;
            // Use horizontal look even when aiming straight up/down. Never snap to world axes.
            var forward = new EntityPos { Yaw = player.Entity.Pos.Yaw }.GetViewVector();
            maps.Placement.FacePlayer(new(forward.X, forward.Y, forward.Z));
            if (selection.Face.IsHorizontal && readingRotation is { } angle)
            {
                maps.Placement.SetReference(slot.Itemstack);
                maps.Placement.Rotation = angle;
            }
            maps.Add(player, slot);
            if (maps.Sheets.Count == 0) world.BlockAccessor.SetBlock(0, pos);
        }
        else world.BlockAccessor.SetBlock(0, pos);
    }

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (world.Side == EnumAppSide.Server && world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityGroundMap maps)
        {
            var slot = byPlayer.InventoryManager.ActiveHotbarSlot;
            if (RecordedPaperMap.IsPaperMap(slot.Itemstack))
            { if (byPlayer.Entity.Controls.CtrlKey) maps.Add(byPlayer, slot); else Tell(world, byPlayer, "controls"); }
            else maps.Sew(byPlayer);
        }
        return true;
    }
    public override float OnGettingBroken(IPlayer player, BlockSelection selection, ItemSlot slot, float resistance, float dt, int counter) => 1;
    public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1)
    {
        // Ordinary left clicks never destroy a collection. Taking one sheet uses a revision-checked packet.
        if (byPlayer != null) return;
        if (world.Side == EnumAppSide.Server) world.BlockAccessor.SetBlock(0, pos);
    }
    public override void OnNeighbourBlockChange(IWorldAccessor world, BlockPos pos, BlockPos neibpos)
    {
        if (world.Side == EnumAppSide.Server && world.BlockAccessor.GetBlockEntity(pos) is BlockEntityGroundMap maps
            && maps.Sheets.Count > 0 && !maps.Surface().Supported)
            world.BlockAccessor.SetBlock(0, pos);
    }
    public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1) => [];
    public override ItemStack OnPickBlock(IWorldAccessor world, BlockPos pos) => null!;
    public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos pos, IPlayer forPlayer)
        => world.BlockAccessor.GetBlockEntity(pos) is BlockEntityGroundMap maps
            ? (maps.IsAtlas ? Text("atlas-controls") : Text("count", maps.Sheets.Count, maps.MaxSheets) + "\n" + Text("controls"))
                + (maps.Sheets.Count >= 2 ? "\n" + Text("thread-cost", GroundMapData.ThreadCost(maps.Sheets)) : "") : "";
}

public sealed class BlockEntityGroundMap : BlockEntity
{
    internal List<ItemStack> Sheets { get; private set; } = [];
    internal int MaxSheets { get; private set; } = PaperMapsConfig.DefaultMaxSheets;
    internal bool IsAtlas => Sheets.Count == 1 && GroundMapData.IsAtlas(Sheets[0]);
    internal List<ItemStack> DisplaySheets => GroundMapData.DisplaySheets(Sheets, Api.World);
    internal GroundMapPlacement Placement { get; private set; } = new();
    internal GroundMapSurface Surface() => GroundMapSurface.Build(Api.World, Pos, new(DisplaySheets), Placement);
    internal string Revision { get; private set; } = Guid.NewGuid().ToString("N");
    internal GroundMapLayout? Layout => Sheets.Count == 0 ? null : new(DisplaySheets);
    private GroundMapRenderer? renderer;
    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        if (api is ICoreClientAPI client) renderer = new(client, this);
        if (api.Side == EnumAppSide.Server && api.Event != null) RegisterGameTickListener(CheckSupport, 2000);
    }
    private void CheckSupport(float dt)
    {
        if (Sheets.Count > 0 && !Surface().Supported) Api.World.BlockAccessor.SetBlock(0, Pos);
    }
    internal void Add(IPlayer player, ItemSlot slot, Vec3d? hit = null)
    {
        if (Api.Side != EnumAppSide.Server || !BlockGroundMap.Access(Api.World, player, Pos, hit, Placement.Outward) || slot.Itemstack is not { } stack) return;
        bool atlas = GroundMapData.IsAtlas(stack);
        // Capture the server setting on the first successful placement, then persist it with this group.
        int maxSheets = Sheets.Count == 0
            ? Api.ModLoader?.GetModSystem<VscnCivilizationModSystem>()?.Config.PaperMaps.MaxSheets ?? PaperMapsConfig.DefaultMaxSheets
            : MaxSheets;
        if (atlas ? Sheets.Count != 0 || !GroundMapData.CanPlace(stack, Api.World) : !GroundMapData.CanAdd(Sheets, stack, maxSheets))
        { BlockGroundMap.Tell(Api.World, player, "disconnected"); return; }
        Placement.SetReference(stack);
        // Keep unavailable parts folded or hidden; never consume a wholly unsupported map.
        var surface = GroundMapSurface.Build(Api.World, Pos, new(GroundMapData.DisplaySheets(Sheets.Append(stack).ToList(), Api.World)), Placement);
        var added = atlas ? surface.Patches.SelectMany(p => p).ToList() : surface.Patches[^1];
        if (added.Count == 0 && !Sheets.Any(s => SameFootprint(s, stack))) { BlockGroundMap.Tell(Api.World, player, "support"); return; }
        var covered = added.SelectMany(p =>
        {
            double cx = p.Average(v => v.X), cy = p.Average(v => v.Y), cz = p.Average(v => v.Z);
            var normal = surface.Outward;
            return p.Select(v => ((int)Math.Floor(v.X + (cx - v.X) * .0001 + Pos.X - normal.X * .01),
                (int)Math.Floor(v.Y + (cy - v.Y) * .0001 + Pos.Y - normal.Y * .01),
                (int)Math.Floor(v.Z + (cz - v.Z) * .0001 + Pos.Z - normal.Z * .01)));
        }).Distinct();
        foreach (var (x, y, z) in covered)
            if (!Api.World.Claims.TryAccess(player, new BlockPos(x, y, z, Pos.dimension), EnumBlockAccessFlags.BuildOrBreak)) return;
        // Ownership of the exact original transfers to the persistent block entity, never to a client preview.
        MaxSheets = maxSheets;
        Sheets.Add(slot.TakeOut(1)); slot.MarkDirty(); Changed();
    }
    private static bool SameFootprint(ItemStack a, ItemStack b)
    {
        var x = RecordedPaperMap.Describe(a)!; var y = RecordedPaperMap.Describe(b)!;
        return x.CenterX == y.CenterX && x.CenterZ == y.CenterZ && Math.Abs(x.Rotation - y.Rotation) < .00001;
    }
    internal void Take(IPlayer player, int index, string revision, Vec3d? hit = null)
    {
        if (IsAtlas && index >= 0 && index < DisplaySheets.Count) index = 0;
        if (Api.Side != EnumAppSide.Server || revision != Revision || index < 0 || index >= Sheets.Count
            || !player.Entity.Controls.CtrlKey || !BlockGroundMap.Access(Api.World, player, Pos, hit, Placement.Outward)) return;
        var stack = Sheets[index]; Sheets.RemoveAt(index); Changed();
        BlockGroundMap.Give(Api.World, player, stack);
        if (Sheets.Count == 0) Api.World.BlockAccessor.SetBlock(0, Pos);
    }
    internal void Sew(IPlayer player, Vec3d? hit = null)
    {
        if (Api.Side != EnumAppSide.Server || !BlockGroundMap.Access(Api.World, player, Pos, hit, Placement.Outward)) return;
        if (IsAtlas) { BlockGroundMap.Tell(Api.World, player, "atlas-controls"); return; }
        var needle = player.InventoryManager.ActiveHotbarSlot;
        var thread = player.InventoryManager.OffhandHotbarSlot;
        if (needle.Itemstack?.Collectible.Code.ToString() != CivilizationMapInventory.NeedleCode
            || thread.Itemstack?.Collectible.Code.ToString() != CivilizationMapInventory.ThreadCode || !thread.CanTake())
        { BlockGroundMap.Tell(Api.World, player, "tools"); return; }
        if (Sheets.Count < 2) { BlockGroundMap.Tell(Api.World, player, "two"); return; }
        if (Sheets.Any(ImmersiveMapCompatibility.Locked)) { BlockGroundMap.Tell(Api.World, player, "locked"); return; }
        if (!GroundMapData.Connected(Sheets)) { BlockGroundMap.Tell(Api.World, player, "disconnected"); return; }
        int cost = GroundMapData.ThreadCost(Sheets);
        var sources = new List<ItemSlot> { thread };
        foreach (string name in new[] { "hotbar", "backpack" })
        {
            var inventory = player.InventoryManager.GetOwnInventory(name);
            if (inventory == null) continue;
            int start = inventory is Vintagestory.Common.InventoryPlayerBackpacks bags ? bags.bagSlots.Length : 0;
            for (int i = start; i < inventory.Count; i++)
                if (inventory[i] is { } source && !sources.Contains(source) && source.CanTake()
                    && source.Itemstack?.Collectible.Code.ToString() == CivilizationMapInventory.ThreadCode) sources.Add(source);
        }
        int available = sources.Sum(s => s.StackSize);
        if (available < cost) { BlockGroundMap.Tell(Api.World, player, "thread-short", cost, available); return; }
        ItemStack atlas;
        try { atlas = GroundMapData.Sew(Sheets, MaxSheets); }
        catch (InvalidOperationException) { BlockGroundMap.Tell(Api.World, player, "too-many-markers"); return; }
        Sheets.Clear(); Changed();
        foreach (var source in sources)
        {
            int take = Math.Min(cost, source.StackSize);
            if (take > 0) { source.TakeOut(take); source.MarkDirty(); cost -= take; }
            if (cost == 0) break;
        }
        // The Butchering bone needle is a stackable sewing tool, not a durability tool.
        // CollectibleObject defaults to durability 1 even when its JSON declares none.
        BlockGroundMap.Give(Api.World, player, atlas);
        Api.World.BlockAccessor.SetBlock(0, Pos);
    }
    private void Changed() { Revision = Guid.NewGuid().ToString("N"); MarkDirty(true); }
    internal void Interact(IServerPlayer player, GroundMapTakeRequest packet)
    {
        if (Api.Side != EnumAppSide.Server || !double.IsFinite(packet.HitX) || !double.IsFinite(packet.HitY) || !double.IsFinite(packet.HitZ)
            || packet.Revision != Revision || Sheets.Count == 0) return;
        var relative = new Vec3d(packet.HitX, packet.HitY, packet.HitZ);
        var point = relative.AddCopy(Pos.X, Pos.InternalY, Pos.Z);
        if (!BlockGroundMap.Access(Api.World, player, Pos, point, Placement.Outward)) return;
        var eye = player.Entity.Pos.XYZ.Add(player.Entity.LocalEyePos).Sub(Pos.X, Pos.InternalY, Pos.Z);
        var direction = relative - eye; double distance = direction.Length(); if (distance < .001 || distance > 6) return;
        var hit = Surface().Hit(eye, direction / distance, distance + .08);
        if (hit == null || hit.Value.Point.DistanceTo(relative) > .08) return;
        if (!GroundMapSurface.Visible(Api.World, Pos, eye, relative)) return;
        if (packet.Action == "take") Take(player, packet.Index, packet.Revision, point);
        else if (packet.Action == "add" && player.Entity.Controls.CtrlKey) Add(player, player.InventoryManager.ActiveHotbarSlot, point);
        else if (packet.Action == "sew") Sew(player, point);
    }
    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree); tree["sheets"] = GroundMapData.Store(Sheets); tree.SetString("revision", Revision); tree["placement"] = Placement.Store();
        tree.SetInt("maxSheets", MaxSheets);
    }
    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);
        Sheets = GroundMapData.ReadSheets(tree.GetTreeAttribute("sheets"), worldAccessForResolve, allowAtlas: true);
        // Pre-configuration ground groups used 16; never shrink below their actual saved contents.
        MaxSheets = Math.Clamp(Math.Max(Sheets.Count, tree.GetInt("maxSheets", Sheets.Count > 0 ? 16 : PaperMapsConfig.DefaultMaxSheets)),
            2, GroundMapData.MaxStoredSheets);
        Placement = GroundMapPlacement.Read(tree.GetTreeAttribute("placement"));
        if (Sheets.Count > 0) Placement.SetReference(Sheets[0]);
        Revision = tree.GetString("revision", ""); renderer?.Invalidate();
    }
    public override void OnBlockRemoved()
    {
        if (Api?.Side == EnumAppSide.Server)
        {
            var originals = Sheets.ToArray(); Sheets.Clear();
            foreach (var sheet in originals) Api.World.SpawnItemEntity(sheet, Pos.ToVec3d().Add(.5, .1, .5));
        }
        renderer?.Dispose(); renderer = null; base.OnBlockRemoved();
    }
    public override void OnBlockUnloaded() { renderer?.Dispose(); renderer = null; base.OnBlockUnloaded(); }
}

public sealed partial class VscnCivilizationModSystem
{
    private void OnGroundMapPlace(IServerPlayer player, GroundMapPlaceRequest packet)
    {
        if (disposed || immersiveMapCompatibility == null) return;
        BlockGroundMap.PlaceOnWall(sapi!.World, player, packet);
    }

    private void OnGroundMapTake(IServerPlayer player, GroundMapTakeRequest packet)
    {
        var pos = new BlockPos(packet.X, packet.Y, packet.Z, packet.Dimension);
        if (disposed || immersiveMapCompatibility == null) return;
        (sapi!.World.BlockAccessor.GetBlockEntity(pos) as BlockEntityGroundMap)?.Interact(player, packet);
    }
}
