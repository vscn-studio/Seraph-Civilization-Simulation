using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.GameContent;

namespace SeraphCivilizationSimulation;

public sealed record ParchmentContract(long Minutes, string Name, bool Construction, bool Benefits)
{
    public TribePermission Permissions { get; init; }
    private static string Header(string text) => text.TrimStart().Split('\n', 2)[0].TrimEnd();
    public static bool LooksLike(string? text) => text != null && (Header(text).StartsWith("[授权书]", StringComparison.Ordinal) || Header(text).StartsWith('[')
        && Regex.IsMatch(Header(text), @"\b(time|auth|name)\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    public static bool TryParse(string text, out ParchmentContract? contract)
    {
        contract = null;
        if (text.Length > ModSystemEditableBook.MaxTextLength) return false;
        string header = Header(text);
        if (header.StartsWith("[授权书]", StringComparison.Ordinal)) return ParseKeywords(header, out contract);
        if (!header.StartsWith('[') || !header.EndsWith(']')) return false;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in header[1..^1].Split(','))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !fields.TryAdd(pair[0], pair[1])) return false;
        }
        if (fields.Count != 3 || !fields.TryGetValue("time", out var duration) || !fields.TryGetValue("auth", out var scope)
            || !fields.TryGetValue("name", out var name) || name.Length == 0 || name.IndexOfAny(['[', ']', '\r', '\n']) >= 0
            || !long.TryParse(duration, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long minutes)
            || minutes != -1 && minutes is not (> 0 and <= 5256000)) return false;
        scope = scope.ToLowerInvariant();
        if (scope is not ("build" or "benefits" or "both")) return false;
        contract = new(minutes, name, scope is "build" or "both", scope is "benefits" or "both");
        return true;
    }

    private static bool ParseKeywords(string header, out ParchmentContract? contract)
    {
        contract = null;
        if (!header.EndsWith("[/授权书]", StringComparison.Ordinal)) return false;
        string body=header[5..^6];
        var fields=new Dictionary<string,string>();
        foreach (var part in body.Split(['；',';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair=part.Split(['=', '：', ':'],2,StringSplitOptions.TrimEntries);
            if (pair.Length!=2) return false;
            string key=pair[0] switch { "玩家" or "姓名" or "被授权人"=>"name", "期限" or "时长" or "有效期"=>"time", "权益" or "权限" or "授权"=>"scope", _=>"" };
            if (key.Length==0 || !fields.TryAdd(key,pair[1])) return false;
        }
        if(fields.Count!=3 || !fields.TryGetValue("name",out var name) || name.Length is <1 or >32 || name.IndexOfAny(['[',']','\r','\n'])>=0
            || !fields.TryGetValue("time",out var duration) || !fields.TryGetValue("scope",out var scope)) return false;
        long minutes=-1;
        if (duration is not ("永久" or "-1"))
        {
            var match=Regex.Match(duration,@"^(\d+)\s*(分钟|小时|天)$",RegexOptions.CultureInvariant);
            if(!match.Success || !long.TryParse(match.Groups[1].Value,out long n) || n<=0 || n>5256000) return false;
            minutes=n*(match.Groups[2].Value switch { "小时"=>60L,"天"=>1440L,_=>1L });
            if(minutes>5256000) return false;
        }
        bool construction=false,benefits=false; TribePermission permissions=TribePermission.None;
        foreach (var word in scope.Split(['、',',','，',' '],StringSplitOptions.RemoveEmptyEntries))
        {
            switch(word)
            {
                case "建造": case "建筑": construction=true; break;
                case "增益": case "领地增益": benefits=true; break;
                case "授权": case "访客授权": permissions|=TribePermission.Authorize; break;
                case "邀请": case "邀请成员": permissions|=TribePermission.Invite; break;
                case "移除成员": permissions|=TribePermission.Kick; break;
                case "转交首领": permissions|=TribePermission.TransferLeader; break;
                case "迁移主厅": permissions|=TribePermission.MoveCenter; break;
                default: return false;
            }
        }
        if(!construction && !benefits && permissions==TribePermission.None) return false;
        contract=new(minutes,name,construction,benefits) {Permissions=permissions}; return true;
    }

    public static string ZeroTime(string text)
    {
        int start = text.Length - text.TrimStart().Length;
        int end = text.IndexOf('\n', start);
        if (end < 0) end = text.Length;
        if (Header(text).StartsWith("[授权书]",StringComparison.Ordinal))
            return text[..start]+Regex.Replace(text[start..end],@"((?:期限|时长|有效期)\s*[=：:]\s*)[^；;\r\n\[]+","${1}0分钟",RegexOptions.CultureInvariant)+text[end..];
        return text[..start] + Regex.Replace(text[start..end], @"(\btime\s*=\s*)[-+]?\d+", "${1}0", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) + text[end..];
    }
}

public sealed partial class VscnCivilizationModSystem
{
    internal const string ContractIdAttribute = "vscncsAuthorizationId";
    internal const string ContractTribeAttribute = "vscncsAuthorizationTribe";
    private readonly List<WeakReference<InventoryBase>> contractInventories = [];
    private readonly ConditionalWeakTable<IInventory, object> failedContractInventories = new();
    private bool contractSyncFailed;

    // The native server edit session owns the slot. Client-provided signatures never issue permissions.
    internal string SignParchmentContract(IServerPlayer leader, ItemSlot slot, string text, string? title)
    {
        var tribe = tribeState.FindMember(leader.PlayerUID);
        if (tribe == null || !HasTribePermission(tribe, leader.PlayerUID, TribePermission.Authorize)) return Ui(leader, "leader-only");
        var settings = config.TribeAuthorization;
        if (!settings.Enabled || !PaperContractsSupported) return Ui(leader, "auth-contract-disabled");
        if (!ParchmentContract.TryParse(text, out var parsed)) return Ui(leader, "auth-contract-invalid");
        var stack = slot.Itemstack;
        if (stack?.Collectible.Code?.ToString() != "game:paper-parchment" || stack.StackSize != 1
            || stack.Attributes.HasAttribute("signedby") || stack.Attributes.HasAttribute(ContractIdAttribute)) return Ui(leader, "auth-contract-paper");
        if (parsed!.Construction && !settings.ConstructionEnabled || parsed.Benefits && !settings.BenefitsEnabled)
            return Ui(leader, "auth-no-scope");
        // Match the character name only; account and combined member labels are display metadata.
        var targets = lifeState.Players.Where(p => p.Value.Accepted && CivilizationIdentity.ValidName(p.Value.FullName)
            && p.Value.FullName.Equals(parsed.Name, StringComparison.Ordinal)).Take(2).ToList();
        if (targets.Count != 1) return Ui(leader, "auth-name-not-unique");
        string uid = targets[0].Key;
        if (uid==tribe.LeaderUid || parsed.Permissions!=TribePermission.None && (!tribe.Members.Contains(uid) || tribe.LeaderUid!=leader.PlayerUID)
            || tribe.Members.Contains(uid) && tribe.LeaderUid!=leader.PlayerUID) return Ui(leader,"leader-only");
        // Separate signed contracts retain independent scopes and deadlines. The window can revoke each one.
        var record = IssueAuthorization(tribe, leader.PlayerUID, uid, parsed.Minutes, parsed.Construction, parsed.Benefits, text);
        record.DurationMinutes=parsed.Minutes; record.Status="pending"; record.IssuedAt=0; record.ExpiresAt=0; record.Permissions=parsed.Permissions;
        var grant=tribe.Authorizations.Last(); grant.Pending=true; grant.ExpiresAt=0; grant.Permissions=parsed.Permissions;
        var before = stack.Attributes.Clone();
        stack.Attributes.SetString("text", text);
        stack.Attributes.SetString("title", (title ?? "")[..Math.Min(title?.Length ?? 0, ModSystemEditableBook.MaxTitleLength)]);
        stack.Attributes.SetString("signedby", record.SignerName);
        stack.Attributes.SetString("signedbyuid", leader.PlayerUID);
        stack.Attributes.SetString(ContractIdAttribute, record.Id);
        stack.Attributes.SetString(ContractTribeAttribute, tribe.Id);
        try { SaveState(); }
        catch
        {
            stack.Attributes = before;
            tribe.Authorizations.RemoveAll(a => a.Id == record.Id); tribe.AuthorizationHistory.Remove(record);
            throw;
        }
        slot.MarkDirty(); RefreshCivilizationViews();
        return "";
    }

    internal bool SyncContractStack(ItemStack? stack)
    {
        if (stack == null || !stack.Attributes.HasAttribute(ContractIdAttribute)) return false;
        string id = stack.Attributes.GetString(ContractIdAttribute);
        var tribe = tribeState.Find(stack.Attributes.GetString(ContractTribeAttribute));
        var record = tribe?.AuthorizationHistory.FirstOrDefault(h => h.Id == id && h.ContractText.Length > 0
            && h.SignerUid == stack.Attributes.GetString("signedbyuid"));
        bool active = record != null && record.Status == "active" && (record.ExpiresAt == -1 || record.ExpiresAt > AuthorizationNow)
            && tribe!.Authorizations.Any(a => a.Id == id && a.Active(AuthorizationNow));
        string text = record?.ContractText ?? stack.Attributes.GetString("text", "");
        if (!active && record?.Status != "pending") text = ParchmentContract.ZeroTime(text);
        if (stack.Attributes.GetString("text", "") == text) return false;
        stack.Attributes.SetString("text", text);
        return true;
    }

    internal void SyncContractSlot(ItemSlot? slot) { if (slot != null && SyncContractStack(slot.Itemstack)) slot.MarkDirty(); }

    // Creative inventories are item catalogues, not owned stacks. On the server their tab may never be initialized.
    private static bool IsContractInventory(IInventory inventory) => inventory is not (InventoryPlayerCreative or CreativeInventoryTab);

    private void SyncContractInventory(IInventory inventory, IServerPlayer? owner = null)
    {
        if (!IsContractInventory(inventory) || inventory is InventoryBase { Api: null }) return;
        try
        {
            foreach (var slot in inventory)
            {
                if (owner != null && slot != null) ActivateParchment(owner, slot);
                SyncContractSlot(slot);
            }
            failedContractInventories.Remove(inventory);
        }
        catch (Exception ex)
        {
            // A joining/unloading/mod inventory must not block other inventories or escape into the tick scheduler.
            // Retry next time, but report only once until this inventory has synchronized successfully.
            if (failedContractInventories.TryGetValue(inventory, out _)) return;
            failedContractInventories.Add(inventory, new());
            sapi?.Logger.Warning("Seraph Civilization Simulation parchment inventory sync deferred ({0}): {1}", inventory.GetType().FullName, ex);
        }
    }

    internal void WatchContractInventory(InventoryBase inventory)
    {
        if (!IsContractInventory(inventory)) return;
        if (!contractInventories.Any(r => r.TryGetTarget(out var target) && ReferenceEquals(target, inventory)))
            contractInventories.Add(new(inventory));
        SyncContractInventory(inventory);
    }

    private void SyncParchmentContracts()
    {
        if (sapi == null || disposed) return;
        try
        {
            SyncParchmentContractsCore();
            contractSyncFailed = false;
        }
        catch (Exception ex)
        {
            // Native tick listeners advance their last-update time only after a callback returns.
            // Contain unexpected synchronization errors so one bad source cannot cause a tight error loop.
            if (!contractSyncFailed) sapi.Logger.Warning("Seraph Civilization Simulation parchment sync deferred: {0}", ex);
            contractSyncFailed = true;
        }
    }

    private void SyncParchmentContractsCore()
    {
        foreach (var player in Online)
            if (player.Entity != null && readyPlayers.Contains(player.PlayerUID) && player.InventoryManager?.Inventories is { } inventories)
                foreach (var inventory in inventories.Values.ToArray())
                {
                    SyncContractInventory(inventory, player);
                }
        contractInventories.RemoveAll(r => !r.TryGetTarget(out _));
        foreach (var reference in contractInventories.ToArray())
            if (reference.TryGetTarget(out var inventory)) SyncContractInventory(inventory);
        if (sapi!.World.LoadedEntities == null) return;
        foreach (var entity in sapi.World.LoadedEntities.Values)
        {
            if (entity is EntityItem item && SyncContractStack(item.Itemstack)) item.WatchedAttributes.MarkAllDirty();
            if (entity.GetType().FullName == "billposting.EntityBillpost") SyncContractBillpost(entity);
        }
    }

    internal void ActivateParchment(IServerPlayer player, ItemSlot slot)
    {
        var stack=slot.Itemstack;
        if(stack?.Collectible.Code?.ToString()!="game:paper-parchment") return;
        var tribe=tribeState.Find(stack.Attributes.GetString(ContractTribeAttribute));
        var record=tribe?.AuthorizationHistory.FirstOrDefault(h=>h.Id==stack.Attributes.GetString(ContractIdAttribute)
            && h.Uid==player.PlayerUID && h.Status=="pending" && h.SignerUid==stack.Attributes.GetString("signedbyuid"));
        var grant=record==null?null:tribe!.Authorizations.FirstOrDefault(a=>a.Id==record.Id && a.Pending);
        if(record==null || grant==null || !config.TribeAuthorization.Enabled || !PaperContractsSupported) return;
        if (grant.Uid!=record.Uid || record.DurationMinutes!=-1 && record.DurationMinutes is not (>0 and <=5256000)
            || record.Construction && !config.TribeAuthorization.ConstructionEnabled || record.Benefits && !config.TribeAuthorization.BenefitsEnabled) return;
        if(!HasTribePermission(tribe!,record.SignerUid,TribePermission.Authorize)
            || record.Permissions!=TribePermission.None && (tribe!.LeaderUid!=record.SignerUid || !tribe.Members.Contains(player.PlayerUID))) return;
        long now=AuthorizationNow;
        record.IssuedAt=now; record.ExpiresAt=record.DurationMinutes==-1?-1:now+record.DurationMinutes*60000;
        record.Status="active"; grant.ExpiresAt=record.ExpiresAt; grant.Pending=false;
        try { SaveState(); }
        catch {record.IssuedAt=record.ExpiresAt=grant.ExpiresAt=0;record.Status="pending";grant.Pending=true;throw;}
        NotifyAuthorization(tribe!,record); slot.MarkDirty(); RefreshCivilizationViews();
    }

    internal void SyncContractBillpost(Entity entity)
    {
        var bytes = entity.WatchedAttributes.GetBytes("billpostdropstackbytes");
        if (bytes == null) return;
        var stack = new ItemStack(bytes);
        if (!SyncContractStack(stack)) return;
        // Billposting reads these bytes again when the parchment is taken off the wall.
        entity.WatchedAttributes.SetBytes("billpostdropstackbytes", stack.ToBytes());
        entity.WatchedAttributes.SetItemstack("billpostdropstack", stack);
        entity.WatchedAttributes.SetString("text", stack.Attributes.GetString("text", ""));
        entity.WatchedAttributes.MarkAllDirty();
        ParchmentBookCompatibility.RefreshBillpost(entity);
    }
}

// Shared reference count supports a local server and client in the same process.
internal sealed class ParchmentBookCompatibility : IDisposable
{
    private const string PatchId = "vsscsmod.parchment";
    private static readonly object Gate = new();
    private static int users;
    private bool disposed;
    private static readonly Dictionary<string, ItemStack> editingStacks = [];
    private static VscnCivilizationModSystem? Server => VscnCivilizationModSystem.ContractServer;

    internal ParchmentBookCompatibility()
    {
        lock (Gate)
        {
            if (users == 0) Install();
            users++;
        }
    }

    private static void Install()
    {
        var harmony = new Harmony(PatchId);
        try
        {
            harmony.Patch(AccessTools.Method(typeof(ModSystemEditableBook), nameof(ModSystemEditableBook.BeginEdit)),
                prefix: new HarmonyMethod(typeof(ParchmentBookCompatibility), nameof(BeforeBeginEdit)));
            harmony.Patch(AccessTools.Method(typeof(ModSystemEditableBook), nameof(ModSystemEditableBook.EndEdit)),
                prefix: new HarmonyMethod(typeof(ParchmentBookCompatibility), nameof(BeforeEndEdit)));
            harmony.Patch(AccessTools.Method(typeof(ModSystemEditableBook), nameof(ModSystemEditableBook.CancelEdit)),
                postfix: new HarmonyMethod(typeof(ParchmentBookCompatibility), nameof(AfterCancelEdit)));
            harmony.Patch(AccessTools.Method(typeof(InventoryBase), nameof(InventoryBase.Open)),
                prefix: new HarmonyMethod(typeof(ParchmentBookCompatibility), nameof(BeforeInventoryOpen)));
            harmony.Patch(AccessTools.Method(typeof(ModSystemEditableBook), nameof(ModSystemEditableBook.Transcribe)),
                prefix: new HarmonyMethod(typeof(ParchmentBookCompatibility), nameof(BeforeTranscribe)));
            var billpost = AccessTools.TypeByName("billposting.EntityBillpost");
            if (billpost != null)
            {
                harmony.Patch(AccessTools.DeclaredMethod(billpost, "Initialize"),
                    postfix: new HarmonyMethod(typeof(ParchmentBookCompatibility), nameof(AfterBillpostInitialize)));
                harmony.Patch(AccessTools.DeclaredMethod(billpost, "OnInteract"),
                    prefix: new HarmonyMethod(typeof(ParchmentBookCompatibility), nameof(BeforeBillpostInteract)));
            }
        }
        catch { harmony.UnpatchAll(PatchId); throw; }
    }

    private static void BeforeBeginEdit(IPlayer player, ItemSlot slot, ICoreAPI ___api)
    {
        if (___api.Side != EnumAppSide.Server) return;
        Server?.SyncContractSlot(slot);
        if (slot.Itemstack != null) editingStacks[player.PlayerUID] = slot.Itemstack;
    }

    private static bool BeforeEndEdit(IPlayer player, string text, string title, bool didSign,
        ICoreAPI ___api, Dictionary<string, ItemSlot> ___nowEditing)
    {
        if (___api.Side != EnumAppSide.Server || player is not IServerPlayer serverPlayer) return true;
        editingStacks.Remove(player.PlayerUID, out var opened);
        if (!___nowEditing.TryGetValue(player.PlayerUID, out var slot)) return true;
        bool bound = slot.Itemstack?.Attributes.HasAttribute(VscnCivilizationModSystem.ContractIdAttribute) == true;
        if (!bound && (!didSign || !ParchmentContract.LooksLike(text))) return true;
        ___nowEditing.Remove(player.PlayerUID);
        string error;
        if (bound || !ReferenceEquals(opened, slot.Itemstack) || !ItemBook.isWritingTool(player.Entity.LeftHandItemSlot))
            error = Server?.ContractUi(serverPlayer, "auth-contract-paper") ?? "Invalid parchment edit session.";
        else
        {
            try { error = Server?.SignParchmentContract(serverPlayer, slot, text, title) ?? "Authorization server unavailable."; }
            catch (Exception ex)
            {
                ___api.Logger.Error("Seraph Civilization Simulation parchment signing failed for {0}: {1}", player.PlayerUID, ex);
                error = Server?.ContractUi(serverPlayer, "auth-contract-save-failed") ?? "Contract signing failed.";
            }
        }
        slot.MarkDirty();
        if (error.Length > 0) serverPlayer.SendIngameError("vscncs-contract", error);
        return false;
    }

    private static void AfterCancelEdit(IPlayer player, ICoreAPI ___api)
    { if (___api.Side == EnumAppSide.Server) editingStacks.Remove(player.PlayerUID); }

    private static void BeforeInventoryOpen(InventoryBase __instance, IPlayer player)
    { if (player.Entity?.World?.Side == EnumAppSide.Server) Server?.WatchContractInventory(__instance); }

    private static bool BeforeTranscribe(IPlayer player, ItemSlot bookSlot)
    {
        if (bookSlot.Itemstack?.Attributes.HasAttribute(VscnCivilizationModSystem.ContractIdAttribute) != true) return true;
        if (player is IServerPlayer server) server.SendIngameError("vscncs-contract", Server?.ContractUi(server, "auth-contract-transcribe") ?? "Use the original signed contract.");
        return false;
    }

    private static void BeforeBillpostInteract(Entity __instance)
    { if (__instance.World.Side == EnumAppSide.Server) Server?.SyncContractBillpost(__instance); }

    private static void AfterBillpostInitialize(Entity __instance)
    {
        __instance.WatchedAttributes.RegisterModifiedListener("text", () => RefreshBillpost(__instance));
        if (__instance.World.Side == EnumAppSide.Server) Server?.SyncContractBillpost(__instance);
    }

    internal static void RefreshBillpost(Entity entity)
    {
        string text = entity.WatchedAttributes.GetString("text", "");
        var type = entity.GetType();
        AccessTools.Field(type, "BillpostText").SetValue(entity, text);
        var renderer = AccessTools.Field(type, "billpostRenderer").GetValue(entity);
        if (renderer == null) return;
        var attributes = entity.Properties.Attributes;
        int color = ColorUtil.ToRgba(255, attributes["colorR"].AsInt(), attributes["colorG"].AsInt(), attributes["colorB"].AsInt());
        AccessTools.Method(renderer.GetType(), "SetNewText").Invoke(renderer, [text[..Math.Min(4096, text.Length)], color]);
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (disposed) return;
            disposed = true;
            if (--users != 0) return;
            new Harmony(PatchId).UnpatchAll(PatchId); editingStacks.Clear();
        }
    }
}

public sealed partial class VscnCivilizationModSystem
{
    // Legacy contract readers are retained for save migration only. No book or Billposting patches are installed.
    private static bool PaperContractsSupported => false;
    internal static VscnCivilizationModSystem? ContractServer => instance is { disposed: false } ? instance : null;
    internal string ContractUi(IServerPlayer player, string key) => Ui(player, key);
}
