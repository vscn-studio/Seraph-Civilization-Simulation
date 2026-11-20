using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace SeraphCivilizationSimulation;

public sealed class TerritoryRenderer : IRenderer, IDisposable
{
    private const int BoundaryHeight = 10;
    private const int BoundaryMaxSegments = 512;
    private const int BoundaryTargetSegmentLength = 6;
    private const int BoundarySupportSpacing = 24;
    private readonly ICoreClientAPI api;
    private readonly LoadedTexture home;
    private readonly LoadedTexture campfire;
    private readonly Dictionary<(int Radius, bool IsOwn), MeshRef> boundaryMeshes = [];
    private readonly Dictionary<(string Id, long Revision, bool Own), MeshRef> buildingMeshes = [];
    private readonly Dictionary<(string TribeId, int X, int Z, int Dimension), int> boundaryTerrainHeights = [];
    private List<TribeTerritoryPoint> points = [];
    private List<TribeBoundaryView> boundaries = [];
    private string? myTribeId;
    private bool boundaryDisplayEnabled;
    private bool boundaryVisible;

    public double RenderOrder => 0.9;
    public int RenderRange => 1200;

    public TerritoryRenderer(ICoreClientAPI api)
    {
        this.api = api;
        home = TribeIcons.CreateTexture(api, TribeIcon.Home);
        campfire = TribeIcons.CreateTexture(api, TribeIcon.Campfire);
        api.Event.RegisterRenderer(this, EnumRenderStage.Ortho, "vscncs-territory-markers");
        api.Event.RegisterRenderer(this, EnumRenderStage.OIT, "vscncs-territory-boundaries");
    }

    public bool BoundaryVisible => boundaryVisible && boundaryDisplayEnabled;
    public bool BoundaryDisplayEnabled => boundaryDisplayEnabled;

    public void SetBoundaryVisibility(bool visible) => boundaryVisible = visible;

    public void Update(CivilizationSnapshotPacket snapshot)
    {
        myTribeId = snapshot.MyTribeId;
        boundaryDisplayEnabled = snapshot.BoundaryDisplayEnabled;
        points = string.IsNullOrEmpty(snapshot.MyTribeId) ? [] : snapshot.Territories;
        boundaries = snapshot.BoundaryDisplayEnabled ? snapshot.Boundaries : [];
        boundaryTerrainHeights.Clear();
        var buildingKeys = boundaries.Where(b => b.RegionId.Length > 0).Select(b => (b.RegionId, b.Revision, b.TribeId == myTribeId)).ToHashSet();
        foreach (var key in buildingMeshes.Keys.Where(k => !buildingKeys.Contains(k)).ToArray())
        { buildingMeshes[key].Dispose(); buildingMeshes.Remove(key); }
        var usedMeshes = boundaries.Select(boundary => (Math.Clamp(boundary.Radius, 1, 1000), boundary.TribeId == myTribeId)).ToHashSet();
        foreach (var key in boundaryMeshes.Keys.Where(key => !usedMeshes.Contains(key)).ToArray())
        {
            boundaryMeshes[key].Dispose();
            boundaryMeshes.Remove(key);
        }
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (stage == EnumRenderStage.OIT)
        {
            RenderBoundaries();
            return;
        }
        if (stage != EnumRenderStage.Ortho) return;
        var player = api.World?.Player?.Entity;
        if (player == null || points.Count == 0) return;
        var render = api.Render;
        foreach (var point in points)
        {
            if (point.Dimension != player.Pos.Dimension || Math.Pow(point.X - player.Pos.X, 2) + Math.Pow(point.Z - player.Pos.Z, 2) > RenderRange * RenderRange) continue;
            double y = point.Y == 0 ? api.World!.BlockAccessor.GetTerrainMapheightAt(new BlockPos(point.X, 0, point.Z, point.Dimension)) + 1 : point.Y;
            var pos = MatrixToolsd.Project(new Vec3d(point.X, y + 3.5, point.Z),
                render.PerspectiveProjectionMat, render.PerspectiveViewMat, render.FrameWidth, render.FrameHeight);
            if (pos.Z < 0 || pos.X < 0 || pos.X > render.FrameWidth || pos.Y < 0 || pos.Y > render.FrameHeight) continue;
            int texture = point.IsOutpost ? campfire.TextureId : home.TextureId;
            render.Render2DTexturePremultipliedAlpha(texture, (float)pos.X - 16, render.FrameHeight - (float)pos.Y - 16, 32, 32, 1000);
        }
    }

