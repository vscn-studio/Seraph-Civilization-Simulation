namespace SeraphCivilizationSimulation;

public sealed record TechnologyNode(string Id, string Title, string Era, string Icon, string Detail,
    string[] Requires, string[] Discover, string[] Complete, string Mod = "", int Row = 0, bool AllOutputs = false,
    string[]? Actions = null);

public static class TechnologyCatalog
{
    private const string Resource = "game:textures/item/resource/";
    private const string Copper = Resource + "nugget/nativecopper.png";
    private const string Rope = Resource + "rope.png";
    private const string Seed = Resource + "seeds/flax.png";
    private const string Iron = Resource + "ironbloom.png";
    private const string Food = "game:textures/item/food/meat/raw.png";

    public static readonly TechnologyNode[] Nodes = [
        new("stone", "石材采集", "石器", "game:textures/block/stone/flint.png", "从地面拾取燧石或可打制的石块。先保留几块作石片；石刀还需要树枝装柄。目标：取得一块燧石或石头。", [], ["game:stone-*", "game:flint"], ["game:stone-*", "game:flint"]),
        new("knapping", "打制石器", "石器", "game:textures/block/stone/flint.png", "手持燧石或可打制的石块，右击地面选择石刀片或斧头片图案；逐格敲去多余材料，再用树枝装柄。目标：取得石刀或石斧。", ["stone"], ["game:flint", "game:stone-*"], ["game:knife-*", "game:axe-*"], Row: 0),
        new("fire", "生火", "石器", Resource + "crushed/charcoal.png", "采集干草、树枝和原木；将原木劈成木柴，叠放燃料，使用起火工具点燃篝火。熟食或木炭可作为火的产物证据。", ["knapping"], ["game:firewood", "game:drygrass"], ["game:charcoal", "game:*meat-cooked"], Row: 0),
        new("clay", "黏土塑形", "陶器", "game:textures/block/clay/brickpile/raw/red-raw1.png", "采集红、蓝或耐火黏土；将黏土放在地面，按图案逐层加减黏土，先制作坩埚、工具模具或陶罐生坯。", ["knapping"], ["game:clay-*"], ["game:crucible-*-raw", "game:claypot-*-raw", "game:toolmold-*-raw-*"], Row: 1),
        new("firing", "坑窑烧制", "陶器", Resource + "crushed/charcoal.png", "挖一格坑窑，放入干燥生坯；依次加入干草和木柴，点火并等待冷却。铜器路线同时需要烧好的坩埚和工具模具。", ["clay", "fire"], ["game:crucible-*-fired", "game:toolmold-*-fired-*"], ["game:crucible-*-fired", "game:toolmold-*-fired-*"], Row: 1, AllOutputs: true),
        new("copperore", "自然铜", "铜器", Copper, "地表散矿、淘洗、矿物陶罐或交易均可取得铜粒；铸镐和锤约需40粒。", ["stone"], ["game:nugget-nativecopper"], ["game:nugget-nativecopper"], Row: 2),
        new("charcoal", "烧制木炭", "铜器", Resource + "crushed/charcoal.png", "将木柴密封成炭坑并点燃，取得木炭用于熔铜。", ["fire"], ["game:firewood"], ["game:charcoal"], Row: 2),
        new("coppertools", "铸铜镐与锤", "铜器", Copper, "烧制坩埚、镐头及锤头模具；熔化铜后分别浇铸，装柄得到铜镐和铜锤。", ["firing", "copperore", "charcoal"], ["game:nugget-nativecopper", "game:ingot-copper"], ["game:pickaxe-copper", "game:hammer-copper"], Row: 1, AllOutputs: true),
        new("coppermine", "铜矿开采", "铜器", Copper, "使用铜镐开采含铜矿层，再用锤子击碎矿石获得铜粒。自然铜以外也可利用孔雀石等铜矿。目标：亲自开采并取得含铜矿石或碎铜矿。", ["coppertools"], ["game:ore-*copper*", "game:ore-*malachite*"], ["game:crushed-nativecopper", "game:ore-*copper*", "game:ore-*malachite*"], Row: 2, Actions: ["break:game:ore-*copper*|break:game:ore-*malachite*"]),
        new("copperanvil", "铜砧与锯", "铜器", Resource + "plate/copper.png", "浇铸铜砧，在砧上锻铜锯；用锯将原木加工为木板。", ["coppertools"], ["game:anvil-copper", "game:saw-copper"], ["game:plank-*", "game:saw-copper"], Row: 0, AllOutputs: true),
        new("prospecting", "探矿", "青铜", Copper, "制作探矿镐，依据矿脉线索寻找锡、锌、铋、金或银。", ["coppertools"], ["game:prospectingpick-*"], ["game:prospectingpick-*"], Row: 3),
        new("tinbronze", "锡青铜", "青铜", Resource + "plate/tinbronze.png", "铜88-92%、锡8-12%；取得锡青铜锭。", ["coppertools"], ["game:nugget-cassiterite", "game:ingot-tin"], ["game:ingot-tinbronze"], Row: 0),
        new("bismuthbronze", "铋青铜", "青铜", Resource + "plate/copper.png", "铜50-70%、锌20-30%、铋10-20%；取得铋青铜锭。", ["coppertools"], ["game:ingot-zinc", "game:ingot-bismuth"], ["game:ingot-bismuthbronze"], Row: 1),
        new("blackbronze", "黑青铜", "青铜", Resource + "plate/copper.png", "铜68-84%、银8-16%、金8-16%；取得黑青铜锭。", ["coppertools"], ["game:ingot-silver", "game:ingot-gold"], ["game:ingot-blackbronze"], Row: 2),
        new("bronzeanvil", "青铜砧", "青铜", Resource + "plate/tinbronze.png", "三种青铜任选其一，铸造青铜级砧，为锻铁做准备。", ["tinbronze|bismuthbronze|blackbronze"], ["game:ingot-tinbronze", "game:ingot-bismuthbronze", "game:ingot-blackbronze"], ["game:anvil-tinbronze", "game:anvil-bismuthbronze", "game:anvil-blackbronze"], Row: 0),
        new("firebrick", "耐火砖", "铁器", Resource + "ironbloom.png", "采集耐火黏土、塑砖并烧成耐火砖。", ["firing"], ["game:clay-fire"], ["game:burnedbrick-fire"], Row: 3),
        new("bloomery", "锻铁炉", "铁器", Iron, "用耐火砖制作炉基和烟囱，加入赤铁矿、磁铁矿或褐铁矿与木炭/煤并点燃；冷却后取出铁坯。", ["bronzeanvil", "firebrick"], ["game:ore-*hematite*", "game:ore-*magnetite*", "game:ore-*limonite*", "game:bloomerybase-*"], ["game:ironbloom"], Row: 0, Actions: ["place:game:bloomerybase-*"]),
        new("iron", "锻铁", "铁器", Resource + "plate/iron.png", "在青铜级以上砧上锻打铁坯为铁锭，继续锻造铁制工具。", ["bloomery"], ["game:ironbloom"], ["game:ingot-iron", "game:pickaxe-iron"], Row: 0, AllOutputs: true),
        new("ironanvil", "铁砧", "铁器", Resource + "plate/iron.png", "锻铁砧基座与台面，使用硼砂焊接，用于后续炼钢。", ["iron"], ["game:anvilpart-*-iron", "game:borax"], ["game:anvil-iron"], Row: 1),
        new("cementation", "渗碳炉", "钢铁", Resource + "plate/steel.png", "耐火砖、六块格栅、铁门及两套石棺；装入铁锭和木炭/焦炭持续渗碳，取得渗碳钢。", ["iron", "firebrick"], ["game:stonecoffin-*", "game:door-iron-*"], ["game:ingot-blistersteel"], Row: 0),
        new("steel", "锻造钢铁", "钢铁", Resource + "plate/steel.png", "将渗碳钢在铁砧上锻成钢锭，再锻钢制工具。", ["cementation", "ironanvil"], ["game:ingot-blistersteel"], ["game:ingot-steel", "game:pickaxe-steel"], Row: 0, AllOutputs: true),
        new("farming", "耕作", "农业", Seed, "获得种子与锄头，翻土播种；成熟后收获作物。需考虑水分、温度及N/P/K养分。目标：亲自收割并取得作物。", ["knapping"], ["game:seeds-*"], ["game:vegetable-*", "game:grain-*"], Row: 4, Actions: ["break:game:crop-*"]),
        new("husbandry", "畜牧", "农业", Food, "围栏和饲料槽吸引家畜，喂养并繁殖；奶和蛋是可记录的产物。", ["farming"], ["game:trough-*"], ["game:milkportion", "game:egg-*"], Row: 4),
        new("barrel", "木桶与保存", "农业", Resource + "rope.png", "铜锯加工木板，制作木桶并用于腌制、制革或食品保存。", ["copperanvil"], ["game:plank-*"], ["game:barrel-*", "game:leather-*"], Row: 4),
        new("mechanical", "风力传动", "机械", Resource + "plate/copper.png", "制作风车转子和至少四片帆；安装在高处，再用木轴及角齿轮传递旋转。目标：放置风车转子并取得传动部件；运行仍需检查风力和连接方向。", ["copperanvil"], ["game:windmillrotor-*", "game:sail-*"], ["game:woodenaxle-*", "game:angledgears-*"], Row: 3, Actions: ["place:game:windmillrotor-*"]),
        new("quern", "自动研磨", "机械", Seed, "将风力传入石磨，可自动研磨谷物等材料。", ["mechanical", "farming"], ["game:quern-*"], ["game:flour-*"], Row: 4),
        new("helve", "机械柄锤", "机械", Iron, "柄锤头、柄锤座与栓扣连接风力；配青铜级以上砧加工铁坯或金属板。", ["mechanical", "bronzeanvil"], ["game:helvehammer-*"], ["game:ironbloom", "game:metalplate-*"], Row: 3),
        new("glass", "玻璃与温室", "铁器", Resource + "plate/iron.png", "收集石英或含石英沙料，在高温炉中熔炼玻璃；取得玻璃后可制作窗户、提灯及温室围护。", ["bloomery"], ["game:clearquartz", "game:smokyquartz"], ["game:glass-*"], Row: 4),
        new("pscordage", "原始绳索", "模组", Rope, "Primitive Survival：野藤或香蒲制绳索。", ["knapping"], ["game:cattailtops", "game:wildvine-*"], ["primitivesurvival:cordage"], "primitivesurvival", 5),
        new("psfish", "鱼篓与捕鱼矛", "模组", Rope, "Primitive Survival：绳索加树枝制鱼篓；刀、树枝、绳索制捕鱼矛。", ["pscordage"], ["primitivesurvival:cordage"], ["primitivesurvival:fishbasket-*", "primitivesurvival:fishingspear-*"], "primitivesurvival", 5),
        new("psraft", "木筏", "模组", Rope, "Primitive Survival：绳索和三根原木制作木筏。", ["pscordage"], ["game:log-*"], ["primitivesurvival:raft-*"], "primitivesurvival", 5),
        new("butchering", "简易屠宰台", "模组", Food, "Butchering：石刀、石斧、木头、燧石和石头制作简易屠宰台。", ["knapping"], ["game:bone-*"], ["butchering:butchertable-primitive-*"], "butchering", 6),
        new("smoking", "烟熏架", "模组", Food, "Butchering：木板、金属钩、树脂和树枝制作烟熏架。", ["butchering", "copperanvil"], ["game:resin"], ["butchering:smokingrack-*"], "butchering", 6),
        new("pemmican", "干粮", "模组", Food, "Long-term Food：果实、肉和脂肪制作干粮。", ["fire"], ["game:fat"], ["pemmican:pemmican-*"], "longtermfood", 7),
        new("pickling", "盐水腌制", "模组", Seed, "Long-term Food：在木桶中用盐水腌制蔬菜。", ["barrel"], ["game:brineportion"], ["pemmican:pickledvegetable-*"], "longtermfood", 7),
        new("foodshelves", "储藏架", "模组", Seed, "FoodShelves：木板和树枝制作木桶架，玻璃与金属部件进一步制作冷藏柜。", ["barrel"], ["game:plank-*"], ["foodshelves:barrelrack-*", "foodshelves:coolingcabinet-*"], "foodshelves", 8),
        new("toolsmith", "工具匠工作台", "模组", Resource + "plate/copper.png", "Toolsmith：锯与木板制作工作台，制作手柄和磨石。", ["copperanvil"], ["toolsmith:crudehandle"], ["toolsmith:workbench-*"], "toolsmith", 9),
        new("quarry", "采石工具", "模组", Resource + "plate/copper.png", "Stone Quarry：金属锭锻造凿头，装柄制楔凿或粗切凿。", ["copperanvil"], ["game:ingot-copper"], ["stonequarry:chisel-wedge-*", "stonequarry:chisel-roughcut-*"], "stonequarryrepckfipil", 9),
        new("cart", "货车", "模组", Resource + "plate/iron.png", "Cartwrights Caravan：支撑梁、绳索、树脂、图纸制车架，铁级钉条、车轮完成货车。", ["iron", "copperanvil"], ["cartwrightscaravan:cartschematics-*"], ["cartwrightscaravan:cartconstruction-*", "cartwrightscaravan:cartsmisc-wheel*"], "cartwrightscaravan", 10),
        new("ship", "造船", "模组", Rope, "Shipwright：木板、绳索和刀制船桨；滚轮与船体材料开启船只建造。", ["copperanvil"], ["game:plank-*"], ["shipwright:oar-*", "shipwright:shipwrightroller-*"], "shipwright", 10),
    ];

    public static bool Matches(string pattern, string code)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(code,
            "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    public static bool CanComplete(TechnologyNode node, IReadOnlySet<string> observed, IReadOnlySet<string> completed,
        IReadOnlySet<string>? actions = null)
    {
        if (!node.Requires.All(group => group.Split('|').Any(completed.Contains))) return false;
        return ActionsMet(node, actions) && OutputsMet(node, observed);
    }

    public static bool ActionsMet(TechnologyNode node, IReadOnlySet<string>? actions)
        => node.Actions == null || actions != null && node.Actions.All(group =>
            group.Split('|').Any(pattern => actions.Any(action => Matches(pattern, action))));

    public static bool OutputsMet(TechnologyNode node, IReadOnlySet<string> observed)
    {
        bool HasOutput(string pattern) => observed.Any(code => Matches(pattern, code));
        return node.AllOutputs ? node.Complete.All(HasOutput) : node.Complete.Any(HasOutput);
    }
}
