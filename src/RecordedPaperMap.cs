using System.IO.Compression;
using HarmonyLib;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace SeraphCivilizationSimulation;

public sealed class RecordedPaperMap
{
    public const string Attribute = "vscncsPaperMapImage";
    public const int VisibleSize = 288; // Immersive Maps 1.2.1: 96 * (5 - 2).
    public const int MaxEncodedLength = 2000000;
    public int MapWidth { get; init; } = VisibleSize;
    public int CenterX { get; init; }
    public int CenterZ { get; init; }
    public int Dimension { get; init; }
    public float Rotation { get; init; }
    public long CapturedAt { get; set; }
    public required int[] Pixels { get; init; } // Native map-cache RGBA, zero means unrecorded.
    public int HalfExtent => (int)Math.Ceiling(MapWidth / 2.0 * (Math.Abs(Math.Cos(Rotation)) + Math.Abs(Math.Sin(Rotation))));
    public int Size => HalfExtent * 2;
    public long Left => (long)CenterX - HalfExtent;
    public long Top => (long)CenterZ - HalfExtent;
    public bool HasTerrain => Pixels.Any(p => (uint)p >> 24 != 0);

    public static bool IsPaperMap(ItemStack? stack) => stack?.Collectible?.Code?.ToString() is "immersivemaps:map-papyrus" or "immersivemaps:map-parchment";

    public static RecordedPaperMap? Describe(ItemStack? stack)
        => Describe(stack, stack?.Collectible?.Code);

    internal static RecordedPaperMap? Describe(ItemStack? stack, AssetLocation? material)
    {
        if (material?.ToString() is not ("immersivemaps:map-papyrus" or "immersivemaps:map-parchment")
            || stack?.Attributes.HasAttribute("mapPositionX") != true || !stack.Attributes.HasAttribute("mapRotation")) return null;
        float rotation = stack.Attributes.GetFloat("mapRotation");
        if (!float.IsFinite(rotation)) return null;
        var position = stack.Attributes.GetBlockPos("mapPosition");
        return new RecordedPaperMap
        {
            MapWidth = material.Path == "map-parchment" ? 480 : VisibleSize,
            CenterX = stack.Attributes.GetInt("mapPositionX"), CenterZ = stack.Attributes.GetInt("mapPositionZ"),
            Dimension = position?.dimension ?? 0, Rotation = rotation % (2 * MathF.PI), Pixels = []
        };
    }

    public bool Contains(double x, double z)
    {
        double dx = x - CenterX, dz = z - CenterZ;
        double cos = Math.Cos(Rotation), sin = Math.Sin(Rotation);
        return Math.Abs(cos * dx + sin * dz) < MapWidth / 2.0 && Math.Abs(-sin * dx + cos * dz) < MapWidth / 2.0;
    }

    public byte[] Encode()
    {
        using var result = new MemoryStream();
        using (var deflate = new DeflateStream(result, CompressionLevel.Fastest, true))
        using (var writer = new BinaryWriter(deflate))
        {
            writer.Write(1); writer.Write(CenterX); writer.Write(CenterZ); writer.Write(Dimension);
            writer.Write(Rotation); writer.Write(CapturedAt); writer.Write(Size);
            foreach (int pixel in Pixels) writer.Write(pixel);
        }
        return result.ToArray();
    }

    public void Write(ItemStack stack) => stack.Attributes[Attribute] = PaperMapAttributeStorage.Pack(Encode());
    public static RecordedPaperMap? Read(ItemStack? stack) => Decode(stack, stack == null ? null : PaperMapAttributeStorage.Read(stack.Attributes));
    public static RecordedPaperMap? Decode(ItemStack? stack, byte[]? bytes)
    {
        var shape = Describe(stack);
        if (shape == null || bytes == null || bytes.Length is < 1 or > MaxEncodedLength) return null;
        try
        {
            using var stream = new DeflateStream(new MemoryStream(bytes, false), CompressionMode.Decompress);
            using var reader = new BinaryReader(stream);
            if (reader.ReadInt32() != 1 || reader.ReadInt32() != shape.CenterX || reader.ReadInt32() != shape.CenterZ
                || reader.ReadInt32() != shape.Dimension || reader.ReadSingle() != shape.Rotation) return null;
            long captured = reader.ReadInt64();
            if (reader.ReadInt32() != shape.Size) return null;
            var pixels = new int[shape.Size * shape.Size];
            for (int z = 0; z < shape.Size; z++)
                for (int x = 0; x < shape.Size; x++)
                {
                    int value = reader.ReadInt32();
                    pixels[z * shape.Size + x] = shape.Contains(shape.Left + x + .5, shape.Top + z + .5) ? value : 0;
                }
            if (stream.ReadByte() != -1) return null;
            return new RecordedPaperMap { MapWidth = shape.MapWidth, CenterX = shape.CenterX, CenterZ = shape.CenterZ, Dimension = shape.Dimension,
                Rotation = shape.Rotation, CapturedAt = captured, Pixels = pixels };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return null; }
    }
}

// Redrawing a sheet deliberately invalidates its old frozen terrain. Moving/reading never does.
internal sealed class PaperMapDrawingCompatibility : IDisposable
{
    private const string PatchId = "vsscsmod.papermaps";
    private static readonly object SyncRoot = new();
    private static int users;
    private bool disposed;
    public PaperMapDrawingCompatibility()
    {
        lock (SyncRoot)
        {
            if (users == 0)
            {
                var type = AccessTools.TypeByName("ImmersiveMaps.Items.ItemMap")
                    ?? throw new MissingMemberException("ImmersiveMaps.Items.ItemMap");
                var draw = AccessTools.DeclaredMethod(type, "DrawStroke", [typeof(ItemStack), typeof(EntityAgent)])
                    ?? throw new MissingMethodException(type.FullName, "DrawStroke");
                var harmony = new Harmony(PatchId);
                try
                {
                    harmony.Patch(AccessTools.Method(typeof(TreeAttribute), nameof(TreeAttribute.FromBytes), [typeof(BinaryReader)]),
                        transpiler: new HarmonyMethod(typeof(PaperMapAttributeStorage), nameof(PaperMapAttributeStorage.LegacyReaderTranspiler)));
                    harmony.Patch(draw, postfix: new HarmonyMethod(typeof(PaperMapDrawingCompatibility), nameof(AfterDraw)));
                }
                catch { harmony.UnpatchAll(PatchId); throw; }
            }
            users++;
        }
    }
    private static void AfterDraw(ItemStack map, EntityAgent byEntity)
    {
        if (byEntity.World.Side == EnumAppSide.Server && !GroundMapData.IsAtlas(map)
            && !map.Attributes.HasAttribute(MapDrawingDocument.Attribute))
        {
            map.Attributes.RemoveAttribute(RecordedPaperMap.Attribute);
            // A fresh native map drawing can change the sheet's position and
            // rotation. Old range coordinates must never follow it to the new map.
            map.Attributes.RemoveAttribute(MapDrawingDocument.Attribute);
            map.Attributes.RemoveAttribute(CivilizationMapInventory.SheetIdAttribute);
            map.Attributes.RemoveAttribute(CivilizationMapInventory.StitchMaskAttribute);
        }
    }
    public void Dispose()
    {
        lock (SyncRoot)
        {
            if (disposed) return;
            disposed = true;
            if (--users == 0) new Harmony(PatchId).UnpatchAll(PatchId);
        }
    }
}
