using System.IO.Compression;
using System.Runtime.Loader;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.GameContent;
using SeraphCivilizationSimulation;

internal static class ParchmentContractChecks
{
    private sealed class JoiningInventory(ICoreAPI api) : InventoryGeneric(1, "joining-player", api)
    {
        public bool Ready;
        public int Reads;
        public override int Count
        {
            get { Reads++; return Ready ? base.Count : throw new NullReferenceException("Inventory not initialized yet."); }
        }
    }

    private sealed class PartialInventory(ICoreAPI api) : InventoryGeneric(2, "backpack-guest", api)
    {
        public override ItemSlot this[int slotId]
        {
            get => slotId == 0 ? null! : base[slotId];
            set => base[slotId] = value;
        }
    }

    private sealed class Writer : EntityPlayer
    {
        public ItemSlot Quill = new DummySlot();
        public override ItemSlot LeftHandItemSlot => Quill;
    }

    public static void Run(Action<bool, string> check)
    {
        const string body = "[time=30, auth=build, name=Jack]\n本部落授予 Jack 建造权。\n正文 time=30 不应被修改";
        check(ParchmentContract.TryParse(body, out var parsed) && parsed is { Minutes: 30, Name: "Jack", Construction: true, Benefits: false }, "Example parchment header failed.");
        check(ParchmentContract.TryParse("\r\n [name=John Smith, TIME=-1, auth=benefits]\r\n自由正文", out parsed)
            && parsed is { Minutes: -1, Name: "John Smith", Construction: false, Benefits: true }, "Permanent/reordered header or spaces in names failed.");
        check(ParchmentContract.TryParse("[time=1,auth=both,name=游戏名]", out parsed) && parsed is { Construction: true, Benefits: true }, "Combined scope failed.");
        foreach (string header in new[] { "[time=0,auth=build,name=Jack]", "[time=-2,auth=build,name=Jack]", "[time=1.5,auth=build,name=Jack]",
            "[time=9223372036854775807,auth=build,name=Jack]", "[time=5256001,auth=build,name=Jack]", "[time=30,auth=admin,name=Jack]",
            "[time=30,auth=build,name=]", "[time=30,auth=build,name=Jack,time=1]", "[time=30,auth=build,name=Jack,extra=x]",
            "[time=30,auth=build]", "[time=30,auth=build,name=Jack", "plain text" })
            check(!ParchmentContract.TryParse(header, out _), "Malformed contract accepted: " + header);
        check(!ParchmentContract.LooksLike("普通书信") && !ParchmentContract.LooksLike("[font=Georgia, size=20]"), "Ordinary books/style headers classified as contracts.");
        check(ParchmentContract.ZeroTime(body) == body.Replace("[time=30", "[time=0") && ParchmentContract.ZeroTime(ParchmentContract.ZeroTime(body)) == ParchmentContract.ZeroTime(body), "Revocation modified body or was not idempotent.");
        check(ParchmentContract.ZeroTime("\r\n  " + body) == "\r\n  " + body.Replace("[time=30", "[time=0"), "Leading blank lines prevented time=0.");

        check(ParchmentContract.TryParse("[授权书]玩家=张三；期限=2小时；权益=建造、领地增益[/授权书]\n正文",out parsed)
            && parsed is {Minutes:120,Name:"张三",Construction:true,Benefits:true},"Chinese authorization keywords failed.");
        check(ParchmentContract.TryParse("[授权书]被授权人:成员;有效期:永久;权限:邀请成员、迁移主厅[/授权书]",out parsed)
            && parsed!.Permissions==(TribePermission.Invite|TribePermission.MoveCenter),"Member permission parsing failed.");
        foreach (var invalid in new[]{"[授权书]玩家=甲；期限=60；权益=建造[/授权书]","[授权书]玩家=甲；期限=1天；权益=所有[/授权书]",
            "[授权书]玩家=甲；期限=1天；权限=建造；权益=增益[/授权书]","[授权书]玩家=甲；期限=永久；权益=建造"})
            check(!ParchmentContract.TryParse(invalid,out _),"Ambiguous authorization must be rejected.");

        if (Environment.GetEnvironmentVariable("VSCN_BILLPOSTING_ZIP") is { } package)
        {
            using var zip = ZipFile.OpenRead(package);
            using var stream = zip.Entries.Single(e => e.Name.Equals("billposting.dll", StringComparison.OrdinalIgnoreCase)).Open();
            using var memory = new MemoryStream(); stream.CopyTo(memory); memory.Position = 0;
            AssemblyLoadContext.Default.LoadFromStream(memory);
        }
        var type = typeof(VscnCivilizationModSystem);
        var compatType = type.Assembly.GetType("SeraphCivilizationSimulation.ParchmentBookCompatibility")!;
        var system = new VscnCivilizationModSystem();
        void Set(string field, object? value) => AccessTools.Field(type, field).SetValue(system, value);
        object? Call(string method, params object?[] args) => AccessTools.Method(type, method).Invoke(system, args);
        var tribes = (TribeState)AccessTools.Field(type, "tribeState").GetValue(system)!;
        var life = (CivilizationLifeState)AccessTools.Field(type, "lifeState").GetValue(system)!;
        var tribe = new TribeRecord { Id = "home", Name = "契约部落", LeaderUid = "chief", Members = ["chief"], AllowsSolo = true };
        tribes.Tribes.Add(tribe);
        life.Players["chief"] = new() { Accepted = true, GameName = "首领" };
        life.Players["guest"] = new() { Accepted = true, GameName = "Jack" };
        var saved = new Dictionary<string, byte[]>(); bool failSave = false;
        var save = Proxy.Make<ISaveGame>((m, a) => {
            if (m.Name == "StoreData") { if (failSave) throw new IOException("Test save failure."); saved[(string)a![0]!] = (byte[])a[1]!; }
            return null;
        });
        var manager = Proxy.Make<IWorldManagerAPI>((m, a) => m.Name == "get_SaveGame" ? save : null);
        var paper = new ItemBook { ItemId = 123, Code = new("game:paper-parchment") };
        var loaded = new System.Collections.Concurrent.ConcurrentDictionary<long, Entity>();
        IPlayer[] online = [];
        var world = Proxy.Make<IServerWorldAccessor>((m, a) => m.Name switch {
            "get_AllOnlinePlayers" => online, "get_LoadedEntities" => loaded, "get_Side" => EnumAppSide.Server, "GetItem" => paper, _ => null });
        var mods = Proxy.Make<IModLoader>((m, a) => m.Name == "IsModEnabled" ? false : null);
        var registry = Proxy.Make<IClassRegistryAPI>((m, a) => m.Name == "CreateInvNetworkUtil" ? Proxy.Make<IInventoryNetworkUtil>((m, a) => null) : null);
        int syncWarnings = 0;
        var logger = Proxy.Make<ILogger>((m, a) => {
            if (m.Name == "Warning" && a?[0] is string message && message.StartsWith("Seraph Civilization Simulation parchment")) syncWarnings++;
            return null;
        });
        var api = Proxy.Make<ICoreServerAPI>((m, a) => m.Name switch { "get_World" => world, "get_WorldManager" => manager,
            "get_Side" => EnumAppSide.Server, "get_ModLoader" => mods, "get_ClassRegistry" => registry, "get_Logger" => logger, _ => null });
        Set("sapi", api); Set("channel", Proxy.Make<IServerNetworkChannel>((m, a) => null));
        var writer = new Writer { World = world, Quill = new DummySlot(new ItemStack(new Item { ItemId = 124, Code = new("game:inkandquill"), Attributes = new JsonObject(JObject.Parse("{\"writingTool\":true}")) })) };
        AccessTools.Property(typeof(Entity), "Properties").SetValue(writer,
            new EntityProperties { Server = new EntityServerProperties([], new()) });
        var worldData = Proxy.Make<IWorldPlayerData>((m, a) => null);
        var errors = new List<string>();
        var inventories = new Dictionary<string, IInventory>();
        bool managerUnavailable = false;
        var inventoryManager = Proxy.Make<IPlayerInventoryManager>((m, a) => m.Name == "get_Inventories"
            ? managerUnavailable ? throw new InvalidOperationException("Inventory manager temporarily unavailable.") : inventories : null);
        IServerPlayer Player(string uid) => Proxy.Make<IServerPlayer>((m, a) => {
            if (m.Name == "SendIngameError") errors.Add((string)a![1]!);
            return m.Name switch { "get_PlayerUID" => uid, "get_PlayerName" => "account-" + uid, "get_Entity" => writer,
                "get_InventoryManager" => inventoryManager, "get_WorldData" => worldData, "get_LanguageCode" => "en", _ => null };
        });
        var chief = Player("chief"); var guest = Player("guest");
        DummySlot Paper(int count = 1) => new(new ItemStack(paper, count));
        var book = new ModSystemEditableBook(); AccessTools.Field(book.GetType(), "api").SetValue(book, api);
        var previous = AccessTools.Field(type, "instance").GetValue(null);
        Set("instance", system);
        using var compatibility = (IDisposable)Activator.CreateInstance(compatType, true)!;
        try
        {
            var unsigned = Paper(); book.BeginEdit(chief, unsigned); book.EndEdit(chief, body, "契约", false);
            check(tribe.Authorizations.Count == 0 && unsigned.Itemstack!.Attributes.GetString("text") == body, "Unsigned writing issued a grant.");
            var ordinary = Paper(); book.BeginEdit(guest, ordinary); book.EndEdit(guest, "普通信件", "信", true);
            check(ordinary.Itemstack!.Attributes.GetString("signedbyuid") == "guest" && tribe.Authorizations.Count == 0, "Normal nonleader letter signing was blocked.");
            foreach (var (who, text, slot) in new[] { (guest, body, Paper()), (chief, body.Replace("Jack", "account-guest"), Paper()),
                (chief, body.Replace("Jack", "account-guest(Jack)"), Paper()),
                (chief, body.Replace("time=30", "time=0"), Paper()), (chief, body, Paper(2)) })
            {
                book.BeginEdit(who, slot); book.EndEdit(who, text, "无效", true);
                check(tribe.Authorizations.Count == 0 && !slot.Itemstack!.Attributes.HasAttribute("signedby"), "Invalid native signing issued a grant.");
            }
            var changed = Paper(); book.BeginEdit(chief, changed); changed.Itemstack = Paper().Itemstack; book.EndEdit(chief, body, "替换", true);
            check(tribe.Authorizations.Count == 0, "Moving/replacing an editing stack bypassed session binding.");
            system.Config.TribeAuthorization.ParchmentContractsEnabled = false;
            var disabled = Paper(); book.BeginEdit(chief, disabled); book.EndEdit(chief, body, "禁用", true);
            check(tribe.Authorizations.Count == 0, "Parchment config switch ignored."); system.Config.TribeAuthorization.ParchmentContractsEnabled = true;
            system.Config.TribeAuthorization.ConstructionEnabled = false;
            book.BeginEdit(chief, disabled); book.EndEdit(chief, body, "禁用建造", true);
            check(tribe.Authorizations.Count == 0, "Disabled construction scope was issued."); system.Config.TribeAuthorization.ConstructionEnabled = true;
            life.Players["ambiguous"] = new() { Accepted = true, GameName = "Jack" };
            book.BeginEdit(chief, disabled); book.EndEdit(chief, body, "同名", true);
            check(tribe.Authorizations.Count == 0, "Duplicate game name resolved to a random player."); life.Players.Remove("ambiguous");
            var signed = Paper(); long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            book.BeginEdit(chief, signed); book.EndEdit(chief, body, "有效契约", true);
            var grant = tribe.Authorizations.Single(); var history = tribe.AuthorizationHistory.Single();
            check(grant.Pending && !grant.Active(before) && history.Status=="pending" && history.IssuedAt==0,"Signing must wait for recipient possession.");
            Call("PruneAuthorizations"); Call("SyncContractStack",signed.Itemstack);
            check(tribe.Authorizations.Count==1 && signed.Itemstack!.Attributes.GetString("text")==body,"Pending letters must not expire or be zeroed.");
            Call("ActivateParchment",chief,signed);
            check(grant.Pending,"Wrong recipient activated parchment.");
            failSave=true;
            try {Call("ActivateParchment",guest,signed);} catch(System.Reflection.TargetInvocationException) { }
            failSave=false;
            check(grant.Pending && history.IssuedAt==0,"Failed receipt persistence must restore pending state.");
            Call("ActivateParchment",guest,signed); long activatedAt=history.IssuedAt;
            Call("ActivateParchment",guest,signed);
            check(!grant.Pending && history.IssuedAt==activatedAt,"Repeated receipt reset authorization duration.");
            check(grant is { Uid: "guest", Construction: true, Benefits: false } && grant.ExpiresAt >= before + 1800000
                && signed.Itemstack!.Attributes.GetString("signedby") == "首领" && signed.Itemstack!.Attributes.GetString("signedbyuid") == "chief", "Server signing did not issue exact scope/name/duration.");
            check(history.Id == grant.Id && history.ContractText == body && history.SignerName == "首领", "Contract history binding missing.");
            int errorsBeforeCopy = errors.Count;
            book.Transcribe(guest, body, "誊抄", 1, signed);
            check(errors.Count == errorsBeforeCopy + 1 && tribe.Authorizations.Count == 1, "Transcription created an unbound copy of a signed contract.");
            book.BeginEdit(chief, signed); book.EndEdit(chief, body, "重复", true);
            check(tribe.Authorizations.Count == 1, "Signed contract replay issued another grant.");
            var benefitPaper = Paper(); book.BeginEdit(chief, benefitPaper); book.EndEdit(chief, body.Replace("auth=build", "auth=benefits").Replace("time=30", "time=-1"), "增益", true);
            Call("ActivateParchment",guest,benefitPaper);
            check(tribe.Authorizations.Count == 2 && tribe.Authorizations.Any(a => a.Benefits && a.ExpiresAt == -1), "Independent benefit contract replaced construction contract.");
            var offlineCopy = signed.Itemstack!.Clone();
            var chest = new InventoryGeneric(1, "contract-chest", api); chest[0].Itemstack = signed.Itemstack!.Clone();
            Call("WatchContractInventory", chest);
            // The server registers this native catalogue even for survival players, without selecting a tab.
            var creative = new InventoryPlayerCreative("creative", "guest", api);
            bool nativeCountThrows = false;
            try { _ = creative.Count; } catch (NullReferenceException) { nativeCountThrows = true; }
            check(nativeCountThrows, "Regression setup no longer represents an uninitialized native creative inventory.");
            var joining = new JoiningInventory(api) { Ready = true }; joining[0].Itemstack = signed.Itemstack.Clone(); joining.Ready = false; joining.Reads = 0;
            var hotbar = new InventoryGeneric(1, "hotbar-guest", api); hotbar[0].Itemstack = signed.Itemstack.Clone();
            var backpack = new PartialInventory(api); backpack[1].Itemstack = signed.Itemstack.Clone();
            var catalogue = new CreativeInventoryTab(1, "creative-tab", api); catalogue[0].Itemstack = signed.Itemstack.Clone();
            inventories.Add("creative", creative); inventories.Add("joining", joining);
            inventories.Add("hotbar", hotbar); inventories.Add("backpack", backpack); inventories.Add("catalogue", catalogue);
            online = [guest];
            Call("SyncParchmentContracts");
            check(joining.Reads == 0, "Inventories were inspected before PlayerNowPlaying.");
            ((HashSet<string>)AccessTools.Field(type, "readyPlayers").GetValue(system)!).Add("guest");
            Call("WatchContractInventory", creative); Call("WatchContractInventory", catalogue);
            Entity? billpost = null; var billType = AccessTools.TypeByName("billposting.EntityBillpost");
            if (billType != null)
            {
                billpost = (Entity)Activator.CreateInstance(billType)!; billpost.World = world;
                billpost.WatchedAttributes.SetBytes("billpostdropstackbytes", signed.Itemstack!.ToBytes());
                billpost.WatchedAttributes.SetString("text", body); loaded[1] = billpost;
                AccessTools.Field(billType, "BillpostText").SetValue(billpost, body);
                check(Harmony.GetPatchInfo(AccessTools.DeclaredMethod(billType, "Initialize"))!.Owners.Contains("vsscsmod.parchment"), "Installed Billposting init hook missing.");
            }
            var item = new EntityItem { World = world, Itemstack = signed.Itemstack!.Clone() }; loaded[2] = item;
            Call("RevokeAuthorization", guest, grant.Id); check(tribe.Authorizations.Count == 2, "Nonleader revoked a contract.");
            Call("RevokeAuthorization", chief, grant.Id);
            check(tribe.Authorizations.Count == 1 && tribe.Authorizations[0].Benefits && history.Status == "revoked"
                && history.ContractText == ParchmentContract.ZeroTime(body), "Individual revoke removed the wrong scope/history.");
            check(chest[0].Itemstack!.Attributes.GetString("text") == history.ContractText && item.Itemstack!.Attributes.GetString("text") == history.ContractText, "Loaded chest/dropped parchment did not sync on revoke.");
            check(hotbar[0].Itemstack!.Attributes.GetString("text") == history.ContractText
                && backpack[1].Itemstack!.Attributes.GetString("text") == history.ContractText,
                "Creative/uninitialized inventories or null slots prevented normal player inventory synchronization.");
            int warningsBeforeRepeat = syncWarnings;
            life.Players["guest"].Living = writer.Alive;
            writer.WatchedAttributes.SetString("vscncsTribeId", "stale-tribe");
            for (int i = 0; i < 100; i++) Call("TickCivilizationLives");
            check(warningsBeforeRepeat == 1 && syncWarnings == 1, "A failing inventory flooded logs on repeated ticks.");
            check(writer.WatchedAttributes.GetString("vscncsTribeId") == "", "Inventory failure blocked the remaining civilization life tick.");
            check(catalogue[0].Itemstack!.Attributes.GetString("text") == body && creative.CurrentTab == null,
                "Synchronization modified or initialized the creative catalogue.");
            joining.Ready = true; Call("SyncParchmentContracts");
            check(joining[0].Itemstack!.Attributes.GetString("text") == history.ContractText,
                "An inventory that finished initialization did not resume synchronization.");
            joining.Ready = false; Call("WatchContractInventory", joining);
            online = []; Call("SyncParchmentContracts"); Call("SyncParchmentContracts");
            check(syncWarnings == 2, "Watched inventory failures escaped or logged repeatedly after recovery.");
            joining.Ready = true; joining[0].Itemstack = signed.Itemstack.Clone(); Call("SyncParchmentContracts");
            check(joining[0].Itemstack!.Attributes.GetString("text") == history.ContractText,
                "Watched container synchronization did not recover after initialization.");
            online = [guest]; managerUnavailable = true;
            writer.WatchedAttributes.SetString("vscncsTribeId", "stale-tribe");
            var listener = new GameTickListener(1, 1000, 0, _ => Call("TickCivilizationLives"), null!);
            for (int i = 1; i <= 100; i++) listener.OnTriggered(i * 1000L);
            check(listener.LastUpdateMilliseconds == 100000 && syncWarnings == 3
                && writer.WatchedAttributes.GetString("vscncsTribeId") == "",
                "Unexpected sync failures prevented native tick scheduling from advancing, flooded logs, or blocked identity updates.");
            managerUnavailable = false; Call("SyncParchmentContracts"); online = [];
            if (billpost != null)
            {
                var drop = new ItemStack(billpost.WatchedAttributes.GetBytes("billpostdropstackbytes"));
                check(billpost.WatchedAttributes.GetString("text") == history.ContractText && drop.Attributes.GetString("text") == history.ContractText
                    && (string)AccessTools.Field(billType, "BillpostText").GetValue(billpost)! == history.ContractText, "Posted text/drop bytes retained revoked permission.");
                check(drop.Attributes.GetString("vscncsAuthorizationId") == history.Id, "Billposting revoke lost original binding.");
            }
            Call("SyncContractStack", offlineCopy);
            check(offlineCopy.Attributes.GetString("text") == history.ContractText, "Offline/container copy did not normalize when encountered.");
            var restored = JsonConvert.DeserializeObject<TribeState>(System.Text.Encoding.UTF8.GetString(saved["vscncsTribeState"]))!;
            check(restored.Tribes[0].AuthorizationHistory.Any(h => h.Id == history.Id && h.Status == "revoked"), "Revocation history not persisted.");
            var snapshot = (CivilizationSnapshotPacket)Call("BuildCivilizationSnapshot", chief, false, "", "", false)!;
            using var wire = new MemoryStream(); Serializer.Serialize(wire, snapshot); wire.Position = 0;
            var decoded = Serializer.Deserialize<CivilizationSnapshotPacket>(wire);
            check(decoded.AuthorizationHistory.Count == 2 && decoded.AuthorizationHistory.Any(h => h.Status == "revoked" && h.Parchment && h.SignerName == "首领"), "Authorization history wire/UI fields missing.");
            grant = tribe.Authorizations.Single(); grant.ExpiresAt = before - 1;
            var benefitHistory = tribe.AuthorizationHistory.Single(h => h.Id == grant.Id); benefitHistory.ExpiresAt = grant.ExpiresAt;
            Call("PruneAuthorizations"); Call("SyncContractStack", benefitPaper.Itemstack);
            check(tribe.Authorizations.Count == 0 && benefitHistory.Status == "expired" && benefitPaper.Itemstack!.Attributes.GetString("text").StartsWith("[time=0"), "Expiry did not stop/zero the contract.");
            var failure = Paper(); failSave = true;
            book.BeginEdit(chief, failure);
            book.EndEdit(chief, body, "保存失败", true);
            check(tribe.Authorizations.Count == 0 && !failure.Itemstack!.Attributes.HasAttribute("signedby"), "Save failure did not roll back item and grant.");
            failSave = false;
            var pendingLetter=Paper();
            const string chinese="[授权书]玩家=Jack；期限=1天；权益=建造[/授权书]\n正文：期限=2小时";
            book.BeginEdit(chief,pendingLetter); book.EndEdit(chief,chinese,"待领取",true);
            var pending=tribe.Authorizations.Single();
            check(!pending.Active(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),"Chinese letter activated before receipt.");
            var pendingSave=JsonConvert.DeserializeObject<TribeState>(System.Text.Encoding.UTF8.GetString(saved["vscncsTribeState"]))!;
            check(pendingSave.Tribes[0].Authorizations.Single().Pending
                && pendingSave.Tribes[0].AuthorizationHistory.Last().DurationMinutes==1440,"Pending duration must survive restart.");
            Call("RevokeAuthorization",chief,pending.Id); Call("ActivateParchment",guest,pendingLetter);
            check(tribe.Authorizations.Count==0 && pendingLetter.Itemstack!.Attributes.GetString("text").EndsWith("正文：期限=2小时"),
                "Cancelled pending letter reactivated or revocation changed the body.");
            tribe.Members.Add("guest"); var memberLetter=Paper();
            book.BeginEdit(chief,memberLetter); book.EndEdit(chief,"[授权书]玩家=Jack；期限=永久；权益=邀请成员、迁移主厅[/授权书]","成员委任",true);
            check(tribe.Authorizations.Single().Pending && !(bool)Call("HasTribePermission",tribe,"guest",TribePermission.Invite)!,"Pending member rights leaked.");
            Call("ActivateParchment",guest,memberLetter);
            check((bool)Call("HasTribePermission",tribe,"guest",TribePermission.Invite)!,"Received member letter did not grant rights.");
            var unreceived=Paper(); book.BeginEdit(chief,unreceived); book.EndEdit(chief,chinese,"未领取旧授权",true);
            check((bool)Call("EndReincarnationAuthorizations","guest")!,"Reincarnation did not cancel received grants.");
            Call("SaveState"); Call("SyncContractSlot",memberLetter); Call("SyncContractSlot",unreceived);
            life.Players["guest"].GameName="New Jack";
            Call("ActivateParchment",guest,memberLetter); Call("ActivateParchment",guest,unreceived);
            check(tribe.Authorizations.Count==0 && tribe.AuthorizationHistory.TakeLast(2).All(h=>h.Status=="reincarnated" && h.EndedAt>0)
                && !(bool)Call("HasTribePermission",tribe,"guest",TribePermission.Invite)!,
                "Old active or pending parchment restored rights after reincarnation.");
            check(unreceived.Itemstack!.Attributes.GetString("text").Contains("期限=0分钟")
                && memberLetter.Itemstack!.Attributes.GetString("text").Contains("期限=0分钟"),"Reincarnation must invalidate old parchment text.");
            check(JsonConvert.DeserializeObject<TribeState>(System.Text.Encoding.UTF8.GetString(saved["vscncsTribeState"]))!.Tribes[0].Authorizations.Count==0,
                "Reincarnation cancellation must persist across restart.");
            check(errors.Count >= 9, "Invalid native signatures did not report errors.");
        }
        finally { Set("instance", previous); }
    }
}
