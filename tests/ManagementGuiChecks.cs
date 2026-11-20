using HarmonyLib;
using Newtonsoft.Json.Linq;
using SeraphCivilizationSimulation;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.Common;

internal static class ManagementGuiChecks
{
    internal static void Run(ICoreClientAPI api,Action<bool,string> check,Action<bool> privilege,
        Action<GuiDialog,string> capture,Action<GuiDialog,GuiElement> click,bool coreOnly=false)
    {
        foreach(bool administrator in new[]{false,true})
        {
            privilege(administrator);
            int selected=-1;
            using var core=new CivilizationCoreHud(api,page=>selected=page);
            core.Show(); click(core,core.SingleComposer.GetElement("core"));
            var menu=core.Composers["core-menu"];
            int count=administrator?6:5;
            check((menu.GetElement("entry-5")!=null)==administrator,"Only administrators may see the settings menu entry.");
            for(int i=0;i<count;i++)
            {
                var b=menu.GetElement("entry-"+i).Bounds; var outer=menu.Bounds;
                check(b.renderX>=outer.renderX && b.renderY>=outer.renderY && b.renderX+b.OuterWidth<=outer.renderX+outer.OuterWidth
                    && b.renderY+b.OuterHeight<=outer.renderY+outer.OuterHeight,"Core buttons must fit within the upward menu border at every GUI scale.");
                if(i>0) { var previous=menu.GetElement("entry-"+(i-1)).Bounds; check(previous.renderY+previous.OuterHeight<=b.renderY,"Core menu buttons must not overlap."); }
            }
            capture(core,$"core-menu-{(administrator?"admin":"player")}-{RuntimeEnv.GUIScale:0.###}");
            click(core,menu.GetElement("entry-"+(count-1)));
            check(selected==count-1 && core.Composers["core-menu"]==null,"Menu click must select the correct independent window.");
            core.ToggleKeyboardMenu();
            check(core.DisableMouseGrab && core.KeyboardMenuOpen,"Keyboard opening must release the mouse through the native HUD contract.");
            core.OnRenderGUI(0);
            check(core.Composers["core-menu"]!=null,"Keyboard menu must remain open with the pointer away from the core.");
            var escape=new KeyEvent { KeyCode=(int)GlKeys.Escape }; core.OnKeyDown(escape);
            check(escape.Handled && !core.DisableMouseGrab && core.Composers["core-menu"]==null,"Escape must dismiss the menu and restore normal mouse grabbing.");
            core.ToggleKeyboardMenu(); core.ToggleKeyboardMenu();
            check(!core.DisableMouseGrab && core.Composers["core-menu"]==null,"A second tap must close the keyboard menu.");
            core.ToggleKeyboardMenu();
            click(core,core.Composers["core-menu"].GetElement("entry-0"));
            check(selected==0 && !core.DisableMouseGrab,"Selecting a page must release the keyboard menu's mouse override.");
            core.ToggleKeyboardMenu();
            var outside=new MouseEvent(api.Render.FrameWidth-1,1,EnumMouseButton.Left,0); core.OnMouseDown(outside);
            check(outside.Handled && !core.DisableMouseGrab,"Clicking outside must dismiss the keyboard menu and consume the world interaction.");
        }
        if(coreOnly) { privilege(false); return; }
        var sent=new List<AdminSettingsRequest>();
        using(var admin=new AdminSettingsWindow(api,sent.Add))
        {
            var document=AdminSettingsData.Document(new(),new());
            admin.Update(new(){Allowed=true,Json=document.ToString(),Revision="r1"});
            var composer=admin.SingleComposer;
            check(admin.IsOpened() && composer.Bounds.OuterWidth<=api.Render.FrameWidth && composer.Bounds.OuterHeight<=api.Render.FrameHeight,"Administrator settings must fit the scaled screen.");
            var container=composer.GetContainer("economy-content");
            var controls=container.Elements.Where(e=>e is GuiElementTextInput or GuiElementSwitch or GuiElementDropDown or GuiElementEconomyTaskEditor).ToArray();
            int fields=document.Descendants().OfType<JProperty>().Count(p=>p.Value is not JObject);
            check(controls.Length==fields,"The form must expose every serialized main and lore setting.");
            check(container.Elements.OfType<GuiElementHoverText>().Count()==fields,"Each setting label requires a detailed hover description.");
            foreach(var control in controls)
                check(control.Bounds.fixedX>0 && control.Bounds.fixedX+control.Bounds.fixedWidth<=container.Bounds.fixedWidth+.01,"Setting inputs must fit inside the right column.");
            var buttons=new[]{"save","reload","reset","refresh"}.Select(key=>composer.GetButton("economy-footer-"+key)).ToArray();
            check(buttons.All(b=>b.Bounds.renderY==buttons[0].Bounds.renderY),"Settings actions must occupy a single fixed row.");
            check(container.Bounds.ParentBounds.renderY+container.Bounds.ParentBounds.OuterHeight<buttons[0].Bounds.renderY,"Settings scrolling content must not overlap the action row.");
            capture(admin,"admin-settings");
            var hint=container.Elements.OfType<GuiElementHoverText>().First();
            click(admin,hint); capture(admin,"admin-settings-hover");
            check(hint.IsNowShown,"Hovering a setting label must display its detailed native tooltip above the form.");
            var inputs=(Dictionary<string,GuiElementTextInput>)AccessTools.Field(typeof(AdminSettingsWindow),"inputs").GetValue(admin)!;
            inputs["Main.PaperMaps.MaxSheets"].SetValue("32");
            var bar=composer.GetScrollbar("scrollbar"); double y=buttons[0].Bounds.renderY;
            bar.CurrentYPosition=(float)Math.Max(0,container.Bounds.OuterHeight-container.Bounds.ParentBounds.InnerHeight); bar.TriggerChanged();
            check(buttons[0].Bounds.renderY==y,"Scrolling settings must keep the actions fixed.");
            capture(admin,"admin-settings-bottom");
            click(admin,buttons[0]);
            check(sent.Last().Action=="save" && sent.Last().Revision=="r1" && JObject.Parse(sent.Last().Json)["Main"]!["PaperMaps"]!["MaxSheets"]!.Value<int>()==32,"Save must send edited typed values and the server revision.");
            for(int i=1;i<buttons.Length;i++) click(admin,buttons[i]);
            check(sent.Skip(1).Select(p=>p.Action).SequenceEqual(new[]{"reload","defaults","refresh"}),"Footer actions must map to reload, defaults draft and refresh.");
        }
        var requests=new List<CivilizationRequestPacket>();
        using var window=new VscnClusterWindow(api,requests.Add);
        var snapshot=new CivilizationSnapshotPacket { Open=true,MyTribeId="tribe",MyTribeName="测试部落",IsLeader=true,
            AuthorizationEnabled=true,ConstructionAuthorizationEnabled=true,BenefitAuthorizationEnabled=true };
        window.Update(snapshot);
        AccessTools.Method(typeof(VscnClusterWindow),"ChangePage").Invoke(window,[2]); window.OnRenderGUI(0);
        window.SingleComposer.GetVerticalTab("tribe-tabs").SetValue(1,true); window.OnRenderGUI(0);
        var body=window.SingleComposer.GetContainer("economy-content");
        var authInputs=body.Elements.OfType<GuiElementTextInput>().ToArray();
        check(authInputs.Length==2,"Window authorization requires player name and duration inputs.");
        authInputs[0].SetValue("访客"); authInputs[1].SetValue("-1");
        var grant=body.Elements.OfType<GuiElementTextButton>().Single(b=>b.Text==Lang.Get("vsscsmod:ui.auth-confirm"));
        var scrollbar=window.SingleComposer.GetScrollbar("scrollbar"); scrollbar.CurrentYPosition=(float)(grant.Bounds.fixedY*RuntimeEnv.GUIScale); scrollbar.TriggerChanged();
        click(window,grant);
        check(requests.Last() is { Action:"authorize",Name:"访客",DurationMinutes:"-1" },"Window authorization must submit the edited visitor and duration.");
        scrollbar.CurrentYPosition=0; scrollbar.TriggerChanged(); capture(window,"window-authorization");
        snapshot.IsLeader=false; window.Update(snapshot); window.OnRenderGUI(0);
        check(!window.SingleComposer.GetContainer("economy-content").Elements.OfType<GuiElementTextInput>().Any(),"Ordinary members must not see grant controls.");
        check(AccessTools.Field(typeof(VscnClientModSystem),"serverAnnouncementHud")==null,"The announcement overlay must be retired from the client.");
        privilege(false);
    }
}
