using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.Common;
using SeraphCivilizationSimulation;

internal static class PaperMapStorageChecks
{
    private static (ItemStack Paper, RecordedPaperMap Image) Sheet(int noisyPixels, float rotation = 0, bool parchment = false)
    {
        var item = new Item { Code = new AssetLocation(parchment ? "immersivemaps:map-parchment" : "immersivemaps:map-papyrus"), ItemId = 101, MaxStackSize = 1 };
        var paper = new ItemStack(item);
        paper.Attributes.SetInt("mapPositionX", 1000); paper.Attributes.SetInt("mapPositionZ", 1000);
        paper.Attributes.SetFloat("mapRotation", rotation);
        var shape = RecordedPaperMap.Describe(paper)!;
        var random = new Random(12345);
        var pixels = new int[shape.Size * shape.Size];
        for (int i = 0; i < pixels.Length; i++)
            if (shape.Contains(shape.Left + i % shape.Size + .5, shape.Top + i / shape.Size + .5))
                pixels[i] = unchecked((int)0xff000000) | (i < noisyPixels ? random.Next(0x1000000) : 0x336699);
        return (paper, new RecordedPaperMap { MapWidth = shape.MapWidth, CenterX = shape.CenterX, CenterZ = shape.CenterZ, Dimension = shape.Dimension,
            Rotation = shape.Rotation, CapturedAt = 123456789, Pixels = pixels });
    }

    public static void Run(Action<bool, string> check)
    {
        var (oldPaper, oldImage) = Sheet(12000);
        byte[] oldBytes = oldImage.Encode();
        check(oldBytes.Length > short.MaxValue && oldBytes.Length < ushort.MaxValue, "Crash reproduction must exceed the native signed attribute length.");
        oldPaper.Attributes.SetBytes(RecordedPaperMap.Attribute, oldBytes);
        bool reproduced = false;
        try { new TreeAttribute().FromBytes(((TreeAttribute)oldPaper.Attributes).ToBytes()); }
        catch (ArgumentOutOfRangeException) { reproduced = true; }
        check(reproduced, "The previous single-byte-array image format must reproduce the reported native deserialization crash.");

        var sheets = new List<ItemStack>();
        foreach (var (paper, image) in new[] { Sheet(0), Sheet(12000), Sheet(int.MaxValue), Sheet(int.MaxValue, MathF.PI / 4), Sheet(int.MaxValue, 0, true), Sheet(int.MaxValue, MathF.PI / 4, true) })
        {
            image.Write(paper);
            var packed = paper.Attributes.GetTreeAttribute(RecordedPaperMap.Attribute)!;
            check(packed != null && packed.Where(p => p.Value is ByteArrayAttribute).All(p => ((ByteArrayAttribute)p.Value).value.Length <= 16000),
                "Every stored map segment must stay below the native byte-array length limit.");
            var tree = new TreeAttribute(); tree.FromBytes(((TreeAttribute)paper.Attributes).ToBytes());
            var restored = paper.Clone(); restored.Attributes = tree;
            check(RecordedPaperMap.Read(restored)!.Pixels.SequenceEqual(image.Pixels), "Native item attribute binary serialization must preserve large map images.");
            var world = Proxy.Make<IWorldAccessor>((m, a) => m.Name == "GetItem" ? paper.Item : null);
            var fromPacket = StackConverter.FromPacket(StackConverter.ToPacket(paper), world);
            check(RecordedPaperMap.Read(fromPacket)!.Pixels.SequenceEqual(image.Pixels), "The real native inventory network conversion must preserve large map images.");
            var registry = Proxy.Make<IClassRegistryAPI>((m, a) => m.Name == "CreateInvNetworkUtil" ? Proxy.Make<IInventoryNetworkUtil>((m, a) => null) : null);
            var api = Proxy.Make<ICoreAPI>((m, a) => m.Name switch { "get_World" => world, "get_ClassRegistry" => registry, _ => null });
            var clientInventory = new CivilizationMapInventory("vscncsmap-test", api);
            new InventoryNetworkUtil(clientInventory, api).UpdateFromPacket(world,
                new Packet_InventoryUpdate { SlotId = 11, ItemStack = StackConverter.ToPacket(paper) });
            check(RecordedPaperMap.Read(clientInventory[11].Itemstack)!.Pixels.SequenceEqual(image.Pixels),
                "The crash stack's actual native inventory update handler must accept large maps in the twelfth slot.");
            using var stream = new MemoryStream(paper.ToBytes());
            var savedPaper = new ItemStack(new BinaryReader(stream)); savedPaper.ResolveBlockOrItem(world);
            check(RecordedPaperMap.Read(savedPaper)!.Pixels.SequenceEqual(image.Pixels), "The native saved item stack reader must preserve large map images.");
            sheets.Add(paper);
        }
        var parchment = RecordedPaperMap.Describe(sheets[4])!;
        check(parchment.MapWidth == 480 && parchment.Contains(1239, 1000) && !parchment.Contains(1241, 1000),
            "Parchment must preserve Immersive Maps' larger 480-block footprint.");
        check(RecordedPaperMap.Read(sheets[2])!.Encode().Length > ushort.MaxValue, "Full-noise regression must also cover truncation beyond UInt16.");
        var inventory = new TreeAttribute(); var slots = new TreeAttribute(); inventory["slots"] = slots;
        for (int i = 0; i < 12; i++) slots.SetItemstack(i.ToString(), sheets[3].Clone());
        var inventoryCopy = new TreeAttribute(); inventoryCopy.FromBytes(inventory.ToBytes());
        var inventoryWorld = Proxy.Make<IWorldAccessor>((m, a) => m.Name == "GetItem" ? sheets[3].Item : null);
        check(Enumerable.Range(0, 12).All(i =>
        {
            var sheet = inventoryCopy.GetTreeAttribute("slots").GetItemstack(i.ToString()); sheet.ResolveBlockOrItem(inventoryWorld);
            return RecordedPaperMap.Read(sheet)!.Pixels.SequenceEqual(RecordedPaperMap.Read(sheets[3])!.Pixels);
        }), "Twelve large snapshots must survive the native nested inventory binary reader.");
        foreach (string corruption in new[] { "version", "length", "segment" })
        {
            var broken = sheets[2].Clone(); var tree = broken.Attributes.GetTreeAttribute(RecordedPaperMap.Attribute);
            if (corruption == "version") tree.SetInt("version", 2);
            else if (corruption == "length") tree.SetInt("length", RecordedPaperMap.MaxEncodedLength + 1);
            else tree.SetBytes("0", [1]);
            check(RecordedPaperMap.Read(broken) == null, "Malformed segmented images must be rejected without crashing: " + corruption);
        }
    }

