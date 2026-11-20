using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeraphCivilizationSimulation;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

internal static class CivilizationManagementChecks
{
    internal static void Run(Action<bool,string> check)
    {
        Succession(check);
        Settings(check);
    }

    private static void Succession(Action<bool,string> check)
    {
        long now=20*TribeSuccession.Day;
        var tribe=new TribeRecord { LeaderUid="chief", Members=["chief","a","b"] };
        tribe.MemberActivity["chief"]=new() { LastSeenUtc=now-TribeSuccession.Absence };
        tribe.MemberActivity["a"]=new(); tribe.MemberActivity["b"]=new();
        TribeSuccession.Record(tribe.MemberActivity["a"],now-1000,60_000);
        TribeSuccession.Record(tribe.MemberActivity["b"],now-2000,120_000);
        var online=new HashSet<string>();
        string? Pick(long time)=>TribeSuccession.Successor(tribe,time,online,_=>true);
        check(Pick(now)==null,"Exactly seven days must not transfer leadership.");
        check(Pick(now+1)=="b","The most active recent member must inherit after seven days.");
        online.Add("chief"); check(Pick(now+1)==null,"An online chief must retain leadership."); online.Clear();
        check(TribeSuccession.Successor(tribe,now+1,online,uid=>uid!="b")=="a","Dead or ineligible members must be excluded.");
        TribeSuccession.Record(tribe.MemberActivity["a"],now,120_000);
        check(Pick(now+1)=="a","Overlapping activity must merge and recent play must affect ranking.");
        check(tribe.MemberActivity["a"].OnlinePeriods.Count==1,"Consecutive samples must merge instead of inflating saved state.");
        var restored=JsonConvert.DeserializeObject<TribeRecord>(JsonConvert.SerializeObject(tribe))!;
        check(TribeSuccession.Successor(restored,now+1,online,_=>true)=="a","Activity and logout times must survive a restart.");
        check(Pick(now+TribeSuccession.Absence+1)==null,"A tribe with no recently active candidate must keep its chief.");
        tribe.MemberActivity["a"].OnlinePeriods=[new() { StartUtc=now-TribeSuccession.Absence-100000,EndUtc=now-TribeSuccession.Absence+10 }];
        check(Pick(now+1)=="b","Only time within the trailing seven days may count.");
        tribe.MemberActivity["a"]=JsonConvert.DeserializeObject<TribeMemberActivity>(JsonConvert.SerializeObject(tribe.MemberActivity["b"]))!;
        check(Pick(now+1)=="a","Tied activity must produce a deterministic successor.");
        tribe.Members.Remove("a"); check(Pick(now+1)=="b","Former members must not inherit.");
        tribe.Members=["chief"]; check(Pick(now+1)==null,"A solo tribe must not lose its chief.");
        var activity=new TribeMemberActivity { OnlinePeriods=[new() { StartUtc=0,EndUtc=1 }] };
        TribeSuccession.Record(activity,now,1000);
        check(activity.OnlinePeriods.Count==1 && activity.LastSeenUtc==now,"Expired activity must be pruned while recording logout time.");
    }

