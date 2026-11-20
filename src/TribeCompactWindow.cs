using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace SeraphCivilizationSimulation;

public sealed partial class VscnClusterWindow
{
    private string selectedMember="";
    private bool territoryPreview;
    private ElementBounds? footerViewport, footerContent;
    private double footerOffset, footerMaximum;
    private bool ScrollTribeFooter(MouseWheelEventArgs args)
    {
        if (page!=TribePage || tribePage!=0 || footerViewport?.PointInside(capi.Input.MouseX,capi.Input.MouseY)!=true || footerMaximum<=0) return false;
        MoveFooter(-args.delta*70); args.SetHandled(); return true;
    }
    private void MoveFooter(double amount)
    {
        footerOffset=Math.Clamp(footerOffset+amount,0,footerMaximum);
        if (footerContent!=null) { footerContent.fixedX=-footerOffset; footerContent.CalcWorldBounds(); }
    }
    private void ComposeTribeDetails(GuiComposer composer,ElementBounds view)
    {
        footerViewport=footerContent=null;
        var area=view.FlatCopy();
        if (snapshot.MyTribeId!=null) area.fixedHeight=Math.Max(70,view.fixedHeight-110);
        var list=new EconomyGuiList(capi,composer,area); content=list.Container;
        if (snapshot.MyTribeId==null)
        {
            list.Text("在当前位置创建部落，范围固定 16×16 格，无需主厅。",detail:true);
            nameInput=list.Input(tribeName,v=>tribeName=v,10);
            list.Buttons((L("create-tribe"),()=>Action("create",name:tribeName)));
        }
        else
        {
            if (renameEditing) nameInput=list.Input(renameName,v=>renameName=v,10);
            else list.Text(snapshot.MyTribeName??"",heading:true);
            foreach (var effect in snapshot.TerritoryEffects) list.Text(L(effect.Code,effect.Value.ToString("+0.##;-0.##;0")),detail:true);
            double column=(list.Width-12)/2;
            for (int i=0;i<snapshot.Members.Count;i+=2)
            {
                double top=list.Y;
                bool stacked=column<180;
                double rowHeight=62;
                for (int col=0;col<2 && i+col<snapshot.Members.Count;col++)
                {
                    var member=snapshot.Members[i+col]; double x=col*(column+12), portrait=Math.Min(48,column*(stacked?.45:.28));
                    content.Add(new GuiElementTribePortrait(capi,member.Uid,ElementBounds.Fixed(x,top,portrait,portrait)));
                    string text=member.Name+(member.IsLeader?" · 首领":"");
                    var font=CairoFont.WhiteSmallText(); double available=Math.Max(25,column-(stacked?4:portrait+12));
                    string label=text;
                    double measured=font.GetTextExtents(label).Width/Math.Max(.25,RuntimeEnv.GUIScale);
                    if (measured>available) font.UnscaledFontsize*=Math.Max(.75,available/measured);
                    bool Fits(string value)=>font.GetTextExtents(value).Width/Math.Max(.25,RuntimeEnv.GUIScale)<=available;
                    if (!Fits(label)) label=member.Name;
                    int separator=label.IndexOf('(');
                    if (!Fits(label) && separator>0 && label.EndsWith(')'))
                    {
                        string account=label[..separator], game=label[(separator+1)..^1];
                        int a=account.Length, g=game.Length;
                        string Format()=>account[..a]+(a<account.Length?"…":"")+"("+game[..g]+(g<game.Length?"…":"")+")";
                        while (!Fits(Format()) && (a>1 || g>1)) { if(a>1 && (a>=g || g<=1)) a--; else g--; }
                        label=Format();
                    }
                    else if (!Fits(label))
                    {
                        while(label.Length>1 && !Fits(label+"…")) label=label[..^1];
                        label+="…";
                    }
                    double buttonX=stacked?x:x+portrait+6, buttonY=top+(stacked?portrait+4:5);
                    var button=new GuiElementTextButton(capi,label,font,font.Clone().WithColor(GuiStyle.ActiveButtonTextColor),
                        ()=> {selectedMember=selectedMember==member.Uid?"":member.Uid;dirty=true;return true;},
                        ElementBounds.Fixed(buttonX,buttonY,available,32),EnumButtonStyle.Small);
                    content.Add(button);
                    content.Add(new GuiElementHoverText(capi,text,CairoFont.WhiteSmallText(),220,button.Bounds.FlatCopy(),OpaqueTooltipBackground()));
                    rowHeight=Math.Max(rowHeight,buttonY-top+44);
                }
                list.Y=top+rowHeight;
            }
        }
        if (pendingTribeAction.Length>0)
        {
            list.Text(pendingTribeAction=="leave"?"确认退出部落？":"确认对所选成员执行此操作？");
            list.Buttons((L("confirm"),()=> {Action(pendingTribeAction,pendingTribeTarget);pendingTribeAction="";dirty=true;}),
                (L("cancel"),()=> {pendingTribeAction="";dirty=true;}));
        }
        foreach (var invite in snapshot.Invitations)
        {
            list.Text(invite.TribeName+" · "+invite.InviterName);
            list.Buttons((L("accept"),()=>Action("accept",invite.TribeId)),(L("decline"),()=>Action("decline",invite.TribeId)));
        }
        list.Finish();
        if (snapshot.MyTribeId==null) return;
        string status=$"{snapshot.TerritoryMemberCount} 人 · 固定范围 16×16 格（256 格）\n中心向下 10 格 · 向上无限 · 不随人数变化";
        composer.AddStaticText(status,CairoFont.WhiteDetailText(),ElementBounds.Fixed(view.fixedX,view.fixedY+view.fixedHeight-100,view.fixedWidth,58),"territory-status");
        List<(string Key,string Text,Action Click,bool Enabled)> actions=[];
        void Add(string key,string text,Action click,bool enabled=true)=>actions.Add((key,text,click,enabled));
        if (renameEditing)
        {
            Add("save",L("save"),()=> {Action("rename",name:renameName);renameEditing=false;dirty=true;});
            Add("cancel",L("cancel"),()=> {renameEditing=false;dirty=true;});
        }
        else if (snapshot.IsLeader) Add("edit-name",L("edit-name"),()=> {renameName=snapshot.MyTribeName??"";renameEditing=true;dirty=true;});
        if (CanManage(TribePermission.Invite)) Add("invite-nearby",L("invite-nearby"),()=>Action("invite"));
        if (CanManage(TribePermission.MoveCenter)) Add("move-center","迁移部落中心",()=>Action("move-center"));
        Add("leave",L("leave"),()=> {pendingTribeAction="leave";pendingTribeTarget="";dirty=true;});
        Add("territory-view","查看领地",()=> {territoryPreview=!territoryPreview; Action(territoryPreview?"territory-preview":"territory-clear-preview");});
        var selected=snapshot.Members.FirstOrDefault(m=>m.Uid==selectedMember);
        if (selected!=null)
        {
            if(CanManage(TribePermission.Kick) && !selected.IsLeader && selected.Uid!=capi.World.Player.PlayerUID)
                Add("kick",L("kick"),()=> {pendingTribeAction="kick";pendingTribeTarget=selected.Uid;dirty=true;});
            if(CanManage(TribePermission.TransferLeader) && !selected.IsLeader)
                Add("transfer-leader",L("transfer-leader"),()=> {pendingTribeAction="transfer-leader";pendingTribeTarget=selected.Uid;dirty=true;});
        }
        var fontButton=CairoFont.SmallButtonText(EnumButtonStyle.Small);
        var widths=actions.Select(a=>fontButton.GetTextExtents(a.Text).Width/Math.Max(.25,RuntimeEnv.GUIScale)+20).ToArray();
        double total=widths.Sum()+6*(actions.Count-1), topFooter=view.fixedY+view.fixedHeight-36;
        bool overflow=total>view.fixedWidth;
        footerViewport=ElementBounds.Fixed(view.fixedX+(overflow?26:0),topFooter,view.fixedWidth-(overflow?52:0),36);
        footerMaximum=Math.Max(0,total-footerViewport.fixedWidth);footerOffset=Math.Clamp(footerOffset,0,footerMaximum);
        footerContent=ElementBounds.Fixed(-footerOffset,0,Math.Max(total,footerViewport.fixedWidth),36);
        composer.BeginClip(footerViewport).AddContainer(footerContent,"tribe-footer");
        var container=composer.GetContainer("tribe-footer"); double left=0;
        for(int i=0;i<actions.Count;i++)
        {
            var action=actions[i];
            var button=new GuiElementTextButton(capi,action.Text,fontButton,fontButton.Clone().WithColor(GuiStyle.ActiveButtonTextColor),
                ()=> {if(action.Enabled)action.Click();return true;},ElementBounds.Fixed(left,0,widths[i],30),EnumButtonStyle.Small) {Enabled=action.Enabled};
            container.Add(button);left+=widths[i]+6;
        }
        composer.EndClip();
        if(overflow)
        {
            composer.AddSmallButton("‹",()=> {MoveFooter(-140);return true;},ElementBounds.Fixed(view.fixedX,topFooter,22,30),key:"footer-left");
            composer.AddSmallButton("›",()=> {MoveFooter(140);return true;},ElementBounds.Fixed(view.fixedX+view.fixedWidth-22,topFooter,22,30),key:"footer-right");
        }
    }
}