    private void RenderBoundaries()
    {
        if (!BoundaryVisible || boundaries.Count == 0) return;
        var player = api.World?.Player?.Entity;
        if (player == null) return;
        var render = api.Render;
        var visible = new List<(TribeBoundaryView Boundary, int BaseY, bool IsOwn)>();
        foreach (var boundary in boundaries)
        {
            if (boundary.Dimension != player.Pos.Dimension) continue;
            int radius = Math.Clamp(boundary.Radius, 1, 1000);
            if (boundary.Edges.Count > 0)
            {
                if (!boundary.Edges.Any(edge => player.Pos.X >= Math.Min(edge.X1, edge.X2) - RenderRange
                    && player.Pos.X <= Math.Max(edge.X1, edge.X2) + RenderRange
                    && player.Pos.Z >= Math.Min(edge.Z1, edge.Z2) - RenderRange
                    && player.Pos.Z <= Math.Max(edge.Z1, edge.Z2) + RenderRange)) continue;
            }
            else if (DistanceToBoundary(player.Pos.X, player.Pos.Z, boundary.X, boundary.Z, radius) > RenderRange) continue;
            int baseY = boundary.Y;
            if (baseY == 0 && boundary.RegionId.Length == 0)
            {
                var key = (boundary.TribeId, boundary.X, boundary.Z, boundary.Dimension);
                if (!boundaryTerrainHeights.TryGetValue(key, out baseY))
                {
                    baseY = api.World!.BlockAccessor.GetTerrainMapheightAt(new BlockPos(boundary.X, 0, boundary.Z, boundary.Dimension)) + 1;
                    boundaryTerrainHeights[key] = baseY;
                }
            }
            visible.Add((boundary, baseY, boundary.TribeId == myTribeId));
        }
        if (visible.Count == 0) return;

        var previous = render.CurrentActiveShader;
        var shader = render.GetEngineShader(EnumShaderProgram.Autocamera);
        bool previousStopped = false;
        bool shaderStarted = false;
        bool rootMatrixPushed = false;
        try
        {
            if (previous != null)
            {
                previous.Stop();
                previousStopped = true;
            }
            shader.Use();
            shaderStarted = true;
            render.LineWidth = 2f;
            render.BindTexture2d(0);
            render.GlPushMatrix();
            rootMatrixPushed = true;
            render.GlLoadMatrix(render.CameraMatrixOrigin);
            var camera = player.CameraPos;
            foreach (var (boundary, baseY, isOwn) in visible)
            {
                MeshRef mesh;
                if (boundary.RegionId.Length > 0)
                {
                    var buildingKey = (boundary.RegionId, boundary.Revision, isOwn);
                    if (!buildingMeshes.TryGetValue(buildingKey, out mesh!))
                    { mesh = render.UploadMesh(BuildBuildingMeshData(boundary, isOwn)); buildingMeshes[buildingKey] = mesh; }
                }
                else
                {
                var key = (Radius: Math.Clamp(boundary.Radius, 1, 1000), IsOwn: isOwn);
                if (!boundaryMeshes.TryGetValue(key, out mesh!))
                {
                    mesh = render.UploadMesh(BuildBoundaryMeshData(key.Radius, key.IsOwn));
                    boundaryMeshes.Add(key, mesh);
                }
                }
                render.GlPushMatrix();
                try
                {
                    render.GlTranslate((float)(boundary.X - camera.X), (float)(baseY - camera.Y), (float)(boundary.Z - camera.Z));
                    shader.UniformMatrix("projectionMatrix", render.CurrentProjectionMatrix);
                    shader.UniformMatrix("modelViewMatrix", render.CurrentModelviewMatrix);
                    render.RenderMesh(mesh);
                }
                finally
                {
                    render.GlPopMatrix();
                }
            }
        }
        finally
        {
            try
            {
                if (rootMatrixPushed) render.GlPopMatrix();
            }
            finally
            {
                try
                {
                    if (shaderStarted) shader.Stop();
                }
                finally
                {
                    if (previousStopped) previous!.Use();
                }
            }
        }
    }

