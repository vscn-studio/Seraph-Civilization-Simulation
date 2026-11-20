using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;

namespace SeraphCivilizationSimulation;

[ProtoContract]
public sealed class MapDrawingSaveRequest
{
    [ProtoMember(1)] public int HotbarSlot { get; set; }
    [ProtoMember(2)] public string MapFingerprint { get; set; } = "";
    [ProtoMember(3)] public byte[] Document { get; set; } = [];
    [ProtoMember(4)] public bool SetTerritory { get; set; }
    [ProtoMember(5)] public string RequestId { get; set; } = "";
}
[ProtoContract]
public sealed class MapEditResult
{
    [ProtoMember(1)] public string RequestId { get; set; } = "";
    [ProtoMember(2)] public bool Success { get; set; }
    [ProtoMember(3)] public string MessageKey { get; set; } = "map-draw-invalid";
}

public sealed partial class VscnCivilizationModSystem
{
    private readonly Dictionary<string, long> mapEditTimes = [];
    private bool AllowMapEdit(IServerPlayer player)
    {
        long now = sapi!.World.ElapsedMilliseconds;
        if (mapEditTimes.TryGetValue(player.PlayerUID, out long last) && now - last < 500) return false;
        mapEditTimes[player.PlayerUID] = now; return true;
    }
    private void MapEditReply(IServerPlayer player, string id, bool success, string key)
        => channel!.SendPacket(new MapEditResult { RequestId = id, Success = success, MessageKey = key }, player);
    private void OnMapDrawingSave(IServerPlayer player, MapDrawingSaveRequest request)
    {
        if (disposed || immersiveMapCompatibility == null) return;
        void Reply(bool ok, string key) => MapEditReply(player, request.RequestId, ok, key);
        if (!AllowMapEdit(player)) { Reply(false, "map-edit-busy"); return; }
        if (player.Entity?.Alive != true || player.InventoryManager.ActiveHotbarSlotNumber != request.HotbarSlot
            || player.InventoryManager.ActiveHotbarSlot is not { Itemstack: { } stack } slot
            || !RecordedPaperMap.IsPaperMap(stack) || GroundMapData.IsAtlas(stack)
            || ImmersiveMapCompatibility.Locked(stack)
            || !ImmersiveMapCompatibility.CanDraw(stack, player.InventoryManager.OffhandHotbarSlot)
            || ImmersiveMapCompatibility.Fingerprint(stack) != request.MapFingerprint
            || request.Document == null || request.Document.Length is < 1 or > MapDrawingDocument.MaxBytes
            || RecordedPaperMap.Describe(stack) is not { } map)
        { Reply(false, "map-edit-stale"); return; }
        try
        {
            var document = MapDrawingDocument.Decode(request.Document, map);
            var projection = new MapDrawingProjection(map);
            projection.ValidateAreas(document);
            byte[] canonical = document.Encode(map);
            TribeRecord? tribe = null;
            RectangularTerritorySite? site = null;
            if (request.SetTerritory)
            {
                tribe = tribeState.FindMember(player.PlayerUID);
                if (tribe == null || tribe.LeaderUid != player.PlayerUID)
                { Reply(false, "map-edit-leader"); return; }
                try { site = projection.Territory(document, tribe); }
                catch (InvalidDataException) { Reply(false, "map-edit-core"); return; }
                if (tribeState.Tribes.Where(t => t.Id != tribe.Id && t.Dimension == site.Dimension)
                    .SelectMany(t => t.Sites).Any(other => site.FootprintCells!.Any(p => other.ContainsColumn(p.X, p.Z))))
                { Reply(false, "map-edit-overlap"); return; }
                int offset = checked(tribe.Dimension * BlockPos.DimensionBoundary);
                var claims = sapi!.World.Claims.All.Where(c => !ClaimAllowsTribe(c, tribe)).ToArray();
                foreach (var run in site.Footprint!)
                {
                    var volume = new Cuboidi(run.X, Math.Max(0, tribe.Y - FixedTerritoryRules.Depth) + offset, run.Z,
                        run.X + run.Length, BlockPos.DimensionBoundary - 1 + offset, run.Z + 1);
                    if (claims.Any(c => c.Intersects(volume))) { Reply(false, "map-edit-overlap"); return; }
                }
            }
            byte[]? old = stack.Attributes.GetBytes(MapDrawingDocument.Attribute);
            stack.Attributes.SetBytes(MapDrawingDocument.Attribute, canonical);
            if (tribe != null && site != null && !TrySaveTerritoryChange(tribe, () => tribe.Sites = [site]))
            {
                if (old == null) stack.Attributes.RemoveAttribute(MapDrawingDocument.Attribute);
                else stack.Attributes.SetBytes(MapDrawingDocument.Attribute, old);
                Reply(false, "map-edit-save-failed"); return;
            }
            slot.MarkDirty();
            Reply(true, "map-edit-saved");
            if (tribe != null) RefreshCivilizationViews();
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        {
            sapi!.Logger.Warning("Map drawing rejected: {0}", ex.Message);
            Reply(false, "map-draw-invalid");
        }
    }
}
