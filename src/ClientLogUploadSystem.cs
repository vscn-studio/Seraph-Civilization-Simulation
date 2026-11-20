using HarmonyLib;
using Newtonsoft.Json;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.Server;

namespace SeraphCivilizationSimulation;

[ProtoContract]
public sealed class ClientLogOffer
{
    [ProtoMember(1)] public string Id { get; set; } = "";
    [ProtoMember(2)] public string IncidentUtc { get; set; } = "";
    [ProtoMember(3)] public string Reason { get; set; } = "";
}

[ProtoContract]
public sealed class ClientLogUpload
{
    [ProtoMember(1)] public string Id { get; set; } = "";
    [ProtoMember(2)] public string Action { get; set; } = "";
    [ProtoMember(3)] public int Length { get; set; }
    [ProtoMember(4)] public int Offset { get; set; }
    [ProtoMember(5)] public byte[] Data { get; set; } = [];
}

[ProtoContract]
public sealed class ClientLogReply
{
    [ProtoMember(1)] public string Id { get; set; } = "";
    [ProtoMember(2)] public string Status { get; set; } = "";
    [ProtoMember(3)] public int Offset { get; set; }
    [ProtoMember(4)] public string Message { get; set; } = "";
}

public sealed partial class ClientLogUploadSystem : ModSystem
{
    internal const string Channel = "vscncs.clientlogs";
    private ICoreServerAPI? server;
    private IServerNetworkChannel? serverChannel;
    private static ClientLogUploadSystem? serverInstance;
    private Harmony? leavePatch;
    private string storage = "";
    private bool stopping;
    private long cleanupListener;
    private readonly HashSet<string> playing = [], cleanLeaves = [];
    private readonly Dictionary<string, string> disconnectReasons = [];
    private Dictionary<string, LogIncident> incidents = [];
    private readonly Dictionary<string, LogRequest> requests = [];
    private readonly Dictionary<string, List<DateTime>> attempts = [];

    internal sealed class LogIncident
    {
        public DateTime AtUtc { get; set; }
        public string Reason { get; set; } = "";
    }
    private sealed class LogRequest(ClientLogOffer offer)
    {
        internal ClientLogOffer Offer = offer;
        internal DateTime Touched = DateTime.UtcNow;
        internal ClientLogIncoming? Incoming;
    }

    public override void Start(ICoreAPI api) => api.Network.RegisterChannel(Channel)
        .RegisterMessageType<ClientLogOffer>().RegisterMessageType<ClientLogUpload>().RegisterMessageType<ClientLogReply>();

    public override void StartServerSide(ICoreServerAPI api)
    {
        server = api; serverInstance = this;
        storage = api.GetOrCreateDataPath("vscncs-clientlogs");
        string pending = Path.Combine(storage, "pending.json");
        if (File.Exists(pending))
        {
            try { incidents = JsonConvert.DeserializeObject<Dictionary<string, LogIncident>>(File.ReadAllText(pending)) ?? []; }
            catch (Exception ex) { api.Logger.Error("Seraph Civilization Simulation client log pending state could not be read: {0}", ex); }
        }
        CleanupStorage();
        serverChannel = api.Network.GetChannel(Channel).SetMessageHandler<ClientLogUpload>(OnUpload);
        api.Event.PlayerNowPlaying += LogPlayerPlaying;
        api.Event.PlayerDisconnect += LogPlayerDisconnect;
        api.Event.ServerRunPhase(EnumServerRunPhase.Shutdown, () => stopping = true);
        cleanupListener = api.Event.RegisterGameTickListener(_ => ExpireLogRequests(), 10000);
        // PlayerLeave also fires for some abrupt socket closes. Inspect the explicit
        // leave packet instead, so a killed process is not mistaken for a clean exit.
        leavePatch = new Harmony("vsscsmod.clientlogs");
        leavePatch.Patch(AccessTools.Method(typeof(ServerMain), "HandleLeave"),
            prefix: new HarmonyMethod(typeof(ClientLogUploadSystem), nameof(BeforeLogLeave)));
        leavePatch.Patch(AccessTools.Method(typeof(ServerMain), nameof(ServerMain.DisconnectPlayer)),
            prefix: new HarmonyMethod(typeof(ClientLogUploadSystem), nameof(BeforeLogDisconnect)));
        api.ChatCommands.Create("clientlogs").WithDescription("确认上传自己的客户端崩溃/掉线日志")
            .RequiresPrivilege(Privilege.chat).RequiresPlayer().HandleWith(args =>
            {
                var player = (IServerPlayer)args.Caller.Player;
                if (!playing.Contains(player.PlayerUID)) return TextCommandResult.Error("请等待进入世界后重试。");
                if (requests.ContainsKey(player.PlayerUID)) return TextCommandResult.Error("已有日志请求，请先处理弹窗或等待请求过期。");
                return OfferLogs(player, new() { AtUtc = DateTime.UtcNow, Reason = "manual-player-request" })
                    ? TextCommandResult.Success("已请求客户端显示日志上传确认窗口。")
                    : TextCommandResult.Error("日志提交过于频繁，每小时最多尝试 3 次。");
            });
    }

