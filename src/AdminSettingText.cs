namespace SeraphCivilizationSimulation;

internal static class AdminSettingText
{
    private static readonly Dictionary<string,(string Title,string Help)> Text = """
        Main|文明模拟|文明模拟主配置。
        Lore|传说内容限制|各项独立控制对应传说内容的限制；保存后需重启服务器。
        ModPolicy|模组校验|控制玩家进入服务器时的模组规则。
        Respawn|生命与转生|控制初始生命、重生保护及部落转生。
        Sleep|睡眠交互|控制睡眠期间的操作限制。
        Clusters|出生聚落|控制出生聚落生成与玩家分配，已有出生聚落不会因热重载而重建。
        Territory|部落领地|控制领地范围、增益及建筑加固。
        BuildingProtection|建筑防火|控制领地建筑受热、起火和燃烧损坏。
        Tribes|部落|控制部落创建规则。
        TribeAuthorization|部落授权|控制窗口授权及访客建造限制。
        Roster|成员 HUD|控制 Tab 成员列表的外观。
        PaperMaps|纸质地图|控制地图可使用的纸张数量。
        Enabled|启用|启用或关闭本节功能。
        DisableVintageCanvasArtistClass|禁用画家职业|隐藏 Vintage Canvas 画家职业；需重启游戏和服务器。
        MaxSheets|最多地图纸张|一张纸质地图允许使用的纸张数量，范围 2–256。
        Mode|校验模式|Whitelist 为白名单，Blacklist 为黑名单。
        IncludeServerMods|包含服务器模组|自动将服务器安装的模组加入校验规则。
        ReportTimeoutSeconds|报告超时（秒）|等待客户端提交模组报告的时间，范围 5–300 秒。
        Mods|模组规则|JSON 数组，每项填写 ModId 与 Version；Version 可填 * 表示任意版本。
        ProtectionSeconds|重生保护（秒）|玩家重生后的保护时长，0 表示无保护。
        InitialLives|初始生命|新玩家的初始生命次数；-1 表示无限。
        NewbornMinimumLivingMembers|转生最低存活人数|允许接收转生玩家的部落最低存活成员人数。
        ReincarnationChance|转生概率|符合条件时转生成功的概率，0–1。
        HomecomingMinimumChance|回归基础概率|达到最低部落人数时的回归概率，0–1。
        HomecomingChancePerExtraLivingMember|每人增加回归概率|每超过最低人数一名存活成员增加的概率，0–1。
        ReincarnationLives|转生生命|新角色转生后获得的生命次数。
        ReincarnationOutsideRange|转生外移范围|转生出生点离领地边界的额外距离，单位格。
        AllowSoloCreation|允许单人创建|允许玩家独自成立部落，已获单人资格的部落保持该资格。
        BoundaryDisplayEnabled|显示领地边界|允许客户端显示部落领地边界。
        ReinforcementPerLivingMember|每人加固强度|每名存活成员提供的建筑加固强度。
        ReinforcementMaximum|加固强度上限|部落建筑加固可达到的最高强度。
        ReinforcementRecoveryPerMemberPerMinute|每人每分钟恢复|每名存活成员每分钟恢复的加固强度。
        ReinforcementRecoveryMaximumPerMinute|每分钟恢复上限|所有成员合计每分钟最多恢复的加固强度。
        ReinforcementRecoveryDelaySeconds|受击后恢复延迟（秒）|建筑最后一次受击后，等待多久开始恢复加固。
        CenterCooldownMinutes|迁移冷却（分钟）|两次迁移部落中心之间的最短时间。
        HealthBonus|领地生命加成|在自家或获授权领地内增加的最大生命值。
        ConstructionEnabled|允许建造授权|允许首领向指定玩家授予领地建造权限。
        BenefitsEnabled|允许增益授权|允许首领向指定玩家授予领地增益。
        BlockUnauthorizedReinforcement|阻止未授权加固|阻止未获授权的玩家加固部落领地内建筑。
        BlockUnauthorizedLocks|阻止未授权上锁|阻止未获授权的玩家给部落领地内建筑上锁。
        ReinforcementExclusionsEnabled|启用加固排除|不对排除列表中的装饰或临时方块应用部落加固。
        ReinforcementExcludedBlocks|加固排除列表|JSON 字符串数组，填写 domain:code，支持 * 通配符。
        OutsiderPlacementCooldownEnabled|访客放置冷却|限制未获授权玩家连续放置方块的频率。
        OutsiderPlacementCooldownSeconds|访客放置间隔（秒）|未获授权玩家两次放置建筑之间的最短间隔。
        OutsiderBlockQuotaEnabled|启用访客建筑配额|限制每名未获授权访客在同一领地放置的建筑总量。
        OutsiderBlockQuota|访客建筑数量|每名未获授权访客可在同一领地保留的建筑上限。
        ProtectCriticalAreas|保护核心区域|禁止未获授权的玩家在部落中心附近放置建筑。
        CriticalAreaRadius|核心区域半径（格）|部落中心附近禁止访客建造的半径。
        FireProtectionDamagePerSecond|每秒火焰加固损耗|建筑燃烧时每秒消耗的部落加固强度。
        ArrowContactSeconds|火箭接触时间（秒）|燃烧箭接触建筑时产生的累计受热时间。
        CoolingPerSecond|每秒冷却|失去热源后每秒减少的热量。
        ManualFireDelayMaximumSeconds|手动点火最大延迟（秒）|加固建筑被手动点火时可获得的最大额外延迟。
        ManualFireStrengthForMaximum|最大延迟所需强度|达到手动点火最大延迟所需的加固强度。
        GrassIgnitionSeconds|草类起火（秒）|草类建筑持续受热到起火所需时间。
        GrassFailureSeconds|草类损坏（秒）|草类建筑燃烧到损坏所需时间。
        ClothIgnitionSeconds|布类起火（秒）|布类建筑持续受热到起火所需时间。
        ClothFailureSeconds|布类损坏（秒）|布类建筑燃烧到损坏所需时间。
        WoodIgnitionSeconds|木材起火（秒）|普通木材建筑持续受热到起火所需时间。
        WoodFailureSeconds|木材损坏（秒）|普通木材建筑燃烧到损坏所需时间。
        TimberIgnitionSeconds|大木梁起火（秒）|大木梁持续受热到起火所需时间。
        TimberFailureSeconds|大木梁损坏（秒）|大木梁燃烧到损坏所需时间。
        TotalClusters|出生聚落总数|自动生成的出生聚落数量；已有出生聚落保持原位置。
        GlobalRadius|全局生成半径（格）|围绕世界出生点搜索出生聚落的最大范围。
        ClusterRadius|出生聚落半径（格）|单个出生聚落的分配范围。
        MinDistanceBetween|出生聚落最小间距（格）|新生成出生聚落之间必须保持的最短距离。
        MaxPlayersPerCluster|每出生聚落人数上限|按顺序分配时单个出生聚落可容纳的玩家数。
        LandValidationRadius|陆地检测半径（格）|选择出生聚落中心时评估周围陆地的范围。
        MinLandRatio|最低陆地比例|候选出生聚落中心周围所需的陆地比例，0–1。
        AvoidOceans|避开海洋|出生聚落选址时避开海洋区域。
        AvoidExtremeClimate|避开极端气候|仅选取温度在指定上下限之间的出生聚落位置。
        MinTemperature|最低气温（℃）|新出生聚落选址允许的最低温度。
        MaxTemperature|最高气温（℃）|新出生聚落选址允许的最高温度。
        AssignmentMode|分配模式|Sequential 顺序填满；Balanced 优先人数较少出生聚落；Random 随机。
        ManualClusters|手动出生聚落坐标|JSON 数组，每项包含整数 X、Y、Z。已有出生聚落坐标不因热重载而重新生成。
        HeaderSvg|成员列表标题图案|Tab 成员 HUD 顶部的 SVG 图案，最多 65536 字符；必须是有效且无外部实体的 SVG。
        OnlyWhenLoreDisabled|仅关闭传说时启用|世界关闭传说内容时才应用本节限制。
        RemoveExistingRestrictedEntities|移除已有受限实体|实体加载时移除已经存在的受限生物。
        LivingDeadSpawns|禁止活死人生成|阻止 Primitive Survival 活死人生成。
        GreenWillowispSpawns|禁止绿色鬼火|阻止绿色鬼火生成。
        WhiteWillowispSpawns|禁止白色鬼火|阻止白色鬼火生成。
        YellowWillowispSpawns|禁止黄色鬼火|阻止黄色鬼火生成。
        LivingDeadTemporalGearDrops|移除活死人齿轮掉落|移除活死人的时空齿轮掉落。
        SkullOfTheDead|限制亡者头骨|禁用 Primitive Survival 亡者头骨相关实体及物品。
        HandOfTheDead|限制亡者之手|禁用 Primitive Survival 亡者之手物品。
        BoneSoilTemporalGearDrops|移除骨土齿轮|移除骨土中的时空齿轮掉落。
        BoneSoilLoreBookDrops|移除骨土传说书|移除骨土中的传说书籍掉落。
        BoneSoilLoreCylinderDrops|移除骨土圆筒|移除骨土中的传说圆筒掉落。
        TemporalAmuletTeleport|禁用时空护符传送|限制时空齿轮护符的传送功能。
        TemporalWinterSkip|禁用时空跳冬|限制通过时空功能跳过冬季。
        EquusRevival|禁用马匹复活|限制 Equus 的复活功能。
        ImmersiveTemporalMaps|禁用时空地图|限制 Immersive Maps 的时空地图。
        TemporalTemperatureForecaster|禁用时空温度预测|限制时空温度预测仪。
        ScribeClockmakerNotebookRecipe|禁用钟表匠笔记配方|移除 Scribe 钟表匠笔记的合成配方。
        VinconomyCatalogRecipe|禁用交易目录配方|移除 Vinconomy 目录的合成配方。
        CaravanTemporalGearSign|禁用商队齿轮标志|限制商队时空齿轮标志。
        ExpandedMatterTemporalMaterials|禁用时空材料|限制 Expanded Matter 的时空材料。
        ShipwrightLoreFigureheads|禁用传说船首像|限制 Shipwright 的传说主题船首像。
        HeraldryDrifterPattern|禁用漂流者纹样|限制 Heraldry 漂流者图案。
        HeraldryEidolonPattern|禁用幻灵纹样|限制 Heraldry 幻灵图案。
        """.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(line=>line.Trim().Split('|'))
        .ToDictionary(parts=>parts[0],parts=>(parts[1],parts[2]),StringComparer.Ordinal);

    internal static string Title(string path) => Text.TryGetValue(path.Split('.').Last(),out var value)?value.Title:path.Split('.').Last();
    internal static string Description(string path)
    {
        string help=Text.TryGetValue(path.Split('.').Last(),out var info)?info.Help:"修改此配置项。";
        if (path.EndsWith(".Enabled",StringComparison.Ordinal))
            help=Text.TryGetValue(path.Split('.')[^2],out var section)?section.Help+" 开关此项以启用或关闭该功能。":help;
        if (path.StartsWith("Lore.",StringComparison.Ordinal)) help+=" 保存后需重启服务器。";
        return help;
    }
}
