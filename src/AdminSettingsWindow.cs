using System.Globalization;
using System.Reflection;
using System.Security;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace SeraphCivilizationSimulation;

internal sealed class AdminSettingsWindow(ICoreClientAPI api, Action<AdminSettingsRequest> send) : GuiDialog(api)
{
    private JObject document = new();
    private readonly Dictionary<string, string> edits = [];
    private readonly Dictionary<string, GuiElementTextInput> inputs = [];
    private string revision = "";
    private bool disposed;
    private (int Width, int Height, double Scale) layout;
    public override string ToggleKeyCombinationCode => null!;
    public override bool UnregisterOnClose => true;

    internal void Update(AdminSettingsPacket packet)
    {
        if (disposed) return;
        if (packet.Message.Length > 0) capi.ShowChatMessage(packet.Message);
        if (!packet.ReplaceDraft) return;
        document=JObject.Parse(packet.Json); revision=packet.Revision; edits.Clear();
        Compose();
        if (!IsOpened()) TryOpen();
    }

    private void Request(string action)
    {
        try
        {
            var copy=(JObject)document.DeepClone();
            if (action=="save")
                foreach (var (path,text) in edits)
                {
                    var original=document.SelectToken(path)!;
                    JToken value;
                    try
                    {
                        value=original.Type==JTokenType.String ? new JValue(text) : JToken.Parse(text);
                        if (original.Type==JTokenType.Integer && value.Type!=JTokenType.Integer
                            || original.Type==JTokenType.Float && value.Type is not (JTokenType.Float or JTokenType.Integer)
                            || original.Type==JTokenType.Array && value.Type!=JTokenType.Array
                            || original.Type==JTokenType.Object && value.Type!=JTokenType.Object)
                            throw new ArgumentException("输入类型不匹配");
                    }
                    catch (Exception ex) { throw new ArgumentException(AdminSettingText.Title(path)+"："+ex.Message); }
                    copy.SelectToken(path)!.Replace(value);
                }
            send(new() { Action=action, Revision=revision, Json=action=="save"?copy.ToString(Formatting.None):"" });
        }
        catch (Exception ex) { capi.ShowChatMessage("无法保存："+ex.Message); }
    }