    private static void BeforeLogLeave(Packet_Client packet, ConnectedClient client)
    {
        if (client.Player == null || serverInstance is not { } system) return;
        if (packet.Leave?.Reason == 0) system.cleanLeaves.Add(client.Player.PlayerUID);
        else if (packet.Leave?.Reason == 1) system.disconnectReasons[client.Player.PlayerUID] = "client-crash-reported";
    }

    private static void BeforeLogDisconnect(ServerMain __instance, ConnectedClient client, string othersKickmessage, string hisKickMessage)
    {
        if (client?.Player == null || serverInstance is not { } system) return;
        // Stop() disconnects clients BEFORE entering the Shutdown run phase.
        if (__instance.stopped) system.stopping = true;
        string uid = client.Player.PlayerUID;
        if (!system.disconnectReasons.ContainsKey(uid))
        {
            string reason = string.Join("; ", new[] { othersKickmessage, hisKickMessage }.Where(s => !string.IsNullOrEmpty(s)));
            system.disconnectReasons[uid] = reason.Length == 0 ? "connection-ended-without-clean-leave" : reason[..Math.Min(512, reason.Length)];
        }
    }

    private void LogPlayerPlaying(IServerPlayer player)
    {
        playing.Add(player.PlayerUID); cleanLeaves.Remove(player.PlayerUID); disconnectReasons.Remove(player.PlayerUID);
        if (incidents.TryGetValue(player.PlayerUID, out var incident)) OfferLogs(player, incident);
    }

    private void LogPlayerDisconnect(IServerPlayer player)
    {
        string uid = player.PlayerUID;
        bool wasPlaying = playing.Remove(uid), clean = cleanLeaves.Remove(uid);
        string reason = disconnectReasons.GetValueOrDefault(uid, "connection-ended-without-clean-leave");
        disconnectReasons.Remove(uid);
        CancelRequest(uid);
        if (stopping || !wasPlaying || clean) return;
        incidents[uid] = new() { AtUtc = DateTime.UtcNow, Reason = reason };
        SaveIncidents();
    }

    private bool OfferLogs(IServerPlayer player, LogIncident incident)
    {
        string uid = player.PlayerUID;
        if (!attempts.TryGetValue(uid, out var history)) attempts[uid] = history = [];
        history.RemoveAll(t => t < DateTime.UtcNow.AddHours(-1));
        if (history.Count >= 3 || requests.ContainsKey(uid)) return false;
        history.Add(DateTime.UtcNow);
        var offer = new ClientLogOffer { Id = Guid.NewGuid().ToString("N"), IncidentUtc = incident.AtUtc.ToString("O"), Reason = incident.Reason };
        requests[uid] = new(offer);
        serverChannel!.SendPacket(offer, player);
        return true;
    }

