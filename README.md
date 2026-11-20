# 炽天使文明模拟

Seraph Civilization Simulation（`vsscsmod`），版本 **1.0.0**。

适用于 Vintage Story 1.22.3 及以上版本，当前验证版本为 1.22.7。客户端和服务端均需安装。

## 功能清单

- 文明核心菜单：公告、教程、部落、交易及管理员设置；首次进入世界填写角色姓名。
- 出生聚落：分配玩家出生位置，支持人数容量、地形与气候筛选，以及手动指定聚落。
- 生命与转生：有限生命、死亡保护、部落归返和转生规则。
- 部落管理：创建、邀请、成员授权、首领转让；首领离线超过现实 7 天后，转交给最近 7 天在线时长最多的存活成员，无活跃成员时保留。
- 领地保护：默认固定 16×16 格，向下 10 格、向上不限；无需主厅，不随人数扩张。支持建筑加固、恢复及火灾保护。
- 纸质地图：兼容 Immersive Maps，支持拼图、自由闭合范围、交叉区域识别、范围命名与颜色、列表编辑删除，以及进入领地时的名称提示。
- 自定义领地：首领绘制包含原有 16×16 核心的闭合范围后，可将其设为实际保护范围；普通绘图随当前纸质地图保存、分享。
- 地图显示：统一调整文字与图标大小，并单独调整标记图标；地图相关界面隐藏坐标等地理信息。
- 玩家交易：设置报价、实物及混合支付；交易历史仅保存在服务端。
- 管理与兼容：模组白名单/黑名单、传说内容限制、睡眠交互限制、第三方模组兼容及日志诊断。

## 常用操作

| 操作 | 功能 |
| --- | --- |
| 短按 Tab | 打开或关闭文明核心菜单 |
| 长按 Tab | 查看自己与本部落在线成员，松开隐藏 |
| Ctrl+Tab | 显示聊天记录 |
| 指向 5 格内有报价的玩家，按 Ctrl+T | 打开交易窗口 |
| 地图窗口 → 绘制范围 | 左手持木炭笔，鼠标左键绘制闭合范围 |
| 地图窗口 → 设置 | 调整地图文字及标记图标缩放 |
| 文明核心 → 管理员设置 | 查看、保存、热重载、重置或刷新配置，仅管理员可见 |

## 指令

`/vscncs` 是 `/vsscsmod` 的兼容别名。下表中“管理员”指拥有 `controlserver` 权限，或从服务器控制台执行。

| 指令 | 权限 | 用途 |
| --- | --- | --- |
| `/vsscsmod` | 管理员 | 查看指令帮助 |
| `/vsscsmod cluster info` | 玩家 | 查看自己的出生聚落 |
| `/vsscsmod statuscopy` | 玩家 | 导出自己的状态报告用于排查问题 |
| `/clientlogs` | 玩家 | 打开本人客户端日志上传确认窗口 |
| `/vsscsmod reload config` | 管理员 | 热重载主配置 |
| `/vsscsmod reset <玩家名>` | 管理员 | 重置死亡计数；离线玩家下次连接时执行 |
| `/vsscsmod resetall` | 管理员 | 重置全部玩家死亡计数 |
| `/vsscsmod reset start <次数>` | 管理员 | 启用登录重置，限制每人可重置次数 |
| `/vsscsmod reset stop` | 管理员 | 停止登录重置 |
| `/vsscsmod name reset <账号名、角色名或UID>` | 管理员 | 要求玩家重新填写姓名 |
| `/vsscsmod sleep on`、`off`、`toggle`、`status` | 管理员 | 开启、关闭、切换或查看睡眠交互限制 |
| `/vsscsmod tribe list` | 管理员 | 列出部落 |
| `/vsscsmod tribe delete <部落名>` | 管理员 | 删除指定部落 |
| `/vsscsmod tribe solo on`、`off`、`toggle`、`status` | 管理员 | 管理单人创建部落开关 |
| `/vsscsmod territory debug` | 管理员 | 查看领地诊断信息 |
| `/vsscsmod cluster list`、`cluster infoall` | 管理员 | 查看出生聚落列表或完整信息 |
| `/vsscsmod cluster who <玩家名>` | 管理员 | 查询玩家的出生聚落 |
| `/vsscsmod cluster tp <编号>` | 管理员 | 传送至指定出生聚落，编号从 0 开始 |
| `/vsscsmod cluster assign <玩家名> <好友名>` | 管理员 | 将在线玩家分配到在线好友所在出生聚落并传送 |
| `/vsscsmod cluster reset <玩家名>` | 管理员 | 重新分配在线玩家的出生聚落并传送 |
| `/vsscsmod cluster resetall` | 管理员 | 清除出生聚落分配记录，并重新分配在线玩家 |
| `/vsscsmod cluster reroll` | 管理员 | 重新生成出生聚落布局 |
| `/vsscsmod content announcements list`、`content tutorials list` | 管理员 | 查看公告或教程及其 ID |
| `/vsscsmod content announcements add <标题> \| <HTML>` | 管理员 | 新增公告；`tutorials` 同样适用 |
| `/vsscsmod content announcements edit <ID> <标题> \| <HTML>` | 管理员 | 编辑公告；`tutorials` 同样适用 |
| `/vsscsmod content announcements delete <ID>`、`content announcements clear` | 管理员 | 删除指定公告或清空公告；`tutorials` 同样适用 |
| `/vsscsmod reload content [all/announcements/tutorials]` | 管理员 | 从文件重载公告、教程并同步在线玩家 |

