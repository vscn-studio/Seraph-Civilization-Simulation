using System.Globalization;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnCivilizationModSystem
{
    private readonly Dictionary<long, string> coinDesignAuthors = [];

    private string DesignDesigner(long design, string legacyOwner = "")
    {
        // Authenticated creation records follow player renames, regardless of tribe or minter.
        if (economy.DesignOwners.TryGetValue(design, out string? uid) && uid.Length > 0) return PlayerName(uid);
        if (!coinDesignAuthors.TryGetValue(design, out string? author))
        {
            var system = sapi!.ModLoader.GetModSystem("Coinage.CoinageModSystem");
            object? details = system?.GetType().GetMethod("GetCoinDesign")?.Invoke(system, [design]);
            author = details?.GetType().GetProperty("CreatedBy")?.GetValue(details)?.ToString()?.Trim() ?? "";
            coinDesignAuthors[design] = author;
        }
        // Coinage's original author is display metadata, never proof of mint ownership.
        if (author.Length > 0) return author;
        if (economy.DesignIssuers.TryGetValue(design, out var legacy) && legacy.OwnerUid.Length > 0) return PlayerName(legacy.OwnerUid);
        return legacyOwner.Length > 0 ? PlayerName(legacyOwner) : "";
    }

    private string CoinDesigner(EconomyCoin coin, IServerPlayer player)
    {
        string designer = DesignDesigner(coin.Design, coin.LegacyOwnerUid);
        return designer.Length > 0 ? designer : Ui(player, "economy-designer-unknown");
    }

}
