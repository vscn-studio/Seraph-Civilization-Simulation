# Seraph Civilization Simulation

[简体中文](README.md)

Seraph Civilization Simulation (`vsscsmod`), version **1.0.0**.

Requires Vintage Story 1.22.3 or later; currently verified against 1.22.7. Install on both the client and server.

## Features

- Civilization Core menu: announcements, tutorials, tribes, trading, and administrator settings. Players choose a character name when first joining a world.
- Spawn settlements: assign player spawn locations with capacity limits, terrain and climate checks, and optional manually placed settlements.
- Lives and reincarnation: limited lives, respawn protection, return to a tribe, and reincarnation rules.
- Tribe management: creation, invitations, member permissions, and leadership transfers. After a leader has been offline for more than 7 real days, leadership passes to the living member with the most online time over the last 7 days. If no active member is available, the leader remains unchanged.
- Territory protection: a fixed 16×16 core by default, extending 10 blocks downward and without an upper height limit. No main hall is required, and membership does not expand the core. Includes building reinforcement, recovery, and fire protection.
- Paper maps: Immersive Maps compatibility, map assembly, freehand closed regions, intersecting region detection, region names and colors, list editing and deletion, and territory name notifications on entry.
- Custom territories: a leader can set a drawn closed region as the actual protected territory if it contains the original 16×16 core. Ordinary drawings are saved on the current paper map and can be shared with it.
- Map display: global text and icon scaling, individual marker icon scaling, and hidden coordinates and other geographic details in map interfaces.
- Player trading: offers, barter, and mixed payments. Trade history is stored only on the server.
- Administration and compatibility: mod allowlists/blocklists, lore restrictions, interaction restrictions during sleep, third-party mod compatibility, and diagnostic logs.

## Controls

| Action | Function |
| --- | --- |
| Tap Tab | Open or close the Civilization Core menu |
| Hold Tab | Show yourself and online tribe members; release to hide |
| Ctrl+Tab | Show chat history |
| Aim at a player with an offer within 5 blocks, then press Ctrl+T | Open the trade window |
| Map window → Draw Region | Hold a charcoal pencil in the offhand and draw a closed region with the left mouse button |
| Map window → Settings | Adjust map text and marker icon scaling |
| Civilization Core → Administrator Settings | View, save, reload, reset, or refresh configuration; administrators only |

## Commands

`/vscncs` is a compatibility alias for `/vsscsmod`. In this table, “Admin” means a caller with the `controlserver` privilege or the server console.

| Command | Permission | Purpose |
| --- | --- | --- |
| `/vsscsmod` | Admin | Show command help |
| `/vsscsmod cluster info` | Player | Show your spawn settlement |
| `/vsscsmod statuscopy` | Player | Export your own status report for troubleshooting |
| `/clientlogs` | Player | Open a confirmation dialog to upload your own client logs |
| `/vsscsmod reload config` | Admin | Reload the main configuration |
| `/vsscsmod reset <player>` | Admin | Reset the death count; offline players are reset on their next connection |
| `/vsscsmod resetall` | Admin | Reset death counts for all players |
| `/vsscsmod reset start <count>` | Admin | Enable resets on login, with a per-player reset limit |
| `/vsscsmod reset stop` | Admin | Stop resets on login |
| `/vsscsmod name reset <account-name, character-name, or UID>` | Admin | Require a player to enter their name again |
| `/vsscsmod sleep on`, `off`, `toggle`, `status` | Admin | Enable, disable, toggle, or inspect interaction restrictions during sleep |
| `/vsscsmod tribe list` | Admin | List tribes |
| `/vsscsmod tribe delete <tribe-name>` | Admin | Delete a tribe |
| `/vsscsmod tribe solo on`, `off`, `toggle`, `status` | Admin | Manage the solo tribe creation setting |
| `/vsscsmod territory debug` | Admin | Show territory diagnostics |
| `/vsscsmod cluster list`, `cluster infoall` | Admin | List spawn settlements or show full details |
| `/vsscsmod cluster who <player>` | Admin | Look up a player's spawn settlement |
| `/vsscsmod cluster tp <index>` | Admin | Teleport to a spawn settlement; indices start at 0 |
| `/vsscsmod cluster assign <player> <friend>` | Admin | Assign an online player to an online friend's spawn settlement and teleport them |
| `/vsscsmod cluster reset <player>` | Admin | Reassign an online player's spawn settlement and teleport them |
| `/vsscsmod cluster resetall` | Admin | Clear spawn settlement assignments and reassign online players |
| `/vsscsmod cluster reroll` | Admin | Regenerate the spawn settlement layout |
| `/vsscsmod content announcements list`, `content tutorials list` | Admin | List announcements or tutorials and their IDs |
| `/vsscsmod content announcements add <title> \| <HTML>` | Admin | Add an announcement; also works with `tutorials` |
| `/vsscsmod content announcements edit <ID> <title> \| <HTML>` | Admin | Edit an announcement; also works with `tutorials` |
| `/vsscsmod content announcements delete <ID>`, `content announcements clear` | Admin | Delete an announcement or clear all announcements; also works with `tutorials` |
| `/vsscsmod reload content [all/announcements/tutorials]` | Admin | Reload content files and synchronize online players |