公告的 `add`、`edit` 可在标题前加入 `--notify` 或 `--silent` 控制通知。删除、清空、重置分配和重新生成布局前，请备份世界。

## 配置与数据文件

以下路径均相对于对应客户端或服务端的游戏数据目录。配置首次运行时自动生成，可在管理员设置窗口中编辑。

| 文件或目录 | 位置及用途 |
| --- | --- |
| `ModConfig/SeraphCivilizationSimulation.json` | 服务端主配置；[配置示例](config/SeraphCivilizationSimulation.example.json) |
| `ModConfig/SeraphCivilizationLoreRestrictions.json` | 服务端传说内容限制；[配置示例](config/SeraphCivilizationLoreRestrictions.example.json) |
| `ModConfig/vsscs-map-client.json` | 客户端地图设置，`IconScale`、`FontScale` 默认均为 `1`，范围 `0.5–3` |
| `ModData/vscncs-content/worlds/<世界标识哈希>/announcements.json` | 服务端按世界独立保存的公告 |
| `ModData/vscncs-content/worlds/<世界标识哈希>/tutorials.json` | 服务端按世界独立保存的教程 |
| 世界存档 | 部落、领地、生命、出生聚落分配等状态；不通过删除配置文件重置 |

旧的 `VscnCivilizationSimulation.json`、`VscnCivilizationLoreRestrictions.json` 在新配置不存在时自动迁移；新旧文件同时存在时使用新文件。

### 主配置常用项

| 配置项 | 默认值 | 用途 |
| --- | --- | --- |
| `ModPolicy.Enabled` / `Mode` / `Mods` | `true` / `Whitelist` / `[]` | 模组校验开关、白名单或黑名单、模组 ID 与版本规则；版本 `*` 表示不限 |
| `Clusters.Enabled` / `TotalClusters` | `true` / `10` | 出生聚落开关与数量 |
| `Clusters.ClusterRadius` / `MaxPlayersPerCluster` | `250` / `15` | 出生范围半径与每个出生聚落分配容量 |
| `Clusters.AssignmentMode` | `Sequential` | 分配方式：`Sequential`、`Balanced`、`Random` |
| `Clusters.ManualClusters` | `[]` | 手动指定出生聚落的绝对世界坐标 `X/Y/Z` |
| `Respawn.Enabled` / `InitialLives` | `true` / `3` | 生命规则开关与初始生命数，`-1` 表示不限 |
| `Respawn.ProtectionSeconds` | `10` | 复活保护秒数 |
| `Respawn.ReincarnationChance` / `ReincarnationLives` | `1.0` / `3` | 转生概率与转生后的生命数 |
| `Respawn.NewbornMinimumLivingMembers` | `5` | 部落归返所需最低存活成员数 |
| `Sleep.Enabled` | `true` | 睡眠时的交互限制 |
| `Tribes.AllowSoloCreation` | `false` | 是否允许单人创建部落 |
| `Territory.BoundaryDisplayEnabled` / `CenterCooldownMinutes` | `true` / `60` | 边界显示与中心迁移冷却分钟数 |
| `Territory.ReinforcementPerLivingMember` / `ReinforcementMaximum` | `10` / `200` | 每位存活成员提供的加固量与上限 |
| `Territory.ReinforcementRecoveryPerMemberPerMinute` / `ReinforcementRecoveryMaximumPerMinute` | `0.2` / `4` | 加固恢复速度与上限 |
| `Territory.ReinforcementRecoveryDelaySeconds` | `600` | 受损后开始恢复前的等待秒数 |
| `TribeAuthorization.Enabled` / `ConstructionEnabled` / `BenefitsEnabled` | 均为 `true` | 部落授权、建造授权与权益授权开关 |
| `BuildingProtection.Enabled` | `true` | 建筑火灾保护；材料燃烧时间等在同节调整 |
| `PaperMaps.MaxSheets` | `49` | 纸质地图最大拼接张数，范围 `2–256` |
| `Roster.HeaderSvg` | 内置 SVG | 在线成员列表标题图案，空字符串隐藏 |
| `DisableVintageCanvasArtistClass` | `true` | 禁用 Vintage Canvas 画家职业，修改后需重启 |

默认领地核心大小和高度固定，不通过旧主厅、人数扩张或哨站参数调整。热重载保留已有出生聚落坐标及分配；改变布局后需按需执行聚落管理指令。

### 传说限制配置

| 配置项 | 默认值 | 用途 |
| --- | --- | --- |
| `Enabled` | `true` | 总开关 |
| `OnlyWhenLoreDisabled` | `true` | 仅在世界禁用传说时应用限制 |
| `RemoveExistingRestrictedEntities` | `true` | 清理已存在的受限实体 |
| 其余实体、掉落、配方及物品开关 | 均为 `true` | `true` 表示启用对应限制，具体字段见配置示例 |

传说限制修改后需重启服务器。管理员设置中的“重置”先载入默认草稿，点击“保存并应用”才生效。

## 许可

Copyright (c) 2026 HansJack。项目使用 [MIT 许可证](LICENSE)，第三方组件和图标的完整许可见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。两份许可文件均随发布包分发。
