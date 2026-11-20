using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnClusterWindow
{
    private int tribePage, composedTribePage = -1;
    private string pendingTribeAction = "", pendingTribeTarget = "";
    private bool CanManage(TribePermission permission) => snapshot.IsLeader || (snapshot.Permissions & permission) == permission;
    private string GrantScopes(TribeAuthorizationView grant) => string.Join(" · ",
        new[] { grant.Construction ? L("auth-construction") : "", grant.Benefits ? L("auth-benefits") : "" }
        .Concat(Enum.GetValues<TribePermission>().Where(p => p != TribePermission.None && p != TribePermission.All && p != TribePermission.Outposts && p != TribePermission.Territory && (grant.Permissions & p) != 0)
            .Select(p => L("permission-" + p.ToString().ToLowerInvariant()))).Where(s => s.Length > 0));
    private readonly List<GuiElementTextInput> authorizationInputs = [];
    private string authorizationName = "", authorizationDuration = "60";
    private bool authorizationConstruction = true, authorizationBenefits = true;
    private TribePermission authorizationPermissions;
    private readonly List<(GuiElementDynamicText Text, long Expires)> authorizationClocks = [];
    private long authorizationReceivedAt, authorizationClockTick;
    private string[] TribeTabs => [L("tribe-details"), L("tribe-authorization")];
    private double TribeTabWidth => Math.Clamp(TribeTabs.Max(t => CairoFont.WhiteSmallText().GetTextExtents(t).Width
        / Math.Max(.25, RuntimeEnv.GUIScale)) + 24, 64, 100);

    private void ComposeTribe(GuiComposer composer, ElementBounds view)
    {
        authorizationInputs.Clear(); authorizationClocks.Clear();
        composer.AddVerticalTabs(TribeTabs.Select(t => new GuiTab { Name = t }).ToArray(),
            ElementBounds.Fixed(-TribeTabWidth, 56, TribeTabWidth, 110),
            (index, _) => { tribePage = index; dirty = true; }, "tribe-tabs");
        composer.GetVerticalTab("tribe-tabs").SetValue(tribePage, false);

        if (tribePage==0) { ComposeTribeDetails(composer,view); return; }
        var list = new EconomyGuiList(capi, composer, view); content = list.Container;
        if (!snapshot.AuthorizationEnabled) list.Text(L("auth-disabled"), detail: true);
        else if (snapshot.MyTribeId != null && CanManage(TribePermission.Authorize))
        {
            authorizationInputs.Add(list.Field("游戏名", authorizationName, value => authorizationName=value, 32));
            authorizationInputs.Add(list.Field("有效分钟", authorizationDuration, value => authorizationDuration=value, 12));
            list.Text("留空姓名可对准 3 格内玩家；-1 表示不限时。", detail: true);
            if (snapshot.ConstructionAuthorizationEnabled) list.Toggle(L("auth-construction"),authorizationConstruction,value=>authorizationConstruction=value);
            if (snapshot.BenefitAuthorizationEnabled) list.Toggle(L("auth-benefits"),authorizationBenefits,value=>authorizationBenefits=value);
            if (snapshot.IsLeader)
            {
                list.Text("成员管理权限", heading: true);
                foreach (var permission in new[] { TribePermission.Invite, TribePermission.Kick, TribePermission.Authorize, TribePermission.MoveCenter, TribePermission.TransferLeader })
                    list.Toggle(L("permission-"+permission.ToString().ToLowerInvariant()),(authorizationPermissions & permission)!=0,
                        value=>authorizationPermissions=value?authorizationPermissions|permission:authorizationPermissions & ~permission);
                list.Text("管理权限仅可授予本部落成员。",detail:true);
            }
            list.Buttons((L("auth-confirm"),()=>send(new CivilizationRequestPacket { Action="authorize",Name=authorizationName,
                DurationMinutes=authorizationDuration,Construction=authorizationConstruction,Benefits=authorizationBenefits,
                Permissions=snapshot.IsLeader?authorizationPermissions:TribePermission.None })));
            list.EndRow();
        }
        if (CanManage(TribePermission.Authorize))
        {
            list.Text(L("auth-issued"), heading: true);
            if (snapshot.Authorizations.Count == 0) list.Text(L("auth-empty"), detail: true);
            foreach (var grant in snapshot.Authorizations)
            {
                Grant(grant, false);
                if (snapshot.IsLeader || grant.Permissions == TribePermission.None && !snapshot.Members.Any(m => m.Uid == grant.Uid))
                    list.Buttons((L("auth-revoke"), () => Action("revoke-authorization", grant.Id.Length > 0 ? grant.Id : grant.Uid)));
                list.EndRow();
            }
        }
        else if (!snapshot.IsLeader) list.Text(L("auth-leader-help"), detail: true);
        list.Text(L("auth-received"), heading: true);
        if (snapshot.ReceivedAuthorizations.Count == 0) list.Text(L("auth-empty"), detail: true);
        foreach (var grant in snapshot.ReceivedAuthorizations) { Grant(grant, true); list.EndRow(); }
        list.Text(L("auth-history"), heading: true);
        if (snapshot.AuthorizationHistory.Count == 0) list.Text(L("auth-empty"), detail: true);
        foreach (var grant in snapshot.AuthorizationHistory)
        {
            list.Text(grant.TribeName + " · " + grant.Name, heading: true);
            list.Text(GrantScopes(grant), detail: true);
            list.Text(L("auth-status-" + grant.Status)+" · "+grant.SignerName, detail: true);
            list.Text("签发："+HistoryDate(grant.IssuedAt)+"\n到期："+(grant.ExpiresAt == -1 ? L("auth-permanent") : HistoryDate(grant.ExpiresAt)), detail: true);
            if (grant.EndedAt > 0) list.Text(L("auth-history-ended", HistoryDate(grant.EndedAt)), detail: true);
            list.EndRow();
        }
        list.Finish();

        void Grant(TribeAuthorizationView grant, bool received)
        {
            list.Text(received ? grant.TribeName : grant.Name, heading: true);
            list.Text(GrantScopes(grant), detail: true);
            if (grant.IssuedAt > 0) list.Text(HistoryDate(grant.IssuedAt), detail: true);
            var clock = new GuiElementDynamicText(capi, AuthorizationTime(grant.ExpiresAt), CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(0, list.Y, list.Width - 8, 28));
            content.Add(clock); authorizationClocks.Add((clock, grant.ExpiresAt)); list.Y += 36;
        }
    }

    private static string HistoryDate(long time) => time <= 0 ? "—"
        : DateTimeOffset.FromUnixTimeMilliseconds(time).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private string AuthorizationTime(long expires)
    {
        if (expires == 0) return "待领取";
        if (expires == -1) return L("auth-permanent");
        long seconds = Math.Max(0, (expires - snapshot.ServerTime - (Environment.TickCount64 - authorizationReceivedAt) + 999) / 1000);
        return L("auth-remaining", seconds / 60, seconds % 60);
    }

    private void TickAuthorizationClocks()
    {
        if (page != TribePage || tribePage != 1 || Environment.TickCount64 < authorizationClockTick) return;
        authorizationClockTick = Environment.TickCount64 + 1000;
        foreach (var (text, expires) in authorizationClocks) text.SetNewText(AuthorizationTime(expires));
    }
}