For announcement `add` and `edit`, place `--notify` or `--silent` before the title to control notifications. Back up the world before deleting or clearing data, resetting assignments, or regenerating the layout.

## Configuration and data files

Paths below are relative to the game data directory of the corresponding client or server. Configuration files are generated on first use and can be edited through Administrator Settings.

| File or directory | Location and purpose |
| --- | --- |
| `ModConfig/SeraphCivilizationSimulation.json` | Server main configuration; [example](config/SeraphCivilizationSimulation.example.json) |
| `ModConfig/SeraphCivilizationLoreRestrictions.json` | Server lore restrictions; [example](config/SeraphCivilizationLoreRestrictions.example.json) |
| `ModConfig/vsscs-map-client.json` | Client map settings; `IconScale` and `FontScale` both default to `1`, with a range of `0.5–3` |
| `ModData/vscncs-content/worlds/<world-identifier-hash>/announcements.json` | Server announcements stored separately for each world |
| `ModData/vscncs-content/worlds/<world-identifier-hash>/tutorials.json` | Server tutorials stored separately for each world |
| World save | Tribe, territory, lives, spawn settlement assignments, and other state; deleting configuration files does not reset these |

Legacy `VscnCivilizationSimulation.json` and `VscnCivilizationLoreRestrictions.json` files are migrated automatically when the new files do not exist. If both versions exist, the new files take precedence.

### Common main configuration settings

| Setting | Default | Purpose |
| --- | --- | --- |
| `ModPolicy.Enabled` / `Mode` / `Mods` | `true` / `Whitelist` / `[]` | Mod validation switch, allowlist or blocklist mode, and mod ID/version rules; version `*` accepts any version |
| `Clusters.Enabled` / `TotalClusters` | `true` / `10` | Enable spawn settlements and set their count |
| `Clusters.ClusterRadius` / `MaxPlayersPerCluster` | `250` / `15` | Spawn radius and assignment capacity per settlement |
| `Clusters.AssignmentMode` | `Sequential` | Assignment mode: `Sequential`, `Balanced`, or `Random` |
| `Clusters.ManualClusters` | `[]` | Manually specified spawn settlements using absolute world coordinates `X/Y/Z` |
| `Respawn.Enabled` / `InitialLives` | `true` / `3` | Enable life rules and set initial lives; `-1` means unlimited |
| `Respawn.ProtectionSeconds` | `10` | Respawn protection duration in seconds |
| `Respawn.ReincarnationChance` / `ReincarnationLives` | `1.0` / `3` | Reincarnation probability and lives after reincarnation |
| `Respawn.NewbornMinimumLivingMembers` | `5` | Minimum living tribe members required for returning to a tribe |
| `Sleep.Enabled` | `true` | Restrict interactions during sleep |
| `Tribes.AllowSoloCreation` | `false` | Allow solo tribe creation |
| `Territory.BoundaryDisplayEnabled` / `CenterCooldownMinutes` | `true` / `60` | Boundary display and territory center relocation cooldown in minutes |
| `Territory.ReinforcementPerLivingMember` / `ReinforcementMaximum` | `10` / `200` | Reinforcement per living member and its cap |
| `Territory.ReinforcementRecoveryPerMemberPerMinute` / `ReinforcementRecoveryMaximumPerMinute` | `0.2` / `4` | Reinforcement recovery rate and its cap |
| `Territory.ReinforcementRecoveryDelaySeconds` | `600` | Delay before recovery begins after damage, in seconds |
| `TribeAuthorization.Enabled` / `ConstructionEnabled` / `BenefitsEnabled` | All `true` | Tribe authorization, construction permissions, and benefit permissions |
| `BuildingProtection.Enabled` | `true` | Building fire protection; material burn times and related settings are in the same section |
| `PaperMaps.MaxSheets` | `49` | Maximum number of paper map sheets that can be joined, from `2` to `256` |
| `Roster.HeaderSvg` | Built-in SVG | Online member list header image; an empty string hides it |
| `DisableVintageCanvasArtistClass` | `true` | Disable the Vintage Canvas artist class; requires a restart after changes |

The default territory core size and height limits are fixed. Legacy main hall, population expansion, and outpost settings do not control them. Reloading configuration preserves existing spawn settlement coordinates and assignments; use settlement management commands when layout changes are needed.

### Lore restrictions

| Setting | Default | Purpose |
| --- | --- | --- |
| `Enabled` | `true` | Master switch |
| `OnlyWhenLoreDisabled` | `true` | Apply restrictions only when lore is disabled in the world |
| `RemoveExistingRestrictedEntities` | `true` | Remove existing restricted entities |
| Other entity, drop, recipe, and item switches | All `true` | `true` enables the corresponding restriction; see the configuration example for individual fields |

Lore restriction changes require a server restart. In Administrator Settings, Reset loads a draft of the defaults; changes take effect only after Save and Apply.

## License

Copyright (c) 2026 HansJack. This project uses the [MIT License](LICENSE). Full third-party component and icon licenses are included in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt). Both license files are distributed with the release package.