    internal static MeshData BuildBoundaryMeshData(int radius, bool isOwn)
    {
        radius = Math.Clamp(radius, 1, 1000);
        int segments = GetBoundarySegmentCount(radius);
        double segmentLength = Math.PI * 2 * radius / segments;
        int supportEvery = Math.Max(1, (int)Math.Round(BoundarySupportSpacing / segmentLength));
        int color = isOwn ? ColorUtil.ToRgba(180, 55, 220, 85) : ColorUtil.ToRgba(180, 235, 70, 70);
        var mesh = new MeshData(segments * 6, segments * 6, withNormals: false, withUv: false);
        mesh.SetMode(EnumDrawMode.Lines);
        int vertexIndex = 0;
        void AddLine(float x1, float y1, float z1, float x2, float y2, float z2)
        {
            mesh.AddVertexSkipTex(x1, y1, z1, color);
            mesh.AddIndex(vertexIndex++);
            mesh.AddVertexSkipTex(x2, y2, z2, color);
            mesh.AddIndex(vertexIndex++);
        }

        for (int i = 0; i < segments; i++)
        {
            double angle = Math.PI * 2 * i / segments;
            double nextAngle = Math.PI * 2 * (i + 1) / segments;
            float x1 = (float)(Math.Cos(angle) * radius);
            float z1 = (float)(Math.Sin(angle) * radius);
            float x2 = (float)(Math.Cos(nextAngle) * radius);
            float z2 = (float)(Math.Sin(nextAngle) * radius);
            AddLine(x1, 0, z1, x2, 0, z2);
            AddLine(x1, BoundaryHeight, z1, x2, BoundaryHeight, z2);
            if (i % supportEvery == 0) AddLine(x1, 0, z1, x1, BoundaryHeight, z1);
        }
        return mesh;
    }

