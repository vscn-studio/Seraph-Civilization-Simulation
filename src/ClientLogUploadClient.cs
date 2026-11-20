using Newtonsoft.Json;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;

namespace SeraphCivilizationSimulation;

public sealed partial class ClientLogUploadSystem
{
    private ICoreClientAPI? client;
    private IClientNetworkChannel? clientChannel;
    private ClientLogOffer? clientOffer;
    private ClientLogFiles.Snapshot? clientSnapshot;
    private ClientLogUploadDialog? logDialog;
    private bool clientReady, collecting, uploading;
    private int clientGeneration, expectedOffset, lastProgress;
    private DateTime clientTouched;
    private long clientListener;
    private Harmony? clientLeavePatch;

    public override void StartClientSide(ICoreClientAPI api)
    {
        client = api;
        clientChannel = api.Network.GetChannel(Channel)
            .SetMessageHandler<ClientLogOffer>(ReceiveLogOffer).SetMessageHandler<ClientLogReply>(ReceiveLogReply);
        clientLeavePatch = new Harmony("vsscsmod.clientlogs.client");
        clientLeavePatch.Patch(AccessTools.Method(typeof(ClientMain), nameof(ClientMain.SendLeave)),
            prefix: new HarmonyMethod(typeof(ClientLogUploadSystem), nameof(BeforeClientLogLeave)));
        api.Event.LevelFinalize += LogClientReady;
        api.Event.LeaveWorld += LogClientLeave;
        clientListener = api.Event.RegisterGameTickListener(_ =>
        {
            if (uploading && DateTime.UtcNow - clientTouched > TimeSpan.FromSeconds(75))
            {
                client.ShowChatMessage("日志上传超时，请使用 /clientlogs 重试。");
                EndClientRequest("cancel");
            }
        }, 1000);
    }

    private static void BeforeClientLogLeave(ClientMain __instance, ref int reason)
    {
        // The engine's worker-thread crash path sends a normal leave (0), unlike
        // its main crash handler (1). Preserve this evidence for the server.
        if (reason == 0 && __instance.KillNextFrame) reason = 1;
    }

    private void LogClientReady() { clientReady = true; PrepareLogOffer(); }

    private void ReceiveLogOffer(ClientLogOffer packet)
    {
        if (!Guid.TryParseExact(packet.Id, "N", out _) || packet.IncidentUtc == null || packet.IncidentUtc.Length > 64
            || clientOffer?.Id == packet.Id) return;
        EndClientRequest("cancel");
        clientOffer = packet;
        PrepareLogOffer();
    }

    private void PrepareLogOffer()
    {
        if (!clientReady || client == null || clientOffer == null || collecting || uploading || clientSnapshot != null) return;
        collecting = true;
        int generation = clientGeneration;
        var offer = clientOffer;
        var api = client;
        string logRoot = GamePaths.Logs;
        string context = JsonConvert.SerializeObject(new
        {
            capturedAtUtc = DateTime.UtcNow, incidentAtUtc = offer.IncidentUtc, reportId = offer.Id,
            gameVersion = GameVersion.ShortGameVersion, clientMods = PlayerStatusLog.CaptureMods(api),
            note = "Previous is the most recent archived client session, not proof of incident ownership. Crash logs may predate this incident. See collection.json for timestamps and byte ranges."
        }, Formatting.Indented);
        _ = Task.Run(() =>
        {
            ClientLogFiles.Snapshot? snapshot = null;
            string? error = null;
            try { snapshot = ClientLogFiles.Capture(logRoot, context); }
            catch (Exception ex) { error = ex.Message; }
            api.Event.EnqueueMainThreadTask(() =>
            {
                if (!clientReady || generation != clientGeneration || clientOffer?.Id != offer.Id) return;
                collecting = false;
                if (snapshot == null)
                {
                    api.ShowChatMessage("无法准备客户端日志：" + error);
                    EndClientRequest("cancel");
                    return;
                }
                clientSnapshot = snapshot;
                logDialog?.Dispose();
                logDialog = new ClientLogUploadDialog(api, offer, snapshot, accepted =>
                {
                    if (clientOffer?.Id != offer.Id || !clientReady) return;
                    if (!accepted) { EndClientRequest("decline"); return; }
                    uploading = true; clientTouched = DateTime.UtcNow; expectedOffset = 0; lastProgress = -1;
                    clientChannel!.SendPacket(new ClientLogUpload { Id = offer.Id, Action = "begin", Length = snapshot.Archive.Length });
                    api.ShowChatMessage("正在上传客户端日志，请保持连接……");
                });
                if (!logDialog.TryOpen())
                {
                    api.ShowChatMessage("无法打开日志上传窗口，请使用 /clientlogs 重试。");
                    EndClientRequest("cancel");
                }
            }, "vscncs-clientlogs-prepare");
        });
    }