    private void OnUpload(IServerPlayer player, ClientLogUpload packet)
    {
        string uid = player.PlayerUID;
        if (stopping || !playing.Contains(uid) || !requests.TryGetValue(uid, out var request) || packet.Id != request.Offer.Id) return;
        try
        {
            if (DateTime.UtcNow - request.Touched > TimeSpan.FromMinutes(10)) throw new InvalidDataException("日志请求已过期，请使用 /clientlogs 重试。");
            request.Touched = DateTime.UtcNow;
            if (packet.Action is "decline" or "cancel")
            {
                CancelRequest(uid);
                if (packet.Action == "decline") { incidents.Remove(uid); SaveIncidents(); }
                return;
            }
            if (packet.Action == "begin")
            {
                if (request.Incoming != null || requests.Values.Count(r => r.Incoming != null) >= 8)
                    throw new InvalidDataException("日志上传繁忙，请稍后使用 /clientlogs 重试。");
                request.Incoming = new ClientLogIncoming(Path.Combine(storage, request.Offer.Id + ".part"), packet.Length);
                Reply(player, packet.Id, "next", 0);
                return;
            }
            if (packet.Action != "chunk" || request.Incoming == null) throw new InvalidDataException("日志传输状态无效。");
            bool complete = request.Incoming.Append(packet.Offset, packet.Data);
            int offset = request.Incoming.Offset;
            if (!complete) { Reply(player, packet.Id, "next", offset); return; }
            string temporary = request.Incoming.Path;
            request.Incoming.Dispose(); request.Incoming = null;
            ClientLogFiles.ValidateArchive(temporary);
            string name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{request.Offer.Id}";
            string destination = Path.Combine(storage, name + ".zip");
            string metadata = Path.Combine(storage, name + ".json");
            File.WriteAllText(metadata, JsonConvert.SerializeObject(new
            {
                reportId = request.Offer.Id, playerUid = uid, playerName = player.PlayerName,
                receivedAtUtc = DateTime.UtcNow, incidentAtUtc = request.Offer.IncidentUtc, disconnectEvidence = request.Offer.Reason,
                serverMods = PlayerStatusLog.CaptureMods(server),
                note = "断开标记用于关联排查，不单独证明客户端崩溃或责任模组。客户端内容由玩家提供，可能截断；详见包内 collection.json。"
            }, Formatting.Indented));
            File.Move(temporary, destination);
            requests.Remove(uid); incidents.Remove(uid); SaveIncidents();
            server!.Logger.Notification("Seraph Civilization Simulation client logs received from {0} ({1}), report {2}: {3}", player.PlayerName, uid, packet.Id, destination);
            Reply(player, packet.Id, "complete", offset, "客户端日志已上传，报告编号：" + packet.Id);
        }
        catch (Exception ex)
        {
            CancelRequest(uid);
            server!.Logger.Warning("Seraph Civilization Simulation client log upload failed for {0}: {1}", uid, ex.Message);
            Reply(player, packet.Id, "error", 0, "日志上传失败，请使用 /clientlogs 重试或联系管理员。");
        }
    }

    private void Reply(IServerPlayer player, string id, string status, int offset, string message = "") =>
        serverChannel!.SendPacket(new ClientLogReply { Id = id, Status = status, Offset = offset, Message = message }, player);

    private void CancelRequest(string uid)
    {
        if (!requests.Remove(uid, out var request)) return;
        request.Incoming?.Dispose();
        string part = Path.Combine(storage, request.Offer.Id + ".part");
        try { if (File.Exists(part)) File.Delete(part); }
        catch (IOException ex) { server?.Logger.Warning("Seraph Civilization Simulation client log cleanup failed: {0}", ex.Message); }
    }

    private void SaveIncidents()
    {
        try
        {
            string path = Path.Combine(storage, "pending.json");
            File.WriteAllText(path + ".tmp", JsonConvert.SerializeObject(incidents));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception ex) { server?.Logger.Error("Seraph Civilization Simulation client log pending state could not be saved: {0}", ex); }
    }

    private void ExpireLogRequests()
    {
        foreach (var entry in requests.ToArray())
        {
            var timeout = entry.Value.Incoming == null ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(60);
            if (DateTime.UtcNow - entry.Value.Touched <= timeout) continue;
            var player = server!.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault(p => p.PlayerUID == entry.Key);
            if (player != null) Reply(player, entry.Value.Offer.Id, "error", 0, "日志请求超时，请使用 /clientlogs 重试。");
            CancelRequest(entry.Key);
        }
    }

    private void CleanupStorage()
    {
        // Reports live in ModData rather than Logs, whose startup rotation deletes old files.
        foreach (var file in Directory.EnumerateFiles(storage))
        {
            string extension = Path.GetExtension(file);
            if (extension == ".part" || ((extension is ".zip" or ".json") && Path.GetFileName(file) != "pending.json"
                && File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-30)))
            {
                try { File.Delete(file); } catch (IOException ex) { server!.Logger.Warning("Seraph Civilization Simulation client log cleanup failed: {0}", ex.Message); }
            }
        }
        foreach (string uid in incidents.Where(p => p.Value.AtUtc < DateTime.UtcNow.AddDays(-30)).Select(p => p.Key).ToArray()) incidents.Remove(uid);
        SaveIncidents();
    }

    public override void Dispose()
    {
        stopping = true;
        if (server != null)
        {
            server.Event.PlayerNowPlaying -= LogPlayerPlaying;
            server.Event.PlayerDisconnect -= LogPlayerDisconnect;
            if (cleanupListener != 0) server.Event.UnregisterGameTickListener(cleanupListener);
        }
        foreach (string uid in requests.Keys.ToArray()) CancelRequest(uid);
        leavePatch?.UnpatchAll("vsscsmod.clientlogs");
        if (ReferenceEquals(serverInstance, this)) serverInstance = null;
        DisposeLogClient();
    }
}
