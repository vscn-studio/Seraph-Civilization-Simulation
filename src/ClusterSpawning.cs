using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnCivilizationModSystem
{
    private readonly Dictionary<string, Guid> pendingSpawns = [];
    private readonly Dictionary<string, (int Index, long Order)> pendingSequentialAssignments = [];
    private long nextAssignmentOrder;
    private long lastCommittedAssignmentOrder;
    private bool generating;

    private void InitializeClusters()
    {
        if (sapi!.ModLoader.IsModEnabled("clusterspawns")) throw new InvalidOperationException("Remove ClusterSpawns before installing Seraph Civilization Simulation; both write the same spawn state.");
        if (state.Clusters.Count > 0)
        {
            clustersReady = true; RebuildClusterCounts();
            if (config.Clusters.Enabled && state.PendingClusterTarget > state.Clusters.Count) GenerateClusters(false);
            return;
        }
        if (!config.Clusters.Enabled) return;
        if (config.Clusters.ManualClusters.Count > 0)
        {
            if (config.Clusters.ManualClusters.Any(c => !WithinWorld(c.X, c.Z))) throw new ArgumentException("Manual cluster coordinates outside the world.");
            state.Clusters = config.Clusters.ManualClusters.Select(p => new ClusterPoint { X = p.X, Y = p.Y, Z = p.Z }).ToList();
            CompleteInitialization();
        }
        else GenerateClusters(false);
    }

    private void CompleteInitialization()
    {
        clustersReady = state.Clusters.Count > 0;
        RebuildClusterCounts(); SaveState();
        foreach (var p in sapi!.World.AllOnlinePlayers.OfType<IServerPlayer>()) HandleVerifiedPlayer(p);
    }

    private void GenerateClusters(bool replace)
    {
        if (generating) return;
        generating = true;
        var settings = config.Clusters;
        var found = replace ? new List<ClusterPoint>() : new List<ClusterPoint>(state.Clusters);
        int target = !replace && state.PendingClusterTarget > 0 ? state.PendingClusterTarget : settings.TotalClusters;
        if (!replace) state.PendingClusterTarget = target;
        long generationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        int attempts = 0;
        int originX = sapi!.WorldManager.MapSizeX / 2, originZ = sapi.WorldManager.MapSizeZ / 2;
        void Finish(bool success)
        {
            generating = false;
            if (disposed) return;
            if (!success)
            {
                sapi!.Logger.Error("Seraph Civilization Simulation cluster generation failed: {0}/{1}. Published clusters and assignments retained; incomplete initial generation resumes on restart. Widen layout/climate settings or use ManualClusters.", found.Count, target);
                return;
            }
            state.Clusters = found;
            if (replace)
            {
                foreach (var player in sapi!.World.AllOnlinePlayers.OfType<IServerPlayer>())
                    ClusterMessages.Send(player, "reroll-broadcast");
                state.PlayerAssignments.Clear(); pendingSpawns.Clear(); pendingSequentialAssignments.Clear();
            }
            if (replace) { state.CurrentActiveIndex = 0; state.CurrentClusterPlayerCount = 0; }
            state.PendingClusterTarget = 0;
            CompleteInitialization();
            sapi!.Logger.Notification("Seraph Civilization Simulation generated {0} clusters in {1} attempts ({2:0.0}s); initial clusters were available as soon as validated.",
                found.Count, attempts, System.Diagnostics.Stopwatch.GetElapsedTime(generationStarted).TotalSeconds);
        }
        void Next()
        {
            if (disposed) { generating = false; return; }
            if (found.Count >= target) { Finish(true); return; }
            int x, z;
            do
            {
                if (attempts++ >= 20000) { Finish(false); return; }
                x = originX + random.Next(-settings.GlobalRadius, settings.GlobalRadius);
                z = originZ + random.Next(-settings.GlobalRadius, settings.GlobalRadius);
            } while (!WithinWorld(x, z) || found.Any(p => Distance(p.X, p.Z, x, z) < settings.MinDistanceBetween));
            try
            {
                LoadColumn(x, z, () =>
                {
                    try
                    {
                        int y = sapi!.World.BlockAccessor.GetTerrainMapheightAt(new BlockPos(x, 0, z));
                        var climate = sapi.World.BlockAccessor.GetClimateAt(new BlockPos(x, y, z), EnumGetClimateMode.WorldGenValues);
                        if (y <= 0 || (settings.AvoidOceans && y <= sapi.World.SeaLevel) ||
                            (settings.AvoidExtremeClimate && climate != null && (climate.Temperature < settings.MinTemperature || climate.Temperature > settings.MaxTemperature)))
                        { Next(); return; }
                        ValidateLand(x, z, settings, valid =>
                        {
                            if (valid)
                            {
                                found.Add(new ClusterPoint { X = x, Y = y, Z = z });
                                if (!replace)
                                {
                                    // Publish append-only indices immediately. Do not wait for the entire layout
                                    // before allowing ready players to find a safe landing and teleport.
                                    state.Clusters = new List<ClusterPoint>(found);
                                    CompleteInitialization();
                                    sapi.Logger.Notification("Seraph Civilization Simulation cluster #{0} ready after {1:0.0}s ({2}/{3}); player assignment enabled.",
                                        found.Count - 1, System.Diagnostics.Stopwatch.GetElapsedTime(generationStarted).TotalSeconds, found.Count, target);
                                }
                            }
                            // Yield to player loading/teleport work between candidates.
                            sapi.Event.EnqueueMainThreadTask(Next, "vsscsmod-next-cluster");
                        }, () => Finish(false));
                    }
                    catch (Exception ex) { sapi!.Logger.Error("Seraph Civilization Simulation cluster sampling failed: {0}", ex); Finish(false); }
                });
            }
            catch (Exception ex) { sapi!.Logger.Error("Seraph Civilization Simulation could not load cluster candidate: {0}", ex); Finish(false); }
        }
        Next();
    }

    private void ValidateLand(int x, int z, ClusterConfig settings, Action<bool> done, Action failed)
    {
        int land = 0, index = 0, r = settings.LandValidationRadius;
        int needed = RequiredLandSamples(settings.MinLandRatio);
        var cached = new Dictionary<(int X, int Z), ushort[]>();
        void Next()
        {
            if (disposed) { generating = false; return; }
            if (land >= needed) { done(true); return; }
            if (land + 25 - index < needed) { done(false); return; }
            int ix = index / 5, iz = index++ % 5;
            int px = x + (int)(-r + 2.0 * r * ix / 4), pz = z + (int)(-r + 2.0 * r * iz / 4);
            if (!WithinWorld(px, pz)) { Next(); return; }
            int cx = px / GlobalConstants.ChunkSize, cz = pz / GlobalConstants.ChunkSize;
            int offset = (pz % GlobalConstants.ChunkSize) * GlobalConstants.ChunkSize + px % GlobalConstants.ChunkSize;
            // Candidate loading often already generated neighboring columns. Reuse their terrain
            // maps instead of asking world generators to reproduce them with PeekChunkColumn.
            if (!cached.ContainsKey((cx, cz)) && sapi!.WorldManager.GetMapChunk(cx, cz)?.WorldGenTerrainHeightMap is { } loadedHeights
                && offset < loadedHeights.Length && loadedHeights[offset] > 0)
                cached[(cx, cz)] = loadedHeights;
            if (cached.TryGetValue((cx, cz), out var existing))
            {
                if (offset >= existing.Length || existing[offset] == 0) throw new InvalidDataException($"No terrain height at {px}, {pz}.");
                if (existing[offset] > sapi!.World.SeaLevel) land++;
                Next();
                return;
            }
            sapi!.WorldManager.PeekChunkColumn(cx, cz, new ChunkPeekOptions
            {
                UntilPass = EnumWorldGenPass.Terrain,
                OnGenerated = columns =>
                {
                    ushort[]? heightsCopy = null;
                    if (columns.TryGetValue(new Vec2i(cx, cz), out var chunks) && chunks.Length > 0 && chunks[0]?.MapChunk?.WorldGenTerrainHeightMap is { } heights)
                        heightsCopy = (ushort[])heights.Clone();
                    sapi.Event.EnqueueMainThreadTask(() =>
                    {
                        if (disposed) { generating = false; return; }
                        try
                        {
                            if (heightsCopy == null || offset >= heightsCopy.Length || heightsCopy[offset] == 0) throw new InvalidDataException($"No terrain height at {px}, {pz}.");
                            cached[(cx, cz)] = heightsCopy;
                            if (heightsCopy[offset] > sapi.World.SeaLevel) land++;
                            Next();
                        }
                        catch (Exception ex) { sapi.Logger.Error("Seraph Civilization Simulation terrain sampling failed: {0}", ex); failed(); }
                    }, "vsscsmod-cluster-sample");
                }
            });
        }
        Next();
    }

    internal static int RequiredLandSamples(float ratio)
    {
        for (int count = 0; count <= 25; count++) if (count / 25f >= ratio) return count;
        return 26;
    }

    private void LoadColumn(int x, int z, Action action)
    {
        sapi!.WorldManager.LoadChunkColumnPriority(x / GlobalConstants.ChunkSize, z / GlobalConstants.ChunkSize,
            new ChunkLoadOptions { KeepLoaded = false, OnLoaded = () => sapi.Event.EnqueueMainThreadTask(() => { if (!disposed) action(); }, "vscncs-column") });
    }

    private bool WithinWorld(int x, int z) => x >= 2 && z >= 2 && x < sapi!.WorldManager.MapSizeX - 2 && z < sapi.WorldManager.MapSizeZ - 2;
    private static double Distance(int ax, int az, int bx, int bz) => Math.Sqrt(Math.Pow((double)ax - bx, 2) + Math.Pow((double)az - bz, 2));

    private void InvalidatePendingSpawn(string uid)
    {
        pendingSpawns.Remove(uid);
        pendingSequentialAssignments.Remove(uid);
    }
    private void AssignIfNeeded(IServerPlayer p)
    {
        bool validationPending = config.ModPolicy.Enabled && pendingReports.ContainsKey(p.PlayerUID);
        if (!config.Clusters.Enabled || !clustersReady || state.PlayerAssignments.ContainsKey(p.PlayerUID) || tribeState.FindMember(p.PlayerUID) != null || !readyPlayers.Contains(p.PlayerUID) || (!verifiedPlayers.Contains(p.PlayerUID) && !validationPending) || pendingSpawns.ContainsKey(p.PlayerUID)) return;
        AssignAndTeleport(p);
    }

    private IEnumerable<(int X, int Z)> SpawnCandidates(int centerX, int centerZ, int radius)
    {
        var seen = new HashSet<(int X, int Z)>();
        for (int i = 0; i < 20; i++)
        {
            double angle = random.NextDouble() * Math.PI * 2;
            double distance = Math.Sqrt(random.NextDouble()) * radius;
            var point = (centerX + (int)(Math.Cos(angle) * distance), centerZ + (int)(Math.Sin(angle) * distance));
            if (WithinWorld(point.Item1, point.Item2) && seen.Add(point)) yield return point;
        }
        foreach (int distance in new[] { 0, Math.Max(1, radius / 2), radius })
        for (int sample = 0; sample < (distance == 0 ? 1 : 8); sample++)
        {
            double angle = Math.PI * 2 * sample / 8;
            var point = (centerX + (int)(Math.Cos(angle) * distance), centerZ + (int)(Math.Sin(angle) * distance));
            if (WithinWorld(point.Item1, point.Item2) && seen.Add(point)) yield return point;
        }
    }

    private IEnumerable<(int X, int Z)> NearbyCandidates(int cx, int cz)
    {
        var seen = new HashSet<(int X, int Z)>();
        if (WithinWorld(cx, cz) && seen.Add((cx, cz))) yield return (cx, cz);
        for (int i = 0; i < 20; i++)
        {
            var point = (cx + random.Next(-15, 16), cz + random.Next(-15, 16));
            if (WithinWorld(point.Item1, point.Item2) && seen.Add(point)) yield return point;
        }
    }

    private IEnumerable<TerritoryCell> MainTerritorySurface(TribeRecord tribe) =>
        tribe.Sites.Where(s => s.Dimension == tribe.Dimension).SelectMany(s => s.Surface());
    private bool IsReincarnationOutside(TribeRecord tribe, int x, int z)
    {
        if (tribe.Dimension != 0 || !WithinWorld(x, z)) return false;
        var floor = MainTerritorySurface(tribe).ToArray();
        if (floor.Length == 0) return false;
        double distance = floor.Min(p => Math.Sqrt(Math.Pow(x - p.X, 2) + Math.Pow(z - p.Z, 2)));
        var blocks = sapi!.World.BlockAccessor;
        int y = blocks.GetTerrainMapheightAt(new BlockPos(x, 0, z)) + 1;
        return distance > 0 && distance <= config.Respawn.ReincarnationOutsideRange
            && !tribeState.Tribes.Any(t => WithinTerritory(t, x + .5, y, z + .5, tribe.Dimension));
    }
    private IEnumerable<(int X, int Z)> ReincarnationCandidates(TribeRecord tribe)
    {
        if (tribe.Dimension != 0) yield break;
        var surface = MainTerritorySurface(tribe).ToArray();
        if (surface.Length == 0) yield break;
        var footprint = surface.Select(p => (p.X, p.Z)).ToHashSet();
        var edge = surface.Where(p => !footprint.Contains((p.X + 1, p.Z)) || !footprint.Contains((p.X - 1, p.Z))
            || !footprint.Contains((p.X, p.Z + 1)) || !footprint.Contains((p.X, p.Z - 1))).ToArray();
        var seen = new HashSet<(int, int)>();
        for (int i = 0; i < 96 && edge.Length > 0; i++)
        {
            var p = edge[i % edge.Length]; double angle = i * Math.PI * 2 / 16;
            double distance = Math.Max(1, config.Respawn.ReincarnationOutsideRange * (1 + i % 3) / 3.0);
            var point = (p.X + (int)Math.Round(Math.Cos(angle) * distance), p.Z + (int)Math.Round(Math.Sin(angle) * distance));
            if (seen.Add(point) && WithinWorld(point.Item1, point.Item2) && !footprint.Contains(point)) yield return point;
        }
    }

    private int? SafeLandingY(int x, int z)
    {
        var blocks = sapi!.World.BlockAccessor;
        int surfaceY = blocks.GetTerrainMapheightAt(new BlockPos(x, 0, z));
        if (surfaceY <= 1 || surfaceY >= sapi.WorldManager.MapSizeY - 3) return null;
        if (!blocks.IsSideSolid(x, surfaceY, z, BlockFacing.UP)) return null;

        var ground = new BlockPos(x, surfaceY, z);
        var feet = new BlockPos(x, surfaceY + 1, z);
        var head = new BlockPos(x, surfaceY + 2, z);
        if (blocks.GetBlock(ground, 2).IsLiquid() || blocks.GetBlock(feet, 2).IsLiquid() || blocks.GetBlock(head, 2).IsLiquid()) return null;
        if (blocks.GetBlock(feet).Replaceable < 6000 || blocks.GetBlock(head).Replaceable < 6000) return null;
        return surfaceY + 1;
    }

    private void FindSafeLanding(IEnumerable<(int X, int Z)> candidates, Func<bool> active,
        Action<int, int, int> onFound, Action onFailed, System.Func<int, int, bool>? accept = null)
    {
        var iterator = candidates.GetEnumerator();
        bool finished = false;
        void Finish()
        {
            if (finished) return;
            finished = true;
            iterator.Dispose();
        }
        void TryNext()
        {
            if (finished) return;
            if (!active()) { Finish(); return; }
            if (!iterator.MoveNext()) { Finish(); onFailed(); return; }
            var (x, z) = iterator.Current;
            bool callbackStarted = false;
            try
            {
                LoadColumn(x, z, () =>
                {
                    callbackStarted = true;
                    if (finished) return;
                    if (!active()) { Finish(); return; }
                    int? y = null;
                    try { y = SafeLandingY(x, z); }
                    catch (Exception ex) { sapi!.Logger.Warning("Seraph Civilization Simulation rejected spawn candidate ({0},{1}): {2}", x, z, ex.Message); }
                    if (y.HasValue && (accept == null || accept(x, z))) { Finish(); onFound(x, y.Value, z); return; }
                    TryNext();
                });
            }
            catch (Exception ex)
            {
                if (callbackStarted) throw;
                sapi!.Logger.Warning("Seraph Civilization Simulation could not load spawn candidate ({0},{1}): {2}", x, z, ex.Message);
                TryNext();
            }
        }
        TryNext();
    }

    private void AssignAndTeleport(IServerPlayer player, int? fixedCluster = null, int? nearX = null, int? nearZ = null)
    {
        if (!clustersReady || state.Clusters.Count == 0) return;
        bool firstAssignment = !state.PlayerAssignments.ContainsKey(player.PlayerUID);
        InvalidatePendingSpawn(player.PlayerUID);
        int index = fixedCluster ?? ChooseCluster();
        var center = state.Clusters[index];
        var token = Guid.NewGuid();
        pendingSpawns[player.PlayerUID] = token;
        long order = 0;
        if (!fixedCluster.HasValue && config.Clusters.AssignmentMode == ClusterAssignmentMode.Sequential)
        {
            order = ++nextAssignmentOrder;
            pendingSequentialAssignments[player.PlayerUID] = (index, order);
        }
        ClusterMessages.Send(player, "assigning", index);
        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        bool Active() => pendingSpawns.TryGetValue(player.PlayerUID, out var current) && current == token
            && readyPlayers.Contains(player.PlayerUID) && player.Entity != null;
        var candidates = nearX.HasValue && nearZ.HasValue
            ? NearbyCandidates(nearX.Value, nearZ.Value)
            : SpawnCandidates(center.X, center.Z, config.Clusters.ClusterRadius);
        FindSafeLanding(candidates, Active,
            (x, y, z) =>
            {
                try
                {
                    player.Entity.TeleportToDouble(x + .5, y, z + .5);
                    player.Entity.Pos.Motion.Set(0, 0, 0);
                    player.SetSpawnPosition(new PlayerSpawnPos(x, y, z));
                    state.PlayerAssignments[player.PlayerUID] = index;
                    if (order > lastCommittedAssignmentOrder)
                    {
                        CommitClusterAssignment(index, fixedCluster.HasValue);
                        lastCommittedAssignmentOrder = order;
                    }
                    RebuildClusterCounts(); SaveState();
                    if (firstAssignment)
                        ClusterMessages.Send(player, "join", index, config.Clusters.ClusterRadius, state.ClusterPlayerCounts.GetValueOrDefault(index));
                    else ClusterMessages.Send(player, "landed", index);
                    if (guidanceOffers.ContainsKey(player.PlayerUID)) SendGuidance(player);
                    sapi!.Logger.Notification("Seraph Civilization Simulation assigned {0} to cluster #{1} in {2:0.0}s.",
                        player.PlayerName, index, System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalSeconds);
                }
                catch (Exception ex) { sapi!.Logger.Error("Seraph Civilization Simulation teleport failed for {0}: {1}", player.PlayerName, ex); }
                finally { InvalidatePendingSpawn(player.PlayerUID); }
            },
            () =>
            {
                InvalidatePendingSpawn(player.PlayerUID);
                ClusterMessages.Send(player, "err-no-safe-spawn");
                sapi!.Logger.Warning("Seraph Civilization Simulation found no safe landing near cluster #{0} for {1}; previous spawn retained.", index, player.PlayerName);
            });
    }

    private int ChooseCluster()
    {
        if (config.Clusters.AssignmentMode == ClusterAssignmentMode.Random) return random.Next(state.Clusters.Count);
        if (config.Clusters.AssignmentMode == ClusterAssignmentMode.Balanced) return Enumerable.Range(0, state.Clusters.Count).OrderBy(i => state.ClusterPlayerCounts.GetValueOrDefault(i)).First();
        int index = Math.Clamp(state.CurrentActiveIndex, 0, state.Clusters.Count - 1);
        int count = state.CurrentClusterPlayerCount;
        foreach (var pending in pendingSequentialAssignments.Values.Where(p => p.Order > lastCommittedAssignmentOrder).OrderBy(p => p.Order))
        {
            if (pending.Index == index) count++;
            else { index = pending.Index; count = 1; }
        }
        return count >= config.Clusters.MaxPlayersPerCluster
            ? (index + 1) % state.Clusters.Count : index;
    }

    private void CommitClusterAssignment(int index, bool fixedCluster)
    {
        if (fixedCluster || config.Clusters.AssignmentMode != ClusterAssignmentMode.Sequential) return;
        if (state.CurrentActiveIndex == index) state.CurrentClusterPlayerCount++;
        else
        {
            state.CurrentActiveIndex = index;
            state.CurrentClusterPlayerCount = 1;
        }
    }

    private void RebuildClusterCounts()
    {
        state.ClusterPlayerCounts = Enumerable.Range(0, state.Clusters.Count).ToDictionary(i => i, _ => 0);
        foreach (int index in state.PlayerAssignments.Values) if (state.ClusterPlayerCounts.ContainsKey(index)) state.ClusterPlayerCounts[index]++;
    }

    public ClusterViewPacket BuildClusterView()
    {
        var spawn = sapi!.World.DefaultSpawnPosition.AsBlockPos;
        var online = sapi.World.AllOnlinePlayers.Select(p => p.PlayerUID).ToHashSet();
        var names = sapi.PlayerData.PlayerDataByUid.ToDictionary(k => k.Key, k => k.Value.LastKnownPlayername);
        return new ClusterViewPacket { WorldSpawnX = spawn.X, WorldSpawnZ = spawn.Z, Clusters = state.Clusters.Select((p, i) =>
        {
            var members = state.PlayerAssignments.Where(k => k.Value == i).Select(k => k.Key).ToList();
            return new ClusterViewItem { Index = i, X = p.X, Y = p.Y, Z = p.Z, PlayerCount = members.Count, OnlineCount = members.Count(online.Contains), Radius = config.Clusters.ClusterRadius, Capacity = config.Clusters.MaxPlayersPerCluster, Active = i == state.CurrentActiveIndex && config.Clusters.AssignmentMode == ClusterAssignmentMode.Sequential, AssignmentMode = config.Clusters.AssignmentMode.ToString(), Players = members.Select(uid => names.GetValueOrDefault(uid, uid)).OrderBy(n => n).ToList() };
        }).ToList() };
    }

    private TextCommandResult ClusterInfo(IServerPlayer? player)
    {
        if (player == null) return TextCommandResult.Error(ClusterMessages.For(Lang.CurrentLocale, "err-only-player"));
        string language = player.LanguageCode;
        if (!state.PlayerAssignments.TryGetValue(player.PlayerUID, out int i))
            return TextCommandResult.Success(string.Join("\n", ClusterMessages.For(language, "info-title"),
                ClusterMessages.For(language, "info-total", state.Clusters.Count), ClusterMessages.For(language, "info-unassigned")));
        return TextCommandResult.Success(string.Join("\n", ClusterMessages.For(language, "info-title"),
            ClusterMessages.For(language, "info-assigned", i), ClusterMessages.For(language, "info-range", config.Clusters.ClusterRadius),
            ClusterMessages.For(language, "info-count", state.ClusterPlayerCounts.GetValueOrDefault(i))));
    }

    private TextCommandResult HandleClusterCommand(string action, CmdArgs words, IServerPlayer? caller, string language)
    {
        string L(string key, params object[] args) => ClusterMessages.For(language, key, args);
        if (action is "list" or "infoall")
        {
            if (state.Clusters.Count == 0) return TextCommandResult.Error(L("err-no-clusters"));
            var view = BuildClusterView();
            if (action == "list") return TextCommandResult.Success(L("list-title", view.Clusters.Count) + "\n" +
                string.Join("\n", view.Clusters.Select(p => L("list-line", p.Index, p.X - view.WorldSpawnX, p.Z - view.WorldSpawnZ, p.X, p.Y, p.Z))));
            var lines = new List<string>
            {
                L("infoall-title"), L("infoall-mode", config.Clusters.AssignmentMode), L("infoall-ready", clustersReady),
                L("infoall-total", view.Clusters.Count), L("infoall-radius", config.Clusters.ClusterRadius),
                L("infoall-capacity", config.Clusters.MaxPlayersPerCluster), L("infoall-count", state.PlayerAssignments.Count)
            };
            lines.AddRange(view.Clusters.Select(p => L("infoall-line", p.Index, p.X - view.WorldSpawnX, p.Z - view.WorldSpawnZ,
                p.X, p.Y, p.Z, p.PlayerCount, p.OnlineCount, p.Active ? L("infoall-marker") : "")));
            if (config.Clusters.AssignmentMode == ClusterAssignmentMode.Sequential)
                lines.Add(L("infoall-current", state.CurrentClusterPlayerCount, config.Clusters.MaxPlayersPerCluster));
            return TextCommandResult.Success(string.Join("\n", lines));
        }
        if (action == "who")
        {
            string name = words.PopWord("");
            if (string.IsNullOrWhiteSpace(name)) return TextCommandResult.Error(L("err-no-name"));
            var online = FindOnline(name);
            string? uid = online?.PlayerUID ?? sapi!.PlayerData.GetPlayerDataByLastKnownName(name)?.PlayerUID;
            if (uid == null) return TextCommandResult.Error(L("err-not-found", name));
            return TextCommandResult.Success(state.PlayerAssignments.TryGetValue(uid, out int i)
                ? L("who-result", name, L(online == null ? "who-offline" : "who-online"), i, state.ClusterPlayerCounts.GetValueOrDefault(i))
                : L("who-unassigned", name));
        }
        if (action == "tp")
        {
            if (caller == null) return TextCommandResult.Error(L("err-only-player"));
            if (!int.TryParse(words.PopWord(""), out int index) || index < 0 || index >= state.Clusters.Count)
                return TextCommandResult.Error(L("err-range", state.Clusters.Count - 1));
            var point = state.Clusters[index];
            FindSafeLanding(SpawnCandidates(point.X, point.Z, config.Clusters.ClusterRadius),
                () => readyPlayers.Contains(caller.PlayerUID) && caller.Entity != null,
                (x, y, z) =>
                {
                    caller.Entity.TeleportToDouble(x + .5, y, z + .5);
                    caller.Entity.Pos.Motion.Set(0, 0, 0);
                },
                () => ClusterMessages.Send(caller, "err-no-safe-spawn"));
            var origin = sapi!.World.DefaultSpawnPosition.AsBlockPos;
            return TextCommandResult.Success(L("tp-teleporting", index, point.X - origin.X, point.Z - origin.Z));
        }
        if (!config.Clusters.Enabled || !clustersReady) return TextCommandResult.Error(L("err-not-ready"));
        if (action == "assign")
        {
            var target = FindOnline(words.PopWord("")); var friend = FindOnline(words.PopWord(""));
            if (target == null || friend == null || target == friend || !state.PlayerAssignments.TryGetValue(friend.PlayerUID, out int index)) return TextCommandResult.Error(L("err-friend"));
            var spawn = friend.GetSpawnPosition(false);
            if (spawn == null) return TextCommandResult.Error(L("err-friend-spawn"));
            AssignAndTeleport(target, index, (int)spawn.X, (int)spawn.Z);
            var origin = sapi!.World.DefaultSpawnPosition.AsBlockPos;
            return TextCommandResult.Success(L("assign-teleporting", target.PlayerName, friend.PlayerName, index, (int)spawn.X - origin.X, (int)spawn.Z - origin.Z));
        }
        if (action == "reset")
        {
            var target = FindOnline(words.PopWord(""));
            if (target == null) return TextCommandResult.Error(L("err-player-offline"));
            if (!state.PlayerAssignments.ContainsKey(target.PlayerUID)) return TextCommandResult.Success(L("reset-no-record", target.PlayerName));
            AssignAndTeleport(target); return TextCommandResult.Success(L("reset-done", target.PlayerName));
        }
        if (action == "resetall")
        {
            int previous = state.PlayerAssignments.Count;
            state.PlayerAssignments.Clear(); pendingSpawns.Clear(); pendingSequentialAssignments.Clear(); state.CurrentActiveIndex = 0; state.CurrentClusterPlayerCount = 0;
            RebuildClusterCounts(); SaveState();
            foreach (var p in sapi!.World.AllOnlinePlayers.OfType<IServerPlayer>()) AssignIfNeeded(p);
            return TextCommandResult.Success(L("resetall-done", previous));
        }
        if (action == "reroll")
        {
            if (generating) return TextCommandResult.Error(L("err-generating"));
            if (config.Clusters.ManualClusters.Count > 0) return TextCommandResult.Error(L("err-manual"));
            GenerateClusters(true); return TextCommandResult.Success(L("reroll-started"));
        }
        return TextCommandResult.Error("未知出生聚落命令。");
    }
}
