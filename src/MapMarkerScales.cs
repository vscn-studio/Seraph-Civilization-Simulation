using Newtonsoft.Json;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace SeraphCivilizationSimulation;

internal static class MapMarkerScales
{
    internal const string Attribute = "vsscsMarkerScales";
    internal static Dictionary<string, double> Read(ItemStack stack)
    {
        try
        {
            string json = stack.Attributes.GetString(Attribute, "{}");
            if (json.Length > 30000) return [];
            var data = JsonConvert.DeserializeObject<Dictionary<string, double>>(json, new JsonSerializerSettings { MaxDepth = 4 }) ?? [];
            return data.Where(p => p.Key.Length <= 128 && double.IsFinite(p.Value) && p.Value >= .5 && p.Value <= 3)
                .Take(512).ToDictionary();
        }
        catch (JsonException) { return []; }
    }
    internal static void Write(ItemStack stack, Dictionary<string, double> scales)
    {
        string json = JsonConvert.SerializeObject(scales);
        if (scales.Count > 512 || json.Length > 30000) throw new InvalidDataException("Too many marker sizes.");
        stack.Attributes.SetString(Attribute, json);
    }
}
[ProtoContract]
public sealed class MapMarkerScaleRequest
{
    [ProtoMember(1)] public int HotbarSlot { get; set; }
    [ProtoMember(2)] public string MapFingerprint { get; set; } = "";
    [ProtoMember(3)] public string MarkerId { get; set; } = "";
    [ProtoMember(4)] public double Scale { get; set; } = 1;
    [ProtoMember(5)] public string RequestId { get; set; } = "";
}
public sealed partial class VscnCivilizationModSystem
{
    private void OnMapMarkerScale(IServerPlayer player, MapMarkerScaleRequest request)
    {
        if (disposed || immersiveMapCompatibility == null) return;
        void Reply(bool ok, string key) => MapEditReply(player, request.RequestId, ok, key);
        if (!AllowMapEdit(player)) { Reply(false, "map-edit-busy"); return; }
        if (player.Entity?.Alive != true || player.InventoryManager.ActiveHotbarSlotNumber != request.HotbarSlot
            || player.InventoryManager.ActiveHotbarSlot is not { Itemstack: { } stack } slot
            || !ImmersiveMapCompatibility.MapType.IsInstanceOfType(stack.Item) || ImmersiveMapCompatibility.Locked(stack)
            || !ImmersiveMapCompatibility.CanDraw(stack, player.InventoryManager.OffhandHotbarSlot)
            || ImmersiveMapCompatibility.Fingerprint(stack) != request.MapFingerprint
            || !double.IsFinite(request.Scale) || request.Scale is < .5 or > 3
            || string.IsNullOrEmpty(request.MarkerId) || !ImmersiveMapCompatibility.Read(stack).Contains(request.MarkerId))
        { Reply(false, "map-edit-stale"); return; }
        var scales = MapMarkerScales.Read(stack);
        var markers = ImmersiveMapCompatibility.Read(stack);
        foreach (string key in scales.Keys.Where(k => !markers.Contains(k)).ToArray()) scales.Remove(key);
        scales[request.MarkerId] = request.Scale;
        try { MapMarkerScales.Write(stack, scales); }
        catch (InvalidDataException) { Reply(false, "map-draw-invalid"); return; }
        slot.MarkDirty(); Reply(true, "map-edit-saved");
    }
}
