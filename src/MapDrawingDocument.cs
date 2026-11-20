using System.Text;
using System.IO.Compression;
using Newtonsoft.Json;
using Vintagestory.API.Common;

namespace SeraphCivilizationSimulation;

// A document belongs to one physical sheet. Keep it below the engine's
// signed-Int16 byte-array limit and validate it before writing item attributes.
internal sealed class MapDrawingDocument
{
    internal const string Attribute = "vsscsMapDrawing";
    internal const int MaxBytes = 30000;
    internal const int MaxLines = 128;
    internal const int MaxPoints = 4000;
    internal const int MaxAreas = 128;
    public int Version { get; set; } = 1;
    public bool TerritorySelected { get; set; }
    public List<MapDrawingLine> Lines { get; set; } = [];
    public List<MapDrawingArea> Areas { get; set; } = [];

    internal void Validate(RecordedPaperMap map)
    {
        if (Version != 1 || Lines == null || Areas == null || Lines.Count > MaxLines || Areas.Count > MaxAreas)
            throw new InvalidDataException("Invalid map drawing document.");
        int points = 0;
        foreach (var line in Lines)
        {
            if (line?.Points == null || line.Points.Count < 2 || (points += line.Points.Count) > MaxPoints)
                throw new InvalidDataException("Invalid map drawing line.");
            foreach (var point in line.Points) ValidatePoint(map, point);
        }
        foreach (var area in Areas)
        {
            if (area == null || string.IsNullOrWhiteSpace(area.Name) || area.Name.Length > 64
                || area.Name.Any(char.IsControl) || area.Seed == null || area.Notes == null || area.Notes.Length > 500)
                throw new InvalidDataException("Invalid map drawing area.");
            ValidatePoint(map, area.Seed);
        }
    }

    private static void ValidatePoint(RecordedPaperMap map, MapDrawingPoint? point)
    {
        if (point == null || !double.IsFinite(point.X) || !double.IsFinite(point.Z) || !map.Contains(point.X, point.Z))
            throw new InvalidDataException("Map drawing point is outside the sheet.");
    }

    internal byte[] Encode(RecordedPaperMap map)
    {
        Validate(map);
        using var output = new MemoryStream();
        using (var zip = new DeflateStream(output, CompressionLevel.Fastest, true))
        {
            byte[] json = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(this));
            zip.Write(json);
        }
        byte[] bytes = output.ToArray();
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Map drawing document is too large.");
        return bytes;
    }

    internal static MapDrawingDocument Read(ItemStack stack, RecordedPaperMap map)
    {
        byte[]? bytes = stack.Attributes.GetBytes(Attribute);
        if (bytes == null || bytes.Length == 0) return new();
        return Decode(bytes, map);
    }

    internal static MapDrawingDocument Decode(byte[] bytes, RecordedPaperMap map)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Map drawing document is too large.");
        try
        {
            if (bytes.Length == 0) throw new InvalidDataException("Empty map drawing.");
            // Accept the early uncompressed format, but bound decompression before JSON parsing.
            if (bytes[0] != (byte)'{')
            {
                using var zip = new DeflateStream(new MemoryStream(bytes), CompressionMode.Decompress);
                using var decoded = new MemoryStream();
                var buffer = new byte[4096];
                int read;
                while ((read = zip.Read(buffer)) > 0)
                {
                    if (decoded.Length + read > 600000) throw new InvalidDataException("Drawing expands beyond its limit.");
                    decoded.Write(buffer, 0, read);
                }
                bytes = decoded.ToArray();
            }
            var doc = JsonConvert.DeserializeObject<MapDrawingDocument>(Encoding.UTF8.GetString(bytes), new JsonSerializerSettings { MaxDepth = 16 })
                ?? throw new InvalidDataException("Missing map drawing document.");
            doc.Validate(map);
            return doc;
        }
        catch (JsonException e) { throw new InvalidDataException("Invalid map drawing document.", e); }
    }
}

internal sealed class MapDrawingLine
{
    public List<MapDrawingPoint> Points { get; set; } = [];
}

internal sealed class MapDrawingPoint
{
    [JsonProperty("x")] public double X { get; set; }
    [JsonProperty("z")] public double Z { get; set; }
}

internal sealed class MapDrawingArea
{
    public string Notes { get; set; } = "";
    public string Name { get; set; } = "";
    public int Color { get; set; }
    public MapDrawingPoint Seed { get; set; } = new();
}