    internal static MeshData BuildBuildingMeshData(TribeBoundaryView boundary, bool own)
    {
        if (boundary.Edges.Count > 0) return BuildPopulationMeshData(boundary, own);
        var cells = TerritoryGeometry.Decode(boundary.Cells).ToHashSet();
        var edges = new HashSet<(TerritoryCell A, TerritoryCell B)>();
        void Edge(TerritoryCell a, TerritoryCell b)
        {
            if (a.X > b.X || a.X == b.X && (a.Y > b.Y || a.Y == b.Y && a.Z > b.Z)) (a, b) = (b, a);
            edges.Add((a, b));
        }
        foreach (var p in cells)
        foreach (var f in BlockFacing.ALLFACES)
        {
            var next = new TerritoryCell(p.X + f.Normali.X, p.Y + f.Normali.Y, p.Z + f.Normali.Z);
            if (cells.Contains(next)) continue;
            TerritoryCell[] corners;
            if (f.Normali.X != 0)
            { int x = p.X + Math.Max(0, f.Normali.X); corners = [new(x,p.Y,p.Z), new(x,p.Y+1,p.Z), new(x,p.Y+1,p.Z+1), new(x,p.Y,p.Z+1)]; }
            else if (f.Normali.Y != 0)
            { int y = p.Y + Math.Max(0, f.Normali.Y); corners = [new(p.X,y,p.Z), new(p.X+1,y,p.Z), new(p.X+1,y,p.Z+1), new(p.X,y,p.Z+1)]; }
            else
            { int z = p.Z + Math.Max(0, f.Normali.Z); corners = [new(p.X,p.Y,z), new(p.X+1,p.Y,z), new(p.X+1,p.Y+1,z), new(p.X,p.Y+1,z)]; }
            for (int i = 0; i < 4; i++) Edge(corners[i], corners[(i + 1) % 4]);
        }
        var mesh = new MeshData(edges.Count * 2, edges.Count * 2, withNormals: false, withUv: false);
        mesh.SetMode(EnumDrawMode.Lines); int index = 0;
        int color = own ? ColorUtil.ToRgba(130, 55, 220, 85) : ColorUtil.ToRgba(130, 235, 70, 70);
        foreach (var (a,b) in edges)
        {
            mesh.AddVertexSkipTex(a.X - boundary.X, a.Y - boundary.Y, a.Z - boundary.Z, color); mesh.AddIndex(index++);
            mesh.AddVertexSkipTex(b.X - boundary.X, b.Y - boundary.Y, b.Z - boundary.Z, color); mesh.AddIndex(index++);
        }
        return mesh;
    }

    internal static MeshData BuildPopulationMeshData(TribeBoundaryView boundary, bool own)
    {
        var mesh = new MeshData(boundary.Edges.Count * 8, boundary.Edges.Count * 8, withNormals: false, withUv: false);
        mesh.SetMode(EnumDrawMode.Lines); int index = 0;
        foreach (var edge in boundary.Edges)
        {
            int color = edge.PendingShrink ? ColorUtil.ToRgba(200, 255, 176, 48)
                : own ? ColorUtil.ToRgba(180, 55, 220, 85) : ColorUtil.ToRgba(180, 235, 70, 70);
            void Line(int x1, int y1, int z1, int x2, int y2, int z2)
            {
                mesh.AddVertexSkipTex(x1 - boundary.X, y1 - boundary.Y, z1 - boundary.Z, color); mesh.AddIndex(index++);
                mesh.AddVertexSkipTex(x2 - boundary.X, y2 - boundary.Y, z2 - boundary.Z, color); mesh.AddIndex(index++);
            }
            foreach (int y in new[] { edge.BelowY, edge.SurfaceY, edge.AboveY }) Line(edge.X1, y, edge.Z1, edge.X2, y, edge.Z2);
            if (boundary.RegionId.EndsWith(":fixed", StringComparison.Ordinal) || (edge.X1 & 3) == 0 && (edge.Z1 & 3) == 0)
                Line(edge.X1, edge.BelowY, edge.Z1, edge.X1, edge.AboveY, edge.Z1);
        }
        return mesh;
    }

    internal static int GetBoundarySegmentCount(int radius) =>
        Math.Clamp((int)Math.Ceiling(Math.PI * 2 * Math.Clamp(radius, 1, 1000) / BoundaryTargetSegmentLength), 24, BoundaryMaxSegments);

    private static double DistanceToBoundary(double x, double z, int centerX, int centerZ, int radius)
    {
        return Math.Max(Math.Sqrt(Math.Pow(x - centerX, 2) + Math.Pow(z - centerZ, 2)) - radius, 0);
    }

    public void Dispose()
    {
        api.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);
        api.Event.UnregisterRenderer(this, EnumRenderStage.OIT);
        foreach (var mesh in boundaryMeshes.Values) mesh.Dispose();
        boundaryMeshes.Clear();
        foreach (var mesh in buildingMeshes.Values) mesh.Dispose();
        buildingMeshes.Clear();
        boundaryTerrainHeights.Clear();
        home.Dispose();
        campfire.Dispose();
    }
}