    private static void Settings(Action<bool,string> check)
    {
        var defaults=AdminSettingsData.Document(new(),new());
        string[] retired = ["HomeRestBonus", "HomeRestRateBonus", "AwayRestPenalty", "ExertionRateBonus",
            "DynamicEffectsRefreshMinutes", "DynamicRestRatePerAdditionalMember", "DynamicRestRatePerAdditionalHomeMember",
            "DynamicEffectsComfortTemperatureC", "DynamicEffectsComfortRangeC", "DynamicRestRatePenaltyPerDegree",
            "DynamicRestRateMaximumBonus", "DynamicRestRateMaximumPenalty"];
        var legacyTerritory = new JObject(retired.Select(name => new JProperty(name, -999)));
        legacyTerritory["HealthBonus"] = 4;
        var legacy = new JObject { ["Territory"] = legacyTerritory }.ToObject<VscnConfig>()!;
        legacy.Validate();
        var retained = AdminSettingsData.Document(legacy, new());
        check(retained["Main"]!["Territory"]!["HealthBonus"]!.Value<float>() == 4,
            "Loading retired territory settings must preserve supported configuration.");
        foreach (string field in retired)
            check(defaults["Main"]!["Territory"]![field] == null && retained["Main"]!["Territory"]![field] == null,
                "Retired effects must not reappear in serialized configuration or administrator settings: " + field);
        foreach(var field in defaults.Descendants().OfType<JProperty>().Where(p=>p.Value is not JObject))
            check(AdminSettingText.Title(field.Path)!=field.Name && AdminSettingText.Description(field.Path).Length>6,
                "Every setting requires a readable title and functional description: "+field.Path);
        var system=new VscnCivilizationModSystem();
        var type=system.GetType();
        void Set(string name,object value)=>AccessTools.Field(type,name).SetValue(system,value);
        void Request(IServerPlayer player,AdminSettingsRequest packet)=>AccessTools.Method(type,"OnAdminSettings").Invoke(system,[player,packet]);
        var responses=new List<AdminSettingsPacket>();
        var writes=new List<string>();
        var channel=Proxy.Make<IServerNetworkChannel>((m,a)=> { if(m.Name=="SendPacket" && a?[0] is AdminSettingsPacket packet) responses.Add(packet); return null; });
        var world=Proxy.Make<IServerWorldAccessor>((m,a)=>m.Name=="get_AllOnlinePlayers"?Array.Empty<IPlayer>():null);
        var loader=Proxy.Make<IModLoader>((m,a)=>m.Name=="get_Mods"?new List<Mod>():null);
        var logger=Proxy.Make<ILogger>((m,a)=>null);
        var directory=Path.Combine(Path.GetTempPath(),"vsscs-admin-check-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previous=ConfigMigration.ConfigDirectoryOverride;
        ConfigMigration.ConfigDirectoryOverride=directory;
        bool failRules=false;
        var api=Proxy.Make<ICoreServerAPI>((m,a)=> {
            if(m.Name=="StoreModConfig") { string name=(string)a![1]!; writes.Add(name); File.WriteAllText(Path.Combine(directory,name),JsonConvert.SerializeObject(a[0])); }
            if(m.Name=="get_ModLoader" && failRules) throw new InvalidOperationException("Rule refresh failure");
            return m.Name switch { "get_World"=>world,"get_ModLoader"=>loader,"get_Logger"=>logger,_=>null };
        });
        Set("sapi",api); Set("channel",channel);
        var settings=new VscnConfig(); settings.ModPolicy.Enabled=false; settings.Clusters.Enabled=false;
        Set("config",settings);
        IServerPlayer Player(bool admin)=>Proxy.Make<IServerPlayer>((m,a)=>m.Name=="HasPrivilege"?admin:m.Name=="get_PlayerUID"?"player":null);
        var ordinary=Player(false); var admin=Player(true);
        try
        {
            foreach(string action in new[]{"open","refresh","defaults","save","reload"})
            { Request(ordinary,new(){Action=action}); check(responses.Last() is {Allowed:false,Json:""},"Unauthorized configuration read/write leaked: "+action); }
            check(writes.Count==0,"Denied requests must not even initialize configuration files.");
            Request(admin,new()); var initial=responses.Last();
            check(initial.Allowed && JObject.Parse(initial.Json)["Main"] is JObject && JObject.Parse(initial.Json)["Lore"] is JObject,"Administrator must receive both configuration files.");
            var doc=JObject.Parse(initial.Json); doc["Main"]!["PaperMaps"]!["MaxSheets"]=32;
            Request(admin,new(){Action="save",Json=doc.ToString(),Revision="stale"});
            check(!responses.Last().ReplaceDraft && system.Config.PaperMaps.MaxSheets!=32,"Stale edits must be rejected without discarding the draft.");
            doc["Main"]!["PaperMaps"]!["MaxSheets"]=1;
            Request(admin,new(){Action="save",Json=doc.ToString(),Revision=initial.Revision});
            check(!responses.Last().ReplaceDraft && writes.Count==1,"Invalid settings must fail before any configuration write.");
            doc["Main"]!["PaperMaps"]!["MaxSheets"]=32;
            Request(admin,new(){Action="save",Json=doc.ToString(),Revision=initial.Revision});
            var saved=responses.Last();
            check(saved.ReplaceDraft && system.Config.PaperMaps.MaxSheets==32 && saved.Revision!=initial.Revision,"Save must apply settings and advance the concurrency revision.");
            check(JObject.Parse(File.ReadAllText(Path.Combine(directory,ConfigMigration.Main)))["PaperMaps"]!["MaxSheets"]!.Value<int>()==32,"Save must persist the validated configuration.");
            int count=writes.Count;
            Request(admin,new(){Action="defaults"});
            check(system.Config.PaperMaps.MaxSheets==32 && writes.Count==count && JObject.Parse(responses.Last().Json)["Main"]!["PaperMaps"]!["MaxSheets"]!.Value<int>()!=32,"Reset must remain a draft until explicitly saved.");
            doc=JObject.Parse(saved.Json); doc["Main"]!["PaperMaps"]!["MaxSheets"]=40;
            failRules=true; Request(admin,new(){Action="save",Revision=saved.Revision,Json=doc.ToString()}); failRules=false;
            check(!responses.Last().ReplaceDraft && system.Config.PaperMaps.MaxSheets==32 && JObject.Parse(File.ReadAllText(Path.Combine(directory,ConfigMigration.Main)))["PaperMaps"]!["MaxSheets"]!.Value<int>()==32,"Failed application must restore the previous runtime and disk configuration.");
            doc["Main"]!["UnknownSetting"]=true;
            bool rejected=false; try { AdminSettingsData.Parse(doc.ToString()); } catch { rejected=true; }
            check(rejected,"Unknown configuration keys must be rejected.");
            Request(admin,new(){Action="refresh"});
            check(responses.Last().Revision==saved.Revision,"Refresh must return the current authoritative runtime state.");
            var onDisk=JsonConvert.DeserializeObject<VscnConfig>(File.ReadAllText(Path.Combine(directory,ConfigMigration.Main)))!;
            onDisk.PaperMaps.MaxSheets=48; File.WriteAllText(Path.Combine(directory,ConfigMigration.Main),JsonConvert.SerializeObject(onDisk));
            Request(admin,new(){Action="reload"});
            check(system.Config.PaperMaps.MaxSheets==48 && responses.Last().ReplaceDraft,"Hot reload must read settings from disk.");
        }
        finally { ConfigMigration.ConfigDirectoryOverride=previous; Directory.Delete(directory,true); }
    }
}