    private void ReceiveLogReply(ClientLogReply packet)
    {
        if (clientOffer?.Id != packet.Id) return;
        if (packet.Status is "error" or "complete")
        {
            client?.ShowChatMessage(packet.Message);
            EndClientRequest(null);
            return;
        }
        if (!uploading || clientSnapshot == null || packet.Status != "next" || packet.Offset != expectedOffset
            || packet.Offset < 0 || packet.Offset >= clientSnapshot.Archive.Length) return;
        clientTouched = DateTime.UtcNow;
        int count = Math.Min(ClientLogFiles.ChunkBytes, clientSnapshot.Archive.Length - packet.Offset);
        byte[] data = clientSnapshot.Archive.AsSpan(packet.Offset, count).ToArray();
        expectedOffset = packet.Offset + count;
        // Limit to at most one 24 KiB chunk per 50 ms, as well as waiting for each ACK.
        int generation = clientGeneration;
        client!.Event.RegisterCallback(_ =>
        {
            if (!clientReady || generation != clientGeneration || !uploading || clientOffer?.Id != packet.Id) return;
            clientChannel!.SendPacket(new ClientLogUpload { Id = packet.Id, Action = "chunk", Offset = packet.Offset, Data = data });
            int progress = packet.Offset * 4 / clientSnapshot!.Archive.Length;
            if (progress > lastProgress) { lastProgress = progress; client.ShowChatMessage($"客户端日志上传：{progress * 25}%"); }
        }, 50);
    }

    private void EndClientRequest(string? action)
    {
        var offer = clientOffer;
        clientOffer = null; clientSnapshot = null; collecting = uploading = false; clientGeneration++;
        logDialog?.TryClose();
        if (offer != null && action != null && clientReady)
            clientChannel?.SendPacket(new ClientLogUpload { Id = offer.Id, Action = action });
    }

    private void LogClientLeave()
    {
        clientReady = false;
        EndClientRequest(null);
        logDialog?.Dispose(); logDialog = null;
    }

    private void DisposeLogClient()
    {
        clientLeavePatch?.UnpatchAll("vsscsmod.clientlogs.client");
        clientLeavePatch = null;
        if (client != null)
        {
            client.Event.LevelFinalize -= LogClientReady;
            client.Event.LeaveWorld -= LogClientLeave;
            if (clientListener != 0) client.Event.UnregisterGameTickListener(clientListener);
        }
        LogClientLeave();
        client = null;
    }
}

internal sealed class ClientLogUploadDialog : GuiDialog
{
    private readonly Action<bool> decide;
    private bool decided;
    public override string ToggleKeyCombinationCode => null!;
    public override bool UnregisterOnClose => true;

    internal ClientLogUploadDialog(ICoreClientAPI api, ClientLogOffer offer, ClientLogFiles.Snapshot snapshot, Action<bool> decide) : base(api)
    {
        this.decide = decide;
        string L(string key, params object[] args) => Lang.Get("vsscsmod:clientlogs." + key, args);
        string incident = DateTimeOffset.TryParse(offer.IncidentUtc, out var at) ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") : offer.IncidentUtc;
        string text = L("prompt") + "\n\n" + L("incident", incident) + "\n" + L("files") + "\n"
            + string.Join("\n", snapshot.Files) + "\n\n" + L("details", (snapshot.Archive.Length / 1024d).ToString("F0"));
        double scale = Math.Max(.25, RuntimeEnv.GUIScale);
        double width = Math.Clamp(api.Render.FrameWidth / scale - 60, 280, 620);
        var font = CairoFont.WhiteSmallText();
        double textHeight = api.Gui.Text.GetMultilineTextHeight(font, text, (width - 64) * scale) / scale + 8;
        double height = Math.Clamp(textHeight + 116, 200, Math.Max(200, api.Render.FrameHeight / scale - 50));
        var bounds = ElementBounds.Fixed(0, 0, width, height).WithAlignment(EnumDialogArea.CenterMiddle);
        var clip = ElementBounds.Fixed(20, 46, width - 64, height - 116);
        var body = ElementBounds.Fixed(0, 0, clip.fixedWidth, Math.Max(textHeight, clip.fixedHeight));
        var composer = api.Gui.CreateCompo("vscncs-clientlogs-confirm", bounds)
            .AddStaticCustomDraw(ElementBounds.Fill, (ctx, _, b) =>
            {
                ctx.SetSourceRGB(GuiStyle.DialogStrongBgColor[0], GuiStyle.DialogStrongBgColor[1], GuiStyle.DialogStrongBgColor[2]);
                GuiElement.RoundRectangle(ctx, b.drawX, b.drawY, b.InnerWidth, b.InnerHeight, GuiStyle.DialogBGRadius); ctx.Fill();
            })
            .AddShadedDialogBG(ElementBounds.Fill, alpha: 1)
            .AddDialogTitleBar(L("title"), () => TryClose());
        composer.BeginClip(clip).AddContainer(body, "log-content");
        composer.GetContainer("log-content").Add(new GuiElementWrappedText(api, text,
            ElementBounds.Fixed(0, 0, clip.fixedWidth, textHeight), font));
        composer.EndClip();
        var bar = new GuiElementAutoScrollbar(api, value => { body.fixedY = -value / scale; body.CalcWorldBounds(); },
            ElementBounds.Fixed(width - 36, 46, 16, clip.fixedHeight));
        composer.AddInteractiveElement(bar, "log-scrollbar")
            .AddSmallButton(L("upload"), () => Choose(true), ElementBounds.Fixed(20, height - 50, (width - 50) / 2, 30), key: "upload")
            .AddSmallButton(L("decline"), () => Choose(false), ElementBounds.Fixed(30 + (width - 50) / 2, height - 50, (width - 50) / 2, 30), key: "decline");
        SingleComposer = composer.Compose();
        bar.SetContentHeights((float)clip.InnerHeight, (float)body.OuterHeight);
    }

    private bool Choose(bool accepted)
    {
        if (decided) return true;
        decided = true; TryClose(); decide(accepted); return true;
    }

    public override void OnGuiClosed()
    {
        base.OnGuiClosed();
        if (!decided) { decided = true; decide(false); }
    }
}
