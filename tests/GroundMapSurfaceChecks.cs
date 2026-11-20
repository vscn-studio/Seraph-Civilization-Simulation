using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using SeraphCivilizationSimulation;

internal static class GroundMapSurfaceChecks
{
    internal static void Run(ItemStack first, Action<bool, string> check)
    {
        ItemStack At(int x, int z = 0) { var map = first.Clone(); map.Attributes.SetInt("mapPositionX", x); map.Attributes.SetInt("mapPositionZ", z); return map; }
        var placement = new GroundMapPlacement(); placement.SetReference(first);
        var layout = new GroundMapLayout([first]);
        var corners = MapPolygon.Corners(layout.Shapes[0]).Select(placement.Ground).ToList();
        check(Math.Abs(corners.Max(p => p.X) - corners.Min(p => p.X) - .5) < 1e-8
            && Math.Abs(corners.Max(p => p.Z) - corners.Min(p => p.Z) - .5) < 1e-8, "Every map must occupy half a block in both directions (2 by 2 per block).");
        foreach (var p in corners) { var world = placement.Map(p); var back = placement.Ground(world); check(Math.Abs(back.X - p.X) + Math.Abs(back.Z - p.Z) < 1e-9, "Surface coordinates must round trip without changing map sampling."); }
        var before = placement.Ground(new(288, 0));
        placement.SetReference(At(288));
        check(placement.Ground(new(288, 0)) == before, "Adding or removing sheets must retain the permanent reference frame.");
        var restored = GroundMapPlacement.Read(placement.Store());
        check(restored.Ground(new(288, 0)) == before, "Surface anchor, scale and rotation must survive saving.");
        var flat = new GroundMapSurface(layout, placement, (x, z) => 0);
        double Area(GroundMapSurface surface) => surface.Patches.SelectMany(p => p).Sum(p => MapPolygon.Area(p.Select(v => new MapPoint(v.X, v.Z)).ToList()));
        check(Math.Abs(Area(flat) - .25) < 1e-8, "A single flat map must cover exactly one quarter of a block.");
        var fourLayout = new GroundMapLayout([first, At(288), At(0, 288), At(288, 288)]);
        var four = new GroundMapSurface(fourLayout, placement, (x, z) => 0);
        check(Math.Abs(Area(four) - 1) < 1e-8, "Four adjacent maps must cover one block without shrinking or seams.");
        var extended = new GroundMapSurface(new([first, At(288), At(576)]), placement, (x, z) => 0);
        check(extended.Patches[2].SelectMany(p => p).Max(v => v.X) > 1 && Math.Abs(Area(extended) - .75) < 1e-8,
            "Maps beyond the first block must extend over adjacent support rather than shrink.");
        var hit = extended.Hit(new Vec3d(1.25, 2, .25), new Vec3d(0, -1, 0));
        check(hit?.Index == 2 && Math.Abs(hit.Value.Point.Y - .006) < 1e-8, "An extended part must be selectable outside the anchor block.");
        check(extended.Hit(new Vec3d(1.25, 2, 1), new Vec3d(0, -1, 0)) == null, "Blank space must not intercept map clicks.");
        check(extended.Hit(new Vec3d(1.25, 8, .25), new Vec3d(0, -1, 0)) == null, "Out-of-reach surface picks must fail.");
        var step = new GroundMapSurface(layout, placement, (x, z) => x < .25 ? -.5 : -.75);
        check(step.Patches[0].SelectMany(p => p).Any(v => Math.Abs(v.Y + .494) < 1e-8)
            && step.Patches[0].SelectMany(p => p).Any(v => Math.Abs(v.Y + .744) < 1e-8), "Maps must follow slab and stepped surface heights.");
        check(step.Patches[0].Any(p => p.Max(v => v.Y) - p.Min(v => v.Y) > .1), "A step must include a vertical fold instead of a floating diagonal bridge.");
        var edge = new GroundMapSurface(layout, placement, (x, z) => x < .25 ? 0 : null);
        check(Math.Abs(Area(edge) - .125) < 1e-8, "Unsupported portions must not float horizontally past a table edge.");
        check(edge.Patches[0].Any(p => Math.Abs(p.Max(v => v.X) - p.Min(v => v.X)) < 1e-8 && p.Min(v => v.Y) < -.2), "Available paper must hang down at an unsupported edge.");
        check(edge.Hit(new Vec3d(.7, -.1, .25), new Vec3d(-1, 0, 0))?.Index == 0, "Hanging paper must support interaction from the side.");
        check(edge.Patches[0].SelectMany(p => p).All(v => v.X <= .256001 && v.Y >= -.5), "A fold must remain against the edge with a small render offset and cannot exceed available paper length.");
        var none = new GroundMapSurface(layout, placement, (x, z) => null);
        check(!none.Supported && none.Hit(new Vec3d(.25, 1, .25), new Vec3d(0, -1, 0)) == null, "A map with no support must have no floating rendering or selection.");
        var overlapLayout = new GroundMapLayout([first, At(144)]);
        var overlap = new GroundMapSurface(overlapLayout, placement, (x, z) => 0);
        check(Math.Abs(Area(overlap) - .375) < 1e-8, "Draped overlapping maps must retain exactly the union surface area.");
        check(flat.Outline(0).All(e => (Math.Abs(e.A.X) < 1e-8 && Math.Abs(e.B.X) < 1e-8)
            || (Math.Abs(e.A.X - .5) < 1e-8 && Math.Abs(e.B.X - .5) < 1e-8)
            || (Math.Abs(e.A.Z) < 1e-8 && Math.Abs(e.B.Z) < 1e-8)
            || (Math.Abs(e.A.Z - .5) < 1e-8 && Math.Abs(e.B.Z - .5) < 1e-8)), "Ctrl highlighting must omit internal subdivision grid lines.");
        first = first.Clone(); first.Attributes.SetFloat("mapRotation", .71f);
        var rotatedPlacement = new GroundMapPlacement(); rotatedPlacement.SetReference(first);
        var rotated = new GroundMapSurface(new([first]), rotatedPlacement, (x, z) => 0);
        check(Math.Abs(Area(rotated) - .25) < 1e-8, "Rotated map coordinates must still produce a half-block-wide first sheet.");
        foreach (var face in BlockFacing.ALLFACES)
        {
            rotatedPlacement.Face = face.Index;
            var forward = new Vec3d(Math.Sin(.37), 0, -Math.Cos(.37));
            rotatedPlacement.FacePlayer(forward);
            var center = rotatedPlacement.Ground(new(rotatedPlacement.MapX, rotatedPlacement.MapZ));
            var top = rotatedPlacement.Ground(new(rotatedPlacement.MapX + Math.Sin(rotatedPlacement.Rotation) * 100,
                rotatedPlacement.MapZ - Math.Cos(rotatedPlacement.Rotation) * 100));
            var printedTop = rotatedPlacement.ToWorld(new(top.X - center.X, 0, top.Z - center.Z)).Normalize();
            check(face.IsHorizontal ? printedTop.DistanceTo(new Vec3d(0, 1, 0)) < 1e-6 : printedTop.DistanceTo(forward) < 1e-6,
                $"{face}: the printed top must face the placer on horizontal surfaces and stay upright on walls, without compass alignment.");
            var restoredAngle = GroundMapPlacement.Read(rotatedPlacement.Store());
            check(restoredAngle.Ground(new(77, -53)) == rotatedPlacement.Ground(new(77, -53)),
                $"{face}: saving must preserve the exact paper angle independently of the original map rotation.");
            var sample = new MapPoint(71.25, -59.3);
            var roundTrip = rotatedPlacement.Map(rotatedPlacement.Ground(sample));
            check(Math.Abs(roundTrip.X - sample.X) + Math.Abs(roundTrip.Z - sample.Z) < 1e-8,
                $"{face}: arbitrary paper orientation must keep map sampling invertible.");
        }
        rotatedPlacement.Face = BlockFacing.UP.Index;
        foreach (double angle in new[] { .13, .37, .78, 1.11, 2.93 })
        {
            rotatedPlacement.FacePlayer(new(Math.Sin(angle), 0, -Math.Cos(angle)));
            var surface = new GroundMapSurface(new([first]), rotatedPlacement, (x, z) => 0);
            check(Math.Abs(rotatedPlacement.PaperAngle - angle) < 1e-6 && Math.Abs(Area(surface) - .25) < 1e-8,
                "Player heading must remain continuous, without cardinal snapping or a change in paper area.");
            check(Math.Abs(surface.MarkerFrame.Rotation - rotatedPlacement.Rotation) < 1e-8,
                "Marker labels must share the original paper frame so their text cannot act as a compass reference.");
            var center = rotatedPlacement.Ground(new(rotatedPlacement.MapX, rotatedPlacement.MapZ));
            check(surface.Hit(new(center.X, 2, center.Z), new(0, -1, 0))?.Index == 0,
                "Rotating the paper must retain interaction at its printed center.");
            VerifyPaper(surface, new([first]), 2);
        }
        var legacy = rotatedPlacement.Store(); legacy.RemoveAttribute("paperangle");
        check(GroundMapPlacement.Read(legacy).PaperAngle == 0, "Existing decorations without an angle must retain their saved alignment.");
        VerifyPaper(flat, layout, 2);
        VerifyPaper(four, fourLayout, 4);
        VerifyPaper(overlap, overlapLayout, 2.5);
        var foldedPaper = GroundMapPaper.Meshes(edge, layout);
        check(foldedPaper[0].xyz.Take(foldedPaper[0].VerticesCount * 3).Where((_, i) => i % 3 == 1).Min() < -.2,
            "The textured paper back must follow hanging edges instead of covering them with a flat plane.");

        void VerifyPaper(GroundMapSurface surface, GroundMapLayout paperLayout, double perimeter)
        {
            var meshes = GroundMapPaper.Meshes(surface, paperLayout);
            var back = meshes[0]; var rim = meshes[1]; var grain = meshes[2];
            check(back.VerticesCount == grain.VerticesCount && back.VerticesCount > 0
                && back.xyz.Take(back.VerticesCount * 3).Where((_, i) => i % 3 == 1).All(y => Math.Abs(y - (.006 - GroundMapPaper.Thickness)) < 1e-7)
                && grain.xyz.Take(grain.VerticesCount * 3).Where((_, i) => i % 3 == 1).All(y => Math.Abs(y - (.006 + GroundMapPaper.GrainDepth)) < 1e-7),
                "Back and translucent grain must stay on opposite sides of the terrain, with the back outside its supporting block.");
            double length = 0;
            for (int i = 0; i < rim.VerticesCount; i += 6)
            {
                var a = new Vec3d(rim.xyz[i * 3], rim.xyz[i * 3 + 1], rim.xyz[i * 3 + 2]);
                var b = new Vec3d(rim.xyz[(i + 1) * 3], rim.xyz[(i + 1) * 3 + 1], rim.xyz[(i + 1) * 3 + 2]);
                length += a.DistanceTo(b);
            }
            check(Math.Abs(length - perimeter) < 1e-5, "Paper thickness must appear only at the outside perimeter, without internal grid walls or seams between sheets.");
            check(grain.Uv.Take(grain.VerticesCount * 2).All(v => v >= -.00001 && v <= 1.00001)
                && grain.Flags.Take(grain.VerticesCount).All(f => (f & VertexFlags.WindBitsMask) == 0),
                "Paper fibres must use the original sheet UVs and remain fixed to its surface.");
        }
        var air = new Block { CollisionBoxes = [] };
        var floor = new Block { CollisionBoxes = [new Cuboidf(0, 0, 0, 1, 1, 1)] };
        var wall = new Block { CollisionBoxes = [new Cuboidf(.25f, 0, 0, .5f, 1, 1)] };
        var accessor = Proxy.Make<IBlockAccessor>((m, args) =>
        {
            if (m.Name != "GetBlock") return null;
            if (args!.Length > 1 && (int)args[1]! == BlockLayersAccess.Fluid) return air;
            var position = (BlockPos)args[0]!;
            return position.Y == 99 ? floor : position.Y == 100 ? wall : air;
        });
        var worldApi = Proxy.Make<IWorldAccessor>((m, args) => m.Name == "get_BlockAccessor" ? accessor : null);
        var anchor = new BlockPos(0, 100, 0);
        var blocked = GroundMapSurface.Build(worldApi, anchor, layout, placement);
        check(Math.Abs(Area(blocked) - .125) < 1e-8 && blocked.Patches[0].SelectMany(p => p).All(v => v.Y >= .005),
            "Actual collision boxes must clip tall obstacles and must not fold paper through their side faces.");
        check(GroundMapSurface.Visible(worldApi, anchor, new(.1, 1, .25), new(.1, .006, .25)),
            "An empty part of a furniture block must not block a surface interaction.");
        check(!GroundMapSurface.Visible(worldApi, anchor, new(1, .3, .25), new(.1, .006, .25)),
            "Collision-based visibility must stop an interaction that crosses an actual furniture side.");
        wall.CollisionBoxes = [new Cuboidf(.301f, 0, 0, .302f, 1, 1)];
        check(!GroundMapSurface.Visible(worldApi, anchor, new(1, .3, .25), new(.1, .006, .25)),
            "Visibility must test thin collision boxes exactly rather than skip them between ray samples.");
        wall.CollisionBoxes = []; floor.CollisionBoxes = [new Cuboidf(0, 0, 0, 1, .5f, 1)]; placement.Height = -.5;
        var slab = GroundMapSurface.Build(worldApi, anchor, layout, placement);
        check(slab.Supported && slab.Patches[0].SelectMany(p => p).All(v => Math.Abs(v.Y + .494) < 1e-8),
            "Actual half-height collision shapes must determine the cloth height without needing a solid UP face.");
    }
}
