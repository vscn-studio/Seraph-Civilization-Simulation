using System.Collections;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using SeraphCivilizationSimulation;

internal static class GroundMapChecks
{
    private sealed class UploadedMesh : MeshRef { public override bool Initialized => true; }
    private sealed class HandPlayer : EntityPlayer
    {
        internal ItemSlot Offhand = new DummySlot();
        public override ItemSlot LeftHandItemSlot => Offhand;
    }

    internal static void Run(Assembly maps, ICoreAPI common, Action<bool, string> check)
    {
        var config = Newtonsoft.Json.JsonConvert.DeserializeObject<VscnConfig>("{}")!;
        config.Validate();
        check(config.PaperMaps.MaxSheets == 49, "Old configuration files must default to 49 sheets.");
        foreach (int limit in new[] { 2, 16, 49, PaperMapsConfig.MaximumMaxSheets })
        {
            config.PaperMaps.MaxSheets = limit; config.Validate();
            var copy = Newtonsoft.Json.JsonConvert.DeserializeObject<VscnConfig>(Newtonsoft.Json.JsonConvert.SerializeObject(config))!;
            check(copy.PaperMaps.MaxSheets == limit, "Configured sheet limits must survive JSON serialization.");
        }
        foreach (int limit in new[] { int.MinValue, 0, 1, PaperMapsConfig.MaximumMaxSheets + 1, int.MaxValue })
        {
            config.PaperMaps.MaxSheets = limit;
            bool rejected = false;
            try { config.Validate(); } catch (ArgumentException) { rejected = true; }
            check(rejected, "Invalid sheet limits must be rejected before a configuration reload.");
        }
        config.PaperMaps = null!;
        bool nullRejected = false;
        try { config.Validate(); } catch (ArgumentException) { nullRejected = true; }
        check(nullRejected, "A null paper map configuration section must be rejected.");
        Type Native(string name) => maps.GetType("ImmersiveMaps." + name, true)!;
        object? Call(object target, string method, params object?[] args) => AccessTools.Method(target.GetType(), method).Invoke(target, args);
        void Set(object target, string name, object? value) => AccessTools.Property(target.GetType(), name).SetValue(target, value);
        var paper = (Item)Activator.CreateInstance(Native("Items.ItemMap"))!;
        paper.Code = new("immersivemaps:map-papyrus"); paper.ItemId = 101;
        var parchment = (Item)Activator.CreateInstance(Native("Items.ItemMap"))!;
        parchment.Code = new("immersivemaps:map-parchment"); parchment.ItemId = 104;
        AccessTools.Field(paper.GetType(), "api").SetValue(paper, common);
        ItemStack Sheet(int x, int z = 0, float rotation = 0, Item? item = null, int dimension = 0)
        {
            var stack = new ItemStack(item ?? paper);
            stack.Attributes.SetBlockPos("mapPosition", new BlockPos(x, 100, z, dimension));
            stack.Attributes.SetFloat("mapRotation", rotation);
            stack.Attributes.SetString("other-mod-data", "preserve me");
            return stack;
        }
        IDictionary Markers(params (string Id, int X, int Z, string Name, bool Pinned)[] values)
        {
            var dictionary = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), Native("Waypoints.Waypoint")))!;
            foreach (var value in values)
            {
                var marker = Activator.CreateInstance(Native("Waypoints.Waypoint"), value.X, value.Z, 0x123456, "home", value.Name)!;
                Set(marker, "Id", value.Id); Set(marker, "Pinned", value.Pinned); Set(marker, "Notes", "notes");
                dictionary.Add(value.Id, marker);
            }
            return dictionary;
        }
        void Reject(Action action, string message)
        { bool rejected = false; try { action(); } catch (InvalidOperationException) { rejected = true; } check(rejected, message); }
        var a = Sheet(0); var b = Sheet(144); var c = Sheet(288);
        check(GroundMapData.CanAdd([], a) && !GroundMapData.CanAdd([], new ItemStack(paper)), "Only initialized paper maps may be placed.");
        check(GroundMapData.CanAdd([a], b) && GroundMapData.CanAdd([a], c), "Overlapping maps and shared edges must connect.");
        check(!GroundMapData.CanAdd([a], Sheet(289)) && !GroundMapData.CanAdd([a], Sheet(288, 288)), "Gaps and corner-only contact must not connect.");
        check(!GroundMapData.CanAdd([a], Sheet(0, item: parchment)) && !GroundMapData.CanAdd([a], Sheet(0, dimension: 1)), "Material and dimension must match.");
        check(!GroundMapData.CanAdd(Enumerable.Repeat(a, PaperMapsConfig.DefaultMaxSheets).ToList(), b), "Sheet limit must be enforced.");
        check(GroundMapData.CanAdd(Enumerable.Repeat(a, 48).ToList(), b)
            && GroundMapData.CanAdd([a, b], c, 3) && !GroundMapData.CanAdd([a, b], c, 2),
            "The default must allow the 49th sheet and custom limits must apply when adding maps.");
        Reject(() => GroundMapData.Sew(Enumerable.Repeat(a, 50).ToList()), "A new default atlas cannot exceed 49 sheets.");
        Reject(() => GroundMapData.Sew([a, b, c], 2), "Sewing must enforce the group's captured limit.");
        check(GroundMapData.ThreadCost([a, c, Sheet(576)]) == 2 && GroundMapData.ThreadCost([a, b, c]) == 3,
            "Each connected pair costs one twine, including cycles; cost must not be merely sheet count minus one.");
        check(GroundMapData.ThreadCost([a, c, Sheet(0, 288), Sheet(288, 288)]) == 4
            && GroundMapData.ThreadCost([a, Sheet(288, 288), Sheet(0, dimension: 1)]) == 0,
            "A two-by-two edge-joined assembly has four seams; corner contact and different dimensions cost nothing.");
        check(GroundMapData.ThreadCost(Enumerable.Repeat(a, 16).ToList()) == 120,
            "All pairwise overlaps must count once, even when more than one normal twine stack is needed.");
        check(GroundMapData.Connected([a, Sheet(576), c]), "Connectivity must not depend on insertion order after replacing a bridge sheet.");
        Reject(() => GroundMapData.Sew([a, Sheet(600)]), "Disconnected groups must not be sewn.");
        a.Attributes.SetString("mapLockedBy", "owner");
        Reject(() => GroundMapData.Sew([a, b]), "Even the owner's locked sheet must reject sewing.");
        a.Attributes.RemoveAttribute("mapLockedBy");
        var payload = new byte[100000]; new Random(7).NextBytes(payload);
        a.Attributes[RecordedPaperMap.Attribute] = PaperMapAttributeStorage.Pack(payload);
        var layout = new GroundMapLayout([a, b]);
        check(Math.Abs(layout.Pieces.SelectMany(p => p).Sum(MapPolygon.Area) - 432 * 288) < .001, "Overlap must be drawn once, with exact union area.");
        var point = layout.Ground(new MapPoint(200, 0));
        check(layout.Hit(point.X, point.Z) == 1 && layout.Hit(0, 0) == -1, "Picking must resolve real geographic footprints and empty margins.");
        var elbow = new GroundMapLayout([Sheet(0), Sheet(288), Sheet(0, 288)]);
        check(new AtlasViewport(new(288, 288), 288, 0, 0).Clip(elbow).All(p => p.Count == 0),
            "A viewport inside the atlas bounding rectangle must still leave gaps in the actual stitched union blank.");
        double Covered(AtlasViewport view, GroundMapLayout group) => view.Clip(group).SelectMany(p => p).Sum(MapPolygon.Area);
        var interiorView = layout.ViewportFor(new(72, 0), 0);
        var boundaryView = layout.ViewportFor(new(270, 100), 0);
        check(GroundAtlasClient.TryGetHudTarget(layout, new(72, 0), 0, 0, new(72, 0), out var insideTarget, out var insideEdge)
            && insideTarget == new MapPoint(72, 0) && !insideEdge,
            "A pinned HUD target already inside the current map viewport must stay at its actual location.");
        check(GroundAtlasClient.TryGetHudTarget(layout, new(72, 0), 0, 0, new(260, 0), out var edgeTarget, out var edgePinned)
            && edgePinned && Math.Abs(new AtlasViewport(new(72, 0), 288, 0, 0).Project(edgeTarget).X - 1) < 1e-8
            && Math.Abs(new AtlasViewport(new(72, 0), 288, 0, 0).Project(edgeTarget).Z) < 1e-8,
            "A pinned marker outside the current view but within recorded map data must be moved to the view edge.");
        check(!GroundAtlasClient.TryGetHudTarget(layout, new(10000, 0), 0, 0, new(400, 0), out _, out _)
            && !GroundAtlasClient.TryGetHudTarget(layout, new(72, 0), 0, 0, new(10000, 0), out _, out _)
            && !GroundAtlasClient.TryGetHudTarget(layout, new(72, 0), 1, 0, new(400, 0), out _, out _),
            "Pinned HUD markers must be hidden outside the player's recorded data area, outside atlas data, or in another dimension.");
        var aheadHud = GroundAtlasClient.HudEdgePosition(new(0, 0), new(0, -10), 0, -1);
        var behindHud = GroundAtlasClient.HudEdgePosition(new(0, 0), new(0, 10), 0, -1);
        var rightHud = GroundAtlasClient.HudEdgePosition(new(0, 0), new(10, 0), 0, -1);
        check(aheadHud == (0d, -1d) && behindHud == (0d, 1d) && rightHud == (1d, 0d),
            "Pinned HUD markers must follow the marker's bearing relative to the player's heading.");
        var turnedHud = GroundAtlasClient.HudEdgePosition(new(0, 0), new(0, -10), 0, 1);
        check(turnedHud == (0d, 1d), "Turning around must move a formerly forward marker to the bottom screen edge.");
        foreach (float angle in new[] { .4f, -.8f, MathF.PI / 2 })
        {
            check(GroundAtlasClient.TryGetHudTarget(layout, new(72, 0), 0, angle, new(260, 0), out var rotatedEdge, out var rotatedPinned)
                && rotatedPinned && Math.Max(Math.Abs(new AtlasViewport(new(72, 0), 288, angle, 0).Project(rotatedEdge).X),
                    Math.Abs(new AtlasViewport(new(72, 0), 288, angle, 0).Project(rotatedEdge).Z)) <= 1 + 1e-8,
                "An out-of-view pinned target must land on its rotated viewport boundary.");
        }
        check(interiorView.Center == new MapPoint(72, 0) && boundaryView.Center == new MapPoint(144, 0)
            && boundaryView.Project(boundaryView.Holder!.Value) == new MapPoint(.875, 100d / 144)
            && Math.Abs(Covered(boundaryView, layout) - 288 * 288) < .01,
            "The view follows in the interior, stops at the recorded edge and moves the player marker off-center while keeping paper filled.");
        var beyondView = layout.ViewportFor(new(10000, 0), 0);
        check(beyondView.Center == new MapPoint(144, 0) && Math.Abs(Covered(beyondView, layout) - 288 * 288) < .01,
            "Walking outside recorded coverage must keep the final filled viewport rather than scroll into blank paper.");
        var elbowView = elbow.ViewportFor(new(180, 180), 0);
        check(Math.Abs(Covered(elbowView, elbow) - 288 * 288) < .01
            && Math.Abs((elbowView.Center.X - 180) * (elbowView.Center.X - 180) + (elbowView.Center.Z - 180) * (elbowView.Center.Z - 180) - 180 * 180) < .01,
            "A concave union must choose the closest fully covered view, not clamp only to its bounding rectangle.");
        var grid = new GroundMapLayout([Sheet(0), Sheet(288), Sheet(0, 288), Sheet(288, 288)]);
        check(grid.ViewportFor(new(140, 140), 0).Center == new MapPoint(140, 140)
            && grid.ViewportFor(new(-100, 400), 0).Center == new MapPoint(0, 288),
            "A full grid scrolls continuously across sheet boundaries and clamps both axes at outer edges.");
        foreach (float angle in new[] { .2f, .7f, -1.2f, MathF.PI / 2 })
        {
            var rotatedGrid = grid.ViewportFor(new(140, 140), 0, angle);
            check(rotatedGrid.Rotation == angle && rotatedGrid.Width == 288
                && Math.Abs(Covered(rotatedGrid, grid) - 288 * 288) < .01
                && Math.Abs(rotatedGrid.Center.X - 140) + Math.Abs(rotatedGrid.Center.Z - 140) < .001,
                "Manual angles must keep an available filled view centered at the native map scale.");
            var rotatedEdge = grid.ViewportFor(new(800, 800), 0, angle);
            check(Math.Abs(Covered(rotatedEdge, grid) - 288 * 288) < .01,
                "A manually rotated view must still clamp to a filled window whenever one exists.");
            foreach (var corner in new[] { new MapPoint(-144, -144), new(432, -144), new(-144, 432), new(432, 432) })
            {
                var cornerView = grid.ViewportFor(corner, 0, angle);
                check(grid.Contains(corner, 0) && Math.Abs(Covered(cornerView, grid) - cornerView.Width * cornerView.Width) < .01,
                    "Rotated recorded corners must retain a filled paper view instead of creating blank margins.");
            }
        }
        var narrowRotated = layout.ViewportFor(new(100, 100), 0, MathF.PI / 4);
        check(narrowRotated.Width == 203 && Math.Abs(Covered(narrowRotated, layout) - 203 * 203) < .01,
            "If no native-width rotated window fits, an inscribed crop must fill every paper pixel with recorded data.");
        var narrowOutside = layout.ViewportFor(new(10000, 10000), 0, MathF.PI / 4);
        check(Math.Abs(Covered(narrowOutside, layout) - narrowOutside.Width * narrowOutside.Width) < .01,
            "Leaving a narrow rotated map must retain filled recorded terrain instead of scrolling into unexplored blank space.");
        var mixedRotation = new GroundMapLayout([Sheet(0, 0, .3f), Sheet(140, 20, -.2f), Sheet(110, 180, .6f)]);
        foreach (float angle in new[] { -.8f, .1f, .9f, 1.7f, 2.8f })
        {
            var fittedRotation = mixedRotation.ViewportFor(new(400, 400), 0, angle);
            check(Math.Abs(Covered(fittedRotation, mixedRotation) - fittedRotation.Width * fittedRotation.Width) < .02,
                "Different original sheet angles must still fill the entire paper at a manually selected heading.");
            check(fittedRotation.Width == 288 || !new AtlasViewportFit(mixedRotation, angle, fittedRotation.Width + 1).HasFilledView,
                "Rotation must retain the largest available filled crop, avoiding unnecessary zoom or invented data.");
        }
        var duplicateLayout = new GroundMapLayout([a, a.Clone()]);
        check(duplicateLayout.ViewportFor(new(80, 80), 0).Center == new MapPoint(0, 0)
            && layout.ViewportFor(new(72, 0), 1).Clip(layout).All(p => p.Count == 0),
            "Identical sheets still fill paper; another dimension must not reveal recorded terrain.");
        var ring = new GroundMapLayout((from x in new[] { -288, 0, 288 } from z in new[] { -288, 0, 288 }
            where x != 0 || z != 0 select Sheet(x, z)).ToList());
        var ringView = ring.ViewportFor(new(0, 0), 0);
        check(Math.Abs(Covered(ringView, ring) - 288 * 288) < .01
            && Math.Abs(ringView.Center.X * ringView.Center.X + ringView.Center.Z * ringView.Center.Z - 288 * 288) < .01,
            "An enclosed hole must choose the nearest filled viewport, without displaying invented terrain in the hole.");
        var fullGrid = new GroundMapLayout((from x in Enumerable.Range(0, 4) from z in Enumerable.Range(0, 4) select Sheet(x * 288, z * 288)).ToList());
        var farGridView = fullGrid.ViewportFor(new(2000, 2000), 0);
        check(farGridView.Center == new MapPoint(864, 864) && Math.Abs(Covered(farGridView, fullGrid) - 288 * 288) < .01,
            "The maximum sixteen-sheet atlas must retain a filled, native-scale view at its farthest edge.");
        var parchmentLayout = new GroundMapLayout([Sheet(0, item: parchment), Sheet(240, item: parchment)]);
        var parchmentView = new AtlasViewport(new(120, 0), parchmentLayout.Shapes[0].MapWidth, 0, 0);
        check(parchmentView.Width == 480 && parchmentView.Project(new(360, 0)) == new MapPoint(1, 0)
            && Math.Abs(parchmentView.Clip(parchmentLayout).SelectMany(p => p).Sum(MapPolygon.Area) - 480 * 480) < .01,
            "Parchment atlases must retain their native 480-block window instead of using papyrus scale or total atlas span.");
        check(parchmentLayout.ViewportFor(new(350, 100), 0).Center == new MapPoint(240, 0),
            "Parchment boundary fitting must retain its original 480-block scale.");
        var random = new Random(473);
        for (int i = 0; i < 80; i++)
        {
            float angle = (float)(random.NextDouble() * Math.PI * 2);
            var rotated = new GroundMapLayout([Sheet(2000000, -2000000, angle), Sheet(2000090, -1999970, angle + .3f)]);
            double area = rotated.Pieces.SelectMany(p => p).Sum(MapPolygon.Area);
            check(area >= 288 * 288 - .01 && area <= 2 * 288 * 288 + .01, "Rotated clipping at large coordinates must preserve union bounds.");
            foreach (var polygon in rotated.Pieces.SelectMany(p => p))
            {
                var center = new MapPoint(polygon.Average(p => p.X), polygon.Average(p => p.Z));
                check(rotated.Shapes.Any(s => s.Contains(center.X, center.Z)), "A rendered piece must never leak outside all maps.");
            }
            var fitted = rotated.ViewportFor(new(2000400, -1999600), 0);
            check(Math.Abs(Covered(fitted, rotated) - 288 * 288) < .05,
                "Differently rotated maps at large coordinates must retain a completely filled viewport near edges.");
        }
        var varied = new GroundMapLayout(Enumerable.Range(0, 16).Select(i => Sheet(random.Next(-100, 101), random.Next(-100, 101), (float)random.NextDouble())).ToList());
        var filledSamples = (from x in Enumerable.Range(-10, 21) from z in Enumerable.Range(-10, 21)
            let samplePoint = new MapPoint(x * 20, z * 20)
            where Math.Abs(Covered(new(samplePoint, 288, varied.Shapes[0].Rotation, 0), varied) - 288 * 288) < .0001
            select samplePoint).Append(new(varied.Shapes[0].CenterX, varied.Shapes[0].CenterZ)).ToList();
        for (int i = 0; i < 80; i++)
        {
            var holder = new MapPoint(random.Next(-500, 501), random.Next(-500, 501));
            var fitted = varied.ViewportFor(holder, 0);
            check(Math.Abs(Covered(fitted, varied) - 288 * 288) < .05,
                "A maximum-size atlas with arbitrary original rotations must fill every chosen viewport.");
            var centered = new AtlasViewport(holder, 288, varied.Shapes[0].Rotation, 0);
            check(Math.Abs(Covered(centered, varied) - 288 * 288) >= .01
                || Math.Abs(fitted.Center.X - holder.X) + Math.Abs(fitted.Center.Z - holder.Z) < .001,
                "Whenever actual stitched coverage can fill a player-centered view, fitting must preserve that center.");
            double Distance(MapPoint p) => (p.X - holder.X) * (p.X - holder.X) + (p.Z - holder.Z) * (p.Z - holder.Z);
            check(Distance(fitted.Center) <= filledSamples.Min(Distance) + .01,
                "The selected filled view must keep the holder at least as central as every independently sampled filled view.");
        }
        ImmersiveMapCompatibility.Write(a, Markers(("same", 0, 0, "Home", false)));
        ImmersiveMapCompatibility.Write(b, Markers(("same", 0, 0, "Home", false), ("extra", 200, 0, "Camp", false)));
        var atlas = GroundMapData.Sew([a, b]);
        check(GroundMapData.IsAtlas(atlas) && atlas.Collectible == a.Collectible && ImmersiveMapCompatibility.Read(atlas).Count == 2,
            "An atlas must reuse the native item and deduplicate identical copied markers.");
        ImmersiveMapCompatibility.Write(b, Markers(("same", 200, 0, "Different", false)));
        check(ImmersiveMapCompatibility.Read(GroundMapData.Sew([a, b])).Count == 2, "Conflicting marker IDs must retain both marker contents.");
        ImmersiveMapCompatibility.Write(atlas, Markers(("edited", 200, 0, "Edited", true), ("outside", 10000, 0, "Outside", true)));
        var current = ImmersiveMapCompatibility.Read(atlas);
        check((bool)GroundMapCompatibility.Property(current["edited"]!, "Pinned")! && !(bool)GroundMapCompatibility.Property(current["outside"]!, "Pinned")!,
            "Only atlas markers inside the stitched union may remain pinned.");
        var world = Proxy.Make<IWorldAccessor>((m, args) => m.Name switch
        { "GetItem" => args![0] is int id ? id == 101 ? paper : parchment : args![0]!.Equals(paper.Code) ? paper : parchment, _ => null });
        foreach (int count in new[] { 16, 49, 64 })
        {
            int columns = count == 49 ? 7 : 8;
            var originals = Enumerable.Range(0, count).Select(i => Sheet(i % columns * 288, i / columns * 288)).ToList();
            var large = GroundMapData.Sew(originals, Math.Max(49, count));
            var reloaded = new ItemStack(large.ToBytes()); reloaded.ResolveBlockOrItem(world);
            var last = RecordedPaperMap.Describe(originals[^1])!;
            check(GroundMapData.Sheets(reloaded, world).Count == count && GroundMapData.CanPlace(reloaded, world)
                && GroundMapData.Contains(reloaded, last.CenterX, last.CenterZ),
                "Saved atlases must retain every sheet and their full coverage independently of creation limits.");
            var restoredOriginals = GroundMapData.Split(reloaded, world);
            check(restoredOriginals.Count == count && restoredOriginals.All(s => s.Attributes.GetString("other-mod-data") == "preserve me")
                && restoredOriginals.Select(s => RecordedPaperMap.Describe(s)!.CenterX).SequenceEqual(originals.Select(s => RecordedPaperMap.Describe(s)!.CenterX)),
                "Old and oversized saved atlases must split back into all original maps in order.");
        }
        var invalid = GroundMapData.Store([Sheet(0)]); invalid.SetInt("count", GroundMapData.MaxStoredSheets + 1);
        check(GroundMapData.ReadSheets(invalid, world).Count == 0, "The storage safety bound must remain independent of configurable limits.");
        var saved = new ItemStack(atlas.ToBytes()); saved.ResolveBlockOrItem(world);
        check(GroundMapData.Contains(saved, 200, 0) && !GroundMapData.Contains(saved, 10000, 0),
            "A deserialized atlas must recognize its fixed coverage before its original sheets are resolved for rendering.");
        var split = GroundMapData.Split(saved, world);
        check(split.Count == 2 && split.All(s => s.Attributes.GetString("other-mod-data") == "preserve me" && !GroundMapData.IsAtlas(s)), "Binary persistence and disassembly must retain original item attributes.");
        check(PaperMapAttributeStorage.Read(split[0].Attributes)!.SequenceEqual(payload), "Large original terrain data must survive atlas tree binary serialization.");
        check(split.Select(s => s.Attributes.GetInt("mapPositionX")).SequenceEqual(new[] { 0, 144 }), "Disassembly must restore every original map footprint.");
        check(split.All(s => ImmersiveMapCompatibility.Read(s).Contains("edited") && !ImmersiveMapCompatibility.Read(s).Contains("same")
            && !(bool)GroundMapCompatibility.Property(ImmersiveMapCompatibility.Read(s)["edited"]!, "Pinned")!), "Current edits/deletions must survive splitting; small maps must clear pins.");
        saved.Attributes.SetString("mapLockedBy", "owner");
        check(GroundMapData.Split(saved, world).Count == 0, "Locked atlas disassembly must be refused.");
        var entity = new EntityAgent { World = world, LeftHandItemSlot = new DummySlot() };
        string before = ImmersiveMapCompatibility.Fingerprint(atlas);
        AccessTools.Method(paper.GetType(), "DrawStroke").Invoke(paper, [atlas, entity]);
        check(ImmersiveMapCompatibility.Fingerprint(atlas) == before && !(bool)AccessTools.Method(paper.GetType(), "CanRedraw").Invoke(paper, [atlas, new DummySlot()])!, "Atlas redraw must not change range, image, markers or durability.");
        var drawnSheet = Sheet(0);
        byte[] drawingBytes = [1, 2, 3];
        drawnSheet.Attributes.SetBytes(MapDrawingDocument.Attribute, drawingBytes);
        using (new PaperMapDrawingCompatibility())
        {
            string drawingFingerprint = ImmersiveMapCompatibility.Fingerprint(drawnSheet);
            AccessTools.Method(paper.GetType(), "DrawStroke").Invoke(paper, [drawnSheet, entity]);
            check(drawnSheet.Attributes.GetBytes(MapDrawingDocument.Attribute).SequenceEqual(drawingBytes)
                && ImmersiveMapCompatibility.Fingerprint(drawnSheet) == drawingFingerprint,
                "A right-click redraw must preserve a paper map with saved areas, including when both compatibility patches are installed.");
        }
        atlas = new ItemStack(atlas.ToBytes()); atlas.ResolveBlockOrItem(world);
        var slot = new DummySlot(atlas);
        var manager = Proxy.Make<IPlayerInventoryManager>((m, args) => m.Name switch { "get_ActiveHotbarSlot" => slot, "get_ActiveHotbarSlotNumber" => 2, _ => null });
        var playerEntity = new EntityPlayer { Alive = true };
        var player = Proxy.Make<IServerPlayer>((m, args) => m.Name switch { "get_Entity" => playerEntity, "get_InventoryManager" => manager, _ => null });
        var packet = Activator.CreateInstance(Native("Network.PinWaypointPacket"))!;
        Set(packet, "HotbarSlot", 2); Set(packet, "Id", "edited"); Set(packet, "Pinned", false);
        check(GroundMapCompatibility.Pin(player, packet), "An unlocked held atlas must accept pin requests for in-range markers.");
        Set(packet, "Id", "outside"); check(!GroundMapCompatibility.Pin(player, packet), "Out-of-union pin requests must fail.");
        Set(packet, "Id", "edited"); Set(packet, "HotbarSlot", 1); check(!GroundMapCompatibility.Pin(player, packet), "Wrong-slot pin requests must fail.");
        Set(packet, "HotbarSlot", 2); atlas.Attributes.SetString("mapLockedBy", "owner");
        check(!GroundMapCompatibility.Pin(player, packet), "Locked atlases must not accept pin changes.");
        atlas.Attributes.RemoveAttribute("mapLockedBy");
        int pinSyncs = 0;
        var channel = Proxy.Make<IServerNetworkChannel>((m, args) => { if (m.Name == "SendPacket") pinSyncs++; return null; });
        var serverApi = Proxy.Make<ICoreServerAPI>((m, args) => null);
        var handler = Activator.CreateInstance(Native("Network.MapRequestHandler"), serverApi, channel, null)!;
        Call(handler, "OnPinWaypoint", player, packet);
        check(!(bool)GroundMapCompatibility.Property(ImmersiveMapCompatibility.Read(atlas)["edited"]!, "Pinned")!, "The real native handler must commit authorized atlas pin changes.");
        Set(packet, "Pinned", true); Call(handler, "OnPinWaypoint", player, packet);
        check((bool)GroundMapCompatibility.Property(ImmersiveMapCompatibility.Read(atlas)["edited"]!, "Pinned")! && pinSyncs == 2,
            "The real server must persist and synchronize pin-on for a deserialized atlas before resolving its originals.");
        Set(packet, "Pinned", false); Call(handler, "OnPinWaypoint", player, packet);
        check(!(bool)GroundMapCompatibility.Property(ImmersiveMapCompatibility.Read(atlas)["edited"]!, "Pinned")! && pinSyncs == 3,
            "The real server must also persist and synchronize unpinning the reloaded atlas.");
        Set(packet, "Pinned", true); atlas.Attributes.SetString("mapLockedBy", "owner");
        Call(handler, "OnPinWaypoint", player, packet);
        check(!(bool)GroundMapCompatibility.Property(ImmersiveMapCompatibility.Read(atlas)["edited"]!, "Pinned")!, "The real native handler must reject locked atlas pin changes.");
        atlas.Attributes.RemoveAttribute("mapLockedBy");
        var rotate = Activator.CreateInstance(Native("Network.MapRotationPacket"))!; Set(rotate, "Rotation", 1f);
        Call(handler, "OnMapRotated", player, rotate);
        check(atlas.Attributes.GetFloat("mapRotation") == 0, "Forged native rotation packets must not change atlas orientation.");
        var crowdedA = Sheet(0); var crowdedB = Sheet(144);
        ImmersiveMapCompatibility.Write(crowdedA, Markers(Enumerable.Range(0, 140).Select(i => ($"a{i}", 0, 0, new string('a', 120), false)).ToArray()));
        ImmersiveMapCompatibility.Write(crowdedB, Markers(Enumerable.Range(0, 140).Select(i => ($"b{i}", 144, 0, new string('b', 120), false)).ToArray()));
        Reject(() => GroundMapData.Sew([crowdedA, crowdedB]), "Oversized combined native marker payloads must reject sewing before consuming materials.");

        VerifyRendering(maps, [a, b], world, check);
        GroundMapSurfaceChecks.Run(a, check);
        VerifyBlock(world, a, b, check);
        VerifyAtlasPlacement(world, a, b, check);
    }

    private static void VerifyRendering(Assembly maps, List<ItemStack> sheets, IWorldAccessor resolve, Action<bool, string> check)
    {
        Type Native(string name) => maps.GetType("ImmersiveMaps." + name, true)!;
        object? Call(object target, string method, params object?[] args) => AccessTools.Method(target.GetType(), method).Invoke(target, args);
        void Set(object target, string name, object value) => AccessTools.Property(target.GetType(), name).SetValue(target, value);
        long now = 10000; int nextTexture = 100;
        var uploads = new List<MeshData>(); var blits = new List<LoadedTexture>();
        var render = Proxy.Make<IRenderAPI>((m, args) =>
        {
            if (m.Name == "UploadMesh") { uploads.Add((MeshData)args![0]!); return new UploadedMesh(); }
            if (m.Name.StartsWith("LoadOrUpdateTexture")) ((LoadedTexture)args![3]!).TextureId = ++nextTexture;
            if (m.Name == "CreateFrameBuffer") return new FrameBufferRef();
            if (m.Name == "RenderTextureIntoFrameBuffer") blits.Add((LoadedTexture)args![1]!);
            return null;
        });
        var holder = new EntityPlayer(); holder.Pos.SetPos(72, 100, 0);
        var activeSlot = new DummySlot();
        var inventory = Proxy.Make<IPlayerInventoryManager>((m, args) => m.Name switch
        { "get_ActiveHotbarSlot" => activeSlot, "get_ActiveHotbarSlotNumber" => 0, _ => null });
        var player = Proxy.Make<IClientPlayer>((m, args) => m.Name switch
        { "get_Entity" => holder, "get_InventoryManager" => inventory, _ => null });
        var world = Proxy.Make<IClientWorldAccessor>((m, args) => m.Name switch
        { "get_Side" => EnumAppSide.Client, "get_ElapsedMilliseconds" => now, "get_Player" => player, "PlayerByUid" => player, _ => m.Invoke(resolve, args) });
        holder.World = world;
        var placements = new List<GroundMapPlaceRequest>();
        var channel = Proxy.Make<IClientNetworkChannel>((m, args) =>
        { if (m.Name == "SendPacket" && args?[0] is GroundMapPlaceRequest packet) placements.Add(packet); return null; });
        var network = Proxy.Make<IClientNetworkAPI>((m, args) => m.Name == "GetChannel" ? channel : null);
        var events = Proxy.Make<IClientEventAPI>((m, args) => null);
        var gui = Proxy.Make<IGuiAPI>((m, args) => null);
        var logger = Proxy.Make<ILogger>((m, args) => null);
        var api = Proxy.Make<ICoreClientAPI>((m, args) => m.Name switch { "get_Network" => network, "get_Render" => render, "get_World" => world, "get_Event" => events, "get_Gui" => gui, "get_Logger" => logger, _ => null });
        using var client = new GroundAtlasClient(api);
        var native = Activator.CreateInstance(Native("Rendering.MapRenderSystem"))!;
        var template = Activator.CreateInstance(Native("Rendering.MapTemplate"))!;
        Set(template, "BackTexture", new LoadedTexture(api, 1, 64, 64)); Set(template, "BackMesh", new UploadedMesh());
        Set(template, "BorderTexture", new LoadedTexture(api, 2, 256, 256)); Set(template, "BlankTerrainTexture", new LoadedTexture(api, 3, 480, 480));
        var templates = (IDictionary)AccessTools.Field(native.GetType(), "_templates").GetValue(native)!;
        var keyType = templates.GetType().GetGenericArguments()[0];
        templates.Add(Activator.CreateInstance(keyType, GroundMapCompatibility.Property(sheets[0].Item, "MapBorderTexture"), GroundMapCompatibility.Property(sheets[0].Item, "MapBackTexture"), 5)!, template);
        using var collage = new MapCollage(api, native, sheets, true);
        var states = ((IDictionary)AccessTools.Field(native.GetType(), "_states").GetValue(native)!).Values.Cast<object>().ToList();
        for (int i = 0; i < states.Count; i++)
        {
            var state = states[i]; var shape = RecordedPaperMap.Describe(sheets[i])!;
            Call(state, "CreateResources", api, template, (System.Func<int, int[]>)(size => new int[size * size]), null);
            Call(state, "SetView", shape.CenterX, shape.CenterZ, shape.Rotation, now);
            Call(state, "OnTerrainPainted", 0, 0, now, true);
            AccessTools.Method(Native("Rendering.MapPainter"), "UpdateTerrainUvs").Invoke(null, [GroundMapCompatibility.Property(state, "TerrainMesh"), shape.CenterX, shape.CenterZ, shape.Rotation, 5]);
        }
        uploads.Clear();
        var viewport = new AtlasViewport(new(72, 0), 288, 0, 0);
        check(collage.GetModel(viewport) != null && uploads.Count == 7, "Real native render states must compose background, back, visible map layers, and one outer border.");
        check(uploads.All(mesh => mesh.Flags.Take(mesh.VerticesCount).All(f => (f & VertexFlags.WindModeBitsMask) == 0)),
            "Every held atlas layer must remain stationary: native wind on blank paper or frame can move it through terrain and hide the map.");
        var initialUploads = uploads.ToArray(); var stableModel = collage.GetModel(viewport); uploads.Clear();
        for (int frame = 0; frame < 12; frame++) { now += 300; check(ReferenceEquals(stableModel, collage.GetModel(viewport)), "A texture repaint must retain the live mesh until geometry actually changes."); }
        check(uploads.Count == 0, "Stationary held maps must not repeatedly dispose and reupload their visible geometry.");
        uploads.AddRange(initialUploads);
        double meshArea = 0;
        for (int sheet = 0; sheet < states.Count; sheet++)
        {
            var mesh = uploads[2 + sheet * 2]; var shape = collage.Layout.Shapes[sheet];
            var nativeUv = ((MeshData)GroundMapCompatibility.Property(states[sheet], "TerrainMesh")!).Uv;
            for (int i = 0; i < mesh.VerticesCount; i++)
            {
                double wx = mesh.xyz[i * 3] * viewport.Width / 2 + viewport.Center.X;
                double wz = mesh.xyz[i * 3 + 1] * viewport.Width / 2 + viewport.Center.Z;
                double u = (wx - shape.CenterX) / shape.MapWidth + .5, v = (wz - shape.CenterZ) / shape.MapWidth + .5;
                check(Math.Abs(mesh.Uv[i * 2] - (nativeUv[0] + (nativeUv[2] - nativeUv[0]) * u + (nativeUv[6] - nativeUv[0]) * v)) < .00001
                    && Math.Abs(mesh.Uv[i * 2 + 1] - (nativeUv[1] + (nativeUv[3] - nativeUv[1]) * u + (nativeUv[7] - nativeUv[1]) * v)) < .00001,
                    "Collage terrain must preserve native sampling and item model orientation.");
            }
            for (int i = 0; i < mesh.VerticesCount; i += 3)
                meshArea += MapPolygon.Area(Enumerable.Range(i, 3).Select(j => new MapPoint(mesh.xyz[j * 3], mesh.xyz[j * 3 + 1])).ToList());
        }
        check(Math.Abs(meshArea * viewport.Width * viewport.Width / 4 - 288 * 288) < .1, "Held atlas triangles must cover only one native-size window within the fixed union, without shrinking all sheets.");
        check(uploads.Skip(2).Take(4).All(m => m.xyz.Where((_, i) => i % 3 < 2).All(v => v >= -1.00001 && v <= 1.00001)),
            "Visible terrain and waypoints must stay within the paper boundary.");
        var clipped = viewport.Clip(collage.Layout);
        check(Math.Abs(clipped.SelectMany(p => p).Sum(MapPolygon.Area) - 288 * 288) < .01,
            "Viewport clipping must preserve the original map scale.");
        var edgeView = viewport with { Center = new(288, 0) };
        check(Math.Abs(edgeView.Clip(collage.Layout).SelectMany(p => p).Sum(MapPolygon.Area) - 144 * 288) < .01,
            "The paper beyond the stitched boundary must remain blank instead of revealing newly visited terrain.");
        check((viewport with { Center = new(10000, 0) }).Clip(collage.Layout).All(p => p.Count == 0)
            && (viewport with { Dimension = 1 }).Clip(collage.Layout).All(p => p.Count == 0),
            "Leaving the recorded region or dimension must not reveal map data.");
        var rotatedView = viewport with { Center = new(31.25, -18.5), Rotation = .7f };
        check(rotatedView.Project(rotatedView.Center) == new MapPoint(0, 0)
            && rotatedView.Clip(collage.Layout).SelectMany(p => p).SelectMany(p => p).Select(rotatedView.Project)
                .All(p => Math.Abs(p.X) <= 1.000001 && Math.Abs(p.Z) <= 1.000001),
            "Rotated windows must keep the holder centered and clip to the same paper rectangle.");
        now += 2100; collage.GetModel(viewport);
        check(states.All(s => (int)GroundMapCompatibility.Property(s, "RequestedX")! == (int)GroundMapCompatibility.Property(s, "ViewX")!
            && (bool)Call(s, "TerrainDue", now)!), "Automatic terrain refresh must keep all recorded centers fixed.");
        var placement = new GroundMapPlacement(); placement.SetReference(sheets[0]);
        collage.Surface = new(collage.Layout, placement, (x, z) => x < .625 ? 0 : null);
        now += 300; uploads.Clear(); collage.GetModel();
        check(uploads.Count == 10 && uploads[4].xyz.Where((_, i) => i % 3 == 1).Min() < -.05,
            "Native terrain textures must upload onto the draped mesh, including vertical paper.");
        check(uploads[4].xyz.Where((_, i) => i % 3 == 0).Max() < .632 && uploads[5].xyz.Where((_, i) => i % 3 == 0).Max() > .632,
            "Waypoint layers on a vertical fold must offset along its surface normal to avoid z-fighting.");
        check(uploads[8].Rgba.Take(uploads[8].VerticesCount * 4).Where((_, i) => i % 4 == 3).All(a => a == 30)
            && uploads[6].Rgba.Take(uploads[6].VerticesCount * 4).Where((_, i) => i % 4 == 3).All(a => a == 255),
            "Placed paper must render an opaque back and blend its subtle grain after the terrain layers.");
        var painter = Activator.CreateInstance(Native("Rendering.MapPainter"), api, null)!;
        Call(painter, "PaintWaypointLayer", states[0], template, 5, 0, 0, 0f, now);
        check(blits.Last().TextureId != 2 && blits.Last().Width == 256 && blits.Last().Height == 256, "Inner border replacement must use a fully transparent texture of the original size.");
        var normal = Activator.CreateInstance(Native("Rendering.MapRenderState"), sheets[0].Item)!;
        Call(normal, "CreateResources", api, template, (System.Func<int, int[]>)(size => new int[size * size]), null);
        Call(painter, "PaintWaypointLayer", normal, template, 5, 0, 0, 0f, now);
        check(blits.Last().TextureId == 2, "Normal held maps must retain the original paper border.");
        var atlas = GroundMapData.Sew(sheets);
        string fixedData = ImmersiveMapCompatibility.Fingerprint(atlas);
        check(client.GetAtlas(native, atlas, null) == null, "A newly held atlas must request native resources before drawing.");
        var nativeStates = (IDictionary)AccessTools.Field(native.GetType(), "_states").GetValue(native)!;
        var heldStates = nativeStates.Values.Cast<object>().Except(states).ToList();
        var markTexture = new LoadedTexture(api, 500, 72, 72);
        var marker = Activator.CreateInstance(Native("Rendering.PlayerMarker"), BindingFlags.Instance | BindingFlags.NonPublic, null,
            [markTexture, 12, 12, 30, 30], null)!;
        foreach (var state in heldStates)
        {
            Call(state, "CreateResources", api, template, (System.Func<int, int[]>)(size => new int[size * size]), marker);
            Call(state, "SetView", GroundMapCompatibility.Property(state, "RequestedX"), GroundMapCompatibility.Property(state, "RequestedZ"), 0f, now);
            Call(state, "OnTerrainPainted", 0, 0, now, true);
        }
        uploads.Clear();
        check(client.GetAtlas(native, atlas, null) != null && uploads.Count == 8,
            "The actual held atlas path must draw clipped sheets with exactly one player marker.");
        int cachedStates = nativeStates.Count;
        var heldModel = client.GetAtlas(native, atlas, null);
        check(ReferenceEquals(heldModel, client.GetAtlas(native, atlas.Clone(), null)) && nativeStates.Count == cachedStates,
            "Equivalent item stack clones must reuse the ready atlas and native textures instead of intermittently restarting blank.");
        MapPoint MarkerCenter(MeshData mesh) => new(
            mesh.xyz.Take(mesh.VerticesCount * 3).Where((_, i) => i % 3 == 0).Average(),
            mesh.xyz.Take(mesh.VerticesCount * 3).Where((_, i) => i % 3 == 1).Average());
        var markerMesh = uploads[^1];
        check(Math.Abs(MarkerCenter(markerMesh).X) < .0001 && Math.Abs(MarkerCenter(markerMesh).Z) < .0001,
            "The holder marker must occupy the paper center rather than follow individual sheet centers.");
        holder.Pos.X = 10000; uploads.Clear();
        check(client.GetAtlas(native, atlas, null) != null && uploads.Count == 7,
            "Moving outside the fixed atlas must keep filled terrain and immediately hide the out-of-view player marker.");
        holder.Pos.SetPos(270, 100, 100); uploads.Clear();
        check(client.GetAtlas(native, atlas, null) != null && uploads.Count == 8,
            "Moving along a stopped viewport must refresh the marker without waiting for the terrain mesh cache.");
        markerMesh = uploads[^1];
        double markerCanvasWidth = ((LoadedTexture)GroundMapCompatibility.Property(heldStates[0], "WaypointTexture")!).Width;
        check(Math.Abs(MarkerCenter(markerMesh).X - .875) < .0001
            && Math.Abs(MarkerCenter(markerMesh).Z - 100d / 144) < .0001,
            "The actual held rendering path must place its single marker at the holder's offset from the clamped map center.");
        void CheckVisibleMarker(MapPoint expected)
        {
            var mesh = uploads[^1];
            double half = 12 / markerCanvasWidth;
            check(Math.Abs(MarkerCenter(mesh).X - Math.Clamp(expected.X, -1 + half, 1 - half)) < .00001
                && Math.Abs(MarkerCenter(mesh).Z - Math.Clamp(expected.Z, -1 + half, 1 - half)) < .00001,
                "Only the icon's half-size margin may inset the player point at a paper edge or corner.");
            check(mesh.xyz.Take(mesh.VerticesCount * 3).Where((_, i) => i % 3 < 2).All(v => v >= -1 && v <= 1)
                && mesh.xyz.Take(mesh.VerticesCount * 3).Where((_, i) => i % 3 == 2).All(v => v > .006f),
                "The complete player icon must stay within the paper, above the outer frame.");
            check(mesh.Uv.Take(mesh.VerticesCount * 2).All(v => v >= 30f / 72 && v <= 42f / 72)
                && Math.Abs(mesh.xyz.Where((_, i) => i % 3 == 0).Max() - mesh.xyz.Where((_, i) => i % 3 == 0).Min() - 2 * half) < .00001,
                "Edge visibility must retain the native icon size and sample its entire sprite without padding or clipping.");
        }
        foreach (double x in new[] { -144d, 72, 288 })
            foreach (double z in new[] { -144d, 0, 144 })
            {
                holder.Pos.SetPos(x, 100, z); uploads.Clear();
                check(client.GetAtlas(native, atlas, null) is { } edgeModel && edgeModel.textureids[^1] == markTexture.TextureId && uploads.Count > 0,
                    "The actual held path must retain its player point at every recorded edge and corner.");
                CheckVisibleMarker(new(x == -144 ? -1 : x == 288 ? 1 : 0, z / 144));
            }
        foreach (var outside in new[] { new MapPoint(-144.01, 0), new(288.01, 0), new(72, -144.01), new(72, 144.01) })
        {
            holder.Pos.SetPos(outside.X, 100, outside.Z); uploads.Clear();
            check(client.GetAtlas(native, atlas, null) is { } outsideModel && !outsideModel.textureids.Contains(markTexture.TextureId),
                "Leaving any recorded edge must hide the marker instead of pinning an outside player to the paper.");
        }
        var previousStates = nativeStates.Values.Cast<object>().ToArray();
        using (var rotatedCollage = new MapCollage(api, native, sheets, true))
        {
            foreach (var state in nativeStates.Values.Cast<object>().Except(previousStates))
            {
                Call(state, "CreateResources", api, template, (System.Func<int, int[]>)(size => new int[size * size]), marker);
                Call(state, "SetView", 0, 0, 0f, now); Call(state, "OnTerrainPainted", 0, 0, now, true);
            }
            foreach (float angle in new[] { 0f, .7f, -1.2f, MathF.PI / 2 })
                foreach (var point in new[] { new MapPoint(1, 1), new(-1, -1), new(1 + 1e-10, 0), new(0, -1 - 1e-10) })
                {
                    var rotated = new AtlasViewport(new(72, 0), 100, angle, 0,
                        new(72 + 50 * (Math.Cos(angle) * point.X - Math.Sin(angle) * point.Z),
                            50 * (Math.Sin(angle) * point.X + Math.Cos(angle) * point.Z)));
                    uploads.Clear(); var model = rotatedCollage.GetModel(rotated)!;
                    check(model.textureids[^1] == markTexture.TextureId,
                        "Rotation roundoff at an exact boundary must not discard the player point.");
                    CheckVisibleMarker(point);
                }
        }
        check(ImmersiveMapCompatibility.Fingerprint(atlas) == fixedData,
            "Scrolling the held viewport must never rewrite the stored footprint or map data.");
        holder.Pos.SetPos(72, 100, 0); holder.Pos.Dimension = 1; uploads.Clear();
        check(client.GetAtlas(native, atlas, null) != null && uploads.Count == 3,
            "A holder in another dimension must see only paper and frame, with no terrain or player marker from the recorded dimension.");
        holder.Pos.Dimension = 0; holder.Pos.Yaw = .8f;
        var directionAtlas = atlas.Clone();
        directionAtlas.Attributes.GetTreeAttribute(GroundMapData.AtlasKey).GetItemstack("0").Attributes.SetBool("heading-test", true);
        activeSlot.Itemstack = directionAtlas;
        var beforeHeadingStates = nativeStates.Values.Cast<object>().ToArray();
        var forward = holder.Pos.GetViewVector();
        float heading = (float)Math.Atan2(forward.X, -forward.Z);
        string directionData = ImmersiveMapCompatibility.Fingerprint(directionAtlas);
        check(client.GetAtlas(native, directionAtlas, activeSlot) == null,
            "First viewing must initialize the heading independently of native texture readiness.");
        var headingStates = nativeStates.Values.Cast<object>().Except(beforeHeadingStates).ToList();
        foreach (var state in headingStates)
        {
            Call(state, "CreateResources", api, template, (System.Func<int, int[]>)(size => new int[size * size]), marker);
            Call(state, "SetView", 0, 0, 0f, now); Call(state, "OnTerrainPainted", 0, 0, now, true);
        }
        void CheckHeading(float expectedAngle)
        {
            uploads.Clear(); client.GetAtlas(native, directionAtlas, activeSlot);
            var mesh = uploads[2];
            var uv = ((MeshData)GroundMapCompatibility.Property(headingStates[0], "TerrainMesh")!).Uv;
            var shape = RecordedPaperMap.Describe(sheets[0])!;
            double halfWidth = Math.Floor(288 / (Math.Abs(Math.Cos(expectedAngle)) + Math.Abs(Math.Sin(expectedAngle)))) / 2;
            MapPoint WorldAt(int i) => new(
                ((mesh.Uv[2 * i] - uv[0]) / (uv[2] - uv[0]) - .5) * shape.MapWidth,
                ((mesh.Uv[2 * i + 1] - uv[1]) / (uv[7] - uv[1]) - .5) * shape.MapWidth);
            for (int i = 1; i < mesh.VerticesCount; i++)
            {
                var delta = WorldAt(i) - WorldAt(0);
                check(Math.Abs((Math.Cos(expectedAngle) * delta.X + Math.Sin(expectedAngle) * delta.Z) / halfWidth - (mesh.xyz[3 * i] - mesh.xyz[0])) < .0001
                    && Math.Abs((-Math.Sin(expectedAngle) * delta.X + Math.Cos(expectedAngle) * delta.Z) / halfWidth - (mesh.xyz[3 * i + 1] - mesh.xyz[1])) < .0001,
                    "The rendered terrain must match the initial forward heading plus manual rotation, independent of later turns.");
            }
        }
        holder.Pos.Yaw = -1.1f; CheckHeading(heading);
        var modSystem = Activator.CreateInstance(Native("ImmersiveMapsModSystem"))!;
        AccessTools.Field(modSystem.GetType(), "_capi").SetValue(modSystem, api);
        check(Call(modSystem, "RotateHeldMap", -MathF.PI / 18) is true,
            "The real native clockwise hotkey must rotate the atlas without sending a coverage-changing packet.");
        CheckHeading(heading - MathF.PI / 36);
        var wallSelection = new BlockSelection { Position = new(0, 99, 0), Face = BlockFacing.NORTH, HitPosition = new(.75, .75, 0) };
        var wallHandling = EnumHandHandling.NotHandled;
        directionAtlas.Item.OnHeldInteractStart(activeSlot, holder, wallSelection, null!, true, ref wallHandling);
        directionAtlas.Item.OnHeldInteractStart(activeSlot, holder, wallSelection, null!, false, ref wallHandling);
        check(wallHandling == EnumHandHandling.PreventDefault && placements.Count == 1
            && Math.Abs(placements[0].ViewRotation - (heading - MathF.PI / 36)) < 1e-6
            && placements[0].Fingerprint == directionData && placements[0].Face == wallSelection.Face.Index
            && placements[0].HitX == .75 && placements[0].HotbarSlot == 0,
            "The real client wall interaction must send exactly the manually rotated reading angle, independent of the player's later facing.");
        using (var packetBytes = new MemoryStream())
        {
            ProtoBuf.Serializer.Serialize(packetBytes, placements[0]); packetBytes.Position = 0;
            var copy = ProtoBuf.Serializer.Deserialize<GroundMapPlaceRequest>(packetBytes);
            check(copy.ViewRotation == placements[0].ViewRotation && copy.Fingerprint == directionData && copy.HitY == .75
                && copy.Y == 99 && copy.Dimension == 0 && !copy.DidOffset,
                "Wall placement must preserve the held angle, target and map identity over the wire.");
        }
        directionAtlas.Attributes.SetString("mapLockedBy", "owner");
        check(Call(modSystem, "RotateHeldMap", MathF.PI / 18) is true,
            "View-only rotation must work on a locked map without editing its saved data.");
        directionAtlas.Attributes.RemoveAttribute("mapLockedBy"); CheckHeading(heading);
        check(ImmersiveMapCompatibility.Fingerprint(directionAtlas) == directionData,
            "Initial heading and either rotation hotkey must preserve recorded footprints, markers and original rotations.");
        foreach (var corner in new[] { new MapPoint(-144, -144), new(288, 144), new(-144, 144), new(288, -144) })
        {
            holder.Pos.SetPos(corner.X, 100, corner.Z); uploads.Clear();
            check(client.GetAtlas(native, directionAtlas, activeSlot) is { } cornerModel && cornerModel.textureids[^1] == markTexture.TextureId,
                "An in-range player at a recorded corner must retain a visible edge point even outside the rotated crop.");
            var cornerView = new GroundMapLayout(sheets).ViewportFor(corner, 0, heading);
            CheckVisibleMarker(cornerView.Project(corner));
        }
        holder.Pos.SetPos(10000, 100, 10000); uploads.Clear();
        check(client.GetAtlas(native, directionAtlas, activeSlot) is { } outsideRotated
            && !outsideRotated.textureids.Contains(markTexture.TextureId),
            "The player point must remain hidden outside fixed coverage when the rotated view uses an inscribed crop.");
        check(!client.RotateHeld(directionAtlas, float.NaN) && !client.RotateHeld(sheets[0], .1f),
            "The atlas rotation integration must reject nonfinite angles and leave native small maps alone.");
        MapMarkerLayerChecks.Run(maps, api, native, sheets[0], template, check);
        Call(marker, "Dispose");
        foreach (var state in nativeStates.Values.Cast<object>()) Call(state, "Dispose");
        nativeStates.Clear(); now += 61000;
        check(collage.GetModel() == null && nativeStates.Count == sheets.Count,
            "Returning after native cache eviction must recreate render states and discard meshes using released textures.");
        var restored = nativeStates.Values.Cast<object>().ToList();
        check(restored.All(s => GroundMapCompatibility.BorderlessStates.TryGetValue(s, out _)),
            "Recreated ground map states must retain seamless internal borders.");
        foreach (var state in restored)
        {
            Call(state, "CreateResources", api, template, (System.Func<int, int[]>)(size => new int[size * size]), null);
            Call(state, "SetView", GroundMapCompatibility.Property(state, "RequestedX"), GroundMapCompatibility.Property(state, "RequestedZ"), 0f, now);
            Call(state, "OnTerrainPainted", 0, 0, now, true);
        }
        now += 300;
        check(collage.GetModel() != null, "A surface must render again after recreated native textures become available.");
        Call(normal, "Dispose"); foreach (var state in restored) Call(state, "Dispose"); Call(template, "Dispose");
    }

    private static void VerifyAtlasPlacement(IWorldAccessor resolve, ItemStack a, ItemStack b, Action<bool, string> check)
    {
        foreach (var face in BlockFacing.ALLFACES)
        {
            var supportPos = new BlockPos(0, 99, 0); var anchor = supportPos.AddCopy(face);
            var support = new Block { BlockId = 1, CollisionBoxes = [new Cuboidf(0, 0, 0, 1, 1, 1)] };
            var air = new Block { BlockId = 0, CollisionBoxes = [] };
            var block = new BlockGroundMap { BlockId = 900, Code = new("vsscsmod:groundmaps") };
            var entity = new HandPlayer { Alive = true }; var hand = new DummySlot();
            entity.Pos.SetPos(supportPos.ToVec3d().Add(.5, .5, .5).Add(face.Normald * 2)); entity.Controls.CtrlKey = false;
            var returned = new List<ItemStack>(); var dropped = new List<ItemStack>();
            bool allowed = true, removedSupport = false; BlockEntityGroundMap? placed = null; ICoreAPI? api = null;
            var manager = Proxy.Make<IPlayerInventoryManager>((m, args) =>
            {
                if (m.Name == "TryGiveItemstack") { returned.Add((ItemStack)args![0]!); return true; }
                return m.Name switch { "get_ActiveHotbarSlot" => hand, "get_OffhandHotbarSlot" => entity.Offhand, _ => null };
            });
            var player = Proxy.Make<IServerPlayer>((m, args) => m.Name switch
            { "get_Entity" => entity, "get_InventoryManager" => manager, "get_LanguageCode" => "en", _ => null });
            var claims = Proxy.Make<ILandClaimAPI>((m, args) => m.Name == "TryAccess" ? allowed : null);
            var accessor = Proxy.Make<IBlockAccessor>((m, args) =>
            {
                if (m.Name == "GetBlockEntity") return anchor.Equals(args![0]) ? placed : null;
                if (m.Name == "GetBlock")
                {
                    if (args!.Length > 1 && (int)args[1]! == BlockLayersAccess.Fluid) return air;
                    var pos = (BlockPos)args[0]!;
                    return !removedSupport && supportPos.Equals(pos) ? support : placed != null && anchor.Equals(pos) ? block : air;
                }
                if (m.Name == "SetBlock")
                {
                    if ((int)args![0]! == block.Id) { placed = new() { Pos = anchor.Copy(), Block = block }; placed.Initialize(api!); }
                    else { placed?.OnBlockRemoved(); placed = null; }
                }
                return null;
            });
            var world = Proxy.Make<IWorldAccessor>((m, args) =>
            {
                if (m.Name == "SpawnItemEntity") { dropped.Add((ItemStack)args![0]!); return null; }
                return m.Name switch
                {
                    "get_Side" => EnumAppSide.Server, "get_Claims" => claims, "get_BlockAccessor" => accessor,
                    "PlayerByUid" => player, "GetBlock" => block, _ => m.Invoke(resolve, args)
                };
            });
            api = Proxy.Make<ICoreAPI>((m, args) => m.Name switch { "get_Side" => EnumAppSide.Server, "get_World" => world, _ => null });
            entity.World = world;
            var hit = face.Index switch
            {
                0 => new Vec3d(.75, .75, 0), 1 => new Vec3d(1, .75, .75), 2 => new Vec3d(.25, .75, 1),
                3 => new Vec3d(0, .75, .25), 5 => new Vec3d(.25, 0, .75), _ => new Vec3d(.25, 1, .25)
            };
            var selection = new BlockSelection { Position = supportPos, Face = face, HitPosition = hit };
            var atlas = GroundMapData.Sew([a, b]); atlas.Attributes.SetString("mapLockedBy", "owner");
            string fingerprint = ImmersiveMapCompatibility.Fingerprint(atlas);
            hand.Itemstack = atlas.Clone(); hand.Itemstack.StackSize = 2;
            var placeRequest = new GroundMapPlaceRequest
            {
                X = supportPos.X, Y = supportPos.Y, Z = supportPos.Z, Face = face.Index,
                HitX = hit.X, HitY = hit.Y, HitZ = hit.Z, Fingerprint = fingerprint, ViewRotation = .19f
            };
            void PlaceWall() => BlockGroundMap.PlaceOnWall(world, player, placeRequest);
            var handling = EnumHandHandling.NotHandled;
            allowed = false; a.Item.OnHeldInteractStart(hand, entity, selection, null!, true, ref handling);
            if (face.IsHorizontal) PlaceWall();
            check(placed == null && hand.StackSize == 2, $"{face}: surface placement must check build permissions before consuming a locked atlas.");
            allowed = true; handling = EnumHandHandling.NotHandled;
            a.Item.OnHeldInteractStart(hand, entity, selection, null!, true, ref handling);
            if (face.IsHorizontal)
            {
                check(placed == null && hand.StackSize == 2,
                    $"{face}: the server's ordinary callback must wait for the client's reading-angle placement request.");
                placeRequest.ViewRotation = float.NaN; PlaceWall(); placeRequest.ViewRotation = .19f;
                placeRequest.HitX = double.NaN; PlaceWall(); placeRequest.HitX = hit.X;
                placeRequest.HotbarSlot = 1; PlaceWall(); placeRequest.HotbarSlot = 0;
                placeRequest.Dimension = 1; PlaceWall(); placeRequest.Dimension = 0;
                placeRequest.Fingerprint = "stale-map"; PlaceWall(); placeRequest.Fingerprint = fingerprint;
                var originalPos = entity.Pos.XYZ;
                entity.Pos.SetPos(originalPos.AddCopy(face.Normald * 20)); PlaceWall(); entity.Pos.SetPos(originalPos);
                entity.Pos.SetPos(supportPos.ToVec3d().Add(.5, .5, .5).Sub(face.Normald * 2));
                PlaceWall(); entity.Pos.SetPos(originalPos);
                check(placed == null && hand.StackSize == 2,
                    $"{face}: invalid angle, hit, active slot, dimension, stale map, reach and through-wall requests must consume nothing.");
                PlaceWall(); PlaceWall();
                check(placed != null && placed.Placement.Rotation == .19f && placed.Placement.PaperAngle == 0,
                    $"{face}: wall placement must preserve the held reading angle, with the paper frame upright and no duplicate consumption.");
            }
            check(handling == EnumHandHandling.PreventDefault && placed?.IsAtlas == true && hand.StackSize == 1,
                $"{face}: the real map right-click entry must place exactly one whole atlas without requiring Ctrl.");
            if (placed == null) continue;
            check(placed.Placement.Face == face.Index && placed.DisplaySheets.Count == 2 && placed.Surface().Supported,
                $"{face}: placed atlas must use all fixed sheet footprints on the selected supporting face.");
            var vertices = placed.Surface().Patches.SelectMany(p => p).SelectMany(p => p).ToArray();
            var point = hit.AddCopy(supportPos.X - anchor.X, supportPos.Y - anchor.Y, supportPos.Z - anchor.Z);
            check(vertices.All(v => Math.Abs((v.Position - point).Dot(face.Normald) - .006) < .00001),
                $"{face}: decoration must sit just outside the actual face, without replacing the support or rendering inside it.");
            check(vertices.All(p => (placed.Placement.FromWorld(p.Position).Y - placed.Placement.Height) > 0),
                $"{face}: face orientation must place every atlas triangle on the outward side.");
            var paperMeshes = GroundMapPaper.Meshes(placed.Surface(), new(placed.DisplaySheets));
            foreach (var (mesh, depth) in new[] { (paperMeshes[0], -GroundMapPaper.Thickness), (paperMeshes[2], GroundMapPaper.GrainDepth) })
                check(Enumerable.Range(0, mesh.VerticesCount).All(i => Math.Abs(
                    (new Vec3d(mesh.xyz[i * 3], mesh.xyz[i * 3 + 1], mesh.xyz[i * 3 + 2]) - point).Dot(face.Normald) - .006 - depth) < 1e-6),
                    $"{face}: paper thickness and fibres must follow the selected face normal and stay outside the supporting block.");
            hand.Itemstack = a.Clone(); placed.Add(player, hand);
            check(!hand.Empty && placed.Sheets.Count == 1, $"{face}: fixed stitched coverage must not accept extra small maps.");
            var tree = new TreeAttribute(); placed.ToTreeAttributes(tree);
            var loaded = new BlockEntityGroundMap { Pos = anchor.Copy(), Block = block }; loaded.FromTreeAttributes(tree, world); loaded.Initialize(api);
            check(loaded.IsAtlas && loaded.DisplaySheets.Count == 2 && loaded.Placement.Face == face.Index && loaded.Surface().Supported
                && loaded.Placement.PaperAngle == placed.Placement.PaperAngle
                && loaded.Placement.Rotation == placed.Placement.Rotation
                && ImmersiveMapCompatibility.Fingerprint(loaded.Sheets[0]) == fingerprint,
                $"{face}: saved atlas decoration must reload with its exact markers, locks, coverage and face.");
            entity.Controls.CtrlKey = true;
            var surface = placed.Surface(); var second = surface.Patches[1][0];
            var target = new Vec3d(second.Average(v => v.X), second.Average(v => v.Y), second.Average(v => v.Z));
            entity.Pos.SetPos(target.AddCopy(anchor.X, anchor.Y, anchor.Z).Add(face.Normald * 2));
            var request = new GroundMapTakeRequest { Revision = placed.Revision, Action = "take", Index = 1,
                HitX = target.X, HitY = target.Y, HitZ = target.Z };
            var owner = placed; owner.Interact(player, request); owner.Interact(player, request);
            check(returned.Count == 1 && GroundMapData.IsAtlas(returned[0]) && dropped.Count == 0 && owner.Sheets.Count == 0
                && ImmersiveMapCompatibility.Fingerprint(returned[0]) == fingerprint,
                $"{face}: Ctrl-taking any part must return the intact atlas once, including all locked state and current markers.");
            selection.DidOffset = true; selection.Position = anchor.Copy();
            entity.Pos.Yaw = .37f; entity.Pos.Pitch = MathF.PI / 2;
            hand.Itemstack = atlas.Clone();
            if (face.IsHorizontal)
            {
                placeRequest.X = anchor.X; placeRequest.Y = anchor.Y; placeRequest.Z = anchor.Z; placeRequest.DidOffset = true;
                placeRequest.ViewRotation = -.31f; PlaceWall();
                check(placed?.Placement.Rotation == -.31f, $"{face}: a new held reading angle must override both the old angle and placement facing.");
            }
            else BlockGroundMap.Place(world, player, selection, hand);
            check(placed?.IsAtlas == true && hand.Empty, $"{face}: engine-offset selections must still attach to the original support face.");
            var horizontal = new EntityPos { Yaw = entity.Pos.Yaw }.GetViewVector();
            var expectedAngle = new GroundMapPlacement { Face = face.Index }; expectedAngle.FacePlayer(new(horizontal.X, 0, horizontal.Z));
            check(placed != null && Math.Abs(placed.Placement.PaperAngle - expectedAngle.PaperAngle) < 1e-7,
                $"{face}: the real placement handler must use the player's continuous horizontal heading even when looking straight up/down.");
            var falling = placed; removedSupport = true;
            block.OnNeighbourBlockChange(world, anchor, supportPos); falling?.OnBlockRemoved();
            check(placed == null && dropped.Count == 1 && ImmersiveMapCompatibility.Fingerprint(dropped[0]) == fingerprint,
                $"{face}: losing all supporting faces must drop the whole atlas exactly once.");
        }
    }

    private static void VerifyBlock(IWorldAccessor resolve, ItemStack a, ItemStack b, Action<bool, string> check)
    {
        var returned = new List<ItemStack>(); int drops = 0; bool access = true, obstruction = false;
        var pos = new BlockPos(0, 100, 0);
        var entity = new HandPlayer { Alive = true };
        entity.Pos.SetPos(.5, 100, .5); entity.Controls.CtrlKey = true;
        var hand = new DummySlot(); var offhand = entity.Offhand;
        InventoryGeneric? reserves = null;
        var manager = Proxy.Make<IPlayerInventoryManager>((m, args) =>
        {
            if (m.Name == "TryGiveItemstack") { returned.Add((ItemStack)args![0]!); return true; }
            return m.Name switch { "get_ActiveHotbarSlot" => hand, "get_OffhandHotbarSlot" => offhand,
                "GetOwnInventory" => args![0] as string == "backpack" ? reserves : null, _ => null };
        });
        var player = Proxy.Make<IServerPlayer>((m, args) => m.Name switch { "get_Entity" => entity, "get_InventoryManager" => manager, "get_LanguageCode" => "en", _ => null });
        var claims = Proxy.Make<ILandClaimAPI>((m, args) => m.Name == "TryAccess" ? access : null);
        BlockEntityGroundMap? selected = null;
        var support = new Block { CollisionBoxes = [new Cuboidf(0, 0, 0, 1, 1, 1)] };
        var air = new Block { CollisionBoxes = [], BlockId = 0 };
        var accessor = Proxy.Make<IBlockAccessor>((m, args) => m.Name switch
        {
            "GetBlockEntity" => selected,
            "GetBlock" => args!.Length > 1 && (int)args[1]! == BlockLayersAccess.Fluid || (((BlockPos)args![0]!).Y >= 100 && !obstruction) ? air : support,
            _ => null
        });
        var world = Proxy.Make<IWorldAccessor>((m, args) =>
        {
            if (m.Name == "SpawnItemEntity") { drops++; return null; }
            return m.Name switch { "get_Side" => EnumAppSide.Server, "get_Claims" => claims, "get_BlockAccessor" => accessor, "PlayerByUid" => player, _ => m.Invoke(resolve, args) };
        });
        entity.World = world;
        var registry = Proxy.Make<IClassRegistryAPI>((m, args) => m.Name == "CreateInvNetworkUtil" ? Proxy.Make<IInventoryNetworkUtil>((m, args) => null) : null);
        var system = new VscnCivilizationModSystem();
        var loader = Proxy.Make<IModLoader>((m, args) => m.Name == "GetModSystem" ? system : null);
        var api = Proxy.Make<ICoreAPI>((m, args) => m.Name switch { "get_Side" => EnumAppSide.Server, "get_World" => world, "get_ClassRegistry" => registry, "get_ModLoader" => loader, _ => null });
        var block = new BlockGroundMap { Code = new AssetLocation("vsscsmod:groundmaps") };
        VerifyGroupLimits();
        var maps = new BlockEntityGroundMap { Pos = pos, Block = block }; maps.Initialize(api);
        var slot = new DummySlot(a.Clone()); maps.Add(player, slot);
        check(maps.Sheets.Count == 1 && slot.Empty, "Server placement must transfer exactly one actual map into block storage.");
        slot.Itemstack = b.Clone(); maps.Add(player, slot);
        var tree = new TreeAttribute(); maps.ToTreeAttributes(tree);
        var loaded = new BlockEntityGroundMap { Block = block }; loaded.FromTreeAttributes(tree, world); loaded.Initialize(api);
        check(loaded.Sheets.Count == 2 && loaded.Revision == maps.Revision, "Block reload must retain original maps and request revision.");
        check(loaded.Placement.Ground(new(288, 0)) == maps.Placement.Ground(new(288, 0)), "Block reload must retain physical map scale and placement.");
        string revision = maps.Revision;
        entity.Controls.CtrlKey = false; maps.Take(player, 0, revision);
        check(returned.Count == 0, "Taking without Ctrl must fail on the server.");
        entity.Controls.CtrlKey = true; access = false; maps.Take(player, 0, revision);
        check(returned.Count == 0, "Claim permission must be checked before taking maps.");
        access = true; maps.Take(player, 0, revision); maps.Take(player, 0, revision);
        check(returned.Count == 1 && maps.Sheets.Count == 1, "Repeated or simultaneous stale take requests must not duplicate maps.");
        maps.OnBlockRemoved(); maps.OnBlockRemoved();
        check(drops == 1 && maps.Sheets.Count == 0, "Removing support must drop every remaining original exactly once.");
        selected = new BlockEntityGroundMap { Pos = pos, Block = block }; selected.Initialize(api);
        selected.Sheets.Add(a.Clone()); selected.Sheets.Add(b.Clone()); returned.Clear();
        var needle = new Item { Code = new(CivilizationMapInventory.NeedleCode), ItemId = 106 };
        hand.Itemstack = new ItemStack(needle, 2);
        selected.Sew(player);
        check(selected.Sheets.Count == 2 && returned.Count == 0 && hand.StackSize == 2, "Sewing without offhand flax must not consume maps or needles.");
        offhand.Itemstack = new ItemStack(new Item { Code = new(CivilizationMapInventory.ThreadCode), ItemId = 107 }, 3);
        selected.Sheets[1].Attributes.SetString("mapLockedBy", "owner"); selected.Sew(player);
        check(selected.Sheets.Count == 2 && offhand.StackSize == 3 && returned.Count == 0, "Locked originals must prevent sewing without any material consumption.");
        selected.Sheets[1].Attributes.RemoveAttribute("mapLockedBy");
        var handling = EnumHandHandling.NotHandled;
        needle.OnHeldInteractStart(hand, entity, new BlockSelection { Position = pos, Face = BlockFacing.UP }, null!, true, ref handling);
        check(handling == EnumHandHandling.PreventDefault && selected.Sheets.Count == 0 && returned.Count == 1
            && GroundMapData.IsAtlas(returned[0]) && offhand.StackSize == 2 && hand.StackSize == 2,
            "The real base-item needle interaction must sew once, consume one flax, and preserve needles without native durability.");
        var result = returned[0]; returned.Clear();
        offhand.Itemstack = result;
        var knife = new Vintagestory.GameContent.ItemKnife { Code = new("game:knife-flint"), Tool = EnumTool.Knife, Durability = 100, ItemId = 108 };
        hand.Itemstack = new ItemStack(knife);
        result.Attributes.SetString("mapLockedBy", "owner"); handling = EnumHandHandling.NotHandled;
        knife.OnHeldInteractStart(hand, entity, null!, null!, true, ref handling);
        check(returned.Count == 0 && GroundMapData.IsAtlas(offhand.Itemstack), "The real native knife must refuse locked atlas disassembly.");
        result.Attributes.RemoveAttribute("mapLockedBy"); handling = EnumHandHandling.NotHandled;
        knife.OnHeldInteractStart(hand, entity, null!, null!, true, ref handling);
        check(handling == EnumHandHandling.PreventDefault && offhand.Empty && returned.Count == 2 && returned.All(s => RecordedPaperMap.IsPaperMap(s) && !GroundMapData.IsAtlas(s)),
            "The real native knife entry must return originals without flax and consume the offhand atlas once.");
        returned.Clear(); entity.Pos.SetPos(1.25, 101.5, .25); entity.Controls.CtrlKey = true;
        selected = new BlockEntityGroundMap { Pos = pos, Block = block }; selected.Initialize(api);
        selected.Sheets.Add(a.Clone()); var distant = a.Clone(); distant.Attributes.SetInt("mapPositionX", 576); selected.Sheets.Add(distant);
        selected.Placement.SetReference(a);
        var request = new GroundMapTakeRequest { Revision = selected.Revision, Index = 1, HitX = 1.25, HitY = .006, HitZ = .25 };
        using var binary = new MemoryStream(); ProtoBuf.Serializer.Serialize(binary, request); binary.Position = 0;
        var restored = ProtoBuf.Serializer.Deserialize<GroundMapTakeRequest>(binary);
        check(restored.HitX == 1.25 && restored.Action == "take" && restored.Revision == request.Revision, "Extended surface requests must preserve target coordinates and revision on the wire.");
        request.HitX = double.NaN; selected.Interact(player, request);
        check(returned.Count == 0, "Nonfinite surface requests must be rejected before ray tests.");
        request.HitX = 4; selected.Interact(player, request);
        check(returned.Count == 0, "A forged point outside the draped surface must not retrieve a map.");
        request.HitX = 1.25; entity.Controls.CtrlKey = false; selected.Interact(player, request);
        check(returned.Count == 0, "Network retrieval must still require Ctrl on an extended map.");
        entity.Controls.CtrlKey = true; access = false; selected.Interact(player, request);
        check(returned.Count == 0, "Network retrieval must validate claims at the extended hit point.");
        access = true; obstruction = true; selected.Interact(player, request);
        check(returned.Count == 0, "A solid obstruction must prevent extended map retrieval through walls.");
        obstruction = false; entity.Pos.SetPos(1.25, 110, .25); selected.Interact(player, request);
        check(returned.Count == 0, "Network surface requests beyond reach must not retrieve maps.");
        entity.Pos.SetPos(1.25, 101.5, .25); selected.Interact(player, request); selected.Interact(player, request);
        check(returned.Count == 1 && selected.Sheets.Count == 1, "An extended map must be retrievable outside the anchor block, exactly once per revision.");
        returned.Clear(); hand.Itemstack = a.Clone(); hand.Itemstack.Attributes.SetInt("mapPositionX", 288);
        request = new() { Action = "add", Revision = selected.Revision, HitX = .25, HitY = .006, HitZ = .25 };
        selected.Interact(player, request); selected.Interact(player, request);
        check(hand.Empty && selected.Sheets.Count == 2, "A surface add request must consume exactly one map and invalidate repeated revisions.");
        hand.Itemstack = a.Clone(); hand.Itemstack.Attributes.SetInt("mapPositionX", 576);
        request = new() { Action = "add", Revision = selected.Revision, HitX = .75, HitY = .006, HitZ = .25 };
        selected.Interact(player, request);
        check(hand.Empty && selected.Sheets.Count == 3 && selected.Surface().Patches[2].Count > 0, "Adding through the surface must extend a connected map onto the next block.");
        hand.Itemstack = new ItemStack(needle, 2); offhand.Itemstack = new ItemStack(new Item { Code = new(CivilizationMapInventory.ThreadCode), ItemId = 107 }, 3);
        request = new() { Action = "sew", Revision = selected.Revision, HitX = 1.25, HitY = .006, HitZ = .25 };
        selected.Sheets[1].Attributes.SetString("mapLockedBy", "owner"); selected.Interact(player, request);
        check(selected.Sheets.Count == 3 && offhand.StackSize == 3, "Sewing through an extended part must preserve the lock check and thread on failure.");
        selected.Sheets[1].Attributes.RemoveAttribute("mapLockedBy"); offhand.Itemstack.StackSize = 1;
        string before = selected.Revision; selected.Interact(player, request);
        check(selected.Sheets.Count == 3 && selected.Revision == before && offhand.StackSize == 1 && returned.Count == 0,
            "Insufficient thread for every seam must preserve all original maps, materials and revision.");
        offhand.Itemstack.StackSize = 3; selected.Interact(player, request); selected.Interact(player, request);
        check(selected.Sheets.Count == 0 && returned.Count == 1 && GroundMapData.IsAtlas(returned[0]) && offhand.StackSize == 1 && hand.StackSize == 2,
            "An extended surface sew request must return one atlas and consume exactly two seam threads once.");
        returned.Clear(); selected.Sheets.AddRange(Enumerable.Range(0, 16).Select(_ => a.Clone()));
        reserves = new InventoryGeneric(3, "backpack-sewing", api);
        var twine = new Item { Code = new(CivilizationMapInventory.ThreadCode), ItemId = 107, MaxStackSize = 32 };
        offhand.Itemstack = new ItemStack(twine, 32);
        reserves[0].Itemstack = new ItemStack(twine, 32); reserves[1].Itemstack = new ItemStack(twine, 32); reserves[2].Itemstack = new ItemStack(twine, 23);
        selected.Sew(player);
        check(selected.Sheets.Count == 16 && offhand.StackSize == 32 && reserves[2].StackSize == 23 && returned.Count == 0,
            "An assembly costing more than one stack must still reject insufficient total carried twine atomically.");
        reserves[2].Itemstack!.StackSize++; selected.Sew(player);
        check(selected.Sheets.Count == 0 && returned.Count == 1 && GroundMapData.Sheets(returned[0], world).Count == 16
            && offhand.Empty && reserves.All(slot => slot.Empty) && hand.StackSize == 2,
            "All 120 seams of 16 fully overlapping maps must be payable from legal twine stacks, without losing needles or maps.");

        void VerifyGroupLimits()
        {
            system.Config.PaperMaps.MaxSheets = 3;
            var group = new BlockEntityGroundMap { Pos = pos, Block = block }; group.Initialize(api);
            var input = new DummySlot(a.Clone()); group.Add(player, input);
            check(input.Empty && group.MaxSheets == 3, "First placement must capture the server's configured sheet limit.");
            system.Config.PaperMaps.MaxSheets = 2;
            input.Itemstack = a.Clone(); group.Add(player, input);
            var stored = new TreeAttribute(); group.ToTreeAttributes(stored);
            var binary = new TreeAttribute(); binary.FromBytes(stored.ToBytes());
            var restored = new BlockEntityGroundMap { Pos = pos, Block = block }; restored.FromTreeAttributes(binary, world); restored.Initialize(api);
            input.Itemstack = a.Clone(); restored.Add(player, input);
            check(restored.MaxSheets == 3 && restored.Sheets.Count == 3 && input.Empty,
                "Lowering the server limit and reloading must preserve an existing group's original capacity.");
            input.Itemstack = a.Clone(); restored.Add(player, input);
            check(restored.Sheets.Count == 3 && !input.Empty, "An old group must still enforce its own saved capacity.");
            group = new BlockEntityGroundMap { Pos = pos, Block = block }; group.Initialize(api);
            input.Itemstack = a.Clone(); group.Add(player, input);
            input.Itemstack = a.Clone(); group.Add(player, input);
            system.Config.PaperMaps.MaxSheets = 7;
            input.Itemstack = a.Clone(); group.Add(player, input);
            check(group.MaxSheets == 2 && group.Sheets.Count == 2 && !input.Empty,
                "New groups must use the changed limit and later increases must leave existing groups unchanged.");
            var legacy = new TreeAttribute(); legacy["sheets"] = GroundMapData.Store([a, b]);
            var old = new BlockEntityGroundMap { Block = block }; old.FromTreeAttributes(legacy, world);
            check(old.Sheets.Count == 2 && old.MaxSheets == 16, "Pre-configuration ground groups must keep their original 16-sheet capacity.");
            system.Config.PaperMaps.MaxSheets = 2;
            var needle = new Item { Code = new(CivilizationMapInventory.NeedleCode), ItemId = 106 };
            hand.Itemstack = new ItemStack(needle);
            offhand.Itemstack = new ItemStack(new Item { Code = new(CivilizationMapInventory.ThreadCode), ItemId = 107 }, 3);
            restored.Sew(player);
            check(returned.Count == 1 && GroundMapData.Sheets(returned[0], world).Count == 3 && offhand.Empty,
                "An existing group must remain sewable using its saved capacity after configuration changes.");
            system.Config.PaperMaps.MaxSheets = PaperMapsConfig.DefaultMaxSheets;
            returned.Clear();
        }
    }
}
