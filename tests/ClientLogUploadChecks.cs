using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.Server;
using Vintagestory.Client.NoObf;
using SeraphCivilizationSimulation;

internal static class ClientLogUploadChecks
{
    public static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "vscncs-clientlogs-" + Guid.NewGuid().ToString("N"));
        string game = Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? "E:/vintagestory/Vintagestory";
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            foreach (string directory in new[] { game, Path.Combine(game, "Lib") })
            {
                string file = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(file)) return context.LoadFromAssemblyPath(Path.GetFullPath(file));
            }
            return null;
        }
        AssemblyLoadContext.Default.Resolving += Resolve;
        Directory.CreateDirectory(root);
        try { Verify(root, check); }
        finally
        {
            AssemblyLoadContext.Default.Resolving -= Resolve;
            Directory.Delete(root, true);
        }
    }

    private static void Verify(string root, Action<bool, string> check)
    {
        var type = typeof(ClientLogUploadSystem);
        object? Call(object target, string method, params object?[] args) => AccessTools.Method(target.GetType(), method).Invoke(target, args);
        void Set(object target, string field, object? value) => AccessTools.Field(target.GetType(), field).SetValue(target, value);
        T Get<T>(object target, string field) => (T)AccessTools.Field(target.GetType(), field).GetValue(target)!;
        bool Reject(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
        T Wire<T>(T packet)
        {
            using var stream = new MemoryStream(); Serializer.Serialize(stream, packet); stream.Position = 0;
            return Serializer.Deserialize<T>(stream);
        }

        string logs = Path.Combine(root, "Logs"), previous = Path.Combine(logs, "Archive", "2026-10-19_00_00_00");
        Directory.CreateDirectory(previous);
        File.WriteAllText(Path.Combine(logs, "client-main.log"), "CURRENT SESSION\n");
        File.WriteAllText(Path.Combine(previous, "client-main.log"), "PREVIOUS SESSION CRASH\n");
        File.WriteAllText(Path.Combine(logs, "client-crash.log"), "CRASH STACK 中文\n");
        File.WriteAllText(Path.Combine(logs, "server-main.log"), "DO NOT UPLOAD SERVER LOGS");
        File.WriteAllText(Path.Combine(logs, "private.txt"), "DO NOT UPLOAD OTHER FILES");
        byte[] large = new byte[ClientLogFiles.FileBytes + 500];
        Random.Shared.NextBytes(large);
        File.WriteAllBytes(Path.Combine(previous, "client-debug.log"), large);
        var snapshot = ClientLogFiles.Capture(logs, "{\"gameVersion\":\"1.22.7\"}");
        string valid = Path.Combine(root, "valid.zip"); File.WriteAllBytes(valid, snapshot.Archive);
        ClientLogFiles.ValidateArchive(valid);
        using (var zip = ZipFile.OpenRead(valid))
        {
            check(zip.GetEntry("previous/client-main.log") != null && zip.GetEntry("current/client-crash.log") != null
                && zip.GetEntry("current/client-main.log") != null, "Capture must preserve crash, previous session and current logs.");
            check(zip.Entries.All(e => !e.FullName.Contains("server-") && !e.FullName.Contains("private")), "Only allowlisted client logs may be collected.");
            using var content = new MemoryStream(); zip.GetEntry("previous/client-debug.log")!.Open().CopyTo(content);
            check(content.ToArray().SequenceEqual(large.Skip(500)), "Oversized logs must retain the precise last 2 MiB.");
            using var reader = new StreamReader(zip.GetEntry("collection.json")!.Open());
            var metadata = JArray.Parse(reader.ReadToEnd());
            check((long)metadata.Single(e => (string?)e["file"] == "previous/client-debug.log")["startByte"]! == 500,
                "Collection must disclose original byte offsets and timestamps for truncated files.");
        }
        string empty = Path.Combine(root, "empty"); Directory.CreateDirectory(empty);
        bool noLogs = false;
        try { ClientLogFiles.Capture(empty, "{}"); } catch (IOException) { noLogs = true; }
        check(noLogs, "Missing client logs must report failure rather than upload an empty report.");
        foreach (string entry in new[] { "../client-main.log", "C:/client-main.log", "current/server-main.log", "current/client-main.log/evil" })
        {
            string bad = Path.Combine(root, "bad.zip");
            using (var zip = ZipFile.Open(bad, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("client-context.json").Open())) writer.Write("{}");
                using (var writer = new StreamWriter(zip.CreateEntry(entry).Open())) writer.Write("test");
            }
            check(Reject(() => ClientLogFiles.ValidateArchive(bad)), "Reject unexpected and traversal archive entries: " + entry);
            File.Delete(bad);
        }
        string bomb = Path.Combine(root, "bomb.zip");
        using (var zip = ZipFile.Open(bomb, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("client-context.json").Open())) writer.Write("{}");
            using var stream = zip.CreateEntry("current/client-main.log").Open(); stream.Write(new byte[ClientLogFiles.FileBytes + 1]);
        }
        check(Reject(() => ClientLogFiles.ValidateArchive(bomb)), "Compressed oversized entries must be rejected before extraction.");
        check(Reject(() => new ClientLogIncoming(Path.Combine(root, "oversize.part"), ClientLogFiles.MaxArchiveBytes + 1)),
            "Oversized transfers must be rejected before creating files.");
        using (var incoming = new ClientLogIncoming(Path.Combine(root, "ordered.part"), 10))
        {
            check(Reject(() => incoming.Append(1, [1])) && Reject(() => incoming.Append(0, new byte[11]))
                && Reject(() => incoming.Append(0, new byte[ClientLogFiles.ChunkBytes + 1])), "Reject unordered, overflowing and oversized chunks.");
            check(!incoming.Append(0, [1, 2]) && incoming.Append(2, new byte[8]), "Only an exact declared length may complete a transfer.");
        }

        string storage = Path.Combine(root, "server"); Directory.CreateDirectory(storage);
        var offered = new List<(IServerPlayer Player, ClientLogOffer Offer)>();
        var replies = new List<ClientLogReply>();
        var player = Proxy.Make<IServerPlayer>((m, a) => m.Name switch { "get_PlayerUID" => "self", "get_PlayerName" => "../玩家/路径", _ => null });
        var other = Proxy.Make<IServerPlayer>((m, a) => m.Name == "get_PlayerUID" ? "other" : null);
        var world = Proxy.Make<IServerWorldAccessor>((m, a) => m.Name == "get_AllOnlinePlayers" ? new IPlayer[] { player } : null);
        var logger = Proxy.Make<ILogger>((m, a) => null);
        var server = Proxy.Make<ICoreServerAPI>((m, a) => m.Name switch { "get_Logger" => logger, "get_World" => world, _ => null });
        var system = new ClientLogUploadSystem(); Set(system, "server", server); Set(system, "storage", storage);
        Set(system, "serverChannel", Proxy.Make<IServerNetworkChannel>((m, a) =>
        {
            if (m.Name == "SendPacket")
            {
                if (a![0] is ClientLogOffer offer) offered.Add((((IServerPlayer[])a[1]!)[0], Wire(offer)));
                if (a[0] is ClientLogReply reply) replies.Add(Wire(reply));
            }
            return null;
        }));
        var incidents = Get<Dictionary<string, ClientLogUploadSystem.LogIncident>>(system, "incidents");
        Call(system, "LogPlayerPlaying", player);
        check(offered.Count == 0, "A first join must not prompt for logs.");
        Get<HashSet<string>>(system, "cleanLeaves").Add("self");
        Call(system, "LogPlayerDisconnect", player);
        check(incidents.Count == 0, "An intentional exit must not create an incident.");
        Call(system, "LogPlayerPlaying", player); Call(system, "LogPlayerDisconnect", player);
        check(incidents.ContainsKey("self") && File.Exists(Path.Combine(storage, "pending.json")), "An unexpected disconnect must persist an incident immediately.");
        var restored = JsonConvert.DeserializeObject<Dictionary<string, ClientLogUploadSystem.LogIncident>>(File.ReadAllText(Path.Combine(storage, "pending.json")))!;
        check(restored["self"].Reason.Contains("without-clean-leave"), "Pending incidents must survive serialization and retain evidence rather than claim a proven crash.");
        Call(system, "LogPlayerPlaying", player);
        var offer1 = offered.Single().Offer;
        check(ReferenceEquals(offered[0].Player, player), "Reconnect offers must target only the affected authenticated player.");
        void Upload(IServerPlayer sender, ClientLogUpload packet) => Call(system, "OnUpload", sender, Wire(packet));
        Upload(other, new() { Id = offer1.Id, Action = "begin", Length = snapshot.Archive.Length });
        Upload(player, new() { Id = "wrong", Action = "begin", Length = snapshot.Archive.Length });
        check(replies.Count == 0 && Directory.GetFiles(storage, "*.part").Length == 0, "Another player or wrong request nonce must not start an upload.");
        Upload(player, new() { Id = offer1.Id, Action = "begin", Length = snapshot.Archive.Length });
        check(replies.Last().Status == "next" && replies.Last().Offset == 0, "Upload begins only after the client accepts and declares its bounded size.");
        for (int offset = 0; offset < snapshot.Archive.Length; offset += ClientLogFiles.ChunkBytes)
            Upload(player, new() { Id = offer1.Id, Action = "chunk", Offset = offset,
                Data = snapshot.Archive.AsSpan(offset, Math.Min(ClientLogFiles.ChunkBytes, snapshot.Archive.Length - offset)).ToArray() });
        check(replies.Last().Status == "complete" && Directory.GetFiles(storage, "*.part").Length == 0
            && Directory.GetFiles(storage, "*.zip").Length == 1 && !incidents.ContainsKey("self"), "Only a complete validated archive may report success and clear the incident.");
        string report = Directory.GetFiles(storage, "*.zip").Single();
        var sidecar = JObject.Parse(File.ReadAllText(Path.ChangeExtension(report, ".json")));
        check(File.ReadAllBytes(report).SequenceEqual(snapshot.Archive) && (string?)sidecar["playerUid"] == "self"
            && (string?)sidecar["reportId"] == offer1.Id && !Path.GetFileName(report).Contains("玩家"),
            "Stored report must preserve bytes, authenticated identity and incident ID without using a player name as a path.");
        Upload(player, new() { Id = offer1.Id, Action = "begin", Length = 1 });
        check(Directory.GetFiles(storage, "*.zip").Length == 1, "A completed nonce cannot be replayed.");
        Call(system, "OfferLogs", player, new ClientLogUploadSystem.LogIncident { AtUtc = DateTime.UtcNow, Reason = "manual" });
        var offer2 = offered.Last().Offer;
        Upload(player, new() { Id = offer2.Id, Action = "begin", Length = 10 });
        Upload(player, new() { Id = offer2.Id, Action = "chunk", Offset = 1, Data = [1] });
        check(replies.Last().Status == "error" && Directory.GetFiles(storage, "*.part").Length == 0, "Bad chunk order must abort and remove partial reports.");
        Call(system, "OfferLogs", player, new ClientLogUploadSystem.LogIncident { AtUtc = DateTime.UtcNow, Reason = "manual" });
        var offer3 = offered.Last().Offer;
        incidents["self"] = new() { AtUtc = DateTime.UtcNow };
        Upload(player, new() { Id = offer3.Id, Action = "decline" });
        check(!incidents.ContainsKey("self") && Directory.GetFiles(storage, "*.zip").Length == 1, "Declining must clear the prompt without creating a report.");
        check(!(bool)Call(system, "OfferLogs", player, new ClientLogUploadSystem.LogIncident())!, "Repeated attempts must be bounded to three per hour.");

        Get<Dictionary<string, List<DateTime>>>(system, "attempts").Clear();
        Call(system, "OfferLogs", player, new ClientLogUploadSystem.LogIncident { AtUtc = DateTime.UtcNow });
        var timedOffer = offered.Last().Offer;
        Upload(player, new() { Id = timedOffer.Id, Action = "begin", Length = 10 });
        var pendingRequests = (System.Collections.IDictionary)AccessTools.Field(type, "requests").GetValue(system)!;
        Set(pendingRequests["self"]!, "Touched", DateTime.UtcNow.AddMinutes(-2));
        Call(system, "ExpireLogRequests");
        check(replies.Last().Status == "error" && Directory.GetFiles(storage, "*.part").Length == 0,
            "Inactive uploads must notify their owner and delete partial data.");
        Call(system, "OfferLogs", player, new ClientLogUploadSystem.LogIncident { AtUtc = DateTime.UtcNow });
        var invalidOffer = offered.Last().Offer;
        Upload(player, new() { Id = invalidOffer.Id, Action = "begin", Length = 4 });
        Upload(player, new() { Id = invalidOffer.Id, Action = "chunk", Data = [1, 2, 3, 4] });
        check(replies.Last().Status == "error" && Directory.GetFiles(storage, "*.part").Length == 0
            && Directory.GetFiles(storage, "*.zip").Length == 1, "A complete but invalid archive must not be published as a report.");
        string stale = Path.Combine(storage, "old-report.zip"); File.WriteAllText(stale, "old");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-31));
        File.WriteAllText(Path.Combine(storage, "orphan.part"), "partial");
        Call(system, "CleanupStorage");
        check(!File.Exists(stale) && Directory.GetFiles(storage, "*.part").Length == 0 && File.Exists(report),
            "Startup cleanup must remove expired and partial reports while preserving recent reports.");

        // Exercise the actual engine signatures and Harmony binding against the installed game.
        var patch = new Harmony("vscncs.clientlogs.checks");
        try
        {
            foreach (var (target, prefix) in new[] { ("HandleLeave", "BeforeLogLeave"), ("DisconnectPlayer", "BeforeLogDisconnect") })
            {
                patch.Patch(AccessTools.Method(typeof(ServerMain), target), prefix: new HarmonyMethod(type, prefix));
                check(Harmony.GetPatchInfo(AccessTools.Method(typeof(ServerMain), target))!.Owners.Contains(patch.Id), "Engine leave/disconnect patch must bind: " + target);
            }
            patch.Patch(AccessTools.Method(typeof(ClientMain), nameof(ClientMain.SendLeave)),
                prefix: new HarmonyMethod(type, "BeforeClientLogLeave"));
            var nativeClient = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
            object?[] leaveArgs = [nativeClient, 0];
            nativeClient.KillNextFrame = true;
            AccessTools.Method(type, "BeforeClientLogLeave").Invoke(null, leaveArgs);
            check((int)leaveArgs[1]! == 1, "Worker-thread crashes must preserve crash evidence instead of sending an intentional exit.");
            nativeClient.KillNextFrame = false; leaveArgs[1] = 0;
            AccessTools.Method(type, "BeforeClientLogLeave").Invoke(null, leaveArgs);
            check((int)leaveArgs[1]! == 0, "An ordinary exit must preserve its normal leave reason.");

            var nativePlayer = (ServerPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ServerPlayer));
            var worldData = new ServerWorldPlayerData();
            AccessTools.Field(typeof(ServerWorldPlayerData), "PlayerUID").SetValue(worldData, "self");
            AccessTools.Field(typeof(ServerPlayer), "worlddata").SetValue(nativePlayer, worldData);
            var connection = (ConnectedClient)RuntimeHelpers.GetUninitializedObject(typeof(ConnectedClient)); connection.Player = nativePlayer;
            Set(system, "serverInstance", system);
            Call(system, "BeforeLogLeave", new Packet_Client { Leave = new Packet_ClientLeave { Reason = 0 } }, connection);
            check(Get<HashSet<string>>(system, "cleanLeaves").Remove("self"), "An explicit engine exit packet must be recognized as intentional.");
            Call(system, "BeforeLogLeave", new Packet_Client { Leave = new Packet_ClientLeave { Reason = 1 } }, connection);
            check(Get<Dictionary<string, string>>(system, "disconnectReasons")["self"] == "client-crash-reported",
                "The engine crash leave reason must be retained for the administrator.");
            var nativeServer = (ServerMain)RuntimeHelpers.GetUninitializedObject(typeof(ServerMain)); nativeServer.stopped = true;
            Call(system, "BeforeLogDisconnect", nativeServer, connection, "shutdown", "shutdown");
            check(Get<bool>(system, "stopping"), "Server Stop must suppress incidents before the later Shutdown run phase.");
        }
        finally { patch.UnpatchAll(patch.Id); Set(system, "serverInstance", null); }

        var client = new ClientLogUploadSystem();
        var sends = new List<ClientLogUpload>(); var callbacks = new Queue<Action<float>>();
        var events = Proxy.Make<IClientEventAPI>((m, a) => { if (m.Name == "RegisterCallback") callbacks.Enqueue((Action<float>)a![0]!); return null; });
        var api = Proxy.Make<ICoreClientAPI>((m, a) => m.Name == "get_Event" ? events : null);
        Set(client, "client", api);
        Set(client, "clientChannel", Proxy.Make<IClientNetworkChannel>((m, a) => { if (m.Name == "SendPacket") sends.Add((ClientLogUpload)a![0]!); return null; }));
        Set(client, "clientOffer", offer1); Set(client, "clientSnapshot", snapshot); Set(client, "clientReady", true);
        Call(client, "ReceiveLogReply", new ClientLogReply { Id = offer1.Id, Status = "next" });
        check(sends.Count == 0 && callbacks.Count == 0, "Server ACKs cannot make a client send logs before consent.");
        Set(client, "uploading", true);
        Call(client, "ReceiveLogReply", new ClientLogReply { Id = offer1.Id, Status = "next" });
        check(sends.Count == 0 && callbacks.Count == 1, "Consent still requires the client pacing callback before sending a chunk.");
        callbacks.Dequeue()(0);
        check(sends.Count == 1 && sends[0].Data.Length == ClientLogFiles.ChunkBytes, "Acknowledged consented uploads must send one bounded chunk.");
        Call(client, "ReceiveLogReply", new ClientLogReply { Id = offer1.Id, Status = "next", Offset = ClientLogFiles.ChunkBytes });
        Call(client, "LogClientLeave"); callbacks.Dequeue()(0);
        check(sends.Count == 1 && Get<ClientLogFiles.Snapshot?>(client, "clientSnapshot") == null,
            "Leaving the world must release log bytes and invalidate scheduled sends.");
        Set(system, "stopping", true); Call(system, "LogPlayerDisconnect", player);
        check(incidents.Count == 0, "Planned shutdown must not be classified as a player crash.");
    }
}