    // Run while the real Immersive Maps integration has installed its scoped legacy migration patch.
    public static void VerifyLegacy(Action<bool, string> check)
    {
        foreach (var (paper, image) in new[] { Sheet(0), Sheet(12000), Sheet(int.MaxValue), Sheet(int.MaxValue, MathF.PI / 4) })
        {
            paper.Attributes.SetBytes(RecordedPaperMap.Attribute, image.Encode());
            paper.Attributes.SetInt("after-image", 7654321);
            paper.Attributes.SetBytes("unrelated-bytes", [9, 8, 7]);
            var world = Proxy.Make<IWorldAccessor>((m, a) => m.Name == "GetItem" ? paper.Item : null);
            var recovered = StackConverter.FromPacket(StackConverter.ToPacket(paper), world);
            check(recovered.Attributes.GetTreeAttribute(RecordedPaperMap.Attribute) != null
                && RecordedPaperMap.Read(recovered)!.Pixels.SequenceEqual(image.Pixels), "Old native inventory packets must recover and migrate the full frozen terrain.");
            check(recovered.Attributes.GetInt("after-image") == 7654321
                && recovered.Attributes.GetBytes("unrelated-bytes").SequenceEqual(new byte[] { 9, 8, 7 }),
                "Legacy image recovery must stop exactly before subsequent attributes and preserve unrelated native bytes.");
            using var stream = new MemoryStream(paper.ToBytes());
            var savedPaper = new ItemStack(new BinaryReader(stream)); savedPaper.ResolveBlockOrItem(world);
            check(savedPaper.Attributes.GetTreeAttribute(RecordedPaperMap.Attribute) != null
                && RecordedPaperMap.Read(savedPaper)!.Pixels.SequenceEqual(image.Pixels), "Already saved old paper items must be recoverable without discarding the sheet or its recorded terrain.");
            check(StackConverter.FromPacket(StackConverter.ToPacket(savedPaper), world).Attributes.GetInt("after-image") == 7654321,
                "Recovered papers must immediately serialize back using the safe segmented format.");
        }
    }
}
