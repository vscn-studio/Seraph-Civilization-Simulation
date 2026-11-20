using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;
using SeraphCivilizationSimulation;

internal static class EntryInteractionChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var client = new VscnClientModSystem();
        void Set(string name, object? value) => AccessTools.Field(typeof(VscnClientModSystem), name).SetValue(client, value);
        object? Call(string name, params object[] args) => AccessTools.Method(typeof(VscnClientModSystem), name).Invoke(client, args);
        var entity = new EntityPlayer(); entity.WatchedAttributes.SetString("playerUID", "self");
        var target = new EntityPlayer(); target.WatchedAttributes.SetString("playerUID", "target");
        EntitySelection? selection = null; bool paused = false;
        var player = Proxy.Make<IClientPlayer>((m, _) => m.Name switch
        { "get_PlayerUID" => "self", "get_Entity" => entity, "get_CurrentEntitySelection" => selection, _ => null });
        var world = Proxy.Make<IClientWorldAccessor>((m, _) => m.Name == "get_Player" ? player : null);
        var events = new List<string>();
        var eventApi = Proxy.Make<IClientEventAPI>((m, a) =>
        { if (m.Name == "PushEvent") events.Add((string)a![0]!); return null; });
        int guiReads = 0, receivedLogs = 0;
        var uninitializedHud = (GuiDialog)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            typeof(Vintagestory.Client.NoObf.HudMouseTools));
        var gui = Proxy.Make<IGuiAPI>((m, _) =>
        {
            if (m.Name != "get_LoadedGuis") return null;
            guiReads++;
            return new List<GuiDialog> { uninitializedHud };
        });
        var logger = Proxy.Make<ILogger>((m, a) =>
        {
            if (m.Name == "Notification" && a?[0] is string message && message.Contains("guidance received")) receivedLogs++;
            return null;
        });
        var api = Proxy.Make<ICoreClientAPI>((m, _) => m.Name switch
        { "get_World" => world, "get_Event" => eventApi, "get_IsGamePaused" => paused, "get_Gui" => gui, "get_Logger" => logger, _ => null });
        var sent = new List<EconomyRequest>();
        var channel = Proxy.Make<IClientNetworkChannel>((m, a) =>
        { if (m.Name == "SendPacket" && a?[0] is EconomyRequest request) sent.Add(request); return null; });
        try
        {
            // Install the real patch before providing the minimal API fixture.
            Call("StartGuidanceClient"); Set("capi", api); Set("channel", channel);
            var earlyRequired = new GuidancePacket { Required = true, LivesLeft = 3 };
            Call("OnGuidance", earlyRequired); Call("TryShowGuidance");
            check(ReferenceEquals(AccessTools.Field(typeof(VscnClientModSystem), "pendingGuidance").GetValue(client), earlyRequired)
                && AccessTools.Field(typeof(VscnClientModSystem), "guidance").GetValue(client) == null,
                "Guidance received on the connection screen must remain queued without opening a dialog.");
            var earlyAccepted = new GuidancePacket { Required = false, LivesLeft = 2 };
            Call("OnGuidance", earlyAccepted); Call("TryShowGuidance");
            check(ReferenceEquals(AccessTools.Field(typeof(VscnClientModSystem), "pendingGuidance").GetValue(client), earlyAccepted)
                && (bool)AccessTools.Field(typeof(VscnClientModSystem), "guidanceAccepted").GetValue(client)!,
                "An accepted name received before level initialization must preserve its pending life update.");
            check(guiReads == 0 && receivedLogs == 2,
                "Early guidance logging must not inspect uninitialized native HUDs for either packet type.");
            Call("LeaveGuidance"); Set("ready", true);
            var characters = new CharacterSystem();
            AccessTools.Field(typeof(CharacterSystem), "capi").SetValue(characters, api);
            AccessTools.Field(typeof(CharacterSystem), "didSelect").SetValue(characters, true);
            var join = AccessTools.Method(typeof(CharacterSystem), "Event_PlayerJoin");
            join.Invoke(characters, [player]);
            check(events.Count == 0 && AccessTools.Field(typeof(VscnClientModSystem), "deferredCharacterJoin").GetValue(client) != null,
                "Native character startup must be deferred while name status is unknown.");
            Set("guidanceAccepted", true); paused = true; Call("ResumeCharacterJoin");
            check(events.Count == 0, "Character startup must wait until the pause menu closes.");
            paused = false; Call("ResumeCharacterJoin"); Call("ResumeCharacterJoin");
            check(events.SequenceEqual(new[] { "skipcharacterselection" }),
                "Confirmed names must resume the native join callback exactly once.");
            Call("LeaveGuidance");
            AccessTools.Field(typeof(CharacterSystem), "didSelect").SetValue(characters, false);
            join.Invoke(characters, [player]);
            check(AccessTools.Field(typeof(CharacterSystem), "createCharDlg").GetValue(characters) == null,
                "A new character must not be constructed or pause the world before naming.");
            Call("LeaveGuidance");
            check(AccessTools.Field(typeof(VscnClientModSystem), "deferredCharacterJoin").GetValue(client) == null,
                "Leaving must discard a deferred character callback.");
            Call("OpenSelectedPlayerTrade"); check(sent.Count == 0, "No target must not send a trade request.");
            selection = new EntitySelection { Entity = target };
            Call("OpenSelectedPlayerTrade");
            check(sent is [{ Action: "open-trade", Id: "target" }], "An empty-handed player must trade with the selected player.");
            target.Pos.X = 6; Call("OpenSelectedPlayerTrade");
            target.Pos.X = 0; target.Pos.Dimension = 1; Call("OpenSelectedPlayerTrade");
            target.Pos.Dimension = 0; target.Alive = false; Call("OpenSelectedPlayerTrade");
            selection.Entity = entity; Call("OpenSelectedPlayerTrade");
            selection.Entity = new EntityAgent(); Call("OpenSelectedPlayerTrade");
            check(sent.Count == 1, "Distant, cross-dimension, dead, self and non-player targets must not send requests.");
            selection.Entity = target; target.Alive = true; Set("ready", false); Call("OpenSelectedPlayerTrade");
            check(sent.Count == 1, "Trade input before level initialization must be ignored.");
        }
        finally { Set("capi", null); client.Dispose(); }
        check(Harmony.GetPatchInfo(AccessTools.Method(typeof(CharacterSystem), "Event_PlayerJoin"))?.Owners.Contains("vsscsmod.guidance.client") != true,
            "Disposal must remove the character startup patch.");
    }
}