    private void Compose()
    {
        layout=(capi.Render.FrameWidth,capi.Render.FrameHeight,RuntimeEnv.GUIScale);
        double scale=Math.Max(.25,layout.Scale);
        double width=Math.Min(820,Math.Max(240,layout.Width/scale-40)), height=Math.Min(660,Math.Max(240,layout.Height/scale-48));
        string? focused=inputs.FirstOrDefault(p=>p.Value.HasFocus).Key;
        int caret=focused==null?0:inputs[focused].CaretPosInLine;
        float scroll=SingleComposer?.GetElement("scrollbar") is GuiElementScrollbar old?old.CurrentYPosition:0;
        ClearComposers(); inputs.Clear();
        var composer=capi.Gui.CreateCompo("vsscs-admin-settings",ElementBounds.Fixed(0,0,width,height).WithAlignment(EnumDialogArea.CenterMiddle))
            .AddStaticCustomDraw(ElementBounds.Fill,(ctx,surface,bounds)=>
            {
                ctx.SetSourceRGB(GuiStyle.DialogStrongBgColor[0],GuiStyle.DialogStrongBgColor[1],GuiStyle.DialogStrongBgColor[2]);
                GuiElement.RoundRectangle(ctx,bounds.drawX,bounds.drawY,bounds.InnerWidth,bounds.InnerHeight,GuiStyle.DialogBGRadius);
                ctx.Fill();
            })
            .AddShadedDialogBG(ElementBounds.Fill,alpha:1)
            .AddDialogTitleBar("管理员设置",()=>TryClose());
        SingleComposer=composer;
        var view=ElementBounds.Fixed(20,50,width-40,height-70);
        double footer=EconomyGuiList.Footer(capi,composer,view,
            ("save","保存并应用",()=>Request("save")),("reload","热重载",()=>Request("reload")),
            ("reset","重置",()=>Request("defaults")),("refresh","刷新",()=>Request("refresh")));
        var list=new EconomyGuiList(capi,composer,ElementBounds.Fixed(view.fixedX,view.fixedY,view.fixedWidth,view.fixedHeight-footer));
        AddObject((JObject)document["Main"]!,typeof(VscnConfig),"Main");
        AddObject((JObject)document["Lore"]!,typeof(LoreRestrictionsConfig),"Lore");
        list.Finish(); composer.Compose(false);
        var bar=(GuiElementAutoScrollbar)composer.GetScrollbar("scrollbar");
        double viewport=list.Container.Bounds.ParentBounds.InnerHeight, total=list.Container.Bounds.OuterHeight;
        bar.SetContentHeights((float)viewport,(float)Math.Max(viewport,total));
        bar.CurrentYPosition=Math.Clamp(scroll,0,(float)Math.Max(0,total-viewport)); bar.TriggerChanged();
        if (focused!=null && inputs.TryGetValue(focused,out var input))
        { composer.FocusElement(list.Container.TabIndex); list.Container.FocusElement(input.TabIndex); input.SetCaretPos(Math.Min(caret,input.GetText().Length)); }

        void AddObject(JObject value, Type type, string prefix)
        {
            list.Text(AdminSettingText.Title(prefix),heading:true);
            foreach (var property in value.Properties())
            {
                string path=prefix+"."+property.Name;
                Type fieldType=type.GetProperty(property.Name,BindingFlags.Public|BindingFlags.Instance)?.PropertyType ?? typeof(string);
                if (property.Value is JObject child && !fieldType.IsGenericType) { AddObject(child,fieldType,path); continue; }
                AddField(property.Value,fieldType,path);
            }
            list.EndRow();
        }

        void AddField(JToken value, Type type, string path)
        {
            string label=AdminSettingText.Title(path), hint=AdminSettingText.Description(path);
            double top=list.Y, labelWidth=Math.Clamp(list.Width*.43,76,300), x=labelWidth+12, fieldWidth=list.Width-x-12;
            var font=CairoFont.WhiteSmallText();
            double labelHeight=Math.Max(24,capi.Gui.Text.GetMultilineTextHeight(font,label,GuiElement.scaled(labelWidth))/scale+4);
            var labelBounds=ElementBounds.Fixed(0,top+4,labelWidth,labelHeight);
            list.Container.Add(new GuiElementWrappedText(capi,label,labelBounds,font));
            list.Container.Add(new GuiElementHoverText(capi,SecurityElement.Escape(hint),CairoFont.WhiteSmallText(),
                (int)Math.Min(420,width-48),labelBounds.FlatCopy()));
            string text=edits.GetValueOrDefault(path,value.Type==JTokenType.String?value.Value<string>()??"":value.ToString(Formatting.None));
            double fieldHeight=32;
            if (type==typeof(bool))
                list.Container.Add(new GuiElementSwitch(capi,on=>edits[path]=on?"true":"false",ElementBounds.Fixed(x,top,24,24),24) { On=bool.Parse(text) });
            else if (type.IsEnum)
            {
                var names=Enum.GetNames(type);
                var titles=names.Select(name=>name switch { "Whitelist"=>"白名单","Blacklist"=>"黑名单","Sequential"=>"顺序","Balanced"=>"均衡","Random"=>"随机",_=>name }).ToArray();
                list.Container.Add(new GuiElementDropDown(capi,names,titles,Math.Max(0,Array.IndexOf(names,text)),
                    (code,_)=>edits[path]=code,ElementBounds.Fixed(x,top,fieldWidth,32),CairoFont.WhiteSmallText(),false));
            }
            else if (value is JContainer || path.EndsWith(".HeaderSvg",StringComparison.Ordinal))
            {
                fieldHeight=112;
                var editor=new GuiElementEconomyTaskEditor(capi,ElementBounds.Fixed(x,top,fieldWidth-20,fieldHeight),changed=>edits[path]=changed);
                editor.Scrollbar=new GuiElementAutoScrollbar(capi,y=>editor.ScrollTo(y),ElementBounds.Fixed(x+fieldWidth-16,top,16,fieldHeight));
                editor.OnTryTextChangeText=lines=>lines.Sum(line=>line.Length)<=262144;
                list.Container.Add(editor); list.Container.Add(editor.Scrollbar);
                editor.Bounds.CalcWorldBounds(); editor.SetValue(text,false); editor.RefreshScroll();
            }
            else
            {
                var input=new GuiElementTextInput(capi,ElementBounds.Fixed(x,top,fieldWidth,32),changed=>edits[path]=changed,CairoFont.TextInput());
                input.OnTryTextChangeText=lines=>lines.Sum(line=>line.Length)<=65536;
                list.Container.Add(input); input.SetValue(text); inputs[path]=input;
            }
            list.Y=top+Math.Max(labelHeight+4,fieldHeight)+12;
        }
    }

    public override void OnRenderGUI(float dt)
    {
        if (!IsOpened() || disposed) return;
        if (layout!=(capi.Render.FrameWidth,capi.Render.FrameHeight,RuntimeEnv.GUIScale)) Compose();
        base.OnRenderGUI(dt);
    }
    public override void Dispose() { if (disposed) return; disposed=true; TryClose(); document.RemoveAll(); edits.Clear(); base.Dispose(); }
}
