using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnClientModSystem : ModSystem
{
    private ICoreClientAPI? capi;
    private IClientNetworkChannel? channel;
    private VscnClusterWindow? dialog;
    private readonly Dictionary<int,VscnClusterWindow> windows=[];
    private int selectedWindow;
    private CivilizationSnapshotPacket? lastCivilizationSnapshot;
    private EconomyTradeWindow? tradeDialog;
    private EconomyClientCatalog? economyCatalog;
    private TerritoryRenderer? territoryRenderer;
    private GroundAtlasClient? groundAtlas;
    private ModReportRequest? request;
    private bool ready;
    private CivilizationSnapshotPacket? pendingLoginNotice;
    private int generation;
    private AdminSettingsWindow? adminSettings;
    private GuiDialogMapDrawing? mapDrawing;
    private GuiDialogMapSettings? mapSettings;
    private TerritoryNameHud? territoryNameHud;
    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        GuiDialogMapSettings.Load(api);
        ImmersiveMapCompatibility.OpenDrawingDialog = native =>
        {
            if (!ready || !ImmersiveMapCompatibility.TryGetDrawingMap(native, out var stack, out int slot)) return;
            try { mapDrawing?.TryClose(); mapDrawing?.Dispose(); mapDrawing = new GuiDialogMapDrawing(api, stack!, slot, lastCivilizationSnapshot?.IsLeader == true); }
            catch (Exception ex) { api.Logger.Warning("Map drawing window failed: {0}", ex); api.ShowChatMessage(Lang.Get("vsscsmod:ui.map-draw-invalid")); }
        };
        ImmersiveMapCompatibility.OpenAreaAction = (native, index, action) =>
        {
            ImmersiveMapCompatibility.OpenDrawingDialog?.Invoke(native);
            mapDrawing?.OpenAreaAction(index, action);
        };
        ImmersiveMapCompatibility.OpenMarkerScaleDialog = (_, stack, slot, id) => { if (!ready) return; mapSettings?.TryClose(); mapSettings?.Dispose(); mapSettings = new GuiDialogMapSettings(api, stack, slot, id); };
        ImmersiveMapCompatibility.OpenSettingsDialog = _ => { if (!ready) return; mapSettings?.TryClose(); mapSettings?.Dispose(); mapSettings = new GuiDialogMapSettings(api); };
        economyCatalog = new(api);
        api.Event.TestBlockAccess += OnSleepingBlockAccess;
        territoryRenderer = new TerritoryRenderer(api);
        channel = api.Network.GetChannel(VscnCivilizationModSystem.ChannelName)
            .SetMessageHandler<CivilizationSnapshotPacket>(OnCivilizationSnapshot)
            .SetMessageHandler<EconomySnapshot>(OnEconomySnapshot)
            .SetMessageHandler<EconomyTradeView>(OnEconomyTrade)
            .SetMessageHandler<EconomyCatalogPacket>(packet => (economyCatalog ??= new(api)).Receive(packet))
            .SetMessageHandler<ModReportRequest>(OnRequest)
            .SetMessageHandler<LivesResetPacket>(OnLivesReset)
            .SetMessageHandler<StatusCopyPacket>(OnStatusCopy)
            .SetMessageHandler<GuidancePacket>(OnGuidance);
        channel.SetMessageHandler<TerritoryDiscoveryPacket>(packet => { if (ready && !string.IsNullOrWhiteSpace(packet.Name)) (territoryNameHud ??= new TerritoryNameHud(api)).Show(packet.Name); });
        channel.SetMessageHandler<MapEditResult>(packet => { mapDrawing?.Receive(packet); mapSettings?.Receive(packet); });
        channel.SetMessageHandler<TribeReinforcementPacket>(OnTribeReinforcement);
        channel.SetMessageHandler<ServerAnnouncementPacket>(_ => { });
        channel.SetMessageHandler<AdminSettingsPacket>(packet =>
        {
            if (!packet.Allowed) { adminSettings?.Dispose(); adminSettings=null; if (packet.Message.Length>0) api.ShowChatMessage(packet.Message); return; }
            if (ready) (adminSettings ??= new AdminSettingsWindow(api, request => { if (ready) channel.SendPacket(request); })).Update(packet);
        });
        StartGuidanceClient();
        api.Event.LevelFinalize += OnLevelFinalize;
        api.Event.LeaveWorld += OnLeaveWorld;
        CoinageEconomyCompatibility.OpenClientTrade = uid =>
        { if (ready) channel!.SendPacket(new EconomyRequest { Action = "open-trade", Id = uid }); };
        api.Input.RegisterHotKey("vscncs-trade", Lang.Get("vsscsmod:ui.economy-trade-hotkey"), GlKeys.T,
            HotkeyType.GUIOrOtherControls, ctrlPressed: true);
        api.Input.SetHotKeyHandler("vscncs-trade", _ => OpenSelectedPlayerTrade());
        api.Input.HotKeys.Remove("vscncs-civilization");
        StartCoreClient(); StartRosterClient();
        channel.SetMessageHandler<TribeRosterPacket>(packet => (rosterHud ??= new(api)).Update(packet));
    }

    private void OnLevelFinalize()
    {
        if (capi!.ModLoader.IsModEnabled("immersivemaps")) groundAtlas ??= new(capi);
        economyCatalog ??= new(capi!); economyCatalog.Start();
        ready = true;
        coreHud ??= new CivilizationCoreHud(capi!, OpenCivilizationPage); coreHud.Show();
        territoryRenderer ??= new TerritoryRenderer(capi!);
        if (request != null) SendReport(request);
        if (pendingLoginNotice is { } notice) { pendingLoginNotice = null; OnCivilizationSnapshot(notice); }
        TryShowGuidance();
    }

    private bool OpenSelectedPlayerTrade()
    {
        if (!ready || channel == null || guidance?.IsOpened() == true) return false;
        if (capi!.World.Player.CurrentEntitySelection?.Entity is not EntityPlayer target
            || target.PlayerUID == capi.World.Player.PlayerUID || !target.Alive
            || !capi.World.Player.Entity.Alive
            || target.Pos.Dimension != capi.World.Player.Entity.Pos.Dimension
            || target.Pos.SquareDistanceTo(capi.World.Player.Entity.Pos) > 25)
        { capi.ShowChatMessage(Lang.Get("vsscsmod:ui.economy-trade-target")); return true; }
        channel.SendPacket(new EconomyRequest { Action = "open-trade", Id = target.PlayerUID });
        return true;
    }

    private EnumWorldAccessResponse OnSleepingBlockAccess(IPlayer player, BlockSelection blockSel,
        EnumBlockAccessFlags accessType, ref string claimant, EnumWorldAccessResponse response)
    {
        if (response != EnumWorldAccessResponse.Granted
            || (accessType & EnumBlockAccessFlags.BuildOrBreak) == 0
            || player.Entity?.WatchedAttributes.GetBool(SleepInteractionGuard.EnabledAttribute) != true
            || !SleepInteractionGuard.IsSleeping(player.Entity)) return response;

        // Deny before OnBlockBroken can remove the local bed and unmount the player.
        claimant = SleepInteractionGuard.Claimant;
        return EnumWorldAccessResponse.DeniedByMod;
    }
    private void OnLeaveWorld() { territoryNameHud?.Dispose(); territoryNameHud = null; mapDrawing?.Dispose(); mapDrawing = null; mapSettings?.Dispose(); mapSettings = null; coreMenuKey.Cancel(); coreHud?.Dispose(); coreHud = null; rosterHud?.Dispose(); rosterHud = null; rosterRequestedAt = 0; adminSettings?.Dispose(); adminSettings = null; groundAtlas?.Dispose(); groundAtlas = null; LeaveGuidance(); ready = false; pendingLoginNotice = null; generation++; request = null; foreach (var window in windows.Values) window.Dispose(); windows.Clear(); dialog = null; lastCivilizationSnapshot=null; tradeDialog?.Dispose(); tradeDialog = null; economyCatalog?.Dispose(); economyCatalog = null; territoryRenderer?.Dispose(); territoryRenderer = null; AnnouncementImages.Clear(); }

    private void OnRequest(ModReportRequest packet)
    {
        request = packet;
        if (ready) SendReport(packet);
    }

    private void SendReport(ModReportRequest packet)
    {
        int currentGeneration = ++generation;
        var sources = capi!.ModLoader.Mods.Select(m => (m.Info.ModID, m.Info.Version)).ToList();
        // Keep inventory collection off the event callback and dispatch only for this challenge.
        _ = Task.Run(() => sources.Select(m => new ClientModItem
        {
            ModId = m.ModID, Version = m.Version
        }).ToList()).ContinueWith(task => capi.Event.EnqueueMainThreadTask(() =>
        {
            if (!ready || currentGeneration != generation || request?.Nonce != packet.Nonce) return;
            if (task.IsFaulted) { capi.Logger.Error("Seraph Civilization Simulation failed to hash client mods: {0}", task.Exception?.GetBaseException().Message); return; }
            channel!.SendPacket(new ClientModReportPacket { Nonce = packet.Nonce, Mods = task.Result });
        }, "vscncs-report"));
    }

    private VscnClusterWindow GetWindow(int page)
    {
        if (windows.TryGetValue(page,out var existing)) return existing;
        var window=new VscnClusterWindow(capi!,request =>
        {
            if (request.Action == "territory-preview") territoryRenderer?.SetBoundaryVisibility(true);
            if (request.Action == "territory-clear-preview") territoryRenderer?.SetBoundaryVisibility(false);
            if (ready) channel!.SendPacket(request);
        },page);
        window.SetEconomySender(request => { if (ready) channel!.SendPacket(request); });
        window.SetEconomyCatalog(economyCatalog); windows[page]=window;
        if (lastCivilizationSnapshot!=null)
        {
            var copy=JsonConvert.DeserializeObject<CivilizationSnapshotPacket>(JsonConvert.SerializeObject(lastCivilizationSnapshot))!;
            copy.Open=false; copy.NotificationId=""; copy.Message=""; window.Update(copy);
        }
        return window;
    }
    private void OpenCivilizationPage(int page)
    {
        if (!ready || channel==null || guidance?.IsOpened()==true) return;
        if (page==4)
        {
            OpenNutritionHandbook(); return;
        }
        if (page==5)
        {
            if (!capi!.World.Player.HasPrivilege(Privilege.controlserver)) return;
            foreach (var window in windows.Values) window.Close();
            channel.SendPacket(new AdminSettingsRequest()); return;
        }
        adminSettings?.TryClose();
        selectedWindow=Math.Clamp(page,0,3);
        foreach (var pair in windows.Where(p => p.Key!=selectedWindow)) pair.Value.Close();
        dialog=GetWindow(selectedWindow);
        channel.SendPacket(new CivilizationRequestPacket {Action="open"});
    }

    private void OpenNutritionHandbook()
    {
        try
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("Seraph.SeraphModSystem", throwOnError: false))
                .FirstOrDefault(candidate => candidate is not null);
            if (type is null) return;
            var system = capi!.ModLoader.GetModSystem(type.FullName!);
            if (system is null) return;
            type.GetMethod("OpenNutritionHandbook", BindingFlags.Public | BindingFlags.Instance)?.Invoke(system, null);
        }
        catch (Exception ex)
        {
            capi?.Logger.Warning("[vsscsmod] Could not open the optional nutrition handbook: {0}", ex.GetBaseException().Message);
        }
    }
    private void OnCivilizationSnapshot(CivilizationSnapshotPacket packet)
    {
        if (capi == null) return;
        rosterRequestedAt = 0;
        if (rosterTribeId != packet.MyTribeId) { rosterTribeId = packet.MyTribeId; rosterHud?.Update(new TribeRosterPacket()); }
        if (!ready && packet.NotificationId.Length > 0) { pendingLoginNotice = packet; return; }
        territoryRenderer?.Update(packet);
        try
        {
            lastCivilizationSnapshot=packet;
            if (!string.IsNullOrWhiteSpace(packet.Message)) capi.ShowChatMessage(packet.Message);
            if (packet.NotificationId.Length>0) selectedWindow=VscnClusterWindow.AnnouncementPage;
            if (packet.Open || packet.NotificationId.Length>0)
            {
                foreach (var pair in windows.Where(p => p.Key!=selectedWindow)) pair.Value.Close();
                dialog=GetWindow(selectedWindow);
            }
            foreach (var pair in windows)
            {
                var copy=JsonConvert.DeserializeObject<CivilizationSnapshotPacket>(JsonConvert.SerializeObject(packet))!;
                copy.Open=(packet.Open || packet.NotificationId.Length>0) && pair.Key==selectedWindow;
                if (pair.Key!=VscnClusterWindow.AnnouncementPage) copy.NotificationId="";
                pair.Value.Update(copy);
            }
        }
        catch (Exception ex)
        {
            capi.Logger.Error("Seraph Civilization Simulation cluster window failed: {0}", ex);
        }
    }

    private void OnEconomySnapshot(EconomySnapshot packet)
    {
        if (capi == null || !ready) return;
        try
        {
            GetWindow(VscnClusterWindow.EconomyPage).UpdateEconomy(packet);
        }
        catch (Exception ex) { capi.Logger.Error("Seraph Civilization Simulation economy window failed: {0}", ex); }
    }
    private void OnEconomyTrade(EconomyTradeView packet)
    {
        if (capi == null || !ready) return;
        try
        {
            if (packet.Closed) { tradeDialog?.Update(packet); if (packet.Message.Length > 0) capi.ShowChatMessage(packet.Message); return; }
            dialog?.Close();
            tradeDialog ??= new EconomyTradeWindow(capi, request => { if (ready) channel!.SendPacket(request); });
            tradeDialog.Update(packet);
        }
        catch (Exception ex) { capi.Logger.Error("Seraph Civilization Simulation trade window failed: {0}", ex); }
    }

    private void OnStatusCopy(StatusCopyPacket packet)
    {
        if (capi == null) return;
        try
        {
            if (!Guid.TryParse(packet.ReportId, out var reportId)) throw new InvalidDataException("状态报告编号无效。");
            var report = JObject.Parse(packet.Report);
            report["clientCapturedAtUtc"] = DateTimeOffset.UtcNow.ToString("O");
            report["clientPlayer"] = PlayerStatusLog.CapturePlayer(capi.World.Player);
            report["clientLoadedMods"] = PlayerStatusLog.CaptureMods(capi);
            string file = PlayerStatusLog.Write(reportId, "client", report.ToString(Formatting.Indented));
            capi.ShowChatMessage("你的状态日志已保存: " + file);
        }
        catch (Exception ex)
        {
            capi.Logger.Error("Seraph Civilization Simulation statuscopy client log failed: {0}", ex);
            capi.ShowChatMessage("状态日志保存失败: " + ex.Message);
        }
    }

    private void OnLivesReset(LivesResetPacket packet)
    {
        if (capi?.World.Player.Entity.Alive != false) return;
        // The engine death dialog caches livesLeft. Update the open dialog after
        // the server resets Deaths so its respawn button reappears immediately.
        foreach (var gui in capi.Gui.LoadedGuis.Where(g => g.GetType().Name == "GuiDialogDead" && g.IsOpened()))
        {
            var type = gui.GetType();
            type.GetField("livesLeft", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(gui, packet.LivesLeft);
            type.GetField("respawning", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(gui, false);
            type.GetMethod("ComposeDialog", BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(gui, null);
        }
    }

    public override void Dispose()
    {
        coreMenuKey.Cancel();
        if (capi != null) capi.Event.KeyUp -= OnCoreMenuKeyUp;
        if (rosterListener != 0) capi?.Event.UnregisterGameTickListener(rosterListener);
        rosterListener = 0; if (rosterPreviousChatMapping != null && capi?.Input.GetHotKeyByCode("chatdialog") is { } chat)
            chat.CurrentMapping = rosterPreviousChatMapping;
        if (coreListener != 0) capi?.Event.UnregisterGameTickListener(coreListener);
        coreListener = 0;
        if (reinforcementListener != 0) capi?.Event.UnregisterGameTickListener(reinforcementListener);
        reinforcementListener = 0;
        tribeReinforcement?.Dispose(); tribeReinforcement = null;
        guidanceHarmony?.UnpatchAll("vsscsmod.guidance.client");
        guidanceHarmony = null; guidanceClient = null;
        CoinageEconomyCompatibility.OpenClientTrade = null;
        ImmersiveMapCompatibility.OpenDrawingDialog = null;
        ImmersiveMapCompatibility.OpenSettingsDialog = null; ImmersiveMapCompatibility.OpenMarkerScaleDialog = null;
        mapDrawing?.Dispose(); mapDrawing = null;
        mapSettings?.Dispose(); mapSettings = null;
        if (capi != null)
        {
            capi.Event.TestBlockAccess -= OnSleepingBlockAccess;
            capi.Event.PauseResume -= OnGuidancePauseResume;
        }
        OnLeaveWorld();
    }
}
