using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnCivilizationModSystem
{
    private readonly HashSet<string> activeTechnologyMods = new(StringComparer.OrdinalIgnoreCase);

    private bool TechnologyAvailable(TechnologyNode node) => node.Mod.Length == 0 || activeTechnologyMods.Contains(node.Mod);

    private void InitializeTechnologyMods()
    {
        if (activeTechnologyMods.Count != 0 || sapi == null) return;
        foreach (var mod in sapi.ModLoader.Mods)
            if (mod.Info?.ModID is { } id) activeTechnologyMods.Add(id);
    }

    private void ScanOnlineTechnology()
    {
        if (disposed || sapi == null) return;
        InitializeTechnologyMods();
        foreach (var player in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
            if (readyPlayers.Contains(player.PlayerUID) && ScanTechnology(player, announce: true))
                SendCivilizationSnapshot(player, false);
    }

    private void OnTechnologyBlockPlaced(IServerPlayer player, int blockId, BlockSelection selection, ItemStack stack)
        => RecordTechnologyAction(player, blockId, "place:");

    private void OnTechnologyBlockBroken(IServerPlayer player, int blockId, BlockSelection selection)
        => RecordTechnologyAction(player, blockId, "break:");

    private void RecordTechnologyAction(IServerPlayer player, int blockId, string verb)
    {
        if (sapi == null || !readyPlayers.Contains(player.PlayerUID) || blockId < 0 || blockId >= sapi.World.Blocks.Count) return;
        var code = sapi.World.Blocks[blockId]?.Code;
        if (code == null) return;
        string action = verb + code;
        if (!TechnologyCatalog.Nodes.Where(TechnologyAvailable).SelectMany(n => n.Actions ?? [])
            .SelectMany(group => group.Split('|')).Any(pattern => TechnologyCatalog.Matches(pattern, action))) return;
        if (!technologyState.ActionsByPlayer.TryGetValue(player.PlayerUID, out var actions))
            technologyState.ActionsByPlayer[player.PlayerUID] = actions = new(StringComparer.OrdinalIgnoreCase);
        if (!actions.Add(action)) return;
        SaveState();
        if (ScanTechnology(player, announce: true)) SendCivilizationSnapshot(player, false);
    }

    private bool ScanTechnology(IServerPlayer player, bool announce)
    {
        if (player.Entity == null) return false;
        InitializeTechnologyMods();
        string uid = player.PlayerUID;
        if (!technologyState.ObservedByPlayer.TryGetValue(uid, out var observed))
            technologyState.ObservedByPlayer[uid] = observed = new(StringComparer.OrdinalIgnoreCase);
        if (!technologyState.CompletedByPlayer.TryGetValue(uid, out var personal))
            technologyState.CompletedByPlayer[uid] = personal = new(StringComparer.OrdinalIgnoreCase);
        if (!technologyState.AnnouncedByPlayer.TryGetValue(uid, out var announced))
            technologyState.AnnouncedByPlayer[uid] = announced = new(StringComparer.OrdinalIgnoreCase);
        var actions = technologyState.ActionsByPlayer.GetValueOrDefault(uid) ?? [];

        bool changed = false;
        foreach (string name in new[] { "hotbar", "backpack", "character" })
        {
            var inventory = player.InventoryManager.GetOwnInventory(name);
            if (inventory == null) continue;
            for (int i = 0; i < inventory.Count; i++)
                if (inventory[i].Itemstack?.Collectible?.Code is { } code)
                    changed |= observed.Add(code.ToString());
        }
        var tribe = tribeState.FindMember(uid);
        HashSet<string>? shared = null;
        if (tribe != null && !technologyState.CompletedByTribe.TryGetValue(tribe.Id, out shared))
            technologyState.CompletedByTribe[tribe.Id] = shared = new(StringComparer.OrdinalIgnoreCase);

        bool advanced;
        do
        {
            advanced = false;
            foreach (var node in TechnologyCatalog.Nodes)
            {
                if (!TechnologyAvailable(node)) continue;
                bool discovered = node.Discover.Any(pattern => observed.Any(code => TechnologyCatalog.Matches(pattern, code)));
                if (discovered && announced.Add(node.Id))
                {
                    changed = true;
                    if (announce) player.SendMessage(GlobalConstants.GeneralChatGroup,
                        $"科技发现：{node.Title}。打开文明模拟 > 科技树查看过程。", EnumChatType.Notification);
                }
                if (personal.Contains(node.Id) || shared?.Contains(node.Id) == true) continue;
                var completed = shared == null ? personal : personal.Union(shared).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!TechnologyCatalog.CanComplete(node, observed, completed, actions)) continue;
                personal.Add(node.Id);
                shared?.Add(node.Id);
                advanced = changed = true;
                if (announce) player.SendMessage(GlobalConstants.GeneralChatGroup,
                    $"科技完成：{node.Title}", EnumChatType.Notification);
            }
        } while (advanced);
        if (changed) SaveState();
        return changed;
    }

    private List<TechnologyNodeStatus> BuildTechnologyView(IServerPlayer player)
    {
        var observed = technologyState.ObservedByPlayer.GetValueOrDefault(player.PlayerUID) ?? [];
        var actions = technologyState.ActionsByPlayer.GetValueOrDefault(player.PlayerUID) ?? [];
        var personal = technologyState.CompletedByPlayer.GetValueOrDefault(player.PlayerUID) ?? [];
        var tribe = tribeState.FindMember(player.PlayerUID);
        var shared = tribe == null ? null : technologyState.CompletedByTribe.GetValueOrDefault(tribe.Id);
        return TechnologyCatalog.Nodes.Where(TechnologyAvailable).Select(node => new TechnologyNodeStatus
        {
            Id = node.Id,
            Discovered = node.Discover.Any(pattern => observed.Any(code => TechnologyCatalog.Matches(pattern, code))),
            Complete = personal.Contains(node.Id) || shared?.Contains(node.Id) == true,
            OutputReady = TechnologyCatalog.OutputsMet(node, observed),
            ActionsReady = TechnologyCatalog.ActionsMet(node, actions)
        }).ToList();
    }
}
