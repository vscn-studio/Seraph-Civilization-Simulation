using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Cairo;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using SeraphCivilizationSimulation;
using Path = System.IO.Path;

// Opt-in Windows smoke check: compose real game controls with Cairo, replacing only GPU uploads and client services.
internal static class NativeGuiChecks
{
    private sealed class CharacterStartupDialog(ICoreClientAPI api)
        : Vintagestory.GameContent.GuiDialogCreateCharacter(api, null!)
    {
        public override void OnGuiOpened() { }
        public override void OnGuiClosed() { }
    }

    private sealed class PortraitRenderer(Entity entity, ICoreClientAPI api) : EntityRenderer(entity, api)
    {
        public override void Dispose() { }
    }
    public static void Run(Action<bool, string> check)
    {
        string game = Environment.GetEnvironmentVariable("VSCN_GUI_GAME_PATH") ?? "E:/vintagestory/Vintagestory";
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            string path = Path.GetFullPath(Path.Combine(game, "Lib", name.Name + ".dll"));
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        }
        AssemblyLoadContext.Default.Resolving += Resolve;
        NativeLibrary.SetDllImportResolver(typeof(Context).Assembly, (name, _, _) =>
            NativeLibrary.Load(Path.GetFullPath(Path.Combine(game, "Lib", name == "nanosvg" ? "nanosvg.dll" : "libcairo-2.dll"))));
        try { Compose(game, check); }
        finally { AssemblyLoadContext.Default.Resolving -= Resolve; }
    }

    private static void Compose(string game, Action<bool, string> check)
    {
        var errors = new List<string>();
        var requests = new List<CivilizationRequestPacket>();
        int frameWidth = 1280, frameHeight = 800, nextTexture = 1;
        int mouseX = 0, mouseY = 0;
        bool playerReady = true, gamePaused = false;
        Context? captureContext = null;
        var textures = new Dictionary<int, ImageSurface>();
        var textureClamp = new Dictionary<int, bool>();
        var renderedMarkers = new List<int>();
        var textureDraws = new List<(int Id, double X, double Y, double Width, double Height, float Depth)>();
        var svgLoads = new List<(AssetLocation Location, int Color, int Size, int TextureId)>();
        int escapedItemRenders = 0;
        var renderedPortraitClips = new List<ElementBounds?>();
        var renderedItemStacks = new List<(ItemSlot Slot, bool ShowQuantity, float Size)>();
        var openedLinks = new List<string>();
        var registeredRenderers = new HashSet<IRenderer>();
        double[] projection = Mat4d.Create();
        Mat4d.Scale(projection, projection, [.005, .005, .005]);
        var scissorStack = new Stack<ElementBounds>();
        string scenario = "";
        string output = Path.GetFullPath("artifacts/native-gui-smoke");
        Directory.CreateDirectory(output);
        string imageFixture = Path.Combine(output, "notice-image-fixture.png");
        using (var image = new ImageSurface(Format.Argb32, 160, 80))
        using (var brush = new Context(image))
        {
            brush.SetSourceRGB(.8, .25, .1); brush.Paint();
            brush.Rectangle(80, 0, 80, 80); brush.SetSourceRGBA(.1, .4, .8, .5); brush.Operator = Operator.Source; brush.Fill();
            image.WriteToPng(imageFixture);
        }
        byte[] fixtureBytes = File.ReadAllBytes(imageFixture);
        byte[]? publicModImage = null;
        using (var bitmap = SkiaSharp.SKBitmap.Decode(fixtureBytes))
        {
            foreach (var format in new[] { SkiaSharp.SKEncodedImageFormat.Jpeg, SkiaSharp.SKEncodedImageFormat.Webp })
            {
                using var encoded = bitmap.Encode(format, 85);
                using var decoded = AnnouncementImages.Decode(encoded.ToArray());
                check(decoded?.Width == 160 && decoded.Height == 80, "Native announcement images must decode " + format + " without an extra codec library.");
            }
        }
        if (Environment.GetEnvironmentVariable("VSCN_ANNOUNCEMENT_NETWORK_SMOKE") == "1")
        {
            foreach (string source in new[] { "https://mods.vintagestory.at/web/img/mod-default.png", "https://upload.wikimedia.org/wikipedia/commons/7/70/Example.png" })
            {
                byte[]? downloaded = AnnouncementImages.Request(source).GetAwaiter().GetResult();
                using var decoded = AnnouncementImages.Decode(downloaded);
                check(decoded != null && decoded.Width > 0 && decoded.Height > 0, "A real public HTTPS image must pass DNS filtering, TLS, download and native decoding: " + source);
                if (source.Contains("mods.vintagestory.at")) publicModImage = downloaded;
            }
        }
        using (var decoded = AnnouncementImages.Decode(fixtureBytes))
        {
            check(decoded != null && decoded.Width == 160 && decoded.Height == 80, "PNG images must decode with their actual dimensions.");
            decoded!.Flush(); var pixels = decoded.Data;
            int i = 120 * 4;
            check(pixels[i + 3] is >= 127 and <= 128 && pixels[i] <= pixels[i + 3] && pixels[i + 1] <= pixels[i + 3],
                "Image transparency must use premultiplied Cairo pixels without dark or bright alpha fringes.");
        }
        check(AnnouncementImages.Decode([1, 2, 3]) == null && AnnouncementImages.Decode(new byte[AnnouncementImages.MaxBytes + 1]) == null,
            "Corrupt and oversized image payloads must fail without crashing the native renderer.");
        using (var large = new ImageSurface(Format.Argb32, 2048, 1025))
        {
            string file = Path.Combine(output, "notice-too-large.png"); large.WriteToPng(file);
            check(AnnouncementImages.Decode(File.ReadAllBytes(file)) == null, "Image dimensions must be checked before allocating decoded pixels.");
        }
        var translations = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText("assets/vsscsmod/lang/zh-cn.json"))!;
        var oldLanguage = Lang.AvailableLanguages["en"];
        string oldLocale = Lang.CurrentLocale;
        Lang.ChangeLanguage("en");
        Lang.AvailableLanguages["en"] = Proxy.Make<ITranslationService>((m, a) =>
        {
            if (m.Name == "get_LineBreakBehavior") return EnumLinebreakBehavior.AfterWord;
            if (!m.Name.StartsWith("Get") || a?[0] is not string key) return null;
            string translated = translations.GetValueOrDefault(key.Replace("vsscsmod:", ""), key);
            return a.Length > 1 && a[1] is object[] { Length: > 0 } values ? string.Format(translated, values) : translated;
        });
        float oldScale = RuntimeEnv.GUIScale;
        double oldBackgroundAlpha = GuiStyle.DialogStrongBgColor[3];
        string oldFont = GuiStyle.StandardFontName, oldDecorativeFont = GuiStyle.DecorativeFontName;
        GuiStyle.StandardFontName = "Microsoft YaHei Light";
        GuiStyle.DecorativeFontName = "Microsoft YaHei";
        GuiStyle.DialogStrongBgColor[3] = .15;
        ICoreClientAPI? api = null;
        IconUtil? icons = null;
        var dialogs = new List<GuiDialog>();
        var composers = new Dictionary<string, GuiComposer>();
        var composerManager = Proxy.Make<IGuiComposerManager>((m, a) => m.Name == "get_Composers" ? composers : null);
        var logger = Proxy.Make<ILogger>((m, a) => { if (m.Name == "Error") errors.Add(string.Join(" ", a ?? [])); return null; });
        IAsset? ReadAsset(AssetLocation loc)
        {
            string root = loc.Domain == "vsscsmod" ? "assets" : Path.Combine(game, "assets");
            string file = Path.Combine(root, loc.Domain, loc.Path);
            if (loc.ToString() == "vsscsmod:textures/tests/notice.png") file = imageFixture;
            if (!File.Exists(file)) return null;
            byte[] bytes = File.ReadAllBytes(file);
            return Proxy.Make<IAsset>((method, args) => method.Name switch
            { "get_Data" => bytes, "get_Location" => loc, "get_Name" => Path.GetFileName(loc.Path), "ToText" => System.Text.Encoding.UTF8.GetString(bytes), _ => null });
        }
        var assets = Proxy.Make<IAssetManager>((m, a) =>
        {
            if (m.Name == "get_AllAssets") return new Dictionary<AssetLocation,IAsset> {
                [new("vsscsmod:textures/tests/notice.png")] = ReadAsset(new("vsscsmod:textures/tests/notice.png"))!,
                [new("game:config/handbook/test.json")] = Proxy.Make<IAsset>((method,args)=>method.Name=="ToObject" ? new Vintagestory.GameContent.GuiHandbookTextPage { pageCode="craftinginfo-MixedCase",Title="Test handbook" } : null)
            };
            if (m.Name == "GetMany" && m.IsGenericMethod) return new Dictionary<AssetLocation,Vintagestory.GameContent.GuiHandbookTextPage> {
                [new("game:config/handbook/test.json")] = new() { pageCode="craftinginfo-MixedCase", Title="Test handbook" }
            };
            if (m.Name == "GetMany" && a?[0] is string prefix)
                return Directory.GetDirectories(Path.Combine(game, "assets"))
                    .SelectMany(domain => Directory.Exists(Path.Combine(domain, prefix))
                        ? Directory.GetFiles(Path.Combine(domain, prefix), "*.svg").Select(file =>
                            ReadAsset(new AssetLocation(Path.GetFileName(domain), prefix + Path.GetFileName(file)))!) : [])
                    .ToList();
            return m.Name is "Get" or "TryGet" && a?[0] is AssetLocation loc ? ReadAsset(loc) : null;
        });
        var render = Proxy.Make<IRenderAPI>((m, a) =>
        {
            if (m.Name == "LoadOrUpdateTextureFromBgra")
            {
                var into = (LoadedTexture)a![3]!;
                if (textures.Remove(into.TextureId, out var oldTexture)) oldTexture.Dispose();
                using var uploaded = new ImageSurface(Format.Argb32, into.Width, into.Height);
                Marshal.Copy((int[])a[0]!, 0, uploaded.DataPtr, into.Width * into.Height);
                uploaded.MarkDirty();
                into.TextureId = RememberTexture(uploaded, (int)a[2]! == 1);
                a[3] = into;
                return null;
            }
            if (m.Name == "RenderItemstackToGui" && a?[0] is ItemSlot slot)
            {
                renderedItemStacks.Add((slot, (bool)a[^1]!, Convert.ToSingle(a[4])));
                if (captureContext != null && slot.Inventory.GetType().Name == "EconomyPreviewInventory"
                    && (scissorStack.Count == 0 || scissorStack.Peek().fixedWidth > 80)) escapedItemRenders++;
            }
            if (m.Name == "Render2DTexturePremultipliedAlpha" && a?[0] is int markerId) renderedMarkers.Add(markerId);
            if (m.Name == "Render2DTexturePremultipliedAlpha" && a?[0] is int drawId)
                textureDraws.Add(a[1] is ElementBounds drawBounds
                    ? (drawId, drawBounds.renderX, drawBounds.renderY, drawBounds.OuterWidth, drawBounds.OuterHeight, Convert.ToSingle(a[2]))
                    : (drawId, Convert.ToDouble(a[1]), Convert.ToDouble(a[2]), Convert.ToDouble(a[3]), Convert.ToDouble(a[4]), Convert.ToSingle(a[5])));
            if (captureContext != null)
            {
                if (m.Name == "RenderEntityToGui") renderedPortraitClips.Add(scissorStack.TryPeek(out var portraitClip) ? portraitClip : null);
                if (m.Name == "PushScissor")
                {
                    var clip = (ElementBounds)a![0]!;
                    scissorStack.Push(clip);
                    captureContext.Save();
                    captureContext.Rectangle(clip.renderX, clip.renderY, clip.OuterWidth, clip.OuterHeight);
                    captureContext.Clip();
                }
                if (m.Name == "PopScissor") { scissorStack.Pop(); captureContext.Restore(); }
                int id = a is { Length: > 0 } && a[0] is LoadedTexture loaded ? loaded.TextureId
                    : a is { Length: > 0 } && a[0] is int rawId ? rawId : -1;
                if ((m.Name.StartsWith("Render2DTexture") || m.Name == "Render2DLoadedTexture") && textures.TryGetValue(id, out var texture))
                {
                    double x, y, width, height;
                    if (m.Name == "Render2DLoadedTexture")
                        (x, y, width, height) = (Convert.ToDouble(a![1]), Convert.ToDouble(a[2]), texture.Width, texture.Height);
                    else if (a![1] is ElementBounds bounds)
                        (x, y, width, height) = (bounds.renderX, bounds.renderY, bounds.OuterWidth, bounds.OuterHeight);
                    else (x, y, width, height) = (Convert.ToDouble(a[1]), Convert.ToDouble(a[2]), Convert.ToDouble(a[3]), Convert.ToDouble(a[4]));
                    captureContext.Save();
                    captureContext.Translate(x, y);
                    captureContext.Scale(width / texture.Width, height / texture.Height);
                    // Match the game's textured quad, including GL_REPEAT for Cairo uploads.
                    // SetSourceSurface's default Extend.None hid edge wrapping in earlier checks.
                    captureContext.Rectangle(0, 0, texture.Width, texture.Height);
                    captureContext.Clip();
                    using var pattern = new SurfacePattern(texture)
                    {
                        Extend = textureClamp.GetValueOrDefault(id) ? Extend.Pad : Extend.Repeat,
                        Filter = Filter.Bilinear
                    };
                    captureContext.SetSource(pattern);
                    captureContext.Paint();
                    captureContext.Restore();
                }
            }
            if (m.Name is "DeleteTexture" or "GLDeleteTexture" && a?[0] is int deleted && textures.Remove(deleted, out var removed)) removed.Dispose();
            return m.Name switch
            {
                "get_FrameWidth" => frameWidth, "get_FrameHeight" => frameHeight,
                "get_PerspectiveProjectionMat" => projection, "get_PerspectiveViewMat" => Mat4d.Create(),
                "get_ScissorStack" => scissorStack,
                "BitmapCreateFromPng" => new BitmapExternal((byte[])a![0]!, ((byte[])a[0]!).Length, logger),
                _ => null
            };
        });
        int RememberTexture(ImageSurface surface, bool clamp = false)
        {
            int id = nextTexture++;
            var copy = new ImageSurface(Format.Argb32, surface.Width, surface.Height);
            using var ctx = new Context(copy);
            ctx.SetSourceSurface(surface, 0, 0);
            ctx.Paint();
            textures[id] = copy;
            textureClamp[id] = clamp;
            return id;
        }
        Vintagestory.Client.NoObf.SvgLoader? svgLoader = null;
        ImageSurface RasterizeIcon(IAsset asset, int size, int color)
        {
            svgLoader ??= new Vintagestory.Client.NoObf.SvgLoader(api!);
            byte[] rgba = svgLoader.rasterizeSvg(asset, size, size, size, size, color);
            // LoadSvg uploads RGBA to OpenGL; use BGRA only for this Cairo framebuffer substitute.
            for (int i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
            var surface = new ImageSurface(Format.Argb32, size, size);
            Marshal.Copy(rgba, 0, surface.DataPtr, rgba.Length); surface.MarkDirty();
            return surface;
        }
        var gui = Proxy.Make<IGuiAPI>((m, a) =>
        {
            switch (m.Name)
            {
                case "get_Icons": return icons;
                case "get_Text": return new TextDrawUtil();
                case "get_TextTexture": return new TextTextureUtil(api!);
                case "get_WindowBounds":
                    var screen = ElementBounds.Fixed(0, 0, frameWidth / RuntimeEnv.GUIScale, frameHeight / RuntimeEnv.GUIScale).WithEmptyParent();
                    screen.CalcWorldBounds(); return screen;
                case "get_LoadedGuis": return dialogs;
                case "OpenLink": openedLinks.Add((string)a![0]!); return null;
                case "get_OpenedGuis": return dialogs.Where(d=>d.IsOpened()).ToList();
                case "RegisterDialog": dialogs.AddRange((GuiDialog[])a![0]!); return null;
                case "CreateCompo":
                    var bounds = (ElementBounds)a![1]!;
                    bounds.ParentBounds = ElementBounds.Fixed(0, 0, frameWidth / RuntimeEnv.GUIScale, frameHeight / RuntimeEnv.GUIScale).WithEmptyParent();
                    bounds.ParentBounds.CalcWorldBounds();
                    var composer = (GuiComposer)Activator.CreateInstance(typeof(GuiComposer), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, [api, bounds, (string)a[0]!], null)!;
                    typeof(GuiComposer).GetField("composerManager", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(composer, composerManager);
                    composers[(string)a[0]!] = composer;
                    return composer;
                case "LoadCairoTexture": return RememberTexture((ImageSurface)a![0]!);
                case "LoadSvg":
                    var location = (AssetLocation)a![0]!;
                    if (!location.Path.StartsWith("textures/icons/worldmap/"))
                        throw new InvalidOperationException("Only native world-map SVG resources should be loaded by the atlas.");
                    int iconSize = (int)a[1]!, iconColor = (int)a[5]!;
                    using (var icon = RasterizeIcon(ReadAsset(location)!, iconSize, iconColor))
                    {
                        int id = RememberTexture(icon);
                        svgLoads.Add((location, iconColor, iconSize, id));
                        return new LoadedTexture(api!, id, iconSize, iconSize);
                    }
                case "DeleteTexture":
                    if (textures.Remove((int)a![0]!, out var deleted)) deleted.Dispose();
                    return null;
                case "LoadOrUpdateCairoTexture":
                    var surface = (ImageSurface)a![0]!;
                    var texture = (LoadedTexture?)a[2] ?? new LoadedTexture(api!);
                    if (textures.Remove(texture.TextureId, out var previousTexture)) previousTexture.Dispose();
                    texture.TextureId = RememberTexture(surface); texture.Width = surface.Width; texture.Height = surface.Height;
                    a[2] = texture;
                    if (surface.Width > 200 && surface.Height > 80)
                        surface.WriteToPng(Path.Combine(output, $"{scenario}-{surface.Width}x{surface.Height}.png"));
                    return null;
                case "DrawSvg": throw new InvalidOperationException("The tribe GUI must not load old SVG icons.");
                default: return null;
            }
        });
        var playerEntity = new EntityPlayer();
        var inventoryNetwork = Proxy.Make<IInventoryNetworkUtil>((m, a) => null);
        var registry = Proxy.Make<IClassRegistryAPI>((m, a) => m.Name == "CreateInvNetworkUtil" ? inventoryNetwork : null);
        var mouseInventory = new InventoryGeneric(1, "mouse-chief", null!);
        mouseInventory.InvNetworkUtil = inventoryNetwork;
        var inventoryManager = Proxy.Make<IPlayerInventoryManager>((m, a) => m.Name switch
        { "GetOwnInventory" => (string)a![0]! == "mouse" ? mouseInventory : null,
            "get_MouseItemSlot" => mouseInventory[0], _ => null });
        bool administrator = false;
        var player = Proxy.Make<IClientPlayer>((m, a) => m.Name switch
        { "HasPrivilege" => administrator, "get_PlayerUID" => "chief", "get_Entity" => playerEntity, "get_InventoryManager" => inventoryManager, _ => null });
        var worldConfig = new TreeAttribute();
        var coinItem = new Item { Code = new AssetLocation("coinage:coin-copper-sm"), ItemId = 700, MaxStackSize = 64 };
        var silverItem = new Item { Code = new AssetLocation("coinage:coin-silver-sm"), ItemId = 701, MaxStackSize = 64 };
        var goodsItem = new Item { Code = new AssetLocation("game:firewood"), ItemId = 702, MaxStackSize = 64, CreativeInventoryTabs = ["general"] };
        var coreItem = new ItemCivilizationCore { Code = new AssetLocation("vsscsmod:civilizationcore"), ItemId = 703, MaxStackSize = 1 };
        var displayStack = new ItemStack(goodsItem); displayStack.Attributes.SetString("trade-preview", "goods");
        var goodsPreview = new EconomyItemView { Key = "wood", Name = "木柴", Code = goodsItem.Code.ToString(), Count = 32, Stack = displayStack.ToBytes() };
        var gridRecipes = new List<GridRecipe>();
        var recipeRandom = new Random(123);
        var world = Proxy.Make<IClientWorldAccessor>((m, a) => m.Name switch
        { "get_GridRecipes" => gridRecipes, "get_Rand" => recipeRandom,
            "get_Collectibles" => new List<CollectibleObject> { goodsItem,coinItem,silverItem },
            "get_Player" => player, "PlayerByUid" => a?[0] as string == "chief" ? player : null, "get_Config" => worldConfig,
            "GetItem" => a?[0] is AssetLocation coreCode && coreCode == coreItem.Code ? coreItem
                : a?[0] is int id ? id == goodsItem.ItemId ? goodsItem : id == silverItem.ItemId ? silverItem : coinItem
                : a?[0] is AssetLocation code && code == goodsItem.Code ? goodsItem : a?[0] is AssetLocation silver && silver == silverItem.Code ? silverItem : coinItem, _ => null });
        var events = Proxy.Make<IClientEventAPI>((m, a) =>
        {
            if (m.Name == "RegisterRenderer") registeredRenderers.Add((IRenderer)a![0]!);
            if (m.Name == "UnregisterRenderer") registeredRenderers.Remove((IRenderer)a![0]!);
            return null;
        });
        var rotateKeys = new Dictionary<string, HotKey>
        {
            ["map-rotate-hk"] = new() { CurrentMapping = new() { KeyCode = (int)GlKeys.R } },
            ["map-rotate-reverse-hk"] = new() { CurrentMapping = new() { KeyCode = (int)GlKeys.R, Shift = true } }
        };
        var slotHoverEvents = new List<(string Event, ItemSlot Slot)>();
        var inputApi = Proxy.Make<IInputAPI>((m, a) =>
        {
            if (m.Name is "TriggerOnMouseEnterSlot" or "TriggerOnMouseLeaveSlot") slotHoverEvents.Add((m.Name, (ItemSlot)a![0]!));
            return m.Name switch
            {
                "get_MouseX" => mouseX, "get_MouseY" => mouseY,
                "get_KeyboardKeyState" or "get_KeyboardKeyStateRaw" => new bool[300],
                "GetHotKeyByCode" => a?[0] is string code ? rotateKeys.GetValueOrDefault(code) : null, _ => null
            };
        });
        var handbookLinks = new List<string>();
        var linkProtocols = new Dictionary<string,Action<LinkTextComponent>> { ["handbook"] = link => handbookLinks.Add(link.Href) };
        api = Proxy.Make<ICoreClientAPI>((m, a) => m.Name switch
        { "get_PlayerReadyFired" => playerReady, "get_IsGamePaused" => gamePaused,
            "get_LinkProtocols" => linkProtocols, "get_Gui" => gui, "get_Render" => render, "get_Assets" => assets, "get_Logger" => logger, "get_World" => world, "get_Input" => inputApi, "get_Event" => events, "get_ClassRegistry" => registry, _ => null });
        icons = new IconUtil(api);
        RuntimeEnv.GUIScale = 1;
        var guidanceDecisions = new List<GuidanceDecision>();
        var guidancePacket = new GuidancePacket
        {
            Required = true, Nonce = "native-guidance", Title = "欢迎来到文明模拟服—复古物语中文社区",
            Settlement = "聚落信息\n当前聚落：1\n范围：250\n人数：5",
            RulesHtml = "<h2>服务器规则</h2>" + string.Concat(Enumerable.Repeat("<p>请文明交流，尊重其他玩家，禁止作弊及利用漏洞破坏游戏。</p>", 30))
        };
        scenario = "guidance-welcome";
        var welcome = new GuidanceWindow(api, guidancePacket, guidanceDecisions.Add);
        welcome.TryOpen();
        Capture(welcome);
        check(!welcome.TryClose(), "Guidance modal must not be bypassed by closing or Escape.");
        var wc = welcome.SingleComposer;
        var nameBackground = wc.GetElement("name-background");
        check(nameBackground?.GetType() == typeof(GuiElementDialogBackground)
            && !(bool)typeof(GuiElementDialogBackground).GetField("withTitlebar", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nameBackground)!,
            "Name entry must use the native background without reserving an absent titlebar.");
        var gameNameInput = wc.GetTextInput("game-name");
        check(wc.GetStaticText("game-name-label").GetText() == "玩家游戏名"
            && wc.Bounds.fixedHeight == 150 && wc.GetElement("decline") == null && wc.GetElement("rules-content") == null,
            "Name entry must be compact and contain no rules or decline control.");
        gameNameInput.SetValue("王文明_2026");
        typeof(GuidanceWindow).GetMethod("Choose", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(welcome, [true]);
        check(guidanceDecisions is [{ Agree: true, GameName: "王文明_2026", Nonce: "native-guidance" }],
            "Guidance must submit the single game name and its server challenge.");
        typeof(GuidanceWindow).GetMethod("Choose", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(welcome, [true]);
        check(guidanceDecisions.Count == 1 && !wc.GetButton("agree").Enabled, "Pending guidance requests must not submit twice.");
        welcome.Suspend();
        check(!welcome.IsOpened() && !welcome.CaptureAllInputs() && !welcome.TryClose(),
            "Suspending guidance must reveal native startup dialogs without accepting the rules.");
        welcome.TryOpen();
        check(welcome.CaptureAllInputs() && !wc.GetButton("agree").Enabled
            && wc.GetTextInput("game-name").GetText() == "王文明_2026",
            "Resuming guidance must preserve its pending decision and name while still requiring server confirmation.");
        frameWidth = 1000; frameHeight = 700; RuntimeEnv.GUIScale = 1.25f;
        welcome.OnRenderGUI(0);
        check(welcome.SingleComposer.Bounds.fixedHeight == 150
            && welcome.SingleComposer.GetTextInput("game-name").GetText() == "王文明_2026"
            && !welcome.SingleComposer.GetButton("agree").Enabled,
            "Guidance resizing must follow civilization sizing while preserving the draft, reading position and pending submission.");
        frameWidth = 1280; frameHeight = 800; RuntimeEnv.GUIScale = 1;
        welcome.OnRenderGUI(0);
        guidancePacket.Error = "这个玩家游戏名已被使用，请选择另一个。";
        welcome.Update(guidancePacket);
        check(welcome.SingleComposer.GetTextInput("game-name").GetText() == "王文明_2026"
            && welcome.SingleComposer.GetButton("agree").Enabled, "A rejected guidance decision must preserve fields and allow retry.");
        welcome.Release();

        // Reproduce an early guidance packet overlapping native character creation
        // and a local pause. Neither path may cover the UI needed to finish joining.
        var guidanceClient = new VscnClientModSystem();
        void SetClient(string name, object value) => typeof(VscnClientModSystem)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(guidanceClient, value);
        GuidanceWindow? ClientGuidance() => (GuidanceWindow?)typeof(VscnClientModSystem)
            .GetField("guidance", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(guidanceClient);
        SetClient("capi", api); SetClient("ready", true);
        SetClient("channel", Proxy.Make<IClientNetworkChannel>((m, a) =>
        { if (m.Name == "SendPacket" && a?[0] is GuidanceDecision decision) guidanceDecisions.Add(decision); return null; }));
        playerReady = false;
        Invoke(guidanceClient, "OnGuidance", guidancePacket);
        check(ClientGuidance()?.IsOpened() == true, "Name entry must open before native player readiness and character creation.");
        playerReady = true; gamePaused = true;
        Invoke(guidanceClient, "TryShowGuidance");
        check(ClientGuidance()?.IsOpened() == false, "Name entry must suspend while the local server is paused.");
        using (var characterDialog = new CharacterStartupDialog(api))
        {
            characterDialog.TryOpen(); gamePaused = false;
            Invoke(guidanceClient, "TryShowGuidance");
            check(ClientGuidance()?.IsOpened() == false, "An unrelated open character dialog must not be covered.");
            characterDialog.TryClose();
        }
        Invoke(guidanceClient, "TryShowGuidance");
        var clientWelcome = ClientGuidance()!;
        check(clientWelcome.IsOpened(), "Deferred guidance must open automatically once native startup completes, without Escape.");
        clientWelcome.SingleComposer.GetTextInput("game-name").SetValue("测试玩家");
        Invoke(clientWelcome, "Choose", true);
        int sentDecisions = guidanceDecisions.Count;
        gamePaused = true;
        Invoke(guidanceClient, "OnGuidancePauseResume", true);
        check(!clientWelcome.IsOpened() && !clientWelcome.CaptureAllInputs(),
            "A native pause must reveal its owning dialog even if guidance has already been submitted.");
        Invoke(guidanceClient, "TryShowGuidance");
        check(!clientWelcome.IsOpened(), "A pending welcome must stay suspended while the local server is paused.");
        gamePaused = false;
        Invoke(guidanceClient, "TryShowGuidance");
        check(clientWelcome.IsOpened() && !clientWelcome.SingleComposer.GetButton("agree").Enabled
            && clientWelcome.SingleComposer.GetTextInput("game-name").GetText() == "测试玩家"
            && guidanceDecisions.Count == sentDecisions,
            "Resuming must preserve the submitted decision without resending or allowing movement before confirmation.");
        gamePaused = true;
        Invoke(guidanceClient, "OnGuidance", new GuidancePacket { Required = false });
        gamePaused = false;
        Invoke(guidanceClient, "TryShowGuidance");
        check(ClientGuidance() == null && !clientWelcome.IsOpened(),
            "Acceptance received during a pause must release guidance permanently instead of reopening a stale welcome.");
        Invoke(guidanceClient, "LeaveGuidance");
        frameWidth = 800; frameHeight = 600; RuntimeEnv.GUIScale = 1.25f;
        guidancePacket.Title = "欢迎转生 新生 部落";
        scenario = "guidance-reincarnation";
        using (var rebirth = new GuidanceWindow(api, guidancePacket, guidanceDecisions.Add))
        {
            Capture(rebirth);
            check(rebirth.SingleComposer.Bounds.OuterWidth <= frameWidth && rebirth.SingleComposer.Bounds.OuterHeight <= frameHeight,
                "Reincarnation guidance must fit a small screen at fractional scale.");
            typeof(GuidanceWindow).GetMethod("Choose", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(rebirth, [false]);
            check(guidanceDecisions.Last().Agree, "Legacy decline invocation must not send a decision.");
            rebirth.Release();
        }
        frameWidth = 1280; frameHeight = 800; RuntimeEnv.GUIScale = 1;
        if (Environment.GetEnvironmentVariable("VSCN_IMMERSIVEMAPS_ZIP") is { } mapsPackage)
            ImmersiveMapGuiChecks.Run(PaperMapCompatibilityChecks.LoadAssembly(mapsPackage), api, check);
        foreach (var item in new[] { coinItem, silverItem, goodsItem })
            typeof(CollectibleObject).GetField("api", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(item, api);
        var reproduction = AnnouncementTextLayout.Components(api, AnnouncementHtml.ToNativeMarkup(AnnouncementImageChecks.PublicModCardsHtml
            .Replace("https://mods.vintagestory.at/web/img/mod-default.png", "vsscsmod:textures/tests/notice.png")));
        static IEnumerable<RichTextComponentBase> Descendants(IEnumerable<RichTextComponentBase> components)
            => components.SelectMany(c => c switch
            {
                AnnouncementCard card => new[] { c }.Concat(Descendants(card.Children)),
                AnnouncementTable table => new[] { c }.Concat(table.Cells.SelectMany(cell => Descendants(cell.Box.Children))),
                AnnouncementBox box => new[] { c }.Concat(Descendants(box.Children)),
                _ => new[] { c }
            });
        RuntimeEnv.GUIScale = 1;
        foreach (string tool in AnnouncementHtmlTools.Tools.Concat(AnnouncementHtmlTools.FormatTools).Concat(AnnouncementHtmlTools.InsertTools))
        {
            var edit = AnnouncementHtmlTools.Format("中文代码 Sample",0,0,tool,"中文代码 Sample");
            var parts = AnnouncementTextLayout.Components(api,AnnouncementDom.Markup(edit.Text));
            double measured = AnnouncementTextLayout.Measure(parts,560);
            var texts = Descendants(parts).OfType<RichTextComponent>().ToArray();
            if (tool == "checkbox")
            {
                var toggle = texts.OfType<AnnouncementSwitch>().Single();
                check(toggle.BoundsPerLine is [{ Width: > 20, Height: > 0 }],
                    "A checkbox with a sibling label must retain its drawn icon and clickable bounds.");
                var target = toggle.BoundsPerLine[0];
                var down = new MouseEvent((int)target.X + 3,(int)(target.Y + target.Height / 2),EnumMouseButton.Left,0);
                toggle.OnMouseDown(down); toggle.OnMouseUp(down);
                check(down.Handled,"The drawn checkbox must accept a click on its icon.");
            }
            check(measured > 0 && double.IsFinite(measured) && texts.All(t => !t.DisplayText.Contains('\uFFFC') && !t.DisplayText.Contains('\uFFFD')),
                "Every format/insert template must render without leaked object placeholders: " + tool);
            foreach (var text in texts)
            {
                using var face = SkiaSharp.SKTypeface.FromFamilyName(text.Font.Fontname);
                check(text.DisplayText.EnumerateRunes().Where(r=> !System.Text.Rune.IsControl(r)).All(r=> face.ContainsGlyph(r.Value)),
                    "Notice text must use a font containing its glyphs, including Chinese code: " + tool + " / " + text.Font.Fontname + " missing " + string.Join(",",text.DisplayText.EnumerateRunes().Where(r=> !System.Text.Rune.IsControl(r) && !face.ContainsGlyph(r.Value)).Select(r=>r.ToString()+":"+r.Value.ToString("X"))));
            }
            using var surface = new ImageSurface(Format.Argb32,600,Math.Clamp((int)Math.Ceiling(measured)+20,40,4000));
            using var ctx = new Context(surface); ctx.SetSourceRGB(.12,.1,.08); ctx.Paint();
            foreach(var part in parts) part.ComposeElements(ctx,surface);
            if (tool is "code" or "details" or "checkbox" or "table") surface.WriteToPng(Path.Combine(output,"html-tool-"+tool+".png"));
            foreach(var part in parts) part.Dispose();
        }
        // Handbook page codes are opaque: case, JSON quotes, percent signs and Unicode survive HTML/VTML.
        foreach (string pageCode in new[] { "craftinginfo-MixedCase", "item-firewood-{\"variant\":\"Oak%20/中文\"}", "item-example-{\"value\":\"A\\\"B\"}" })
        {
            string target=AnnouncementGameResources.HandbookTarget(pageCode);
            var parts=AnnouncementTextLayout.Components(api,AnnouncementDom.Markup("<a href=\""+target+"\">手册</a>"));
            var link=parts.OfType<LinkTextComponent>().Single(); link.Trigger();
            check(handbookLinks.Last() == "handbook://"+pageCode,"Handbook clicks must deliver the exact original page code: "+pageCode);
            foreach(var part in parts) part.Dispose();
        }
        var attemptedPages=new List<string>();
        check(AnnouncementGameResources.OpenHandbookPage("item-firewood-{\"extra\":1}", page=>{attemptedPages.Add(page);return page=="item-firewood";})
            && attemptedPages.SequenceEqual(new[]{"item-firewood-{\"extra\":1}","item-firewood"}),
            "Item handbook navigation must try the precise variant first, then the native generic page.");
        attemptedPages.Clear();
        check(!AnnouncementGameResources.OpenHandbookPage("craftinginfo-Missing",page=>{attemptedPages.Add(page);return false;}) && attemptedPages.Count==1,
            "Unavailable custom handbook pages must not navigate to an unrelated page.");
        var handbookEntry=AnnouncementGameResources.Catalog(api,"handbook",[]).Single();
        var handbookParts=AnnouncementTextLayout.Components(api,AnnouncementDom.Markup(handbookEntry.CreateHtml()));
        handbookParts.OfType<LinkTextComponent>().Single().Trigger();
        check(handbookLinks.Last()=="handbook://craftinginfo-MixedCase","Selected text handbook entries must link to their own exact page.");
        foreach(var part in handbookParts) part.Dispose();

        // Single items and explicit variant lists share the handbook's square native cell.
        var embeddedStack = displayStack.Clone(); embeddedStack.StackSize = 7;
        embeddedStack.Attributes.SetString("variant", "Oak%20/中文\"&<");
        var secondEmbeddedStack = new ItemStack(silverItem, 3);
        string singleItemHtml = AnnouncementGameResources.ItemHtml(embeddedStack);
        string slideshowHtml = AnnouncementGameResources.SlideshowHtml([embeddedStack, secondEmbeddedStack]);
        var inlineParts = AnnouncementTextLayout.Components(api, AnnouncementDom.Markup("<p>左侧文字" + singleItemHtml + "右侧文字</p>"));
        double inlineMeasure = AnnouncementTextLayout.Measure(inlineParts, 176);
        var inlineCell = Descendants(inlineParts).OfType<SlideshowItemstackTextComponent>().Single();
        check(inlineCell.BoundsPerLine[0].X > 0 && inlineCell.BoundsPerLine[0].Y == 0
            && inlineParts.OfType<RichTextComponent>().Any(c => c.DisplayText.Contains("右侧文字")),
            $"Single item embeds must remain inline with surrounding paragraph text: x={inlineCell.BoundsPerLine[0].X} y={inlineCell.BoundsPerLine[0].Y}");
        foreach (var part in inlineParts) part.Dispose();
        foreach (string itemHtml in new[] { singleItemHtml, singleItemHtml.Replace("></figure>", "><figcaption>旧名称链接</figcaption></figure>"), slideshowHtml })
        foreach (float itemScale in new[] { 1f, 1.5f })
        {
            RuntimeEnv.GUIScale = itemScale;
            var parts = AnnouncementTextLayout.Components(api, AnnouncementDom.Markup(itemHtml));
            var cell = Descendants(parts).OfType<SlideshowItemstackTextComponent>().Single();
            check(!Descendants(parts).OfType<RichTextComponent>().Any(), "Item embeds, including legacy captions, must contain only a clickable icon without a name or text link.");
            check(cell.Itemstacks[0].StackSize == 7 && cell.Itemstacks[0].Attributes.GetString("variant") == "Oak%20/中文\"&<",
                "Item embed HTML must preserve stack attributes, Unicode, special characters and quantity: " + itemHtml + " / " + cell.Itemstacks[0].Attributes.ToJsonToken() + " count=" + cell.Itemstacks[0].StackSize);
            foreach (double cellWidth in new[] { 40d, 176d })
            {
                double total = AnnouncementTextLayout.Measure(parts, cellWidth * itemScale);
                var itemBounds = cell.BoundsPerLine[0];
                var box = Descendants(parts).OfType<AnnouncementBox>().Single();
                check(cell.Background && cell.ShowStackSize && cell.ShowTooltip && itemBounds.Width == itemBounds.Height
                    && box.ContentX + itemBounds.X + itemBounds.Width <= cellWidth * itemScale,
                    "Native item cells must retain square backgrounds, quantities and tooltips even inside narrow containers.");
                using var image = new ImageSurface(Format.Argb32, (int)Math.Ceiling(cellWidth * itemScale), (int)Math.Ceiling(total));
                using var brush = new Context(image);
                foreach (var part in parts) part.ComposeElements(brush, image);
                image.Flush();
                int pixel = (int)(box.ContentY + itemBounds.Y + 2) * image.Stride + (int)(box.ContentX + itemBounds.X + 2) * 4;
                check(image.Data[pixel + 3] is >= 50 and <= 52, "The native item cell must actually paint the handbook's 20% white background.");
                image.WriteToPng(Path.Combine(output, $"item-cell-{cellScaleLabel(itemScale)}-{cellWidth}.png"));
            }
            AnnouncementTextLayout.Measure(parts, 176 * itemScale);
            cell.RenderInteractiveElements(0, 2000, 2000, 50);
            check(renderedItemStacks.Last().Slot.Itemstack!.Collectible == goodsItem && renderedItemStacks.Last().ShowQuantity,
                "Native item embeds must render the selected item's model and its quantity.");
            var firstBounds = cell.BoundsPerLine[0];
            var click = new MouseEvent((int)firstBounds.X + 2, (int)firstBounds.Y + 2, EnumMouseButton.Left, 0);
            int linksBefore = handbookLinks.Count;
            cell.OnMouseDown(click);
            check(click.Handled && handbookLinks.Count == linksBefore + 1
                && handbookLinks.Last() == "handbook://" + Uri.UnescapeDataString(AnnouncementGameResources.HandbookTarget(api, cell.Itemstacks[0])[11..]),
                "Clicking an item icon must open exactly one handbook link for its precise variant.");
            if (cell.Itemstacks.Length > 1)
            {
                cell.RenderInteractiveElements(1.1f, 2000, 2000, 50); cell.RenderInteractiveElements(0, 2000, 2000, 50);
                check(renderedItemStacks.Last().Slot.Itemstack!.Collectible == silverItem, "Native slideshows must advance to the next item after approximately one second.");
                cell.ShowTooltip = false;
                mouseX = (int)(2000 + firstBounds.X + 2); mouseY = (int)(2000 + firstBounds.Y + 2);
                cell.RenderInteractiveElements(2, 2000, 2000, 50); cell.RenderInteractiveElements(0, 2000, 2000, 50);
                check(renderedItemStacks.Last().Slot.Itemstack!.Collectible == silverItem && cell.UseMouseOverCursor(ElementBounds.Empty),
                    "Hovering a slideshow must pause on the displayed variant and show the clickable cursor.");
                cell.OnMouseDown(click);
                check(handbookLinks.Last() == "handbook://" + Uri.UnescapeDataString(AnnouncementGameResources.HandbookTarget(api, cell.Itemstacks[1])[11..]),
                    "Clicking after a slideshow advances must open the displayed item's page.");
                mouseX = mouseY = 0;
            }
            foreach (var part in parts) part.Dispose();
        }
        static string cellScaleLabel(float scale) => scale.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RuntimeEnv.GUIScale = 1;
        var missingParts = AnnouncementTextLayout.Components(api, AnnouncementDom.Markup("<figure data-game-slideshow><span data-game-item='missing' data-class='block'></span></figure>"));
        check(!Descendants(missingParts).OfType<SlideshowItemstackTextComponent>().Any()
            && Descendants(missingParts).OfType<RichTextComponent>().Any(t => t.DisplayText.Contains("物品不可用")),
            "A slideshow whose items are unavailable must render a fallback instead of creating an empty native slideshow.");
        foreach (var part in missingParts) part.Dispose();
        foreach (string badAttributes in new[] { "not-base64!", Convert.ToBase64String([255, 0]) })
        {
            var invalidParts = AnnouncementTextLayout.Components(api, AnnouncementDom.Markup("<figure data-game-item='game:firewood' data-attributes64='" + badAttributes + "'></figure>"));
            check(!Descendants(invalidParts).OfType<SlideshowItemstackTextComponent>().Any(),
                "Malformed embedded item attributes must fall back without crashing the native renderer.");
            foreach (var part in invalidParts) part.Dispose();
        }
        var legacyItemParts = AnnouncementTextLayout.Components(api, AnnouncementDom.Markup("<figure data-game-item='game:firewood' data-class='item' data-count='2' data-attributes='{&quot;variant&quot;:&quot;oak&quot;}'><figcaption>木柴</figcaption></figure>"));
        check(Descendants(legacyItemParts).OfType<SlideshowItemstackTextComponent>().Single().Itemstacks[0] is { StackSize: 2 } legacyStack
            && legacyStack.Attributes.GetString("variant") == "oak" && !Descendants(legacyItemParts).OfType<RichTextComponent>().Any(),
            "Existing JSON-attribute item embeds must retain their data while adopting the icon-only native cell.");
        foreach (var part in legacyItemParts) part.Dispose();

        var slideshowChoices = Enumerable.Range(0, 33).Select(i => new EconomyItemView { Key = "cycle-" + i.ToString("D2"), Name = "Cycle " + i.ToString("D2"),
            Code = goodsPreview.Code, Stack = displayStack.ToBytes() }).ToArray();
        string? insertedSlideshow = null;
        using (var picker = new AnnouncementResourcePicker(api, "slideshow", AnnouncementGameResources.Catalog(api, "slideshow", slideshowChoices),
            ElementBounds.Fixed(0, 0, 320, 350), html => insertedSlideshow = html))
        {
            while (picker.Loading) picker.Tick();
            check(!picker.Composer.GetButton("insert-selected").Enabled, "An empty slideshow selection must disable insertion.");
            void PickCycle(int index)
            {
                picker.Composer.GetTextInput("search").SetValue("Cycle " + index.ToString("D2"));
                var grid = picker.Composer.GetElement("grid");
                grid.OnMouseDownOnElement(api, new MouseEvent((int)grid.Bounds.renderX + 5, (int)grid.Bounds.renderY + 5, EnumMouseButton.Left, 0));
            }
            PickCycle(0); PickCycle(0);
            check(!picker.Composer.GetButton("insert-selected").Enabled && !picker.CloseRequested && insertedSlideshow == null,
                "Clicking a selected item must remove it without inserting or closing the multi-selection picker.");
            for (int i = 0; i < slideshowChoices.Length; i++) PickCycle(i);
            picker.Recompose(ElementBounds.Fixed(0, 0, 300, 330));
            var insertButton = picker.Composer.GetButton("insert-selected");
            check(insertButton.Enabled && picker.Composer.GetDynamicText("selected").GetText().Contains("32/32"),
                "Slideshow selections must persist across searches and recomposition, with a limit of 32 items.");
            var down = new MouseEvent((int)insertButton.Bounds.renderX + 5, (int)insertButton.Bounds.renderY + 5, EnumMouseButton.Left, 0);
            insertButton.OnMouseDownOnElement(api, down); insertButton.OnMouseUpOnElement(api, down);
            check(picker.CloseRequested && insertedSlideshow != null && AnnouncementDom.Parse(insertedSlideshow).QuerySelectorAll("[data-game-item]").Length == 32,
                "Confirming the slideshow picker must insert only the selected items as one persistent embed.");
        }

        int enumerated=0, generated=0;
        IEnumerable<AnnouncementGameResources.Resource> LazyCatalog()
        {
            for(int i=0;i<4096;i++) { enumerated++; yield return new("lazy-"+i,"Entry "+i,()=>{generated++;return "<p>chosen</p>";},goodsPreview); }
        }
        using(var picker=new AnnouncementResourcePicker(api,"item",LazyCatalog(),ElementBounds.Fixed(0,0,320,350),_=>{}))
        {
            check(enumerated==0 && generated==0,"Opening the resource picker must defer enumeration and HTML generation.");
            picker.Tick(); var sameComposer=picker.Composer; var sameGrid=picker.Composer.GetElement("grid");
            int firstBatch=enumerated;
            check(firstBatch is >0 and <=32 && picker.Loading,"Resource loading must process a bounded batch per frame.");
            picker.Composer.GetTextInput("search").SetValue("Entry 1");
            check(ReferenceEquals(sameComposer,picker.Composer) && ReferenceEquals(sameGrid,picker.Composer.GetElement("grid")) && generated==0,
                "Searching must update the grid in place without rebuilding the composer or generating resource HTML.");
            picker.Refresh(LazyCatalog()); picker.Tick();
            check(picker.LoadedCount<=64 && generated==0,"Catalog refresh must deduplicate entries and retain lazy HTML factories.");
        }
        // Native recipe preview handles wildcard ingredients, quantities and output from shown inputs.
        gridRecipes.Add(new GridRecipe { Width=1,Height=1,ResolvedIngredients=[null] }); // Skipped output must still count in the persisted index.
        var fixtureRecipe=new GridRecipe { Name=new("game:announcement-fixture"),Width=2,Height=2,Shapeless=false,
            Output=new() { Type=EnumItemClass.Item,Code=coinItem.Code,Quantity=3,ResolvedItemStack=new ItemStack(coinItem,3) },
            ResolvedIngredients=[new() { Id="1",Type=EnumItemClass.Item,Code=new("game:fire*"),Quantity=2,MatchingType=EnumRecipeMatchType.Wildcard },null,
                null,new() { Id="4",Type=EnumItemClass.Item,Code=silverItem.Code,Quantity=1,ResolvedItemStack=new ItemStack(silverItem) }] };
        gridRecipes.Add(fixtureRecipe);
        string recipeHtml=AnnouncementGameResources.Catalog(api,"recipe",[]).Single().CreateHtml();
        var recipeElement=AnnouncementDom.Parse(recipeHtml).FirstElementChild!;
        check(recipeElement.GetAttribute("data-recipe-index")=="1" && ReferenceEquals(AnnouncementRecipes.Resolve(api,recipeElement),fixtureRecipe),
            "Recipe indices must refer to the actual world recipe, even when earlier recipes have no output.");
        gridRecipes.Reverse();
        check(ReferenceEquals(AnnouncementRecipes.Resolve(api,recipeElement),fixtureRecipe),"Recipe identity must survive reordered recipe lists.");
        var recipeParts=AnnouncementTextLayout.Components(api,AnnouncementDom.Markup(recipeHtml));
        var nativeRecipe=Descendants(recipeParts).OfType<SlideshowGridRecipeTextComponent>().Single();
        AnnouncementTextLayout.Measure(recipeParts,176);
        foreach(var part in recipeParts) part.RenderInteractiveElements(0,2000,2000,50);
        var inputs=(ItemSlot[])typeof(SlideshowGridRecipeTextComponent).GetField("currentVisibleInputs",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(nativeRecipe)!;
        check(inputs[0].Itemstack?.Collectible==goodsItem && inputs[0].StackSize==2 && inputs[1].Empty && inputs[3].Empty && inputs[4].Itemstack?.Collectible==silverItem,
            "Native recipe preview must show wildcard material quantities and preserve the 2x2 pattern in its 3x3 grid.");
        check(nativeRecipe.GenerateCurrentVisibleOutputStack() is { StackSize:3 } result && result.Collectible==coinItem,
            "Recipe output must be generated by native recipe logic with its quantity.");
        check(Descendants(recipeParts).OfType<ItemstackTextComponent>().Single().ShowStacksize,
            "Embedded recipe outputs must show their stack quantity.");
        check(!Descendants(recipeParts).OfType<LinkTextComponent>().Any(),
            "Recipe captions must not add duplicate text links beside the clickable item icons.");
        foreach(float recipeScale in new[]{1f,1.5f})
        {
            RuntimeEnv.GUIScale=recipeScale;
            var layoutParts=AnnouncementTextLayout.Components(api,AnnouncementDom.Markup(recipeHtml));
            var row=Descendants(layoutParts).OfType<AnnouncementBox>().Single(b=>b.Children.OfType<SlideshowGridRecipeTextComponent>().Any());
            var grid=row.Children.OfType<SlideshowGridRecipeTextComponent>().Single();
            var outputIcon=row.Children.OfType<ItemstackTextComponent>().Single();
            foreach(double layoutWidth in new[]{176d,560d})
            {
                double total=AnnouncementTextLayout.Measure(layoutParts,layoutWidth*recipeScale);
                var inputBounds=grid.BoundsPerLine[0]; var outputBounds=outputIcon.BoundsPerLine[0];
                double arrowX=(double)row.GetType().GetProperty("ArrowX",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(row)!;
                double arrowY=(double)row.GetType().GetProperty("ArrowY",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(row)!;
                check(Math.Abs(inputBounds.Y+45*recipeScale-arrowY)<.01
                    && Math.Abs(outputBounds.Y+outputBounds.Height/2-arrowY)<.01
                    && arrowX>inputBounds.X+inputBounds.Width && arrowX+20*recipeScale<outputBounds.X
                    && outputBounds.X+outputBounds.Width<=layoutWidth*recipeScale
                    && total>=row.BoundsPerLine[0].Y+row.BoundsPerLine[0].Height,
                    "Recipe grid, arrow and output must remain centered on one row and inside the document at each GUI scale.");
                using var surface=new ImageSurface(Format.Argb32,(int)(layoutWidth*recipeScale),(int)Math.Ceiling(total));
                using var brush=new Context(surface); brush.SetSourceRGB(.15,.12,.09); brush.Paint();
                foreach(var part in layoutParts) part.ComposeElements(brush,surface);
                surface.WriteToPng(Path.Combine(output,$"recipe-layout-{recipeScale}-{layoutWidth}.png"));
            }
            AnnouncementTextLayout.Measure(layoutParts,120*recipeScale);
            check(outputIcon.BoundsPerLine[0].Y>=grid.BoundsPerLine[0].Height
                && outputIcon.BoundsPerLine[0].X+outputIcon.BoundsPerLine[0].Width<=120*recipeScale,
                "A recipe in a very narrow container must keep the arrow and output together below the grid.");
            foreach(var part in layoutParts) part.Dispose();
        }
        RuntimeEnv.GUIScale=1;
        foreach(var part in recipeParts) part.Dispose();
        HtmlDocumentChecks.Run(api, check, (dialog, name) =>
        {
            using var surface = new ImageSurface(Format.Argb32, frameWidth, frameHeight);
            using var ctx = new Context(surface);
            captureContext = ctx;
            try { foreach (var composer in dialog.Composers.Values) composer.Render(0); }
            finally { captureContext = null; }
            surface.WriteToPng(Path.Combine(output, name + ".png"));
        }, ClickElement, ClickAt, (texture, name) => textures[texture.TextureId].WriteToPng(Path.Combine(output, name + ".png")),
            singleItemHtml, slideshowHtml, recipeHtml);
        fixtureRecipe.Output!.Quantity=4;
        check(AnnouncementRecipes.Resolve(api,recipeElement)==null,"Changed recipes must show unavailable rather than display the wrong recipe.");
        gridRecipes.Clear();
        var reproducedImages = Descendants(reproduction).OfType<AnnouncementImage>().ToArray();
        check(reproducedImages.Length == 2 && reproducedImages.All(i => i.Loaded), "Images nested inside mod-card links must remain renderable components.");
        var reproducedHeadings = Descendants(reproduction).OfType<LinkTextComponent>().Where(t => t.DisplayText.StartsWith("VSCN ")).ToArray();
        check(reproducedHeadings.Length == 2 && reproducedHeadings.All(t => t.Font.UnscaledFontsize == 18 && t.Font.FontWeight == FontWeight.Bold),
            "Headings within linked cards must preserve their bold heading font rather than flatten to ordinary link text: " + string.Join(";",reproducedHeadings.Select(t=>$"{t.DisplayText} {t.Font.UnscaledFontsize} {t.Font.FontWeight}")));
        foreach (var component in reproduction) component.Dispose();
        var crowded = AnnouncementTextLayout.Components(api, AnnouncementHtml.ToNativeMarkup(string.Concat(Enumerable.Repeat(
            "<article><img src='vsscsmod:textures/tests/notice.png' width='16' height='8'></article>", 20))));
        check(crowded.OfType<AnnouncementCard>().Sum(c => c.Children.OfType<AnnouncementImage>().Count()) == AnnouncementImages.MaxImages,
            "The image budget must apply to the entire announcement, including separate cards.");
        foreach (var component in crowded) component.Dispose();
        mouseInventory.LateInitialize("mouse-chief", api);
        void Capture(GuiDialog window, bool preserveMouse = false)
        {
            using var surface = new ImageSurface(Format.Argb32, frameWidth, frameHeight);
            using var ctx = new Context(surface);
            captureContext = ctx;
            if (!preserveMouse) mouseX = mouseY = 0;
            try { foreach (var composer in window.Composers.Values) composer.Render(0); }
            finally { captureContext = null; }
            check(escapedItemRenders == 0, "Every economy item model must render inside its own slot scissor, including long weapons.");
            surface.WriteToPng(Path.Combine(output, scenario + "-composed.png"));
            surface.Flush();
            byte[] pixels = surface.Data;
            if (window is CivilizationCoreHud)
            {
                var menu = window.Composers["core-menu"];
                var first = menu.GetElement("entry-0").Bounds;
                int x = (int)(menu.Bounds.renderX + menu.Bounds.OuterWidth / 2);
                int y = (int)((menu.Bounds.renderY + first.renderY) / 2);
                check(pixels[y * surface.Stride + x * 4 + 3] > 0,
                    "The painted menu background must extend above the first button; an unused title-bar cutout leaves buttons outside the visible border.");
                return;
            }
            var bounds = window.SingleComposer.Bounds;
            bool opaque = true;
            int translucentPixels = 0;
            for (int y = (int)bounds.renderY + 12; y < bounds.renderY + bounds.OuterHeight - 12; y++)
                for (int x = (int)bounds.renderX + 12; x < bounds.renderX + bounds.OuterWidth - 12; x++)
                {
                    byte alpha = pixels[y * surface.Stride + x * 4 + 3];
                    if (alpha != 255) opaque = false;
                    if (alpha is > 0 and < 255) translucentPixels++;
                }
            if (window is ServerAnnouncementHud)
            {
                int x = (int)(bounds.renderX + GuiStyle.ElementToDialogPadding * RuntimeEnv.GUIScale / 4);
                int y = (int)(bounds.renderY + bounds.OuterHeight / 2);
                check(translucentPixels > 100 && Math.Abs(pixels[y * surface.Stride + x * 4 + 3]
                        - GuiStyle.DialogLightBgColor[3] * 255) <= 1,
                    "The notification background must render with the native tutorial alpha, leaving the game visible behind it.");
            }
            else if (window is GuidanceWindow)
                check(translucentPixels > 100, "Native windows must follow the game's translucent background instead of painting an opaque custom layer.");
            else check(opaque, "Dialog must stay opaque even with translucent game GUI colors.");
            if (window.SingleComposer.GetElement("economy-content") is GuiElementContainer economyBody)
            {
                var clip = economyBody.InsideClipBounds ?? economyBody.Bounds.ParentBounds;
                var text = economyBody.Elements.FirstOrDefault(e => e is GuiElementWrappedText
                    && e.Bounds.renderY >= clip.renderY && e.Bounds.renderY + e.Bounds.OuterHeight <= clip.renderY + clip.InnerHeight)
                    ?? economyBody.Elements.FirstOrDefault(e => e is GuiElementTextButton
                    && e.Bounds.renderY >= clip.renderY && e.Bounds.renderY + e.Bounds.OuterHeight <= clip.renderY + clip.InnerHeight);
                // A scrolled inventory page can contain only slots; its fixed labeled footer remains visible.
                bool fixedTradeAction = text == null && window is EconomyTradeWindow && economyBody.Elements.Any(e =>
                    e.GetType().Name is "GuiElementEconomyItem" or "GuiElementEconomyCoin"
                    && e.Bounds.renderY < clip.renderY + clip.InnerHeight && e.Bounds.renderY + e.Bounds.OuterHeight > clip.renderY);
                if (fixedTradeAction)
                    text = window.SingleComposer.GetElement("trade-action-cancel");
                check(text != null, "Economy dialogs must have visible text or a labeled action.");
                if (text != null)
                {
                    int visibleGlyphPixels = 0;
                    for (int y = Math.Max(0, (int)text.Bounds.renderY); y < Math.Min(frameHeight, text.Bounds.renderY + text.Bounds.OuterHeight); y++)
                        for (int x = Math.Max(0, (int)text.Bounds.renderX); x < Math.Min(frameWidth, text.Bounds.renderX + text.Bounds.OuterWidth); x++)
                        {
                            int pixel = y * surface.Stride + x * 4;
                            // Native footer buttons use brown-gold text; body copy uses white.
                            if (fixedTradeAction
                                ? pixels[pixel] > 60 && pixels[pixel + 1] > 105 && pixels[pixel + 2] > 135
                                : pixels[pixel] > 150 && pixels[pixel + 1] > 175 && pixels[pixel + 2] > 190) visibleGlyphPixels++;
                        }
                    check(visibleGlyphPixels > 3, $"Visible economy text or a fixed trade action must actually render: {scenario}, {text.GetType().Name}, "
                        + $"x={text.Bounds.renderX}, y={text.Bounds.renderY}, w={text.Bounds.OuterWidth}, h={text.Bounds.OuterHeight}, scissor={scissorStack.Count}, cairo={ctx.Status}.");
                }
            }
            if (window.Composers["economy-picker"] == null && window.SingleComposer.GetElement("economy-footer-save") is GuiElementTextButton saveFooter)
            {
                int goldPixels = 0;
                for (int y = Math.Max(0, (int)saveFooter.Bounds.renderY); y < Math.Min(frameHeight, saveFooter.Bounds.renderY + saveFooter.Bounds.OuterHeight); y++)
                    for (int x = Math.Max(0, (int)saveFooter.Bounds.renderX); x < Math.Min(frameWidth, saveFooter.Bounds.renderX + saveFooter.Bounds.OuterWidth); x++)
                    {
                        int p = y * surface.Stride + x * 4;
                        if (pixels[p] > 60 && pixels[p + 1] > 105 && pixels[p + 2] > 135) goldPixels++;
                    }
                check(goldPixels > 3, $"Editor footer must remain visibly rendered after scrolling: {scenario}, x={saveFooter.Bounds.renderX}, y={saveFooter.Bounds.renderY}, w={saveFooter.Bounds.OuterWidth}, h={saveFooter.Bounds.OuterHeight}, gold={goldPixels}.");
            }
            if (window.SingleComposer.GetElement("tribe-content") is GuiElementContainer container)
            {
                var controls = container.Elements.Where(e => e is GuiElementTextButton or GuiElementTextInput).ToArray();
                check(controls.All(e => e.Bounds.renderX >= container.Bounds.renderX - 1 &&
                    e.Bounds.renderX + e.Bounds.OuterWidth <= container.Bounds.renderX + container.Bounds.InnerWidth + 1),
                    $"Tribe controls overflow the content width: {scenario}; container={container.Bounds.renderX}/{container.Bounds.InnerWidth}; " +
                    string.Join("; ", controls.Select(e => $"{e.GetType().Name}={e.Bounds.renderX}/{e.Bounds.OuterWidth}")));
                bool overlap = controls.Where((a, i) => controls.Skip(i + 1).Any(b =>
                    a.Bounds.renderX < b.Bounds.renderX + b.Bounds.OuterWidth && b.Bounds.renderX < a.Bounds.renderX + a.Bounds.OuterWidth &&
                    a.Bounds.renderY < b.Bounds.renderY + b.Bounds.OuterHeight && b.Bounds.renderY < a.Bounds.renderY + a.Bounds.OuterHeight)).Any();
                check(!overlap, "Tribe buttons or inputs overlap.");
                var footer = new[] { "edit-name", "invite-nearby", "create-outpost", "move-center", "leave", "save", "cancel" }
                    .Select(key => window.SingleComposer.GetElement("tribe-action-" + key)).OfType<GuiElementTextButton>().ToArray();
                check(footer.All(b => b.Bounds.renderX >= bounds.renderX && b.Bounds.renderX + b.Bounds.OuterWidth <= bounds.renderX + bounds.InnerWidth
                    && Math.Abs(b.Bounds.renderY + b.Bounds.OuterHeight - (bounds.renderY + bounds.InnerHeight - 20 * RuntimeEnv.GUIScale)) < 1),
                    "Tribe footer buttons must fit the window and sit at the bottom.");
                check(footer.Length == 0 || footer.All(b => Math.Abs(b.Bounds.renderY - footer[0].Bounds.renderY) < 1)
                    && footer.Zip(footer.Skip(1)).All(pair => pair.First.Bounds.renderX + pair.First.Bounds.OuterWidth <= pair.Second.Bounds.renderX)
                    && container.Bounds.ParentBounds.renderY + container.Bounds.ParentBounds.OuterHeight < footer[0].Bounds.renderY,
                    "Tribe operations must occupy one separate footer row without overlapping each other or the scrolling body.");
            }
        }
        void ClickElement(GuiDialog window, GuiElement element)
        {
            ClickAt(window, element.Bounds.renderX + element.Bounds.OuterWidth / 2,
                element.Bounds.renderY + element.Bounds.OuterHeight / 2);
        }
        void WithoutFlowErrors(Action operation, string phase)
        {
            var original = Console.Out;
            using var captured = new StringWriter();
            try { Console.SetOut(captured); operation(); }
            finally { Console.SetOut(original); }
            check(!captured.ToString().Contains("Flow path underflow"), "Native text overflowed its flow path while " + phase);
        }
        void ClickAt(GuiDialog window, double x, double y)
        {
            mouseX = (int)x; mouseY = (int)y;
            window.OnMouseDown(new MouseEvent(mouseX, mouseY, EnumMouseButton.Left, 0));
            window.OnMouseUp(new MouseEvent(mouseX, mouseY, EnumMouseButton.Left, 0));
        }
        void ClickAnnouncement(VscnClusterWindow window, string state)
        {
            var container = window.SingleComposer.GetContainer("announcement-content");
            var clip = container.InsideClipBounds ?? container.Bounds.ParentBounds;
            ClickAt(window, container.Bounds.renderX + 10 * RuntimeEnv.GUIScale,
                Math.Max(container.Bounds.renderY, clip.renderY) + 10 * RuntimeEnv.GUIScale);
            check(!container.HasFocus, "Reading or previewing an announcement must not focus a container with no editable child: " + state);
            window.OnKeyDown(new KeyEvent { KeyCode = (int)GlKeys.Tab });
            window.OnKeyUp(new KeyEvent { KeyCode = (int)GlKeys.Tab });
            check(window.SingleComposer.CurrentTabIndexElement != container,
                "Keyboard navigation must skip announcement text without an editable child: " + state);
        }
        void Click(VscnClusterWindow window, string key)
        {
            if (window.SingleComposer.GetElement("tribe-action-" + key) is GuiElementTextButton footer)
            {
                ClickElement(window, footer);
                return;
            }
            var container = window.SingleComposer.GetContainer("tribe-content");
            var button = container.Elements.OfType<GuiElementTextButton>().First(b => b.Text == translations["ui." + key]);
            var scroll = window.SingleComposer.GetScrollbar("scrollbar");
            scroll.CurrentYPosition = (float)Math.Clamp(button.Bounds.fixedY * RuntimeEnv.GUIScale - 10, 0,
                Math.Max(0, container.Bounds.OuterHeight - container.Bounds.ParentBounds.InnerHeight));
            scroll.TriggerChanged();
            ClickElement(window, button);
        }
        void CheckNoticeLayout(VscnClusterWindow window)
        {
            var notice = window.SingleComposer.GetContainer("announcement-content").Elements.OfType<GuiElementRichtext>().Single();
            var lines = notice.Components.OfType<RichTextComponent>().SelectMany(c => c.Lines).Where(l => l.Text.Length > 0).ToArray();
            var items = lines.Where(l => l.Text.StartsWith("•")).ToArray();
            double paragraphMargin = CairoFont.WhiteSmallText().UnscaledFontsize * RuntimeEnv.GUIScale;
            check(items.Length == 2 && Math.Abs(items[1].Bounds.Y - items[0].Bounds.Y - items[0].Bounds.Height - paragraphMargin) < 2,
                "An actual paragraph in a list must retain one paragraph margin in reading and preview.");
            check(lines.Length > 1 && lines[1].Bounds.Y >= lines[0].Bounds.Y + lines[0].Bounds.Height + paragraphMargin - 1,
                "Text following a larger heading must begin below its full line height and collapsed block margin.");
        }
        double[] CheckBlockMargins(VscnClusterWindow window)
        {
            var notice = window.SingleComposer.GetContainer("announcement-content").Elements.OfType<GuiElementRichtext>().Single();
            var lines = notice.Components.OfType<RichTextComponent>().SelectMany(c => c.Lines).Where(l => l.Text.Length > 0).ToArray();
            TextLine Find(string value) => lines.Single(l => l.Text.Contains(value));
            double margin = CairoFont.WhiteSmallText().UnscaledFontsize * RuntimeEnv.GUIScale;
            void Gap(string first, string second, double expected, string message)
            {
                var before = Find(first); var after = Find(second);
                check(Math.Abs(after.Bounds.Y - before.Bounds.Y - before.Bounds.Height - expected) < 2,
                    message + $" actual={after.Bounds.Y - before.Bounds.Y - before.Bounds.Height} expected={expected} scale={RuntimeEnv.GUIScale} "
                    + string.Join(" / ", notice.Components.OfType<RichTextComponent>().Select(c => c.Font.Fontname + ":" + c.DisplayText.Replace("\n", "\\n"))));
            }
            Gap("段落甲", "段内乙", 0, "A br inside a paragraph must use normal line spacing.");
            Gap("段内乙", "段落丙", margin, "Adjacent paragraph margins must collapse through a div, rather than vanish or double.");
            Gap("段落丙", "项目甲", margin, "A paragraph followed by a list must share one collapsed margin.");
            Gap("项目甲", "项目乙", 0, "Plain list items must retain normal consecutive line spacing.");
            Gap("项目乙", "嵌套甲", 0, "A nested list must not add an outer list margin.");
            Gap("嵌套甲", "嵌套乙", 0, "Nested plain items must also remain compact.");
            Gap("嵌套乙", "项目丙", 0, "Leaving a nested list must not add a blank row.");
            Gap("项目丙", "段落丁", margin, "A list followed by a paragraph must share one collapsed margin.");
            check(Find("段落甲").Bounds.Y == 0, "Document edges must not reserve an unnecessary top block margin.");
            return lines.Select(l => l.Bounds.Y).ToArray();
        }
        try
        {
            if (Environment.GetEnvironmentVariable("VSCN_MANAGEMENT_GUI_ONLY")=="1")
            {
                foreach(var size in new[]{(1280,800,1f),(1280,800,1.125f),(640,480,1.5f)})
                {
                    (frameWidth,frameHeight,RuntimeEnv.GUIScale)=size;
                    ManagementGuiChecks.Run(api,check,value=>administrator=value,
                        (dialog,name)=>{scenario=$"{frameWidth}-{name}";Capture(dialog,name.EndsWith("-hover"));},ClickElement,
                        coreOnly: size.Item3==1.125f);
                }
                return;
            }
            int initialTextures = textures.Count;
            using (var markers = new TerritoryRenderer(api))
            {
                check(registeredRenderers.Contains(markers) && textures.Count == initialTextures + 2,
                    "World markers must register their renderer and upload both native icons.");
                var markerPoints = new List<TribeTerritoryPoint>
                {
                    new() { X = -10, Y = 1, Z = 10 }, new() { X = 10, Y = 1, Z = 10, IsOutpost = true },
                    new() { X = 20, Y = 1, Z = 10, Dimension = 1 }, new() { X = markers.RenderRange + 20, Y = 1, Z = 10 }
                };
                markers.Update(new() { MyTribeId = "own", Territories = markerPoints });
                renderedMarkers.Clear();
                markers.OnRenderFrame(0, EnumRenderStage.Ortho);
                check(renderedMarkers.Count == 2 && renderedMarkers.Distinct().Count() == 2,
                    "Nearby home/outpost markers must use distinct icons and hide distant/other-dimension territory.");
                using var sheet = new ImageSurface(Format.Argb32, 400, 180);
                using var ctx = new Context(sheet);
                ctx.SetSourceRGB(.20, .16, .12); ctx.Paint();
                for (int row = 0; row < 2; row++)
                {
                    var texture = textures[renderedMarkers[row]];
                    texture.Flush();
                    byte[] iconPixels = texture.Data;
                    check(iconPixels[3] == 0 && Enumerable.Range(0, texture.Height).Any(y =>
                        Enumerable.Range(0, texture.Width).Any(x => iconPixels[y * texture.Stride + x * 4 + 3] == 255)),
                        "Native marker artwork must have transparent corners and visible opaque pixels.");
                    int x = 18;
                    foreach (int size in new[] { 26, 32, 48, 64 })
                    {
                        ctx.Save(); ctx.Translate(x, 8 + row * 86); ctx.Scale(size / 128.0, size / 128.0);
                        ctx.SetSourceSurface(texture, 0, 0); ctx.Paint(); ctx.Restore();
                        x += size + 28;
                    }
                }
                sheet.WriteToPng(Path.Combine(output, "tribe-icons.png"));
                markers.Update(new() { Territories = markerPoints });
                renderedMarkers.Clear();
                markers.OnRenderFrame(0, EnumRenderStage.Ortho);
                check(renderedMarkers.Count == 0, "Leaving a tribe must clear its world markers immediately.");
            }
            check(registeredRenderers.Count == 0 && textures.Count == initialTextures,
                "Disposing world markers must unregister the renderer and release both textures.");
            frameWidth = 1280; frameHeight = 800; RuntimeEnv.GUIScale = 1.125f;
            using (var fractionalGuidance = new GuidanceWindow(api, new GuidancePacket { Required = true, Nonce = "fractional" }, guidanceDecisions.Add))
            {
                fractionalGuidance.TryOpen(); scenario = "1125-native-name"; Capture(fractionalGuidance); fractionalGuidance.Release();
            }
            foreach (var size in new[] { (1280, 800, 1f), (640, 480, 1.5f) }
                .Where(s => Environment.GetEnvironmentVariable("VSCN_GUI_WIDTH") is not { } width || s.Item1.ToString() == width))
            {
                (frameWidth, frameHeight, RuntimeEnv.GUIScale) = size;
                int coreClicks = 0;
                using var nativeHotbar = new Vintagestory.Client.NoObf.HudHotbar(api);
                void RebuildHotbar(double x)
                {
                    nativeHotbar.ClearComposers();
                    nativeHotbar.Composers["hotbar"] = api.Gui.CreateCompo("test-hotbar", ElementBounds.Fixed(x, frameHeight / RuntimeEnv.GUIScale - 70, 60, 60))
                        .AddItemSlotGrid(mouseInventory, _ => { }, 1, [0], ElementStdBounds.SlotGrid(EnumDialogArea.LeftFixed, 0, 0, 1, 1), "offhandgrid")
                        .AddItemSlotGrid(mouseInventory, _ => { }, 1, [0], ElementStdBounds.SlotGrid(EnumDialogArea.LeftFixed, 100, 0, 1, 1), "hotbargrid").Compose();
                }
                RebuildHotbar(100); dialogs.Add(nativeHotbar);
                using (var core = new CivilizationCoreHud(api, () => coreClicks++))
                {
                    core.Show();
                    check(core.IsOpened() && core.SingleComposer.GetElement("core") != null,
                        "Core must use a passive item preview.");
                    var button = core.SingleComposer.GetElement("core");
                    void CheckCoreAlignment()
                    {
                        var first = nativeHotbar.Composers["hotbar"].GetSlotGrid("hotbargrid").SlotBounds[0];
                        var cell = core.SingleComposer.GetElement("core").Bounds;
                        check(Math.Abs(cell.renderY - first.renderY) < .01 && Math.Abs(cell.OuterHeight - first.OuterHeight) < .01,
                            "Core must align exactly with the first hotbar cell, without extra inset.");
                        check(Math.Abs(first.renderX - cell.renderX - cell.OuterWidth - GuiElement.scaled(GuiElementItemSlotGridBase.unscaledSlotPadding)) < .01,
                            "Core must be immediately left of slot one with native grid spacing.");
                    }
                    CheckCoreAlignment();
                    check(button.Bounds.renderX >= 0 && button.Bounds.renderY >= 0
                        && button.Bounds.renderX + button.Bounds.OuterWidth <= frameWidth
                        && button.Bounds.renderY + button.Bounds.OuterHeight <= frameHeight,
                        "Core slot must remain fully on screen.");
                    ClickElement(core, button);
                    check(coreClicks==0 && core.Composers["core-menu"]!=null,"Core click must open the menu without opening a window.");
                    var menu=core.Composers["core-menu"];
                    check(menu.Bounds.renderY+menu.Bounds.OuterHeight<=button.Bounds.renderY,"Core menu must open upwards.");
                    ClickElement(core,menu.GetElement("entry-2"));
                    check(coreClicks==1 && core.Composers["core-menu"]==null,"Menu selection must open exactly once and close the menu.");
                    var beforeHotbarRebuild = core.SingleComposer;
                    RebuildHotbar(160); core.Show();
                    CheckCoreAlignment();
                    check(!ReferenceEquals(beforeHotbarRebuild, core.SingleComposer), "Core must follow a newly composed native hotbar instead of keeping stale slot bounds.");
                    var coreInventory = (CivilizationCoreInventory)typeof(CivilizationCoreHud).GetField("inventory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(core)!;
                    coreInventory[0].Itemstack = null;
                    core.OnEscapePressed(); core.TryClose(); core.Show();
                    check(core.IsOpened() && !coreInventory[0].Empty, "Escape, close and emptied slot must recover.");
                    var previousCoreComposer = core.SingleComposer;
                    frameHeight += 80; RebuildHotbar(160); core.Show();
                    check(!ReferenceEquals(previousCoreComposer, core.SingleComposer), "Resize must rebuild core composer.");
                    frameHeight -= 80; RebuildHotbar(160); core.Show();
                    CheckCoreAlignment();
                    renderedItemStacks.Clear();
                    using var coreSurface = new ImageSurface(Format.Argb32, frameWidth, frameHeight);
                    using var coreContext = new Context(coreSurface);
                    captureContext = coreContext;
                    try { core.OnRenderGUI(0); } finally { captureContext = null; }
                    check(renderedItemStacks.Any(r => ReferenceEquals(r.Slot, coreInventory[0])), "Native HUD must render restored item after resize.");
                    check(scissorStack.Count == 0, "Core must balance clipping.");
                    coreSurface.Flush();
                    check(coreSurface.Data.All(b => b == 0), "Without GPU item rendering, preview must draw no slot frame, background or highlight.");
                    coreSurface.WriteToPng(Path.Combine(output, $"{frameWidth}-civilization-core.png"));
                }
                dialogs.Remove(nativeHotbar);
                var logOffer = new ClientLogOffer { Id = Guid.NewGuid().ToString("N"), IncidentUtc = DateTime.UtcNow.ToString("O") };
                var logSnapshot = new ClientLogFiles.Snapshot(new byte[12345],
                    ["current/client-crash.log", "previous/client-main.log", "previous/client-debug.log (tail)", "previous/client-audit.log",
                     "current/client-main.log", "current/client-debug.log", "current/client-audit.log"]);
                var logDecisions = new List<bool>();
                using (var logDialog = new ClientLogUploadDialog(api, logOffer, logSnapshot, logDecisions.Add))
                {
                    logDialog.TryOpen();
                    check(logDialog.SingleComposer.Bounds.OuterHeight <= frameHeight - 30 && logDecisions.Count == 0,
                        "Log consent must fit small/scaled screens without implicitly accepting.");
                    scenario = $"{frameWidth}-client-log-consent"; Capture(logDialog);
                    ClickElement(logDialog, logDialog.SingleComposer.GetButton("decline"));
                    check(logDecisions.SequenceEqual(new[] { false }), "Declining client logs must invoke exactly one negative decision.");
                }
                logDecisions.Clear();
                using (var logDialog = new ClientLogUploadDialog(api, logOffer, logSnapshot, logDecisions.Add))
                {
                    logDialog.TryOpen();
                    ClickElement(logDialog, logDialog.SingleComposer.GetButton("upload"));
                    check(logDecisions.SequenceEqual(new[] { true }), "Explicit upload must invoke exactly one positive decision.");
                }
                logDecisions.Clear();
                using (var logDialog = new ClientLogUploadDialog(api, logOffer, logSnapshot, logDecisions.Add))
                {
                    logDialog.TryOpen(); logDialog.TryClose();
                    check(logDecisions.SequenceEqual(new[] { false }), "Closing client log consent must behave as refusal.");
                }
                if (Environment.GetEnvironmentVariable("VSCN_GUI_CLIENTLOGS_ONLY") == "1") continue;
                AnnouncementGuiRegressionChecks.Run(api, textures, textureClamp, check,
                    (dialog, name) => { scenario = $"{frameWidth}-{name}"; Capture(dialog); },
                    ClickElement, ClickAt, singleItemHtml, slideshowHtml, fixtureBytes, output);
                EconomyOverlayChecks.Run(api, textures, output, check);
                scenario = $"{frameWidth}-tribe";
                using var window = new VscnClusterWindow(api, requests.Add);
                window.Update(new() { Open = true, AllowSoloCreation = frameWidth == 1280 });
                check(window.IsVisible && window.SingleComposer.Composed, "Native create-tribe dialog did not open.");
                double mainWidth = window.SingleComposer.Bounds.fixedWidth;
                void CheckCenteredMain()
                {
                    var main = window.SingleComposer.Bounds;
                    check(main.fixedWidth == mainWidth && Math.Abs(main.renderX + main.OuterWidth / 2 - frameWidth / 2) < 1,
                        "The civilization window must keep its original width and screen center across pages and picker changes.");
                }
                CheckCenteredMain();
                check(window.SingleComposer.GetElement("pages")==null,"Independent windows must not have main page tabs.");
                check(window.SingleComposer.GetElement("announcement-content") != null, "The default page must be announcements.");
                var emptyNotice = window.SingleComposer.GetContainer("announcement-content");
                check(float.IsFinite(window.SingleComposer.GetScrollbar("scrollbar").CurrentYPosition)
                    && double.IsFinite(emptyNotice.Bounds.renderY) && !window.SingleComposer.GetScrollbar("scrollbar").Focusable,
                    "Short notices must retain a finite viewport position and hide the scrollbar at fractional GUI scales.");
                scenario = $"{frameWidth}-announcement";
                Capture(window);
                ClickAnnouncement(window, "empty notice for an ordinary player");
                check(window.SingleComposer.GetElement("announcement-0") == null, "Ordinary players must not see announcement editing controls.");
                var announcementSnapshot = new CivilizationSnapshotPacket
                {
                    CanEditAnnouncement = true, AnnouncementHtml = "<h1>文明纪事</h1>\n<p>欢迎来到部落。<b>尊重历史与生存。</b></p>\n<ul>\n  <li><p>先建立家园</p></li>\n  <li>交换纸图</li>\n</ul>", AnnouncementRevision = 4
                };
                window.Update(announcementSnapshot); window.OnRenderGUI(0);
                scenario = $"{frameWidth}-notice-admin"; Capture(window);
                CheckNoticeLayout(window);
                ClickAnnouncement(window, "published notice for an administrator");
                string originalHtml = announcementSnapshot.AnnouncementHtml;
                const string visualHtml = "<article style='background-color:#30291f;border-color:#b1905a'><h2>共同家园</h2><p>图片与卡片公告：<a href='https://example.org/rules'>查看规则</a></p><img src='vsscsmod:textures/tests/notice.png' alt='公告横幅' width='100%'></article><div class='card'><p>第二张卡片</p><p>段落间距正常</p><img src='game:textures/not-found.png' alt='资源缺失时保留说明' width='200' height='70'></div><p>卡片之后的正文</p>";
                announcementSnapshot.AnnouncementHtml = visualHtml;
                window.Update(announcementSnapshot); window.OnRenderGUI(0);
                var visualNotice = window.SingleComposer.GetContainer("announcement-content").Elements.OfType<GuiElementAnnouncement>().Single();
                var cards = visualNotice.Components.OfType<AnnouncementCard>().ToArray();
                var loadedImage = cards[0].Children.OfType<AnnouncementImage>().Single();
                var missingImage = cards[1].Children.OfType<AnnouncementImage>().Single();
                var imageBox = loadedImage.BoundsPerLine[0];
                check(cards.Length == 2 && loadedImage.Loaded && !missingImage.Loaded
                    && Math.Abs(imageBox.Width / imageBox.Height - 2) < .01
                    && imageBox.Width <= cards[0].BoundsPerLine[0].Width - 28 * RuntimeEnv.GUIScale + 1,
                    "Cards must contain working local images, safe missing-image placeholders and a responsive undistorted image box.");
                double margin = CairoFont.WhiteSmallText().UnscaledFontsize * RuntimeEnv.GUIScale;
                var secondCard = cards[1].BoundsPerLine[0];
                check(secondCard.Y >= cards[0].BoundsPerLine[0].Y + cards[0].BoundsPerLine[0].Height + margin - 1,
                    "Adjacent cards must have one collapsed paragraph margin.");
                var lastText = visualNotice.Components.OfType<RichTextComponent>().Single(t => t.DisplayText.Contains("卡片之后"));
                check(lastText.BoundsPerLine[0].Y >= secondCard.Y + secondCard.Height + margin - 1
                    && visualNotice.Bounds.InnerHeight >= lastText.BoundsPerLine[^1].Y + lastText.BoundsPerLine[^1].Height,
                    "The full document and scrollbar must include image and card height without overlapping the next paragraph.");
                var cardLink = cards[0].Children.OfType<LinkTextComponent>().Single();
                scenario = $"{frameWidth}-notice-images-cards"; Capture(window);
                var linkBox = cardLink.BoundsPerLine[0]; var cardBox = cards[0].BoundsPerLine[0];
                ClickAt(window, visualNotice.Bounds.renderX + cardBox.X + 14 * RuntimeEnv.GUIScale + linkBox.X + 2,
                    visualNotice.Bounds.renderY + cardBox.Y + 14 * RuntimeEnv.GUIScale + linkBox.Y + linkBox.Height / 2);
                check(openedLinks.LastOrDefault() == "https://example.org/rules", "Links inside a padded card must retain native click handling.");
                double[] publishedCardBounds = cards.SelectMany(c => new[] { c.BoundsPerLine[0].Y, c.BoundsPerLine[0].Height }).ToArray();
                ClickElement(window, window.SingleComposer.GetElement("announcement-0")); window.OnRenderGUI(0);
                scenario = $"{frameWidth}-notice-toolbar-layout"; Capture(window);
                ClickElement(window, window.SingleComposer.GetElement("announcement-0")); window.OnRenderGUI(0);
                check(window.SingleComposer.GetContainer("announcement-content").Elements.OfType<GuiElementAnnouncement>().Any(),
                    $"Preview click must remain accessible at {frameWidth}px after opening the editor.");
                visualNotice = window.SingleComposer.GetContainer("announcement-content").Elements.OfType<GuiElementAnnouncement>().Single();
                check(visualNotice.Components.OfType<AnnouncementCard>().SelectMany(c => new[] { c.BoundsPerLine[0].Y, c.BoundsPerLine[0].Height }).SequenceEqual(publishedCardBounds),
                    "Image and card layout must match exactly between reading and draft preview.");
                ClickElement(window, window.SingleComposer.GetElement("announcement-2")); window.OnRenderGUI(0);

                // Complete a pending HTTPS response without touching the public network in GUI tests.
                var imageCache = (Dictionary<string, AnnouncementImages.CachedRequest>)typeof(AnnouncementImages).GetField("Cache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                string remoteSource = $"https://example.org/notice-{frameWidth}.png";
                var completion = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
                imageCache[remoteSource] = new(completion.Task);
                announcementSnapshot.AnnouncementHtml = "<article><img src='" + remoteSource + "' alt='网络横幅' width='100%'></article>"
                    + string.Concat(Enumerable.Repeat("<p>加载后正文</p>", 12));
                scenario = $"{frameWidth}-notice-remote-pending";
                window.Update(announcementSnapshot); window.OnRenderGUI(0); Capture(window);
                visualNotice = window.SingleComposer.GetContainer("announcement-content").Elements.OfType<GuiElementAnnouncement>().Single();
                check(!visualNotice.Components.OfType<AnnouncementCard>().Single().Children.OfType<AnnouncementImage>().Single().Loaded
                    && !visualNotice.HasNewImages, "A pending HTTPS image must compose immediately with a loading placeholder.");
                var scrollBar = window.SingleComposer.GetScrollbar("scrollbar"); scrollBar.CurrentYPosition = 10; scrollBar.TriggerChanged();
                double beforeScroll = scrollBar.CurrentYPosition;
                completion.SetResult(fixtureBytes); window.OnRenderGUI(0);
                visualNotice = window.SingleComposer.GetContainer("announcement-content").Elements.OfType<GuiElementAnnouncement>().Single();
                var remoteImage = visualNotice.Components.OfType<AnnouncementCard>().Single().Children.OfType<AnnouncementImage>().Single();
                check(remoteImage.Loaded && !visualNotice.HasNewImages && Math.Abs(remoteImage.BoundsPerLine[0].Width / remoteImage.BoundsPerLine[0].Height - 2) < .01,
                    "A completed remote response must trigger one reflow using its real aspect ratio.");
                check(Math.Abs(window.SingleComposer.GetScrollbar("scrollbar").CurrentYPosition - beforeScroll) < 1,
                    "Remote-image completion must preserve the scroll position within the new document range.");
                scenario = $"{frameWidth}-notice-remote-loaded"; Capture(window);
                imageCache.Remove(remoteSource);
                imageCache[remoteSource] = new(Task.FromResult<byte[]?>(null));
                scenario = $"{frameWidth}-notice-remote-failed";
                window.Update(announcementSnapshot); window.OnRenderGUI(0); Capture(window);
                visualNotice = window.SingleComposer.GetContainer("announcement-content").Elements.OfType<GuiElementAnnouncement>().Single();
                check(!visualNotice.HasNewImages && !visualNotice.Components.OfType<AnnouncementCard>().Single().Children.OfType<AnnouncementImage>().Single().Loaded,
                    "Failed downloads must settle on an alt-text placeholder without endless recomposition.");
                imageCache.Remove(remoteSource);

                const string modImageSource = "https://mods.vintagestory.at/web/img/mod-default.png";
                var modImageCompletion = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
                imageCache[modImageSource] = new(modImageCompletion.Task);
                announcementSnapshot.AnnouncementHtml = AnnouncementImageChecks.PublicModCardsHtml;
                window.Update(announcementSnapshot); window.OnRenderGUI(0);
                visualNotice = window.SingleComposer.GetContainer("announcement-content").Elements.OfType<GuiElementAnnouncement>().Single();
                var modImages = Descendants(visualNotice.Components).OfType<AnnouncementImage>().ToArray();
                check(modImages.Length == 2 && modImages.All(i => !i.Loaded) && !visualNotice.HasNewImages,
                    "The supplied two-card HTML must compose both linked images while a shared download is pending.");
                var modScroll = window.SingleComposer.GetScrollbar("scrollbar"); modScroll.CurrentYPosition = 10; modScroll.TriggerChanged();
                double modScrollBefore = modScroll.CurrentYPosition;
                modImageCompletion.SetResult(publicModImage ?? fixtureBytes); window.OnRenderGUI(0);
                visualNotice = window.SingleComposer.GetContainer("announcement-content").Elements.OfType<GuiElementAnnouncement>().Single();
                modImages = Descendants(visualNotice.Components).OfType<AnnouncementImage>().ToArray();
                check(modImages.All(i => i.Loaded) && !visualNotice.HasNewImages
                    && Math.Abs(window.SingleComposer.GetScrollbar("scrollbar").CurrentYPosition - modScrollBefore) < 1,
                    "One completed mod-site image download must update both linked cards and retain scroll position.");
                var modOuter = visualNotice.Components.OfType<AnnouncementCard>().First();
                var modInner = modOuter.Children.OfType<AnnouncementCard>().Single();
                var modHeading = modInner.Children.OfType<LinkTextComponent>().First();
                check(modHeading.Font.FontWeight == FontWeight.Bold && modHeading.Font.UnscaledFontsize == 18
                    && modHeading.BoundsPerLine[0].Y >= modImages[0].BoundsPerLine[0].Y + modImages[0].BoundsPerLine[0].Height + 18 * RuntimeEnv.GUIScale - 1,
                    "The linked h3 must keep its font and appear below the complete image with the proper block margin.");
                scenario = $"{frameWidth}-notice-modsite-cards"; Capture(window);
                var modImageBox = modImages[0].BoundsPerLine[0];
                double linkedImageX = visualNotice.Bounds.renderX + modOuter.BoundsPerLine[0].X + modInner.BoundsPerLine[0].X + 28 * RuntimeEnv.GUIScale + modImageBox.X;
                double linkedImageY = visualNotice.Bounds.renderY + modOuter.BoundsPerLine[0].Y + modInner.BoundsPerLine[0].Y + 28 * RuntimeEnv.GUIScale + modImageBox.Y;
                int beforeImageClick = openedLinks.Count;
                ClickAt(window, linkedImageX + 5, linkedImageY + 5);
                check(openedLinks.Count == beforeImageClick + 1 && openedLinks[^1] == "https://mods.vintagestory.at/vsscsmod",
                    "Clicking the downloaded image nested in two cards must open the surrounding mod-details link.");
                modImages[0].OnMouseDown(new MouseEvent(5, (int)modImageBox.Y + 5, EnumMouseButton.Left, 0));
                modImages[0].OnMouseUp(new MouseEvent(-5, -5, EnumMouseButton.Left, 0));
                check(openedLinks.Count == beforeImageClick + 1, "Releasing the mouse outside a linked image must not open its link.");
                double[] modReadBounds = modImages.SelectMany(i => new[] { i.BoundsPerLine[0].Y, i.BoundsPerLine[0].Height }).ToArray();
                ClickElement(window, window.SingleComposer.GetElement("announcement-0")); window.OnRenderGUI(0);
                ClickElement(window, window.SingleComposer.GetElement("announcement-0")); window.OnRenderGUI(0);
                visualNotice = window.SingleComposer.GetContainer("announcement-content").Elements.OfType<GuiElementAnnouncement>().Single();
                check(Descendants(visualNotice.Components).OfType<AnnouncementImage>().SelectMany(i => new[] { i.BoundsPerLine[0].Y, i.BoundsPerLine[0].Height }).SequenceEqual(modReadBounds),
                    "The supplied mod-card HTML must preserve image layout between reading and administrator preview.");
                ClickElement(window, window.SingleComposer.GetElement("announcement-2")); window.OnRenderGUI(0);
                announcementSnapshot.AnnouncementHtml = originalHtml;
                window.Update(announcementSnapshot); window.OnRenderGUI(0);
                const string spacingHtml = "<p>段落甲<br>段内乙</p>\n<div><p>段落丙</p></div>\n<ul><li>项目甲</li><li>项目乙<ul><li>嵌套甲</li><li>嵌套乙</li></ul></li><li>项目丙</li></ul>\n<p>段落丁</p>";
                announcementSnapshot.AnnouncementHtml = spacingHtml;
                window.Update(announcementSnapshot); window.OnRenderGUI(0);
                var publishedPositions = CheckBlockMargins(window);
                scenario = $"{frameWidth}-notice-spacing"; Capture(window);
                ClickElement(window, window.SingleComposer.GetElement("announcement-0")); window.OnRenderGUI(0);
                var spacingEditor = window.SingleComposer.GetContainer("announcement-content").Elements.OfType<GuiElementTextArea>().Single();
                spacingEditor.SetValue(spacingHtml);
                ClickElement(window, window.SingleComposer.GetElement("announcement-0")); window.OnRenderGUI(0);
                check(CheckBlockMargins(window).SequenceEqual(publishedPositions), "Published notices and draft previews must use the same semantic block spacing.");
                ClickElement(window, window.SingleComposer.GetElement("announcement-2")); window.OnRenderGUI(0);
                string chinese = string.Concat(Enumerable.Repeat("文明模拟需要共同维护家园，尊重历史与真实生存。请查看（部落公告），携带莎草纸地图！", 12));
                announcementSnapshot.AnnouncementHtml = "<p>前段文字<b>" + chinese + "</b>正文 <a href='https://example.org'>Immersive Maps 链接</a></p>";
                window.Update(announcementSnapshot); window.OnRenderGUI(0);
                var notice = window.SingleComposer.GetContainer("announcement-content").Elements.OfType<GuiElementRichtext>().Single();
                var richText = notice.Components.OfType<RichTextComponent>().ToArray();
                check(richText.All(c => c.BoundsPerLine.All(b => b.X + b.Width <= notice.Bounds.InnerWidth + .5)),
                    "Continuous Chinese after inline formatting must wrap within the notice even with an AfterWord locale.");
                check(richText.All(c => string.Concat(c.Lines.Select(l => l.Text)).Replace("\r", "").Replace("\n", "") == c.DisplayText.Replace("\r", "").Replace("\n", "")),
                    "Wrapping Chinese, formatting and links must preserve every displayed character.");
                check(richText.All(c => c.Lines.Where(l => l.Text.Length > 0).All(l => !"，。！？、；：）】》".Contains(l.Text[0]))),
                    "Chinese closing punctuation must stay with the preceding text.");
                check(notice.Components.OfType<LinkTextComponent>().Single().Href == "https://example.org/",
                    "Local notice wrapping must retain native hyperlink targets.");
                scenario = $"{frameWidth}-notice-chinese"; Capture(window);
                announcementSnapshot.AnnouncementHtml = originalHtml;
                window.Update(announcementSnapshot); window.OnRenderGUI(0);
                ClickElement(window, window.SingleComposer.GetElement("announcement-0")); window.OnRenderGUI(0);
                var editor = window.SingleComposer.GetContainer("announcement-content").Elements.OfType<GuiElementTextArea>().Single();
                var toolbar = AnnouncementHtmlTools.Tools.Select(tool => window.SingleComposer.GetElement("announcement-html-" + tool)).ToArray();
                check(toolbar.All(button => button.Bounds.renderY >= window.SingleComposer.GetElement("announcement-title").Bounds.renderY
                    + window.SingleComposer.GetElement("announcement-title").Bounds.OuterHeight
                    && button.Bounds.renderY + button.Bounds.OuterHeight <= editor.Bounds.renderY
                    && button.Bounds.renderX + button.Bounds.OuterWidth <= window.SingleComposer.GetScrollbar("scrollbar").Bounds.renderX),
                    "All HTML tools must fit between the title and editor at every window size and GUI scale.");
                check(editor.Bounds.InnerHeight >= 28 * RuntimeEnv.GUIScale
                    && editor.Bounds.renderY + editor.Bounds.OuterHeight <= window.SingleComposer.GetElement("announcement-0").Bounds.renderY,
                    "The HTML toolbar must leave a usable editor without overlapping the fixed footer.");
                var toolbarMenus=new[]{"format","insert","embed"}.Select(name=>window.SingleComposer.GetElement("announcement-html-"+name)).ToArray();
                check(toolbarMenus.All(menu=>Math.Abs(menu.Bounds.renderY-toolbar[0].Bounds.renderY)<.5)
                    && toolbarMenus[0].Bounds.renderX>=toolbar[^1].Bounds.renderX+toolbar[^1].Bounds.OuterWidth
                    && toolbarMenus[^1].Bounds.renderX+toolbarMenus[^1].Bounds.OuterWidth<=window.SingleComposer.GetScrollbar("scrollbar").Bounds.renderX,
                    "All three dropdowns must remain on the same row to the right of the shortcut icons at every screen size.");
                check(Math.Abs(toolbarMenus[^1].Bounds.renderX+toolbarMenus[^1].Bounds.OuterWidth-editor.Bounds.renderX-editor.Bounds.OuterWidth-2*RuntimeEnv.GUIScale)<.5
                    && toolbarMenus.All(menu=>Math.Abs(menu.Bounds.OuterWidth-toolbarMenus[0].Bounds.OuterWidth)<.5),
                    "The three dropdowns must divide the remaining toolbar width evenly and fill the editor row.");
                check(toolbar.All(b=> b is GuiElementAnnouncementTool && b.Bounds.fixedWidth == b.Bounds.fixedHeight),
                    "Shortcut buttons must use drawn icons with square bounds.");
                var shortHint = (GuiElementHoverText)window.SingleComposer.GetElement("announcement-html-hint-image");
                shortHint.SetVisible(true); shortHint.RenderInteractiveElements(0); shortHint.SetVisible(false);
                var longHint = (GuiElementHoverText)window.SingleComposer.GetElement("announcement-html-hint-bold");
                longHint.SetVisible(true); longHint.RenderInteractiveElements(0); longHint.SetVisible(false);
                double HintWidth(GuiElementHoverText hint) => (double)typeof(GuiElementHoverText).GetField("hoverWidth",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(hint)!;
                check(HintWidth(shortHint) < HintWidth(longHint) && HintWidth(shortHint) < 160*RuntimeEnv.GUIScale,
                    "Tooltip widths must follow their text rather than a fixed wide box.");
                void SelectEmbed(int index)
                {
                    var menu = window.SingleComposer.GetElement("announcement-html-embed");
                    ClickElement(window,menu);
                    ClickAt(window,menu.Bounds.renderX+8*RuntimeEnv.GUIScale,
                        menu.Bounds.renderY+menu.Bounds.InnerHeight+(index+.5)*30*RuntimeEnv.GUIScale);
                    window.OnRenderGUI(0);
                }
                using(var embedCatalog = new EconomyClientCatalog(api))
                {
                    window.SetEconomyCatalog(embedCatalog);
                    editor.SetValue("嵌入位置"); ClickElement(window,editor);
                    window.OnKeyDown(new KeyEvent { KeyCode=(int)GlKeys.End });
                    SelectEmbed(3);
                    var resource = window.Composers["announcement-resource"];
                    check(resource != null && resource.GetTextInput("search").HasFocus,
                        "Selecting an embed category through the actual dropdown must open and focus its side picker.");
                    check(resource!.Bounds.renderX >= 0 && resource.Bounds.renderX+resource.Bounds.OuterWidth <= frameWidth+1,
                        "The resource picker must remain inside the screen, including compact windows.");
                    embedCatalog.Receive(new EconomyCatalogPacket { Total=1,Index=0,Items=[goodsPreview] }); embedCatalog.Advance(); embedCatalog.Items[0].Name="木柴";
                    window.OnRenderGUI(0);
                    resource=window.Composers["announcement-resource"];
                    resource.GetTextInput("search").SetValue("木柴"); window.OnRenderGUI(0);
                    resource=window.Composers["announcement-resource"];
                    check(resource.GetTextInput("search").HasFocus && resource.GetTextInput("search").GetText()=="木柴",
                        "Late catalog arrival and search rebuilds must retain search focus and text.");
                    scenario=$"{frameWidth}-notice-resource-picker"; Capture(window);
                    var resourceGrid=resource.GetElement("grid");
                    ClickAt(window,resourceGrid.Bounds.renderX+10,resourceGrid.Bounds.renderY+10); window.OnRenderGUI(0);
                    check(window.Composers["announcement-resource"]==null && editor.HasFocus && editor.GetText().StartsWith("嵌入位置<span data-game-item="),
                        "Selecting a resource must insert its HTML at the saved caret, close the picker and restore editor focus: " + editor.GetText() + " focus=" + editor.HasFocus + " closed=" + (window.Composers["announcement-resource"]==null));
                    SelectEmbed(5);
                    resource = window.Composers["announcement-resource"];
                    check(resource != null && !resource.GetButton("insert-selected").Enabled,
                        "The slideshow embed dropdown must open the multi-selection picker at every screen size.");
                    resource!.GetTextInput("search").SetValue("木柴"); window.OnRenderGUI(0);
                    resource = window.Composers["announcement-resource"];
                    resourceGrid = resource.GetElement("grid");
                    ClickAt(window, resourceGrid.Bounds.renderX + 10, resourceGrid.Bounds.renderY + 10); window.OnRenderGUI(0);
                    check(window.Composers["announcement-resource"] != null && resource.GetButton("insert-selected").Enabled,
                        "Choosing slideshow items must keep the resource picker open until insertion is confirmed.");
                    scenario = $"{frameWidth}-notice-slideshow-picker"; Capture(window);
                    ClickElement(window, resource.GetButton("insert-selected")); window.OnRenderGUI(0);
                    check(window.Composers["announcement-resource"] == null && editor.HasFocus && editor.GetText().Contains("data-game-slideshow=\"true\""),
                        "Confirming a slideshow must insert its embed at the caret and restore the editor focus.");
                    foreach (int categoryIndex in new[]{1,2,3,4})
                    {
                        SelectEmbed(categoryIndex);
                        check(window.Composers["announcement-resource"]!=null,"Every embed category must open a visible side picker: "+categoryIndex);
                        window.OnEscapePressed();
                    }
                    window.SetEconomyCatalog(null);
                }
                foreach (string tool in AnnouncementHtmlTools.Tools)
                {
                    ClickElement(window, editor); editor.SetValue("共同家园");
                    window.OnKeyDown(new KeyEvent { KeyCode = (int)GlKeys.Home, ShiftPressed = true });
                    ClickElement(window, window.SingleComposer.GetElement("announcement-html-" + tool));
                    check(editor.HasFocus && editor.GetText().Contains("共同家园") && editor.GetText() != "共同家园",
                        "A toolbar click must format the selected text and restore editor focus: " + tool);
                }
                ClickElement(window, editor); editor.SetValue("共同家园");
                window.OnKeyDown(new KeyEvent { KeyCode = (int)GlKeys.Home, ShiftPressed = true });
                ClickElement(window, window.SingleComposer.GetElement("announcement-html-bold"));
                check(editor.GetText() == "<strong>共同家园</strong>", "Clicking Bold must retain the selection while the toolbar takes focus: " + editor.GetText());
                window.OnKeyPress(new KeyEvent { KeyChar = '中' });
                check(editor.GetText() == "<strong>中</strong>", "Typing after a toolbar action must replace its selected placeholder or formatted text.");
                editor.SetValue("快捷键");
                window.OnKeyDown(new KeyEvent { KeyCode = (int)GlKeys.Home, ShiftPressed = true });
                window.OnKeyDown(new KeyEvent { KeyCode = (int)GlKeys.I, CtrlPressed = true });
                check(editor.GetText() == "<em>快捷键</em>", "Ctrl+I must apply HTML to the real editor selection.");
                editor.SetValue(announcementSnapshot.AnnouncementHtml);
                ClickElement(window, editor);
                var firstTyped = new KeyEvent { KeyChar = 'X' };
                window.OnKeyPress(firstTyped);
                check(editor.HasFocus && firstTyped.Handled && editor.GetText().Contains('X'),
                    "Clicking the Markdown editor must focus it and accept keyboard input through the real dialog.");
                const string draft = "<h2>新公告</h2>\n<p>请携带纸图。&amp; <strong>保留草稿</strong></p>\n<ul><li><p>先建立家园</p></li><li><p>交换纸图</p></li></ul>\n<p><a href=\"https://example.org/rules\">规则</a></p>";
                editor.SetValue(draft);
                scenario = $"{frameWidth}-notice-html-editor"; Capture(window);
                ClickElement(window, window.SingleComposer.GetElement("announcement-0")); window.OnRenderGUI(0);
                check(!window.SingleComposer.GetContainer("announcement-content").Elements.OfType<GuiElementTextArea>().Any()
                    && window.SingleComposer.GetElement("announcement-html-bold") == null, "Preview must render HTML and hide its editing toolbar.");
                var htmlPreview = window.SingleComposer.GetContainer("announcement-content").Elements.OfType<GuiElementAnnouncement>().Single();
                var previewText = htmlPreview.Components.OfType<RichTextComponent>().Select(c => c.DisplayText).ToArray();
                check(string.Concat(previewText).Contains("• 交换纸图") && !string.Concat(previewText).Contains("<strong>")
                    && htmlPreview.Components.OfType<LinkTextComponent>().Any(c => c.Href == "https://example.org/rules"),
                    "The preview must render HTML lists, emphasis and native clickable links.");
                ClickAnnouncement(window, "HTML draft preview");
                scenario = $"{frameWidth}-notice-preview"; Capture(window);
                CheckNoticeLayout(window);
                Invoke(window,"ChangePage",1); window.OnRenderGUI(0);
                Invoke(window,"ChangePage",0); window.OnRenderGUI(0);
                ClickElement(window, window.SingleComposer.GetElement("announcement-0")); window.OnRenderGUI(0);
                editor = window.SingleComposer.GetContainer("announcement-content").Elements.OfType<GuiElementTextArea>().Single();
                check(editor.GetText() == draft, $"Switching tabs must preserve the announcement draft: {editor.GetText()} != {draft}");
                ClickElement(window, window.SingleComposer.GetElement("announcement-1"));
                var saveRequest = requests.Last();
                check(saveRequest.Action == "announcement-save" && !saveRequest.IsMarkdown && saveRequest.Html == draft && saveRequest.Revision == 4 && saveRequest.RequestId.Length > 0,
                    "Announcement save must submit the draft, original revision and acknowledgement id.");
                announcementSnapshot.AnnouncementSaveRequestId = saveRequest.RequestId;
                announcementSnapshot.AnnouncementSaved = false;
                announcementSnapshot.AnnouncementRevision = 5;
                announcementSnapshot.AnnouncementHtml = "<p>其他管理员的新公告</p>";
                window.Update(announcementSnapshot); window.OnRenderGUI(0);
                check(window.SingleComposer.GetContainer("announcement-content").Elements.OfType<GuiElementTextArea>().Single().GetText() == draft,
                    "A rejected save must preserve the draft.");
                ClickElement(window, window.SingleComposer.GetElement("announcement-1"));
                var retry = requests.Last();
                check(retry.Action == "announcement-save" && retry.Revision == 5 && retry.Html == draft && retry.RequestId != saveRequest.RequestId,
                    "Retrying a rejected save must use the latest revision and a new acknowledgement id.");
                announcementSnapshot.AnnouncementSaveRequestId = retry.RequestId;
                announcementSnapshot.AnnouncementSaved = true; announcementSnapshot.AnnouncementHtml = draft;
                announcementSnapshot.AnnouncementCollection = true;
                announcementSnapshot.Announcements = [new() { Id = "legacy", Title = "公告", Html = draft, IsMarkdown = false }];
                announcementSnapshot.AnnouncementRevision = 6;
                window.Update(announcementSnapshot); window.OnRenderGUI(0);
                check(!window.SingleComposer.GetContainer("announcement-content").Elements.OfType<GuiElementTextArea>().Any()
                    && window.SingleComposer.GetElement("announcement-1") == null, "Successful saving must return to viewing the published notice.");
                ClickAnnouncement(window, "published notice after saving");
                check(window.SingleComposer.GetContainer("announcement-content").Elements.OfType<GuiElementAnnouncement>().Single()
                    .Components.OfType<RichTextComponent>().Select(c => c.DisplayText).SequenceEqual(previewText),
                    "Published HTML must use exactly the same rendered content as its preview.");
                ClickElement(window, window.SingleComposer.GetElement("announcement-0")); window.OnRenderGUI(0);
                editor = window.SingleComposer.GetContainer("announcement-content").Elements.OfType<GuiElementTextArea>().Single();
                string longDraft = string.Concat(Enumerable.Range(0, 1200).Select(i => $"<p>公告 {i}：历史与生存</p>\n"));
                WithoutFlowErrors(() => editor.SetValue(longDraft), "loading long HTML into the editor");
                window.SingleComposer.FocusElement(window.SingleComposer.GetContainer("announcement-content").TabIndex);
                window.SingleComposer.GetContainer("announcement-content").FocusElement(editor.TabIndex);
                editor.SetCaretPos(2, editor.GetLines().Count - 2);
                int lastLine = editor.CaretPosLine;
                int lastCaret = editor.CaretPosInLine;
                check(window.SingleComposer.GetScrollbar("scrollbar").CurrentYPosition > 0 && editor.Bounds.InnerHeight <= frameHeight,
                    $"Long HTML must scroll to the caret while keeping native editor textures within the viewport: frame {frameWidth}, scroll {window.SingleComposer.GetScrollbar("scrollbar").CurrentYPosition}, height {editor.Bounds.InnerHeight}, lines {editor.GetLines().Count}.");
                scenario = $"{frameWidth}-notice-editor"; Capture(window);
                window.Update(announcementSnapshot); WithoutFlowErrors(() => window.OnRenderGUI(0), "restoring the long HTML editor");
                editor = window.SingleComposer.GetContainer("announcement-content").Elements.OfType<GuiElementTextArea>().Single();
                check(editor.GetText() == longDraft && editor.HasFocus && editor.CaretPosLine == lastLine && editor.CaretPosInLine == lastCaret,
                    $"Snapshot refresh must preserve long HTML, editor focus and caret position (text {editor.GetText().Length}/{longDraft.Length}, focus {editor.HasFocus}, line {editor.CaretPosLine}/{lastLine}, caret {editor.CaretPosInLine}).");
                ClickElement(window, editor);
                check(editor.CaretPosLine > 100, "Clicking a scrolled editor must position the caret in the visible document section.");
                int caretBeforeScroll = editor.CaretPosWithoutLineBreaks;
                string textBeforeScroll = editor.GetText();
                var editBar = window.SingleComposer.GetScrollbar("scrollbar");
                double editorY = editor.Bounds.renderY, editorHeight = editor.Bounds.InnerHeight;
                mouseX = (int)(editBar.Bounds.renderX + editBar.Bounds.InnerWidth / 2);
                mouseY = (int)(editBar.Bounds.renderY + editBar.Bounds.InnerHeight / 3);
                window.OnMouseDown(new MouseEvent(mouseX, mouseY, EnumMouseButton.Left, 0));
                window.OnMouseMove(new MouseEvent(mouseX - 40, mouseY + 30, EnumMouseButton.Left, 0));
                window.OnMouseUp(new MouseEvent(mouseX - 40, mouseY + 30, EnumMouseButton.Left, 0));
                check(editor.HasFocus && editor.CaretPosWithoutLineBreaks == caretBeforeScroll && editor.GetText() == textBeforeScroll,
                    "Dragging the notice scrollbar into the editor must preserve text, caret and focus.");
                check(editor.Bounds.renderY == editorY && editor.Bounds.InnerHeight == editorHeight,
                    "Scrolling must leave the editor viewport position and size fixed.");
                var typed = new KeyEvent { KeyChar = 'X' };
                window.OnKeyPress(typed);
                check(typed.Handled && editor.GetText() == textBeforeScroll.Insert(caretBeforeScroll, "X")
                    && editor.CaretPosWithoutLineBreaks == caretBeforeScroll + 1,
                    "Native keyboard input must update the HTML at the scrolled caret.");
                string boundaryDraft = string.Concat(Enumerable.Repeat("中文换行编辑保留字词，WesternWord 与 HTML 文本。", 15));
                editor.SetValue(boundaryDraft);
                editor.SetCaretPos(editor.GetLines()[2].Length - 1, 2);
                int boundaryCaret = editor.CaretPosWithoutLineBreaks;
                window.OnKeyPress(new KeyEvent { KeyChar = '中' });
                check(editor.GetText() == boundaryDraft.Insert(boundaryCaret, "中") && editor.CaretPosWithoutLineBreaks == boundaryCaret + 1,
                    "Typing at a Chinese soft-wrap boundary must preserve every character and the document caret.");
                window.OnKeyDown(new KeyEvent { KeyCode = (int)GlKeys.Enter });
                check(editor.GetText() == boundaryDraft.Insert(boundaryCaret, "中\n") && editor.CaretPosWithoutLineBreaks == boundaryCaret + 2,
                    "Enter in wrapped HTML must insert a real newline at the caret and reflow the editor.");
                window.OnKeyDown(new KeyEvent { KeyCode = (int)GlKeys.BackSpace });
                check(editor.GetText() == boundaryDraft.Insert(boundaryCaret, "中") && editor.CaretPosWithoutLineBreaks == boundaryCaret + 1,
                    "Backspace at a wrapped newline must join the text while preserving the document caret.");
                editor.SetValue(longDraft + "X");
                ClickElement(window, window.SingleComposer.GetElement("announcement-0")); WithoutFlowErrors(() => window.OnRenderGUI(0), "previewing long HTML");
                scenario = $"{frameWidth}-notice-long-preview"; Capture(window);
                check(window.SingleComposer.GetContainer("announcement-content").Bounds.OuterHeight > frameHeight,
                    "Long announcement previews must retain their full scrollable document height.");
                ClickAnnouncement(window, "long HTML draft preview");
                ClickElement(window, window.SingleComposer.GetElement("announcement-2")); window.OnRenderGUI(0);
                check(!window.SingleComposer.GetContainer("announcement-content").Elements.OfType<GuiElementTextArea>().Any(), "Cancel must return to the saved notice.");
                var multiNotice = new CivilizationSnapshotPacket { CanEditAnnouncement = true, AnnouncementCollection = true, AnnouncementRevision = 9,
                    Announcements = [new() { Id = "welcome", Title = "欢迎", Html = "<p>欢迎正文</p>" },
                        new() { Id = "rules", Title = "服务器规则", Html = "<p>规则正文</p>", Notify = true, NotificationRevision = 9 }],
                    Open = true, NotificationId = "rules", NotificationRevision = 9 };
                window.Update(multiNotice); window.OnRenderGUI(0);
                check(window.SingleComposer.GetVerticalTab("announcement-tabs").ActiveElement == 1
                    && requests.Last() is { Action: "announcement-seen", Target: "rules", Revision: 9 },
                    "Login notifications must select and acknowledge exactly the notified announcement page.");
                var notifiedHtml = window.SingleComposer.GetContainer("announcement-content").Elements.OfType<GuiElementAnnouncement>().Single();
                check(!notifiedHtml.Components.OfType<RichTextComponent>().Any(c => c.DisplayText.Contains("服务器规则")),
                    "The announcement title belongs to its tab, not an extra heading in the HTML content.");
                ClickElement(window, window.SingleComposer.GetElement("announcement-0")); window.OnRenderGUI(0);
                var noticeTitle = (GuiElementTextInput)window.SingleComposer.GetElement("announcement-title");
                var noticeEditor = window.SingleComposer.GetContainer("announcement-content").Elements.OfType<GuiElementTextArea>().Single();
                check(noticeTitle.GetText() == "服务器规则" && noticeTitle.Bounds.renderY + noticeTitle.Bounds.OuterHeight < noticeEditor.Bounds.renderY
                    && noticeTitle.Bounds.renderX + noticeTitle.Bounds.OuterWidth <= window.SingleComposer.Bounds.renderX + window.SingleComposer.Bounds.InnerWidth,
                    "Editing must show a fully visible fixed title field above the HTML editor.");
                var notifySwitch = (GuiElementSwitch)window.SingleComposer.GetElement("announcement-notify");
                check(notifySwitch.On && notifySwitch.Bounds.renderX > window.SingleComposer.GetElement("announcement-0").Bounds.renderX,
                    "The notification switch must be visible at the right of the editor footer.");
                var noticeActions = Enumerable.Range(0, 4).Select(i => window.SingleComposer.GetElement("announcement-" + i)).ToArray();
                check(noticeActions.All(button => button.Bounds.renderY + button.Bounds.OuterHeight <= window.SingleComposer.Bounds.renderY + window.SingleComposer.Bounds.InnerHeight
                    && (button.Bounds.renderY + button.Bounds.OuterHeight <= notifySwitch.Bounds.renderY
                        || button.Bounds.renderX + button.Bounds.OuterWidth <= notifySwitch.Bounds.renderX)),
                    "Notice actions must fit above the window bottom and leave the notification switch unobstructed.");
                scenario = $"{frameWidth}-notice-multiple-editor"; Capture(window);
                noticeTitle.SetValue("更新规则");
                ClickElement(window, window.SingleComposer.GetElement("announcement-1"));
                check(requests.Last() is { Action: "announcement-save", Target: "rules", Name: "更新规则", Notify: true },
                    "Saving must send the selected notice title, HTML and notification state together.");
                multiNotice.AnnouncementSaveRequestId = requests.Last().RequestId; multiNotice.AnnouncementSaved = true;
                multiNotice.NotificationId = ""; multiNotice.Announcements[1].Title = "更新规则";
                window.Update(multiNotice); window.OnRenderGUI(0);
                ClickElement(window, window.SingleComposer.GetElement("announcement-0")); window.OnRenderGUI(0);
                ClickElement(window, window.SingleComposer.GetElement("announcement-3"));
                check(requests.Last() is { Action: "announcement-delete", Target: "rules" }, "The editor footer must delete only its selected notice.");
                multiNotice.AnnouncementSaveRequestId = requests.Last().RequestId; multiNotice.Announcements.RemoveAt(1);
                window.Update(multiNotice); window.OnRenderGUI(0);
                ClickElement(window, window.SingleComposer.GetElement("announcement-new")); window.OnRenderGUI(0);
                ((GuiElementTextInput)window.SingleComposer.GetElement("announcement-title")).SetValue("新公告");
                ClickElement(window, window.SingleComposer.GetElement("announcement-1"));
                check(requests.Last() is { Action: "announcement-create", Name: "新公告", Target: "" }, "Adding a notice must allocate a new server-owned entry.");
                multiNotice.AnnouncementSaveRequestId = requests.Last().RequestId; multiNotice.Announcements.Add(new() { Id = "new", Title = "新公告" });
                window.Update(multiNotice); window.OnRenderGUI(0);
                window.Update(announcementSnapshot); window.OnRenderGUI(0);

                ClickAnnouncement(window, "published notice after canceling edits");
                check(window.SingleComposer.GetElement("pages")==null,"Main switch tabs must be absent.");
                Invoke(window,"ChangePage",2); window.OnRenderGUI(0);
                scenario=$"{frameWidth}-tribe"; Capture(window);
                var container=window.SingleComposer.GetContainer("economy-content");
                var input=container.Elements.OfType<GuiElementTextInput>().Single(); input.SetValue("测试部落");
                window.SingleComposer.FocusElement(container.TabIndex); container.FocusElement(input.TabIndex);
                window.Update(new() {Open=true}); window.OnRenderGUI(0);
                input=window.SingleComposer.GetContainer("economy-content").Elements.OfType<GuiElementTextInput>().Single();
                check(input.GetText()=="测试部落" && input.HasFocus,"Creation draft/focus must survive refresh.");
                ClickElement(window,window.SingleComposer.GetContainer("economy-content").Elements.OfType<GuiElementTextButton>().Single(b=>b.Text==translations["ui.create-tribe"]));
                check(requests.LastOrDefault() is {Action:"create",Name:"测试部落"},"Create request lost entered name.");

                using (var roster = new TribeRosterHud(api))
                {
                    roster.Update(new TribeRosterPacket { TribeId = "own", HeaderSvg = TribeRosterConfig.DefaultHeader,
                        Members = Enumerable.Range(0, 12).Select(i => new TribeRosterMember { Uid = i == 0 ? "chief" : "roster-" + i,
                            Name = "成员 " + i, PingMs = 20 + i * 13 }).ToList() });
                    roster.Show();
                    check(roster.SingleComposer.Bounds.renderY < GuiElement.scaled(20), "Roster must appear at the top.");
                    using var surface = new ImageSurface(Format.Argb32, frameWidth, frameHeight);
                    using var context = new Context(surface);
                    captureContext = context;
                    try { roster.SingleComposer.Render(0); } finally { captureContext = null; }
                    surface.WriteToPng(Path.Combine(output, $"{frameWidth}-tribe-roster.png"));
                    check(!errors.Any(e => e.StartsWith("Roster SVG")), "Native SVG rendering failed.");
                    roster.Hide();
                    check(!roster.IsOpened(), "Roster remained visible after release.");
                }
                scenario = $"{frameWidth}-leader";
                var snapshot = new CivilizationSnapshotPacket
                {
                    MyTribeId = "own", MyTribeName = "测试部落", IsLeader = true, OutpostLimit = 2, TerritoryRadius = 50, MyOutpostCount = 1,
                    TerritoryEffects = [new() { Code = "effect-health", Value = 10 }],
                    Members = Enumerable.Range(0, 30).Select(i => new TribePerson { Uid = i == 0 ? "chief" : "member-" + i, Name = "成员 " + i, IsLeader = i == 0 }).ToList(),
                };
                window.Update(snapshot);
                Invoke(window, "ComposeWindow");
                window.SingleComposer.GetScrollbar("scrollbar").CurrentYPosition = 0;
                window.SingleComposer.GetScrollbar("scrollbar").TriggerChanged();
                var originalProperties = playerEntity.Properties;
                typeof(EntityPlayer).GetProperty(nameof(EntityPlayer.Properties))!.SetValue(playerEntity,
                    new EntityProperties { EyeHeight = 1.7, Client = new([], new()) { Renderer = new PortraitRenderer(playerEntity, api) } });
                var firstPortrait=window.SingleComposer.GetContainer("economy-content").Elements.OfType<GuiElementTribePortrait>().First();
                window.SingleComposer.GetScrollbar("scrollbar").CurrentYPosition=(float)GuiElement.scaled(firstPortrait.Bounds.fixedY);
                window.SingleComposer.GetScrollbar("scrollbar").TriggerChanged();
                renderedPortraitClips.Clear();
                Capture(window);
                check(renderedPortraitClips.Count > 0 && renderedPortraitClips.All(c => c != null
                    && c.renderX >= c.ParentBounds.renderX + GuiElement.scaled(3) - .01
                    && c.renderY >= c.ParentBounds.renderY + GuiElement.scaled(3) - .01
                    && c.renderX + c.OuterWidth <= c.ParentBounds.renderX + c.ParentBounds.InnerWidth - GuiElement.scaled(3) + .01
                    && c.renderY + c.OuterHeight <= c.ParentBounds.renderY + c.ParentBounds.InnerHeight - GuiElement.scaled(3) + .01),
                    "Loaded player portraits must be clipped inside all four native frame borders, including the bottom edge.");
                typeof(EntityPlayer).GetProperty(nameof(EntityPlayer.Properties))!.SetValue(playerEntity, originalProperties);
                container=window.SingleComposer.GetContainer("economy-content");
                var portraits=container.Elements.OfType<GuiElementTribePortrait>().ToArray();
                check(portraits.Length==snapshot.Members.Count && portraits[0].Bounds.fixedY==portraits[1].Bounds.fixedY
                    && portraits[0].Bounds.fixedX<portraits[1].Bounds.fixedX && portraits[2].Bounds.fixedY>portraits[0].Bounds.fixedY,"Members must occupy two columns.");
                check(window.SingleComposer.GetElement("tribe-tabs")!=null,"Tribe details and authorization require left tabs.");
                GuiElementTextButton FooterButton(string text)
                {
                    var buttons=window.SingleComposer.GetContainer("tribe-footer").Elements.OfType<GuiElementTextButton>().ToArray();
                    var button=buttons.Single(b=>b.Text==text);
                    Invoke(window,"MoveFooter",button.Bounds.fixedX-80-(double)typeof(VscnClusterWindow).GetField("footerOffset",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!);
                    return button;
                }
                var footerButtons=window.SingleComposer.GetContainer("tribe-footer").Elements.OfType<GuiElementTextButton>().ToArray();
                check(footerButtons.All(b=>b.Bounds.fixedY==footerButtons[0].Bounds.fixedY) && !footerButtons.Any(b=>b.Text.Contains("附庸")),"Footer must remain one row without outpost controls.");
                var fixedButton=FooterButton("查看领地"); double footerY=fixedButton.Bounds.renderY;
                window.SingleComposer.GetScrollbar("scrollbar").CurrentYPosition=150;window.SingleComposer.GetScrollbar("scrollbar").TriggerChanged();
                check(fixedButton.Bounds.renderY==footerY,"Scrolling members moved footer.");
                ClickElement(window,FooterButton(translations["ui.invite-nearby"]));check(requests.Last().Action=="invite","Invite action lost.");
                ClickElement(window,FooterButton(translations["ui.edit-name"]));window.OnRenderGUI(0);
                window.SingleComposer.GetContainer("economy-content").Elements.OfType<GuiElementTextInput>().Single().SetValue("新部落名");
                ClickElement(window,FooterButton(translations["ui.save"]));check(requests.Last() is {Action:"rename",Name:"新部落名"},"Rename failed.");
                window.OnRenderGUI(0);
                snapshot.IsLeader=false;window.Update(snapshot);window.OnRenderGUI(0);
                check(!window.SingleComposer.GetContainer("tribe-footer").Elements.OfType<GuiElementTextButton>().Any(b=>b.Text==translations["ui.edit-name"]),"Member sees leader rename action.");
                check(!window.SingleComposer.GetContainer("tribe-footer").Elements.OfType<GuiElementTextButton>().Any(b=>b.Text=="确认扩张"),"Population territory must not have an expansion confirmation button.");
                snapshot.IsLeader=true;window.Update(snapshot);window.OnRenderGUI(0);
                snapshot.AuthorizationEnabled = snapshot.ConstructionAuthorizationEnabled = snapshot.BenefitAuthorizationEnabled = true;
                snapshot.ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                snapshot.Authorizations = [new() { Id = "parchment-1", Uid = "guest", Name = "测试访客", TribeName = "测试部落", ExpiresAt = -1, Construction = true, Benefits = true }];
                snapshot.AuthorizationHistory = [new() { Id = "ended-parchment", Uid = "guest", Name = "测试访客", TribeName = "测试部落",
                    Status = "revoked", Parchment = true, SignerName = "测试首领", Construction = true,
                    IssuedAt = snapshot.ServerTime - 60000, ExpiresAt = snapshot.ServerTime + 1800000, EndedAt = snapshot.ServerTime }];
                snapshot.ReceivedAuthorizations = [new() { Uid = "chief", Name = "首领", TribeName = "其他部落", ExpiresAt = snapshot.ServerTime + 60000, Benefits = true }];
                window.Update(snapshot); window.OnRenderGUI(0);
                window.SingleComposer.GetVerticalTab("tribe-tabs").SetValue(1,true); window.OnRenderGUI(0);
                var authorizationBody=window.SingleComposer.GetContainer("economy-content");
                check(authorizationBody.Elements.OfType<GuiElementTextInput>().Count()==2
                    && authorizationBody.Elements.OfType<GuiElementTextButton>().Any(b=>b.Text==translations["ui.auth-confirm"]),
                    "Authorization must be issued directly from the window.");
                var authScroll=window.SingleComposer.GetScrollbar("scrollbar");
                var revokeGrant = authorizationBody.Elements.OfType<GuiElementTextButton>().Single(b => b.Text == translations["ui.auth-revoke"]);
                authScroll.CurrentYPosition = (float)(revokeGrant.Bounds.fixedY * RuntimeEnv.GUIScale); authScroll.TriggerChanged();
                ClickElement(window, revokeGrant); check(requests.Last() is { Action: "revoke-authorization", Target: "parchment-1" }, "Early revoke button did not target its individual contract.");
                authScroll.CurrentYPosition = 0; authScroll.TriggerChanged();
                scenario = $"{frameWidth}-tribe-authorization"; Capture(window);
                snapshot.IsLeader = false; window.Update(snapshot); window.OnRenderGUI(0);
                check(!window.SingleComposer.GetContainer("economy-content").Elements.OfType<GuiElementTextInput>().Any(), "Nonleader must not see grant controls.");
                snapshot.IsLeader = true;
                snapshot.HasMainHall = true; snapshot.TerritoryArea = 484; snapshot.TerritoryBudget = 1536;
                snapshot.TerritorySites = [new() { Id = "main", Bounds = new() { MinX = 91, MinZ = 91, MaxX = 112, MaxZ = 112 }, Area = 484, SupportLimit = 768, CooldownSeconds = 120, Status = "范围有效" }];
                snapshot.Regions = [new() { Id = "hall", Kind = "room", Root = true, SiteId = "main", Status = "valid", Area = 16 },
                    new() { Id = "yard", Kind = "courtyard", SiteId = "main", Status = "disconnected", Area = 32 }];
                snapshot.TerritoryCandidates = [new() { Id = "candidate-room", Kind = "room", Ready = true, Area = 484, Message = "确认前请核对建筑范围。" }];
                typeof(VscnClusterWindow).GetField("tribePage",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,0);
                window.Update(snapshot);window.OnRenderGUI(0);
                scenario=$"{frameWidth}-building-territory";Capture(window);
                check(!requests.Any(r=>r.Action=="territory-preview" && r.Target=="candidate-room"),"Retired room candidates must not trigger a preview.");
                int beforePreview=requests.Count;
                ClickElement(window,FooterButton("查看领地"));
                check(requests.Count==beforePreview+1 && requests.Last().Action=="territory-preview","Polygon preview action missing.");
                snapshot.TerritoryMemberCount=5; snapshot.TerritoryArea=2560; snapshot.TerritoryBudget=2304;
                snapshot.TerritoryPendingShrinkArea=256;
                snapshot.TerritoryShrinkDeadlines=[new() {MemberTier=6,ExpiresAtUtc=snapshot.ServerTime+86400000}];
                snapshot.TerritoryHint="超额领地保留至缩地期限";
                window.Update(snapshot);window.OnRenderGUI(0);
                scenario=$"{frameWidth}-population-territory";Capture(window);
                var economyRequests = new List<EconomyRequest>();
                window.SetEconomySender(economyRequests.Add);
                var usedPreview = JsonConvert.DeserializeObject<EconomyItemView>(JsonConvert.SerializeObject(goodsPreview))!;
                usedPreview.DurabilityLow = 27; usedPreview.DurabilityHigh = 100; usedPreview.DurabilityMax = 100;
                var economic = new EconomySnapshot
                {
                    TribeId = "tribe", IsLeader = true, CanReadLedger = true, CoinageAvailable = true, ReferenceCoin = "copper",
                    Coins = [new() { Key = "copper", Name = "铜币[铜]", Code = "coinage:coin-copper-sm", Design = 7, Shape = -6, Own = true, Accepted = true, Rate = "1", Issuers = "测试部落", Minters = "首领", Held = 64 },
                        new() { Key = "silver", Name = "银币[银]", Code = "coinage:coin-silver-sm", Design = 8, Shape = -5, Accepted = true, Rate = "10", Issuers = "远方部落", Minters = "访客" }],
                    Items = [goodsPreview, new() { Key = "stone", Name = "石头", Count = 64 }],
                    Offers = [new() { Id = "offer", Name = "木柴", DisplayItem = usedPreview, ItemKey = "wood", Coin = "copper", CoinName = "铜币[铜]", Price = 3, Quantity = 4, Available = 32, Enabled = true, Ready = true }],
                    WorldTradeCount = 17, WorldPlayerCount = 31,
                    WorldTribes = [new() { Id = "tribe", Name = "测试部落", Coins = ["copper"], TradeCount = 4 }, new() { Id = "other", Name = "远方部落", Coins = ["silver"] }],
                    WorldRates = [new() { TribeId = "tribe", Tribe = "测试部落", CoinKey = "silver", ReferenceKey = "copper", Coin = "银币[银]", ReferenceCoin = "铜币[铜]", Rate = "10" }],
                    ArchivedCoins = [new() { Key = "retired", Name = "旧币[铜]", Code = "coinage:coin-copper-sm", Design = 9, Shape = -6 }],
                    History = [
                        new() { Utc = DateTimeOffset.UtcNow.ToString("O"), Text = "木柴", DisplayItem = goodsPreview, Quantity = 3,
                            SellerName = "首领", BuyerName = "成员", Coin = "copper", CoinName = "铜币", Amount = 5 },
                        new() { Utc = DateTimeOffset.UtcNow.ToString("O"), Text = "木柴与混合支付", DisplayItem = goodsPreview, Quantity = 7,
                            SellerName = "首领", BuyerName = "成员", Exchange = Enumerable.Range(0, 18).Select(i => new EconomyExchangeItem
                            { Coin = i % 3 == 0 ? "silver" : i % 3 == 1 ? "retired" : "", Display = goodsPreview, Quantity = i + 1 }).ToList() },
                        new() { Utc = DateTimeOffset.UtcNow.ToString("O"), Text = "建造公共道路", Content = "修建广场到农田的道路。",
                            SellerName = "首领", BuyerName = "成员", Coin = "copper", CoinName = "铜币", Amount = 5, Quantity = 1, IsTask = true }
                    ], HistoryPages = 2,
                    Ledger = [new() { Uid = "chief", Name = "首领", CoinKey = "copper", Coin = "铜币", Held = 64, Received = 10, Paid = 5, Count = 3, Online = true },
                        new() { Uid = "former", Name = "旧成员", CoinKey = "copper", Coin = "铜币", Paid = 7, Count = 1, Departed = true }]
                };
                window.UpdateEconomy(economic);
                Invoke(window, "ChangePage", 3); window.OnRenderGUI(0);
                check(economyRequests.Last().Action == "open", "Entering the economy page must subscribe to server snapshots.");
                foreach (int tab in new[]{1})
                {
                    window.OnRenderGUI(0);
                    CheckCenteredMain();
                    check(window.SingleComposer.GetElement("economy-tabs")==null, "Removed economy pages must have no tabs.");
                    var economyContainer = window.SingleComposer.GetContainer("economy-content");
                    check(economyContainer.Elements.All(e => e.Bounds.renderX >= economyContainer.Bounds.renderX - 1 && e.Bounds.renderX + e.Bounds.OuterWidth <= economyContainer.Bounds.renderX + economyContainer.Bounds.InnerWidth + 1), "Economy fields must fit the viewport at all GUI scales.");
                    scenario = $"{frameWidth}-economy-{tab}"; Capture(window);
                    var coinElements = economyContainer.Elements.Where(e => e.GetType().Name == "GuiElementEconomyCoin").ToArray();
                    check(coinElements.Length > 0, "Each economy list must show its real currency item pictures.");

                    if (tab == 1)
                    {
                        check(!economyContainer.Elements.OfType<GuiElementWrappedText>().Any(t => t.GetText().Contains("实际库存耐久")),
                            "Offer condition details belong in native hover information rather than an extra text row.");
                        var goodsElement = economyContainer.Elements.Single(e => e.GetType().Name == "GuiElementEconomyItem");
                        var goodsSlot = (ItemSlot)goodsElement.GetType().GetField("slot", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(goodsElement)!;
                        check(goodsSlot.Itemstack!.Collectible.Code == goodsItem.Code && goodsSlot.Itemstack.Attributes.GetString("trade-preview") == "goods"
                            && goodsElement.Bounds.fixedX == 0 && coinElements[0].Bounds.fixedX > 0 && coinElements[0].Bounds.fixedWidth == 36
                            && coinElements[0].Bounds.fixedY > goodsElement.Bounds.fixedY,
                            "Sale payments must show a currency item slot on its own row below the goods name.");
                        var paymentSlot = (ItemSlot)coinElements[0].GetType().GetField("slot", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(coinElements[0])!;
                        check(paymentSlot.StackSize == 3 && coinElements[0].GetType().BaseType!.GetField("quantityTexture", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(coinElements[0]) != null
                            && !economyContainer.Elements.OfType<GuiElementWrappedText>().Any(t => t.GetText().Contains("枚") || t.GetText() == "铜币[铜]"),
                            "Legacy single-currency payments must use only their real item image and bottom-right quantity, without price/name text.");
                        var rightControls = economyContainer.Elements.Where(e => e is GuiElementSwitch or GuiElementTextButton
                            && e.Bounds.fixedY >= goodsElement.Bounds.fixedY).ToArray();
                        check(goodsElement.Bounds.fixedWidth == 48 && goodsElement.Bounds.fixedHeight == 48
                            && rightControls.All(e => e.Bounds.fixedX >= goodsElement.Bounds.fixedWidth + 8)
                            && rightControls.Min(e => e.Bounds.fixedY) < goodsElement.Bounds.fixedY + 150,
                            "The item must keep a normal square size, with information and compact actions in the column immediately to its right.");
                        check(renderedItemStacks.Any(r => r.Slot == goodsSlot
                            && Math.Abs(r.Size - GuiElement.scaled(GuiElementPassiveItemSlot.unscaledItemSize)) < .01),
                            "Goods must use the native inventory's item size, leaving room for long or rotated models.");
                    }

                    ClickAt(window, economyContainer.Bounds.renderX + 2, economyContainer.Bounds.ParentBounds.renderY + 2);
                    window.OnKeyDown(new KeyEvent { KeyCode = (int)GlKeys.Tab }); window.OnKeyUp(new KeyEvent { KeyCode = (int)GlKeys.Tab });
                }
                var unaffiliated = JsonConvert.DeserializeObject<EconomySnapshot>(JsonConvert.SerializeObject(economic))!;
                unaffiliated.TribeId = ""; unaffiliated.IsLeader = unaffiliated.CanReadLedger = false;
                window.UpdateEconomy(unaffiliated); window.OnRenderGUI(0);
                window.OnRenderGUI(0);
                check(window.SingleComposer.GetElement("economy-footer-new-offer") != null
                    && window.SingleComposer.GetElement("economy-footer-new-task") != null,
                    "Unaffiliated players must see personal offer and task creation controls.");
                check(window.SingleComposer.GetElement("economy-tabs")==null && window.SingleComposer.GetElement("economy-history-next")==null,
                    "Personal trading must omit removed pages and history.");
                scenario = $"{frameWidth}-personal-offers"; Capture(window);
                var longEconomy = JsonConvert.DeserializeObject<EconomySnapshot>(JsonConvert.SerializeObject(economic))!;
                longEconomy.Coins = Enumerable.Range(0, 16).Select(i =>
                {
                    var coin = JsonConvert.DeserializeObject<EconomyCoinView>(JsonConvert.SerializeObject(economic.Coins[i % 2]))!;
                    coin.Key = "long-coin-" + i; coin.Name = "测试货币 " + i; return coin;
                }).ToList();
                longEconomy.Offers = Enumerable.Range(0, 24).Select(i =>
                {
                    var offer = JsonConvert.DeserializeObject<EconomyOfferView>(JsonConvert.SerializeObject(economic.Offers[0]))!;
                    offer.Id = "long-offer-" + i; offer.Coin = longEconomy.Coins[i % 16].Key; return offer;
                }).ToList();
                longEconomy.WorldTribes[0].Coins = longEconomy.Coins.Select(c => c.Key).ToList();
                longEconomy.WorldRates = Enumerable.Range(0, 24).Select(i => new EconomyRateView
                { TribeId = "rate-tribe-" + i, Tribe = "汇率部落 " + i, ReferenceKey = "long-coin-0", CoinKey = "long-coin-1", Rate = (i + 1).ToString() }).ToList();
                longEconomy.Ledger = Enumerable.Range(0, 24).Select(i =>
                {
                    var member = JsonConvert.DeserializeObject<EconomyLedgerRow>(JsonConvert.SerializeObject(economic.Ledger[0]))!;
                    member.Uid = "long-member-" + i; member.Name = "测试成员 " + i; member.CoinKey = longEconomy.Coins[i % 16].Key; return member;
                }).ToList();
                window.UpdateEconomy(longEconomy);
                foreach (int tab in new[] { 1 })
                {
                    window.OnRenderGUI(0);
                    var body = window.SingleComposer.GetContainer("economy-content");
                    var bar = window.SingleComposer.GetScrollbar("scrollbar");
                    var viewport = body.Bounds.ParentBounds;
                    bar.CurrentYPosition = 0; bar.TriggerChanged();
                    check(body.Bounds.OuterHeight > viewport.InnerHeight && bar.Focusable,
                        $"Economy subpage {tab} must expose a scrollbar for a long list at {frameWidth} pixels.");
                    var images = body.Elements.Where(e => e.GetType().Name is "GuiElementEconomyItem" or "GuiElementEconomyCoin").ToArray();
                    bool InView(ElementBounds b) => b.renderY + b.OuterHeight + 2 > viewport.renderY
                        && b.renderY - 2 < viewport.renderY + viewport.OuterHeight;
                    if (tab == 1)
                    {
                        var hiddenGoods = images.Where(e => e.GetType().Name == "GuiElementEconomyItem" && !InView(e.Bounds)).ToArray();
                        check(hiddenGoods.Length > 0 && hiddenGoods.All(e =>
                            ((ItemSlot)e.GetType().GetField("slot", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(e)!).Empty),
                            "Offscreen goods must defer deserializing their native item attributes until they first become visible.");
                    }
                    void CheckVisibleRendering(string position)
                    {
                        renderedItemStacks.Clear(); textureDraws.Clear();
                        body.RenderInteractiveElements(0);
                        var expected = images.Where(e => InView(e.Bounds)).Select(e =>
                            (ItemSlot)e.GetType().GetField("slot", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(e)!).ToArray();
                        check(renderedItemStacks.Select(r => r.Slot).SequenceEqual(expected) && expected.Length < images.Length,
                            $"Only visible 3D previews may render on long economic page {tab} at its {position} ({frameWidth}): {renderedItemStacks.Count}/{images.Length}.");
                        check(textureDraws.All(d => d.Y + d.Height > viewport.renderY - 2 && d.Y < viewport.renderY + viewport.OuterHeight + 2),
                            $"Offscreen economic text and controls must not submit GPU draws on page {tab} at its {position}.");
                        int warmedUploads = nextTexture;
                        body.RenderInteractiveElements(0);
                        check(nextTexture == warmedUploads, "A stationary economic viewport must reuse its composed textures without uploading every frame.");
                        check(images.All(e => e.Bounds.fixedWidth == e.Bounds.fixedHeight && e.Bounds.fixedWidth <= (tab is 2 or 3 ? 64 : 48)),
                            "Long rows must never stretch item pictures to text or button height.");
                    }
                    CheckVisibleRendering("top");
                    mouseX = (int)(viewport.renderX + 8); mouseY = (int)(viewport.renderY + 8);
                    window.OnMouseWheel(new MouseWheelEventArgs { delta = -1, deltaPrecise = -1 });
                    check(bar.CurrentYPosition > 0, $"Economy subpage {tab} must support mouse-wheel scrolling.");
                    ClickAt(window, bar.Bounds.renderX + bar.Bounds.OuterWidth / 2, bar.Bounds.renderY + bar.Bounds.InnerHeight - 1);
                    check(Math.Abs(body.Bounds.renderY + body.Bounds.OuterHeight - viewport.renderY - viewport.InnerHeight) < 1,
                        $"Economy subpage {tab} must scroll all the way to its last row.");
                    check(body.Elements.All(e => e.Bounds.renderX + e.Bounds.OuterWidth <= viewport.renderX + viewport.InnerWidth + 1),
                        $"Long economy subpage {tab} must keep controls fully inside the viewport.");
                    CheckVisibleRendering("bottom");
                }
                window.UpdateEconomy(economic);
                window.OnRenderGUI(0);
                var stockStates = JsonConvert.DeserializeObject<EconomySnapshot>(JsonConvert.SerializeObject(economic))!;
                var missingGoods = JsonConvert.DeserializeObject<EconomyOfferView>(JsonConvert.SerializeObject(stockStates.Offers.First(o => !o.IsTask)))!;
                missingGoods.Id = "missing-stock"; missingGoods.Name = "缺货报价"; missingGoods.Available = 0; missingGoods.Ready = false;
                stockStates.Offers.Insert(0, missingGoods);
                window.UpdateEconomy(stockStates); window.OnRenderGUI(0);
                scenario = $"{frameWidth}-economy-stock-status"; Capture(window);
                var stockSlots = window.SingleComposer.GetContainer("economy-content").Elements
                    .Where(e => e.GetType().Name == "GuiElementEconomyItem").ToArray();
                var listingField = stockSlots[0].GetType().GetField("listed", BindingFlags.Instance | BindingFlags.NonPublic)!;
                check(stockSlots.Any(e => Equals(listingField.GetValue(e), false)) && stockSlots.Any(e => Equals(listingField.GetValue(e), true)),
                    "Seller quotes must render both unavailable red borders and available green borders with retained item previews.");
                window.UpdateEconomy(economic); window.OnRenderGUI(0);
                // Inventory snapshots can arrive after a player has already opened the goods picker.
                window.Close();
                check(economyRequests.Last().Action == "close", "Closing civilization must release its economy subscription.");
                int beforeReopen = economyRequests.Count;
                window.TryOpen(); window.OnRenderGUI(0);
                check(economyRequests.Count == beforeReopen + 1 && economyRequests.Last().Action == "open",
                    "Reopening civilization on its retained economy page must resubscribe and request fresh carried stock.");
                var pendingStock = JsonConvert.DeserializeObject<EconomySnapshot>(JsonConvert.SerializeObject(economic))!;
                pendingStock.Items.Clear();
                window.UpdateEconomy(pendingStock);
                Invoke(window, "Edit", null!, false); window.OnRenderGUI(0);
                var initialGoodsSlot = window.SingleComposer.GetContainer("economy-content").Elements
                    .First(e => e.GetType().Name == "GuiElementEconomyItem");
                ClickElement(window, initialGoodsSlot); window.OnRenderGUI(0);
                var pendingPicker = window.Composers["economy-picker"];
                var pendingGrid = pendingPicker.GetElement("economy-picker-grid");
                var pendingSearch = pendingPicker.GetTextInput("economy-picker-search");
                pendingSearch.SetValue("木");
                renderedItemStacks.Clear(); pendingGrid.RenderInteractiveElements(0);
                check(renderedItemStacks.Count == 0, "An initial goods picker must not invent stock before its inventory snapshot arrives.");
                window.UpdateEconomy(economic); window.OnRenderGUI(0);
                renderedItemStacks.Clear(); pendingGrid.RenderInteractiveElements(0);
                check(renderedItemStacks.Count > 0 && ReferenceEquals(pendingPicker, window.Composers["economy-picker"])
                    && pendingSearch.HasFocus && pendingSearch.GetText() == "木",
                    "A late inventory snapshot must populate an already-open goods picker without rebuilding it or losing search focus.");
                window.UpdateEconomy(pendingStock); window.OnRenderGUI(0);
                renderedItemStacks.Clear(); pendingGrid.RenderInteractiveElements(0);
                check(renderedItemStacks.Count == 0, "Removing carried goods must clear stale choices from an open goods picker.");
                window.UpdateEconomy(economic); window.OnRenderGUI(0);
                var pendingTitle = pendingPicker.Bounds;
                ClickAt(window, pendingTitle.renderX + pendingTitle.OuterWidth - 20 * RuntimeEnv.GUIScale,
                    pendingTitle.renderY + 15 * RuntimeEnv.GUIScale);
                window.OnRenderGUI(0);
                Invoke(window, "Edit", null!, false); window.OnRenderGUI(0);
                CheckCenteredMain();
                var settingsBody = window.SingleComposer.GetContainer("economy-content");
                GuiElement[] SettingsSlots() => window.SingleComposer.GetContainer("economy-content").Elements
                    .Where(e => e.GetType().Name == "GuiElementEconomyItem").ToArray();
                var settingsSlots = SettingsSlots();
                check(settingsSlots.Length == 3 && window.SingleComposer.GetElement("economy-tabs") == null,
                    "Settings must be embedded in civilization, with a goods slot, one payment slot and an add button.");
                check(window.SingleComposer.GetScrollbar("scrollbar").CurrentYPosition == 0,
                    "Opening settings must reset the previous offer-list scroll position.");
                var fixedSave = window.SingleComposer.GetElement("economy-footer-save");
                var fixedPermit = window.SingleComposer.GetElement("economy-footer-permit");
                var fixedCancel = window.SingleComposer.GetElement("economy-footer-cancel");
                check(fixedPermit is GuiElementSwitch && Math.Abs(fixedSave.Bounds.renderY + fixedSave.Bounds.OuterHeight / 2
                    - fixedPermit.Bounds.renderY - fixedPermit.Bounds.OuterHeight / 2) < 1 && fixedSave.Bounds.fixedY == fixedCancel.Bounds.fixedY
                    && settingsBody.InsideClipBounds!.renderY + settingsBody.InsideClipBounds.OuterHeight < fixedSave.Bounds.renderY,
                    "Permit, save and cancel must share one fixed footer below the editor viewport.");
                check(!settingsBody.Elements.OfType<GuiElementTextButton>().Any(b => b.Text == translations["ui.economy-select-item"]
                    || b.Text == translations["ui.economy-clear-slot"]), "The editor must replace the old choose/clear buttons with slot interactions.");
                var paymentCells = settingsBody.Elements.Where(e => e.GetType().Name is "GuiElementEconomyItem" or "GuiElementEconomyEmptySlot")
                    .Where(e => e.Bounds.fixedY == settingsSlots[1].Bounds.fixedY).ToArray();
                check(paymentCells.Length == 2 && !settingsBody.Elements.Any(e => e.GetType().Name == "GuiElementEconomyEmptySlot")
                    && paymentCells.Last().Bounds.fixedX + paymentCells.Last().Bounds.fixedWidth <= settingsBody.Bounds.fixedWidth + 1,
                    "A new payment grid must contain only one blank slot and one plus, without filler frames.");
                ClickElement(window, fixedPermit); window.OnRenderGUI(0);
                check(((GuiElementSwitch)fixedPermit).On && ReferenceEquals(settingsBody, window.SingleComposer.GetContainer("economy-content")),
                    "Permit checkbox must toggle without rebuilding or losing the trade draft.");
                scenario = $"{frameWidth}-economy-offer-settings"; Capture(window);
                void ScrollSettingsTo(GuiElement element)
                {
                    settingsBody = window.SingleComposer.GetContainer("economy-content");
                    var settingsBar = window.SingleComposer.GetScrollbar("scrollbar");
                    settingsBar.CurrentYPosition = (float)Math.Clamp(element.Bounds.fixedY * RuntimeEnv.GUIScale - 12, 0,
                        Math.Max(0, settingsBody.Bounds.OuterHeight - settingsBody.Bounds.ParentBounds.InnerHeight)); settingsBar.TriggerChanged();
                }
                ScrollSettingsTo(settingsSlots[1]); ClickElement(window, settingsSlots[1]); window.OnRenderGUI(0);
                var pickerComposer = window.Composers["economy-picker"];
                var pickerGrid = pickerComposer.GetElement("economy-picker-grid");
                check(pickerGrid != null && pickerComposer.GetTextInput("economy-picker-search") != null
                    && pickerComposer.GetElement("economy-picker-titlebar") is GuiElementDialogTitleBar
                    && pickerComposer.GetElement("economy-picker-close") == null,
                    "An empty payment slot must open an independent native window with its own titlebar and search, without the old inline close button.");
                CheckCenteredMain();
                var pickerSearch = pickerComposer.GetTextInput("economy-picker-search");
                check(pickerSearch.HasFocus && !window.SingleComposer.GetContainer("economy-content").Elements.Any(e => e.HasFocus),
                    "Opening the picker must focus only its search field, without leaving draft inputs focused underneath.");
                var pickerTyped = new KeyEvent { KeyChar = '木' };
                window.OnKeyPress(pickerTyped);
                check(pickerTyped.Handled && pickerSearch.GetText() == "木",
                    "Keyboard text input must reach the independent picker search field.");
                pickerSearch.SetValue("");
                check(frameWidth >= 780
                    ? pickerGrid!.Bounds.renderX > window.SingleComposer.Bounds.renderX + window.SingleComposer.Bounds.InnerWidth
                    : pickerGrid!.Bounds.renderX >= window.SingleComposer.Bounds.renderX,
                    "Picker must dock on the right on desktop and fit within civilization on a compact screen.");
                check(pickerGrid!.Bounds.renderX + pickerGrid.Bounds.OuterWidth < frameWidth
                    && pickerComposer.GetTextInput("economy-picker-search").Bounds.renderX >= 0,
                    "The independent picker must fit on screen without moving or resizing civilization.");
                scenario = $"{frameWidth}-economy-item-picker"; Capture(window);
                void PickFirst()
                {
                    var grid = window.Composers["economy-picker"].GetElement("economy-picker-grid");
                    ClickAt(window, grid.Bounds.renderX + 12 * RuntimeEnv.GUIScale, grid.Bounds.renderY + 12 * RuntimeEnv.GUIScale);
                    window.OnRenderGUI(0);
                }
                PickFirst();
                check(window.Composers["economy-picker"] == null && mouseInventory.Empty,
                    "Selecting a preview must fill the draft and close the picker without moving inventory items.");
                var oneQuantity = SettingsSlots()[1]; oneQuantity.RenderInteractiveElements(0);
                var countTexture = (LoadedTexture?)oneQuantity.GetType().GetField("quantityTexture", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(oneQuantity);
                check(countTexture != null && textures[countTexture.TextureId].Data.Where((_, i) => i % 4 == 3).Any(a => a > 0),
                    "A one-item payment must display its quantity in the slot, including when native stack rendering would hide 1.");
                check(textureDraws.Any(d => d.Id == countTexture!.TextureId && d.Depth > 100),
                    "Slot quantities must render above the item model.");
                settingsBody = window.SingleComposer.GetContainer("economy-content");
                var countInput = settingsBody.Elements.OfType<GuiElementTextInput>().Last();
                ScrollSettingsTo(countInput); countInput.SetValue("2");
                var quantityComposer = window.SingleComposer;
                window.OnRenderGUI(0);
                var selectedSlot = (ItemSlot)oneQuantity.GetType().GetProperty("DisplaySlot", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(oneQuantity)!;
                check(ReferenceEquals(quantityComposer, window.SingleComposer) && selectedSlot.StackSize == 2,
                    "Adjusting a payment quantity must update its slot without rebuilding or losing input focus.");
                settingsSlots = SettingsSlots(); ScrollSettingsTo(settingsSlots.Last()); ClickElement(window, settingsSlots.Last()); window.OnRenderGUI(0);
                PickFirst();
                CheckCenteredMain();
                check(SettingsSlots().Length == 4, "The add button must create exactly one new payment slot.");
                settingsBody = window.SingleComposer.GetContainer("economy-content");
                countInput = settingsBody.Elements.OfType<GuiElementTextInput>().Last(); ScrollSettingsTo(countInput); countInput.SetValue("5");
                settingsSlots = SettingsSlots(); ScrollSettingsTo(settingsSlots[1]); ClickElement(window, settingsSlots[1]); window.OnRenderGUI(0);
                settingsBody = window.SingleComposer.GetContainer("economy-content");
                check(settingsBody.Elements.OfType<GuiElementTextInput>().Last().GetText() == "2",
                    "Clicking a filled payment slot must select its saved quantity for adjustment.");
                var goodsSave = window.SingleComposer.GetElement("economy-footer-save");
                ClickElement(window, goodsSave); window.OnRenderGUI(0);
                check(economyRequests.Last() is { Action: "offer-save", IsTask: false, Enabled: true } && economyRequests.Last().Exchange.Select(e => e.Quantity).SequenceEqual(new[] { 2, 5 })
                    && mouseInventory.Empty, "Dynamic payment slots must save independent quantities without moving real items.");
                Invoke(window, "Edit", null!, true); window.OnRenderGUI(0);
                settingsBody = window.SingleComposer.GetContainer("economy-content");
                check(settingsBody.Elements.Where(e => e is GuiElementTextInput or GuiElementTextArea)
                    .All(e => e.Bounds.renderX + e.Bounds.OuterWidth <= settingsBody.InsideClipBounds!.renderX + settingsBody.InsideClipBounds.OuterWidth + 1),
                    "Embedded settings inputs must fit the clipped viewport.");
                var taskInput = settingsBody.Elements.OfType<GuiElementTextInput>().First();
                ClickElement(window, taskInput); window.UpdateEconomy(economic); window.OnRenderGUI(0);
                check(ReferenceEquals(settingsBody, window.SingleComposer.GetContainer("economy-content")) && taskInput.HasFocus,
                    "Unchanged snapshots must preserve task settings focus and draft text.");
                var taskBody = settingsBody.Elements.OfType<GuiElementTextArea>().Single();
                const string taskTitle = "修建公共道路";
                string taskDetails = string.Concat(Enumerable.Repeat("修建三格宽的石材道路，连接广场和农田。\n", 10));
                taskInput.SetValue(taskTitle); taskBody.SetValue(taskDetails);
                check(taskBody.GetText() == taskDetails && taskBody.Bounds.fixedHeight == 116,
                    "Task details must preserve multiline Chinese inside a bounded editor.");
                ScrollSettingsTo(taskBody); ClickElement(window, taskBody);
                mouseX = (int)(taskBody.Bounds.renderX + 8); mouseY = (int)(taskBody.Bounds.renderY + 8);
                var settingsBar = window.SingleComposer.GetScrollbar("scrollbar"); float taskScroll = settingsBar.CurrentYPosition;
                window.OnMouseWheel(new MouseWheelEventArgs { delta = -1, deltaPrecise = -1 });
                check(settingsBar.CurrentYPosition == taskScroll, "Task content wheel must not move the surrounding settings page.");
                settingsSlots = SettingsSlots();
                check(settingsSlots.Length == 2, "A new task reward grid must contain only one blank slot and one plus.");
                ScrollSettingsTo(settingsSlots[0]); ClickElement(window, settingsSlots[0]); window.OnRenderGUI(0);
                PickFirst();
                var taskPermit = window.SingleComposer.GetElement("economy-footer-permit");
                check(taskPermit is GuiElementSwitch, "Task editor must also use the fixed permit checkbox.");
                ClickElement(window, taskPermit); window.OnRenderGUI(0);
                scenario = $"{frameWidth}-economy-task-settings"; Capture(window);
                settingsBody = window.SingleComposer.GetContainer("economy-content");
                var saveButton = window.SingleComposer.GetElement("economy-footer-save");
                ClickElement(window, saveButton); window.OnRenderGUI(0);
                check(economyRequests.Last() is { Action: "offer-save", IsTask: true, Text: taskTitle, Enabled: true } && economyRequests.Last().Content == taskDetails && economyRequests.Last().Exchange.Count == 1,
                    "Publishing a task must send title, full details and item rewards separately.");
                Invoke(window, "Edit", null!, false); window.OnRenderGUI(0);
                settingsSlots = SettingsSlots();
                ClickAt(window, settingsSlots[0].Bounds.renderX + settingsSlots[0].Bounds.InnerWidth - GuiElement.scaled(9),
                    settingsSlots[0].Bounds.renderY + GuiElement.scaled(9)); window.OnRenderGUI(0);
                check(window.Composers["economy-picker"] == null && !(bool)SettingsSlots()[0].GetType()
                    .GetProperty("CanClear", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(SettingsSlots()[0])!,
                    "The goods slot red X must clear the draft without opening the picker.");
                settingsSlots = SettingsSlots(); ScrollSettingsTo(settingsSlots[1]); ClickElement(window, settingsSlots[1]); window.OnRenderGUI(0); PickFirst();
                settingsSlots = SettingsSlots(); ScrollSettingsTo(settingsSlots[1]);
                ClickAt(window, settingsSlots[1].Bounds.renderX + settingsSlots[1].Bounds.InnerWidth - GuiElement.scaled(9),
                    settingsSlots[1].Bounds.renderY + GuiElement.scaled(9)); window.OnRenderGUI(0);
                check(window.Composers["economy-picker"] == null && !(bool)SettingsSlots()[1].GetType()
                    .GetProperty("CanClear", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(SettingsSlots()[1])!,
                    "The payment red X must empty the last slot without selecting another item or moving real inventory.");
                foreach (bool taskEditor in new[] { false, true })
                {
                    var fullDraft = new EconomyOfferView { Id = "boundary", IsTask = taskEditor, Name = "24 slot boundary", Content = "Reward boundary check",
                        ItemKey = goodsPreview.Key, DisplayItem = goodsPreview, Quantity = 1, Enabled = true,
                        Exchange = Enumerable.Range(0, 23).Select(_ => new EconomyExchangeItem { Key = goodsPreview.Key, Quantity = 1, Display = goodsPreview }).ToList() };
                    Invoke(window, "Edit", fullDraft, taskEditor); window.OnRenderGUI(0);
                    int goodsCells = taskEditor ? 0 : 1;
                    settingsSlots = SettingsSlots();
                    check(settingsSlots.Length == goodsCells + 24, "23 payment/reward slots must retain one plus without padding the row.");
                    ScrollSettingsTo(settingsSlots.Last()); ClickElement(window, settingsSlots.Last()); window.OnRenderGUI(0); PickFirst();
                    settingsSlots = SettingsSlots();
                    check(settingsSlots.Length == goodsCells + 24 && settingsSlots.Skip(goodsCells).All(e =>
                        (bool)e.GetType().GetProperty("CanClear", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(e)!),
                        "Adding the 24th payment/reward must replace the plus with a filled slot and leave no 25th slot.");
                    ScrollSettingsTo(settingsSlots.Last());
                    ClickAt(window, settingsSlots.Last().Bounds.renderX + settingsSlots.Last().Bounds.InnerWidth - GuiElement.scaled(9),
                        settingsSlots.Last().Bounds.renderY + GuiElement.scaled(9)); window.OnRenderGUI(0);
                    settingsSlots = SettingsSlots();
                    check(settingsSlots.Length == goodsCells + 24 && !(bool)settingsSlots.Last().GetType()
                        .GetProperty("CanClear", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(settingsSlots.Last())!,
                        "Removing a full-grid payment/reward must restore the plus button.");
                    ScrollSettingsTo(settingsSlots.Last()); ClickElement(window, settingsSlots.Last()); window.OnRenderGUI(0); PickFirst();
                    scenario = $"{frameWidth}-economy-24-{(taskEditor ? "rewards" : "payments")}"; Capture(window);
                    ClickElement(window, window.SingleComposer.GetElement("economy-footer-save")); window.OnRenderGUI(0);
                    check(economyRequests.Last() is { Action: "offer-save", Exchange.Count: 24 } && economyRequests.Last().IsTask == taskEditor,
                        "Goods and task editors must both save all 24 configured payment/reward entries.");
                }
                // A large cached catalog must remain a virtual grid, with stable search and scroll.
                using (var catalog = new EconomyClientCatalog(api))
                {
                    catalog.Items.AddRange(Enumerable.Range(0, 2000).Select(i => new EconomyItemView
                        { Key = "catalog-" + i, Name = "Cached item " + i, Code = goodsPreview.Code, Stack = goodsPreview.Stack }));
                    window.SetEconomyCatalog(catalog); Invoke(window, "Edit", null!, true); window.OnRenderGUI(0);
                    settingsSlots = SettingsSlots(); ScrollSettingsTo(settingsSlots[0]); ClickElement(window, settingsSlots[0]); window.OnRenderGUI(0);
                    pickerGrid = window.Composers["economy-picker"].GetElement("economy-picker-grid");
                    renderedItemStacks.Clear(); pickerGrid.RenderInteractiveElements(0);
                    check(renderedItemStacks.Count > 0 && renderedItemStacks.Count < 100,
                        "A 2000-item cached picker may deserialize and render only its visible rows.");
                    var searchInput = window.Composers["economy-picker"].GetTextInput("economy-picker-search"); ClickElement(window, searchInput);
                    var stablePicker = window.Composers["economy-picker"]; searchInput.SetValue("Cached item 1999"); window.OnRenderGUI(0);
                    check(ReferenceEquals(stablePicker, window.Composers["economy-picker"]) && searchInput.HasFocus,
                        "Searching the cached catalog must update only the item grid and retain input focus.");
                    renderedItemStacks.Clear(); pickerGrid.RenderInteractiveElements(0);
                    check(renderedItemStacks.Count == 1, "The native item grid must filter cached names without requesting server data.");
                    searchInput.SetValue("");
                    mouseX = (int)(pickerGrid.Bounds.renderX + 8); mouseY = (int)(pickerGrid.Bounds.renderY + 8);
                    window.OnMouseWheel(new MouseWheelEventArgs { delta = -1, deltaPrecise = -1 });
                    check(window.Composers["economy-picker"].GetScrollbar("economy-picker-scrollbar").CurrentYPosition > 0,
                        "The docked cached picker must support independent mouse-wheel scrolling.");
                    var pickerTitle = window.Composers["economy-picker"].Bounds;
                    ClickAt(window, pickerTitle.renderX + pickerTitle.OuterWidth - 20 * RuntimeEnv.GUIScale, pickerTitle.renderY + 15 * RuntimeEnv.GUIScale);
                    window.OnRenderGUI(0);
                    check(window.Composers["economy-picker"] == null && window.IsVisible,
                        "Closing the picker's native titlebar must return to the draft without closing civilization.");
                    CheckCenteredMain();
                    Invoke(window, "ChangePage", 0); window.OnRenderGUI(0); window.SetEconomyCatalog(null);
                }
                Invoke(window, "ChangePage", 0); window.OnRenderGUI(0);
                check(window.SingleComposer.GetElement("economy-content") == null && economyRequests.Last().Action == "close",
                    "Leaving the economy page must rebuild the native page and unsubscribe.");
                using (var trade = new EconomyTradeWindow(api, economyRequests.Add))
                {
                    var materialPrice = new EconomyExchangeItem { Key = "material-price", Quantity = 3, Display = goodsPreview };
                    var goodsA = new EconomyOfferView { Id = "offer-a", Name = "木柴", Ready = true, Quantity = 4, Revision = 7,
                        DisplayItem = goodsPreview, Exchange = [new() { Coin = "copper", Quantity = 2 }] };
                    var goodsB = new EconomyOfferView { Id = "offer-b", Name = "第二件商品", Ready = true, Quantity = 1, Revision = 8,
                        DisplayItem = goodsPreview, Exchange = [new() { Coin = "copper", Quantity = 4 }, materialPrice] };
                    var goodsC = new EconomyOfferView { Id = "offer-c", Name = "第三件商品", Ready = true, Quantity = 2, Revision = 9,
                        DisplayItem = goodsPreview, Exchange = [materialPrice] };
                    var paymentStock = new Dictionary<string, int> { ["copper"] = 5, [materialPrice.Key] = 4 };
                    trade.Update(new() { Id = "session", Partner = "成员", Offers = [goodsA, goodsB, goodsC], Coins = economic.Coins, PaymentStock = paymentStock });
                    check(trade.IsOpened() && trade.SingleComposer.Composed, "The native trade dialog must open.");
                    var tradeBody = trade.SingleComposer.GetContainer("economy-content");
                    void CheckTradeFooter()
                    {
                        var body = trade.SingleComposer.GetContainer("economy-content");
                        var footer = new[] { "buy", "confirm", "cancel" }
                            .Select(key => trade.SingleComposer.GetElement("trade-action-" + key)).OfType<GuiElementTextButton>().ToArray();
                        var clip = body.InsideClipBounds!;
                        check(footer.Length > 0 && footer.Select(b => b.Bounds.fixedY).Distinct().Count() == 1
                            && footer.All(b => b.Bounds.renderY > clip.renderY + clip.InnerHeight
                                && b.Bounds.renderX >= trade.SingleComposer.Bounds.renderX + GuiElement.scaled(19)
                                && b.Bounds.renderX + b.Bounds.OuterWidth <= trade.SingleComposer.Bounds.renderX + trade.SingleComposer.Bounds.InnerWidth - GuiElement.scaled(19)
                                && b.Bounds.renderY + b.Bounds.OuterHeight < trade.SingleComposer.Bounds.renderY + trade.SingleComposer.Bounds.InnerHeight),
                            "Trade confirmation and close must fit one fixed footer row below the scrolling content.");
                    }
                    void ScrollTradeTo(GuiElement element)
                    {
                        var body = trade.SingleComposer.GetContainer("economy-content");
                        var bar = trade.SingleComposer.GetScrollbar("scrollbar");
                        bar.CurrentYPosition = (float)Math.Clamp(element.Bounds.fixedY * RuntimeEnv.GUIScale - 12, 0,
                            Math.Max(0, body.Bounds.OuterHeight - body.Bounds.ParentBounds.InnerHeight)); bar.TriggerChanged();
                    }
                    GuiElement[] TradeSlots(bool left, bool filled = true)
                    {
                        var body = trade.SingleComposer.GetContainer("economy-content");
                        double split = body.Bounds.fixedWidth / 2;
                        return body.Elements.Where(e => e.GetType().Name is "GuiElementEconomyItem" or "GuiElementEconomyCoin")
                            .Where(e => (e.Bounds.fixedX < split) == left && (!filled || e.Focusable)).ToArray();
                    }
                    object? SlotFlag(GuiElement element, string field) => (element.GetType().Name == "GuiElementEconomyCoin"
                        ? element.GetType().BaseType! : element.GetType()).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(element);
                    int SlotCount(GuiElement element) => (int)SlotFlag(element, "count")!;
                    void TradeClick(GuiElement element)
                    {
                        var clip = trade.SingleComposer.GetContainer("economy-content").InsideClipBounds!;
                        if (element.Bounds.renderY < clip.renderY || element.Bounds.renderY + element.Bounds.OuterHeight > clip.renderY + clip.InnerHeight)
                            ScrollTradeTo(element);
                        ClickElement(trade, element); trade.OnRenderGUI(0);
                    }
                    void SelectTradeTab(int index)
                    {
                        var tabs = trade.SingleComposer.GetHorizontalTabs("trade-tabs");
                        var widths = (int[])typeof(GuiElementHorizontalTabs).GetField("tabWidths", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tabs)!;
                        double x = GuiElement.scaled(tabs.unscaledTabSpacing * (index + 1)) + widths.Take(index).Sum() + widths[index] / 2;
                        ClickAt(trade, tabs.Bounds.renderX + x, tabs.Bounds.renderY + tabs.Bounds.InnerHeight / 2);
                        trade.OnRenderGUI(0);
                        check(trade.SingleComposer.GetHorizontalTabs("trade-tabs").activeElement == index,
                            "Clicking the native top tab must select and retain the requested page.");
                    }
                    CheckTradeFooter();
                    check(TradeSlots(true).Length == 3 && TradeSlots(false).Length == 2
                        && TradeSlots(true, false).Length >= 3 && TradeSlots(true, false).Length == TradeSlots(false, false).Length
                        && !trade.SingleComposer.GetButton("trade-action-buy").Enabled,
                        "Browsing must show all goods and grouped payment items with matching padded grids and require selection.");
                    check(TradeSlots(false).All(e => SlotFlag(e, "listed") is false) && TradeSlots(false).Select(SlotCount).SequenceEqual(new[] { 6, 6 }),
                        "The complete catalog must show cumulative requirements and red borders for insufficient inventory.");
                    var tradeTabs = trade.SingleComposer.GetHorizontalTabs("trade-tabs");
                    check(tradeTabs.tabs.Select(t => t.Name).SequenceEqual(new[] { translations["ui.economy-goods-trade"], translations["ui.economy-task-trade"] })
                        && tradeTabs.Bounds.renderY >= 0 && tradeTabs.Bounds.renderY + tradeTabs.Bounds.OuterHeight <= trade.SingleComposer.Bounds.renderY + 1
                        && tradeTabs.Bounds.renderX >= 0 && tradeTabs.Bounds.renderX + tradeTabs.Bounds.OuterWidth <= frameWidth + 1
                        && Math.Abs(trade.SingleComposer.Bounds.renderX + trade.SingleComposer.Bounds.OuterWidth / 2 - frameWidth / 2) < 1,
                        "Goods and tasks must use native tabs attached above a centered screen-fitting dialog.");
                    scenario = $"{frameWidth}-economy-trade"; Capture(trade);
                    var clipBounds = tradeBody.InsideClipBounds!;
                    var lastSlot = TradeSlots(true, false).Last().Bounds;
                    var arrowBounds = trade.SingleComposer.GetElement("trade-arrow").Bounds;
                    double gridTop = TradeSlots(true, false)[0].Bounds.renderY;
                    check(lastSlot.renderY + lastSlot.OuterHeight <= clipBounds.renderY + clipBounds.InnerHeight
                        && Math.Abs(lastSlot.renderY + lastSlot.OuterHeight - clipBounds.renderY - clipBounds.InnerHeight) <= GuiElement.scaled(2)
                        && tradeBody.Elements.OfType<GuiElementWrappedText>().Count() == 2
                        && Math.Abs(arrowBounds.renderX + arrowBounds.InnerWidth / 2 - tradeBody.Bounds.renderX - tradeBody.Bounds.InnerWidth / 2) < 1
                        && Math.Abs(arrowBounds.renderY + arrowBounds.InnerHeight / 2 - (gridTop + lastSlot.renderY + lastSlot.OuterHeight) / 2) < 1,
                        "The grids must fill the visible height with whole slots, retain only their two headings, and center the arrow between them.");
                    TradeClick(TradeSlots(true)[0]);
                    check(TradeSlots(false).Length == 1 && SlotCount(TradeSlots(false)[0]) == 2
                        && SlotFlag(TradeSlots(false)[0], "listed") is true && trade.SingleComposer.GetButton("trade-action-buy").Enabled,
                        "Selecting goods must show its affordable payment with a green border.");
                    TradeClick(TradeSlots(true)[1]);
                    check(TradeSlots(true).Count(e => SlotFlag(e, "selected") is true) == 2
                        && TradeSlots(false).Select(SlotCount).SequenceEqual(new[] { 6, 3 })
                        && SlotFlag(TradeSlots(false)[0], "listed") is false && SlotFlag(TradeSlots(false)[1], "listed") is true
                        && !trade.SingleComposer.GetButton("trade-action-buy").Enabled,
                        "Multi-selection must sum duplicate payments and compare total stock, keeping independent red/green borders.");
                    scenario = $"{frameWidth}-economy-trade-multi-shortage"; Capture(trade);
                    TradeClick(TradeSlots(true)[0]); TradeClick(TradeSlots(true)[1]);
                    check(TradeSlots(false).Length == 2 && TradeSlots(false).All(e => SlotCount(e) == 6),
                        "Deselecting all goods must restore the full payment catalog.");
                    TradeClick(TradeSlots(false)[1]);
                    check(TradeSlots(true).Length == 2, "Clicking a payment item must filter goods that require it.");
                    TradeClick(TradeSlots(false)[1]);
                    check(TradeSlots(true).Length == 3, "Clicking the same payment again must restore all goods.");
                    TradeClick(TradeSlots(true)[0]);
                    ClickElement(trade, trade.SingleComposer.GetButton("trade-action-buy"));
                    check(economyRequests.Last() is { Action: "buy", Revision: 0 } && economyRequests.Last().Selections.Single() is { Id: "offer-a", Revision: 7 },
                        "Buy must carry selected quote IDs and their current revisions.");
                    var taskOfferView = new EconomyOfferView { Id = "task-offer", Name = taskTitle, Content = taskDetails,
                        IsTask = true, Ready = true, Enabled = true, Exchange = [new() { Quantity = 2, Display = goodsPreview }] };
                    var taskContractView = new EconomyOfferView { Id = "task-contract", Name = "已接取的道路任务", Content = "按约定铺设三格宽道路。",
                        IsTask = true, Contract = true, Ready = true, Exchange = taskOfferView.Exchange };
                    var tradeOffers = new[] { goodsA, goodsB, goodsC, taskOfferView, taskContractView }.ToList();
                    trade.Update(new() { Id = "session", Partner = "成员", Offers = tradeOffers, Coins = economic.Coins, PaymentStock = paymentStock });
                    trade.OnRenderGUI(0); SelectTradeTab(1);
                    tradeBody = trade.SingleComposer.GetContainer("economy-content"); CheckTradeFooter();
                    var taskButtons = tradeBody.Elements.OfType<GuiElementTextButton>().ToArray();
                    check(taskButtons.Select(b => b.Text).SequenceEqual(new[] { translations["ui.economy-task-accept-short"],
                        translations["ui.economy-task-finish-short"], translations["ui.economy-task-abandon-short"] })
                        && taskButtons.All(b => b.Bounds.renderX > tradeBody.Bounds.renderX + tradeBody.Bounds.InnerWidth / 2
                            && b.Bounds.renderX + b.Bounds.OuterWidth <= tradeBody.Bounds.renderX + tradeBody.Bounds.InnerWidth + 1),
                        "Each task row must place its accept or complete/abandon buttons at the right side. "
                        + string.Join("; ", taskButtons.Select(b => $"{b.Text}: x={b.Bounds.fixedX}, w={b.Bounds.OuterWidth}, content={tradeBody.Bounds.fixedWidth}")));
                    TradeClick(taskButtons[0]);
                    check(economyRequests.Last() is { Action: "propose", Item: "task-offer", IsTask: false }, "Task row acceptance must target that task.");
                    TradeClick(taskButtons[2]);
                    check(economyRequests.Last() is { Action: "propose", Item: "task-contract", IsTask: true, Text: "cancel-task" },
                        "Abandon must preserve bilateral cancellation rather than complete the task.");
                    TradeClick(taskButtons[1]);
                    check(economyRequests.Last() is { Action: "propose", Item: "task-contract", IsTask: true, Text: "" },
                        "Completion must preserve the task confirmation protocol.");
                    scenario = $"{frameWidth}-economy-trade-contract"; Capture(trade);
                    SelectTradeTab(0);
                    var manyOffers = Enumerable.Range(0, 80).Select(i => new EconomyOfferView
                    { Id = "grid-offer-" + i, Name = "木柴 " + i, DisplayItem = goodsPreview, Ready = true, Quantity = 4, Revision = i + 1,
                        Exchange = Enumerable.Range(0, 18).Select(n => new EconomyExchangeItem
                        { Key = n == 0 ? "" : "payment-" + n, Display = goodsPreview, Coin = n == 0 ? "copper" : "", Quantity = n + 1 }).ToList() }).ToList();
                    var plentiful = Enumerable.Range(1, 17).ToDictionary(n => "payment-" + n, _ => 100000);
                    plentiful["copper"] = 100000;
                    trade.Update(new() { Id = "session", Partner = "成员", Revision = 2, Offers = manyOffers, Coins = economic.Coins, PaymentStock = plentiful }); trade.OnRenderGUI(0);
                    TradeClick(TradeSlots(true)[0]); TradeClick(TradeSlots(true)[1]);
                    tradeBody = trade.SingleComposer.GetContainer("economy-content");
                    var gridSlots = TradeSlots(true, false).Concat(TradeSlots(false, false)).ToArray();
                    check(TradeSlots(true).Length == 80 && TradeSlots(false).Length == 18 && gridSlots.Length >= 160
                        && gridSlots.All(e => e.Bounds.fixedWidth == e.Bounds.fixedHeight && e.Bounds.fixedWidth <= 48)
                        && gridSlots.All(e => e.Bounds.fixedX >= 0 && e.Bounds.fixedX + e.Bounds.fixedWidth <= tradeBody.Bounds.fixedWidth + .01)
                        && Math.Abs(gridSlots.Max(e => e.Bounds.fixedX + e.Bounds.fixedWidth) - tradeBody.Bounds.fixedWidth) < .01,
                        "Responsive padded inventory rows must fill both halves across the full width, including long catalogs.");
                    scenario = $"{frameWidth}-economy-trade-grid"; Capture(trade);
                    double tradeFooterY = trade.SingleComposer.GetButton("trade-action-buy").Bounds.renderY;
                    ScrollTradeTo(TradeSlots(true, false).Last()); CheckTradeFooter();
                    check(trade.SingleComposer.GetScrollbar("scrollbar").CurrentYPosition > 0
                        && trade.SingleComposer.GetButton("trade-action-buy").Bounds.renderY == tradeFooterY,
                        "Scrolling a large grid must keep its confirmation action fixed.");
                    scenario = $"{frameWidth}-economy-trade-grid-scrolled"; Capture(trade);
                    check(Math.Abs(trade.SingleComposer.GetElement("trade-arrow").Bounds.renderY - arrowBounds.renderY) < 1,
                        "Scrolling goods must leave the arrow centered in the visible viewport.");
                    ClickElement(trade, trade.SingleComposer.GetButton("trade-action-buy"));
                    check(economyRequests.Last() is { Action: "buy", Revision: 2 } && economyRequests.Last().Selections.Select(o => o.Id)
                        .SequenceEqual(new[] { "grid-offer-0", "grid-offer-1" }),
                        "Multi-select confirmation must send every selected quote after scrolling.");
                    trade.Update(new() { Id = "session", Partner = "成员", Proposed = true, TaskProposal = true, Terms = "任务酬劳：三枚铜币。双方确认后接取。", Revision = 1 }); trade.OnRenderGUI(0);
                    scenario = $"{frameWidth}-economy-confirm"; Capture(trade);
                    CheckTradeFooter(); ClickElement(trade, trade.SingleComposer.GetButton("trade-action-confirm"));
                    check(economyRequests.Last() is { Action: "confirm", Id: "session", Revision: 1 },
                        "The fixed task confirmation button must preserve the server's bilateral confirmation request.");
                    trade.Update(new() { Id = "session", Partner = "成员", Proposed = true, TaskProposal = true, Terms = "等待对方确认任务。", YouConfirmed = true, Revision = 2 }); trade.OnRenderGUI(0);
                    check(!trade.SingleComposer.GetButton("trade-action-confirm").Enabled,
                        "A task already confirmed by the player must disable duplicate confirmations in its footer.");
                    trade.Update(new() { Id = "session", Closed = true });
                    check(!trade.IsOpened() && economyRequests.Last().Action == "confirm", "Server completion must close the dialog without sending a redundant cancel.");
                }
                var bounds = window.SingleComposer.Bounds;
                int closeRequests = requests.Count(r => r.Action == "close");
                ClickAt(window, bounds.renderX + bounds.OuterWidth - 20 * RuntimeEnv.GUIScale, bounds.renderY + 15 * RuntimeEnv.GUIScale);
                check(!window.IsVisible, "Native dialog did not close.");
                check(requests.Count(r => r.Action == "close") == closeRequests + 1,
                    "A real window close must request closing the civilization view exactly once.");
            }
            check(textures.Count == 0, "Closing all native dialogs must release announcement and editor textures. "
                + "Remaining texture IDs: " + string.Join(", ", textures.Keys));
            check(errors.Count == 0, "Native composition logged errors: " + string.Join("; ", errors));
        }
        finally
        {
            foreach (var texture in textures.Values) texture.Dispose();
            RuntimeEnv.GUIScale = oldScale;
            GuiStyle.DialogStrongBgColor[3] = oldBackgroundAlpha;
            GuiStyle.StandardFontName = oldFont; GuiStyle.DecorativeFontName = oldDecorativeFont;
            Lang.AvailableLanguages["en"] = oldLanguage; Lang.ChangeLanguage(oldLocale);
        }
    }

    private static void Invoke(object target, string method, params object[] args) => target.GetType()
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

}
