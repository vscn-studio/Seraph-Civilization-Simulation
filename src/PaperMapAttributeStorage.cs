using System.Globalization;
using System.IO.Compression;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Datastructures;

namespace SeraphCivilizationSimulation;

// The native byte-array attribute reads a signed Int16 length. Keep every segment below that limit.
internal static class PaperMapAttributeStorage
{
    public const int SegmentLength = 16000;

    public static TreeAttribute Pack(byte[] bytes)
    {
        if (bytes.Length is < 1 or > RecordedPaperMap.MaxEncodedLength) throw new InvalidDataException("Invalid paper map image length.");
        var tree = new TreeAttribute();
        tree.SetInt("version", 1);
        tree.SetInt("length", bytes.Length);
        for (int offset = 0, i = 0; offset < bytes.Length; offset += SegmentLength, i++)
            tree.SetBytes(i.ToString(CultureInfo.InvariantCulture), bytes.AsSpan(offset, Math.Min(SegmentLength, bytes.Length - offset)).ToArray());
        return tree;
    }

    public static byte[]? Read(ITreeAttribute attributes)
    {
        var attribute = attributes[RecordedPaperMap.Attribute];
        if (attribute is ByteArrayAttribute legacy) return legacy.value; // In-memory pre-migration sheets.
        if (attribute is not ITreeAttribute tree || tree.GetInt("version") != 1) return null;
        int length = tree.GetInt("length");
        if (length is < 1 or > RecordedPaperMap.MaxEncodedLength) return null;
        var result = new byte[length];
        for (int offset = 0, i = 0; offset < length; offset += SegmentLength, i++)
        {
            if (tree[i.ToString(CultureInfo.InvariantCulture)] is not ByteArrayAttribute segment
                || segment.value.Length != Math.Min(SegmentLength, length - offset)) return null;
            segment.value.CopyTo(result, offset);
        }
        return result;
    }

    // Only intercept this mod's old image field; every other native attribute follows its original reader.
    // Older saves wrote all compressed bytes but truncated their length to UInt16. Recover the exact
    // DEFLATE boundary without reading ahead into the next attribute, then immediately migrate it.
    private static IAttribute ReadAttribute(IAttribute attribute, BinaryReader stream, string key)
    {
        if (key != RecordedPaperMap.Attribute || attribute is not ByteArrayAttribute)
        {
            attribute.FromBytes(stream);
            return attribute;
        }
        int storedLength = stream.ReadUInt16();
        using var source = new LegacyImageStream(stream.BaseStream);
        using (var deflate = new DeflateStream(source, CompressionMode.Decompress, true))
        using (var reader = new BinaryReader(deflate))
        {
            if (reader.ReadInt32() != 1) throw new InvalidDataException("Invalid legacy paper map version.");
            reader.ReadInt32(); reader.ReadInt32(); reader.ReadInt32();
            float rotation = reader.ReadSingle(); reader.ReadInt64();
            int size = reader.ReadInt32();
            if (!float.IsFinite(rotation) || size is < RecordedPaperMap.VisibleSize or > 408 || size % 2 != 0)
                throw new InvalidDataException("Invalid legacy paper map geometry.");
            if (reader.ReadBytes(size * size * sizeof(int)).Length != size * size * sizeof(int) || deflate.ReadByte() != -1)
                throw new InvalidDataException("Invalid legacy paper map pixels.");
        }
        byte[] bytes = source.Captured.ToArray();
        if ((ushort)bytes.Length != storedLength) throw new InvalidDataException("Invalid legacy paper map compressed length.");
        return Pack(bytes);
    }

    internal static IEnumerable<CodeInstruction> LegacyReaderTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var method = AccessTools.Method(typeof(TreeAttribute), nameof(TreeAttribute.FromBytes), [typeof(BinaryReader)]);
        var locals = method.GetMethodBody()!.LocalVariables;
        int keyIndex = locals.Single(v => v.LocalType == typeof(string)).LocalIndex;
        int attributeIndex = locals.Single(v => v.LocalType == typeof(IAttribute)).LocalIndex;
        var read = AccessTools.Method(typeof(IAttribute), nameof(IAttribute.FromBytes));
        var replacement = AccessTools.Method(typeof(PaperMapAttributeStorage), nameof(ReadAttribute));
        bool replaced = false;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(read))
            {
                var loadKey = new CodeInstruction(OpCodes.Ldloc, keyIndex);
                loadKey.labels.AddRange(instruction.labels);
                loadKey.blocks.AddRange(instruction.blocks);
                yield return loadKey;
                yield return new CodeInstruction(OpCodes.Call, replacement);
                yield return new CodeInstruction(OpCodes.Stloc, attributeIndex);
                replaced = true;
            }
            else yield return instruction;
        }
        if (!replaced) throw new MissingMethodException("Native tree attribute reader changed; legacy paper map migration cannot be installed.");
    }

    private sealed class LegacyImageStream(Stream source) : Stream
    {
        public MemoryStream Captured { get; } = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Captured.Length; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;
            if (Captured.Length >= RecordedPaperMap.MaxEncodedLength) throw new InvalidDataException("Legacy paper map image is too large.");
            int value = source.ReadByte();
            if (value == -1) return 0;
            buffer[0] = (byte)value;
            Captured.WriteByte((byte)value);
            return 1;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) Captured.Dispose(); base.Dispose(disposing); }
    }
}
