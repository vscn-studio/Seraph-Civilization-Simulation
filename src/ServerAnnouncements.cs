using System.Reflection;
using HarmonyLib;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace SeraphCivilizationSimulation;

[ProtoContract]
public sealed class ServerAnnouncementPacket
{
    [ProtoMember(1)] public string Html { get; set; } = "";
}

// Decorate existing handlers after vanilla registration. Execute still owns
// privilege checks and parsing; the original handler keeps its chat broadcast.
internal sealed class ServerAnnouncementRelay : IDisposable
{
    private readonly List<(IChatCommand Command, FieldInfo Field, OnCommandDelegate Original, OnCommandDelegate Wrapper)> hooks = [];

    internal void Install(IChatCommandApi commands, Action<TextCommandCallingArgs, string, bool> publish)
    {
        Hook("announce", 0, false);
        Hook("announcenear", 1, true);
        void Hook(string name, int messageIndex, bool nearby)
        {
            var command = commands.Get(name);
            if (command == null) return;
            var field = AccessTools.Field(command.GetType(), "handler");
            if (field?.GetValue(command) is not OnCommandDelegate original)
                throw new InvalidOperationException($"Cannot attach server notice to /{name}.");
            var wrapper = Wrap(original, messageIndex, (args, html) => publish(args, html, nearby));
            command.HandleWith(wrapper);
            hooks.Add((command, field, original, wrapper));
        }
    }

    internal static OnCommandDelegate Wrap(OnCommandDelegate original, int messageIndex, Action<TextCommandCallingArgs, string> publish)
        => args =>
        {
            string source = args[messageIndex] as string ?? "";
            if (string.IsNullOrWhiteSpace(source) || source.Length > HtmlDocumentApi.MaxSourceLength)
                return TextCommandResult.Error($"公告不能为空，HTML 源码最多 {HtmlDocumentApi.MaxSourceLength} 个字符。");
            var result = original(args);
            // Send source HTML unchanged; the client reader sanitizes and converts it once.
            // Preparing here would discard table/details controls on the second conversion.
            if (result.Status == EnumCommandStatus.Success) publish(args, source);
            return result;
        };

    public void Dispose()
    {
        foreach (var hook in hooks)
            if (ReferenceEquals(hook.Field.GetValue(hook.Command), hook.Wrapper)) hook.Command.HandleWith(hook.Original);
        hooks.Clear();
    }
}

public sealed partial class VscnCivilizationModSystem
{
    private ServerAnnouncementRelay? announcementRelay;

    private void StartAnnouncementRelay()
    {
        announcementRelay?.Dispose();
        announcementRelay = new();
        announcementRelay.Install(sapi!.ChatCommands, (args, html, nearby) =>
        {
            var packet = new ServerAnnouncementPacket { Html = html };
            if (!nearby) { channel!.BroadcastPacket(packet); return; }
            int range = (int)args[0];
            foreach (var player in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
                if (args.Caller.Pos.DistanceTo(player.Entity.Pos.XYZ) <= range) channel!.SendPacket(packet, player);
        });
    }
}
