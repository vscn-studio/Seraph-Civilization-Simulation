using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace SeraphCivilizationSimulation;

[ProtoContract]
public sealed class AdminSettingsRequest
{
    [ProtoMember(1)] public string Action { get; set; } = "open";
    [ProtoMember(2)] public string Json { get; set; } = "";
    [ProtoMember(3)] public string Revision { get; set; } = "";
}

[ProtoContract]
public sealed class AdminSettingsPacket
{
    [ProtoMember(1)] public bool Allowed { get; set; }
    [ProtoMember(2)] public string Json { get; set; } = "";
    [ProtoMember(3)] public string Revision { get; set; } = "";
    [ProtoMember(4)] public string Message { get; set; } = "";
    [ProtoMember(5)] public bool ReplaceDraft { get; set; } = true;
}

internal static class AdminSettingsData
{
    internal static JObject Document(VscnConfig main, LoreRestrictionsConfig lore)
        => new() { ["Main"] = JObject.FromObject(main), ["Lore"] = JObject.FromObject(lore) };
    internal static string Revision(JObject doc) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(doc.ToString(Formatting.None))));

    internal static (VscnConfig Main, LoreRestrictionsConfig Lore) Parse(string json)
    {
        if (json.Length > 1_048_576) throw new ArgumentException("配置内容过长。");
        var document = JObject.Parse(json);
        if (document.Count != 2 || document["Main"] is not JObject main || document["Lore"] is not JObject lore)
            throw new ArgumentException("需要完整的主配置和传说限制配置。");
        var serializer = JsonSerializer.Create(new JsonSerializerSettings { MissingMemberHandling=MissingMemberHandling.Error });
        var settings = main.ToObject<VscnConfig>(serializer) ?? throw new ArgumentException("主配置不能为空。");
        settings.Validate();
        return (settings, LoreRestrictionSystem.ParseConfig(lore));
    }
}

public sealed partial class VscnCivilizationModSystem
{
    private void OnAdminSettings(IServerPlayer player, AdminSettingsRequest request)
    {
        if (disposed) return;
        // Check every operation, including reads; never broadcast configuration snapshots.
        if (!player.HasPrivilege(Privilege.controlserver))
        { channel!.SendPacket(new AdminSettingsPacket { Allowed=false, Message="仅管理员可以查看和修改配置。" },player); return; }
        try
        {
            var lore = ConfigMigration.LoadLore(sapi!);
            var current = AdminSettingsData.Document(config,lore);
            string revision = AdminSettingsData.Revision(current), message = "";
            bool defaults = false;
            switch (request.Action)
            {
                case "open": case "refresh": break;
                case "defaults": defaults=true; message="已载入默认值，点击“保存并应用”后生效。"; break;
                case "save":
                    if (request.Revision != revision) throw new ArgumentException("配置已被其他管理员更改，请刷新后重新编辑。");
                    var next = AdminSettingsData.Parse(request.Json);
                    var previous = config;
                    // Compensate the first file write if the second file cannot be saved.
                    sapi!.StoreModConfig(next.Main,ConfigFile);
                    try { sapi.StoreModConfig(next.Lore,ConfigMigration.Lore); }
                    catch { sapi.StoreModConfig(previous,ConfigFile); throw; }
                    try { ApplyConfiguration(next.Main); }
                    catch
                    {
                        sapi.StoreModConfig(previous,ConfigFile); sapi.StoreModConfig(lore,ConfigMigration.Lore); throw;
                    }
                    message="配置已保存并应用。";
                    if (!JToken.DeepEquals(JObject.FromObject(lore),JObject.FromObject(next.Lore))) message+="传说限制需重启服务器后生效。";
                    if (previous.DisableVintageCanvasArtistClass!=next.Main.DisableVintageCanvasArtistClass) message+="画家职业限制需重启后生效。";
                    lore=next.Lore;
                    break;
                case "reload": ApplyConfiguration(ReadConfig(false)); message="已从配置文件热重载。传说限制和画家职业限制需重启后生效。"; break;
                default: throw new ArgumentException("未知配置操作。");
            }
            current=AdminSettingsData.Document(config,lore);
            channel!.SendPacket(new AdminSettingsPacket { Allowed=true, Revision=AdminSettingsData.Revision(current),
                Json=(defaults?AdminSettingsData.Document(new(),new()):current).ToString(Formatting.None), Message=message },player);
        }
        catch (Exception ex)
        {
            sapi!.Logger.Warning("Seraph Civilization Simulation administrator configuration failed: {0}",ex);
            channel!.SendPacket(new AdminSettingsPacket { Allowed=true, ReplaceDraft=false, Message="配置操作失败："+ex.Message },player);
        }
    }

    private void ApplyConfiguration(VscnConfig next)
    {
        next.Validate();
        var previous=config;
        config=next;
        try { RefreshServerRules(); } catch { config=previous; throw; }
        // Configuration and rules are committed. A failed client refresh must not undo files
        // while leaving the server running the new configuration.
        try { RefreshConfigurationClients(); }
        catch (Exception ex) { sapi!.Logger.Warning("Configuration applied, but refreshing clients failed: {0}", ex); }
    }

    private void RefreshConfigurationClients()
    {
        SyncSleepInteractionSettings();
        foreach (var onlinePlayer in Online) SendGuidance(onlinePlayer);
        if (config.Clusters.Enabled && !clustersReady) InitializeClusters();
        foreach (var player in sapi!.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            if (config.ModPolicy.Enabled) RequestReport(player);
            else { pendingReports.Clear(); verifiedPlayers.Add(player.PlayerUID); HandleVerifiedPlayer(player); }
        }
        RefreshCivilizationViews();
    }
}
