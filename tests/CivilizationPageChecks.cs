using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using SeraphCivilizationSimulation;

internal static class CivilizationPageChecks
{
    private sealed class PaperItem : Item
    {
        public override string GetHeldItemName(ItemStack stack) => "Paper map";
        public void Bind(ICoreAPI value) => api = value;
    }
    public static void Run(Action<bool, string> check)
    {
        object? Invoke(object target, string name, params object?[] args) => target.GetType()
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
        void Set(object target, string name, object value) => target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
        var errors = new List<string>();
        var logger = Proxy.Make<ILogger>((m, a) => { if (m.Name == "Error") errors.Add((string)a![0]!); return null; });
        var saved = new Dictionary<string, byte[]>();
        var snapshots = new Dictionary<string, CivilizationSnapshotPacket>();
        string contentDataPath = Path.Combine(Path.GetTempPath(), "vscncs-pages-content-" + Guid.NewGuid().ToString("N"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { if (Directory.Exists(contentDataPath)) Directory.Delete(contentDataPath, recursive: true); };
        string announcementsPath = new ModDataContentStore(contentDataPath, "civilization-page-check-save").AnnouncementsPath;
        bool failBroadcast = false;
        var save = Proxy.Make<ISaveGame>((m, a) =>
        {
            if (m.Name == "GetData") return saved.GetValueOrDefault((string)a![0]!);
            if (m.Name == "StoreData")
            {
                saved[(string)a![0]!] = (byte[])a[1]!;
            }
            return null;
        });
        var worldManager = Proxy.Make<IWorldManagerAPI>((m, a) => m.Name == "get_SaveGame" ? save : null);
        var item = new PaperItem { Code = new AssetLocation("immersivemaps:map-papyrus"), ItemId = 101, MaxStackSize = 1 };
        var network = Proxy.Make<IInventoryNetworkUtil>((m, a) => null);
        var registry = Proxy.Make<IClassRegistryAPI>((m, a) => m.Name == "CreateInvNetworkUtil" ? network : null);
        var loader = Proxy.Make<IModLoader>((m, a) => m.Name == "IsModEnabled" ? false : null);
        CivilizationMapInventory? inventory = null;
        var inventories = new Dictionary<string, IInventory>();
        var inventoryManager = Proxy.Make<IPlayerInventoryManager>((m, a) => m.Name switch
        {
            "GetOwnInventory" => (string)a![0]! == CivilizationMapInventory.InventoryClass ? inventory
                : inventories.Values.FirstOrDefault(i => i.ClassName == (string)a[0]!),
            "get_Inventories" => new Dictionary<string, IInventory>(inventories),
            "OpenInventory" => Register((IInventory)a![0]!), _ => null
        });
        object? Register(IInventory added) { inventories[added.InventoryID] = added; return null; }
        IServerPlayer Player(string uid, bool admin)
        {
            var entity = new EntityPlayer(); entity.Stats = new EntityStats(entity);
            var worldData = Proxy.Make<IWorldPlayerData>((m, a) => m.Name == "get_PlayerUID" ? uid : null);
            return Proxy.Make<IServerPlayer>((m, a) => m.Name switch
            {
                "get_PlayerUID" or "get_PlayerName" => uid, "get_LanguageCode" => "en", "HasPrivilege" => admin,
                "get_Entity" => entity, "get_InventoryManager" => inventoryManager, "get_WorldData" => worldData, _ => null
            });
        }
        var administrator = Player("admin", true); var ordinary = Player("ordinary", false);
        var world = Proxy.Make<IServerWorldAccessor>((m, a) => m.Name switch
        {
            "get_AllOnlinePlayers" => new IPlayer[] { administrator, ordinary },
            "get_SavegameIdentifier" => "civilization-page-check-save",
            "GetItem" => item, "PlayerByUid" => administrator, "get_Logger" => logger, _ => null
        });
        var api = Proxy.Make<ICoreServerAPI>((m, a) => m.Name switch
        {
            "get_World" => world, "get_WorldManager" => worldManager, "get_Logger" => logger,
            "get_ClassRegistry" => registry, "get_ModLoader" => loader,
            "GetOrCreateDataPath" => contentDataPath, _ => null
        });
        item.Bind(api);
        var channel = Proxy.Make<IServerNetworkChannel>((m, a) =>
        {
            if (m.Name == "SendPacket" && a?[0] is CivilizationSnapshotPacket packet && a[1] is IEnumerable<IServerPlayer> recipients)
                foreach (var recipient in recipients)
                {
                    if (failBroadcast && recipient.PlayerUID == "ordinary") throw new IOException("simulated recipient failure");
                    snapshots[recipient.PlayerUID] = packet;
                }
            return null;
        });
        VscnCivilizationModSystem Server()
        {
            var server = new VscnCivilizationModSystem(); Set(server, "sapi", api); Set(server, "channel", channel); return server;
        }
        var system = Server();
        Invoke(system, "LoadState");
        var initialAnnouncements = File.ReadAllBytes(announcementsPath);
        void WithFailedAnnouncementStorage(byte[] original, Action attempt)
        {
            File.Delete(announcementsPath);
            Directory.CreateDirectory(announcementsPath);
            try { attempt(); }
            finally
            {
                Directory.Delete(announcementsPath, recursive: true);
                File.WriteAllBytes(announcementsPath, original);
            }
        }
        void Request(IServerPlayer player, string html, long revision = 0, string id = "notice-save") => Invoke(system, "OnCivilizationRequest", player,
            new CivilizationRequestPacket { Action = "announcement-save", Html = html, Revision = revision, RequestId = id });
        Request(ordinary, "forbidden");
        check(File.ReadAllBytes(announcementsPath).SequenceEqual(initialAnnouncements)
            && !snapshots["ordinary"].CanEditAnnouncement && !snapshots["ordinary"].AnnouncementSaved,
            "A non-administrator must not write announcements, even with a crafted packet.");
        Request(administrator, "<h1>文明</h1><p>欢迎 &amp; 生存</p>");
        check(snapshots["admin"].AnnouncementSaved && snapshots["admin"].AnnouncementSaveRequestId == "notice-save"
            && snapshots["ordinary"].AnnouncementRevision == 1 && snapshots["ordinary"].AnnouncementHtml.Contains("欢迎"),
            $"Saving must acknowledge the request and synchronize the HTML and revision to all players (saved={snapshots["admin"].AnnouncementSaved}, request={snapshots["admin"].AnnouncementSaveRequestId}, revision={snapshots["ordinary"].AnnouncementRevision}, html={snapshots["ordinary"].AnnouncementHtml}, message={snapshots["admin"].Message}, errors={errors.Count}).");
        var persisted = File.ReadAllBytes(announcementsPath);
        Request(administrator, "stale", 0);
        check(!snapshots["admin"].AnnouncementSaved && snapshots["admin"].AnnouncementRevision == 1
            && File.ReadAllBytes(announcementsPath).SequenceEqual(persisted), "A stale revision must not overwrite another administrator's notice.");
        Request(administrator, new string('a', AnnouncementState.MaxLength + 1), 1);
        check(!snapshots["admin"].AnnouncementSaved && File.ReadAllBytes(announcementsPath).SequenceEqual(persisted), "Oversized HTML must not be persisted.");
        WithFailedAnnouncementStorage(persisted, () => Request(administrator, "new", 1));
        check(!snapshots["admin"].AnnouncementSaved && snapshots["admin"].AnnouncementRevision == 1
            && File.ReadAllBytes(announcementsPath).SequenceEqual(persisted) && errors.Count == 1,
            "Storage failures must preserve the previously published notice and revision.");
        var reloaded = Server(); Invoke(reloaded, "LoadState");
        Invoke(reloaded, "SendCivilizationSnapshot", ordinary, false, "", "", false);
        check(snapshots["ordinary"].AnnouncementRevision == 1 && snapshots["ordinary"].AnnouncementHtml.Contains("欢迎"),
            "Notices must survive a server restart.");
        Request(administrator, "", 1);
        check(snapshots["admin"].AnnouncementSaved && snapshots["ordinary"].AnnouncementHtml == "", "An administrator must be able to clear the notice.");
        failBroadcast = true; Request(administrator, "saved despite a disconnected recipient", 2); failBroadcast = false;
        check(snapshots["admin"].AnnouncementSaved && snapshots["admin"].AnnouncementRevision == 3
            && JsonConvert.DeserializeObject<AnnouncementState>(File.ReadAllText(announcementsPath))!.Revision == 3,
            "A broadcast failure after persistence must not falsely report that saving failed.");

        void Notice(string action, string target, string title, string html, long revision, bool notify = false, IServerPlayer? sender = null)
            => Invoke(system, "OnCivilizationRequest", sender ?? administrator, new CivilizationRequestPacket
            { Action = action, Target = target, Name = title, Html = html, Revision = revision, Notify = notify, RequestId = "multi-notice" });
        Notice("announcement-create", "", "维护", "<p>更新</p>", 3, true);
        var firstNotice = snapshots["admin"].Announcements.Last();
        check(snapshots["admin"].Announcements.Count == 2 && firstNotice.Title == "维护" && firstNotice.Notify,
            "Multiple notices must have independent titles, content and one enabled login notification.");
        Invoke(system, "SendLoginAnnouncement", ordinary);
        check(snapshots["ordinary"].Open && snapshots["ordinary"].NotificationId == firstNotice.Id,
            "An unseen enabled announcement must open its own page on login.");
        Invoke(system, "OnCivilizationRequest", ordinary, new CivilizationRequestPacket
        { Action = "announcement-seen", Target = firstNotice.Id, Revision = firstNotice.NotificationRevision - 1 });
        Invoke(system, "SendLoginAnnouncement", ordinary);
        check(snapshots["ordinary"].NotificationId == firstNotice.Id, "Stale acknowledgements must not mark a new notification read.");
        Invoke(system, "OnCivilizationRequest", ordinary, new CivilizationRequestPacket
        { Action = "announcement-seen", Target = firstNotice.Id, Revision = firstNotice.NotificationRevision });
        var restartedNotices = Server(); Invoke(restartedNotices, "LoadState");
        Invoke(restartedNotices, "SendCivilizationSnapshot", ordinary, false, "", "", false);
        Invoke(restartedNotices, "SendLoginAnnouncement", ordinary);
        check(!snapshots["ordinary"].Open && snapshots["ordinary"].NotificationId == "",
            "A displayed notice must remain read across server restarts.");
        Invoke(system, "SendLoginAnnouncement", administrator);
        check(snapshots["admin"].NotificationId == firstNotice.Id, "Reading a notice is scoped to the authenticated player.");
        Notice("announcement-save", firstNotice.Id, "维护", "<p>第二次更新</p>", 4, true);
        Invoke(system, "SendLoginAnnouncement", ordinary);
        check(snapshots["ordinary"].NotificationRevision == 5, "Updating notified HTML must reset each player's next-login notification.");
        Notice("announcement-create", "", "规则", "<p>规则内容</p>", 5, true);
        var secondNotice = snapshots["admin"].Announcements.Last();
        check(snapshots["admin"].Announcements.Count(e => e.Notify) == 1 && secondNotice.Notify,
            "Enabling another notice must atomically disable the previous notification.");
        Notice("announcement-delete", secondNotice.Id, "", "", 6, sender: ordinary);
        check(!snapshots["ordinary"].AnnouncementSaved && snapshots["ordinary"].Announcements.Count == 3,
            "Only administrators may delete notices.");
        Notice("announcement-delete", secondNotice.Id, "", "", 6);
        check(snapshots["admin"].AnnouncementSaved && snapshots["admin"].Announcements.Count == 2
            && snapshots["admin"].Announcements.All(e => !e.Notify), "Deleting the notified notice must remove its tab and login popup.");
        Notice("announcement-save", firstNotice.Id, "维护", "<p>第三次更新</p>", 7, true);
        Notice("announcement-save", firstNotice.Id, "维护", "<p>第三次更新</p>", 8, false);
        Notice("announcement-save", firstNotice.Id, "维护", "<p>第三次更新</p>", 9, true);
        Invoke(system, "SendLoginAnnouncement", ordinary);
        check(snapshots["ordinary"].NotificationRevision == 10, "Re-enabling notification must re-arm next-login display even when the HTML is unchanged.");
        var currentNotices = File.ReadAllBytes(announcementsPath);
        WithFailedAnnouncementStorage(currentNotices, () => Notice("announcement-delete", firstNotice.Id, "", "", 10));
        check(!snapshots["admin"].AnnouncementSaved && File.ReadAllBytes(announcementsPath).SequenceEqual(currentNotices),
            "Failed multi-notice deletion must preserve the collection and read records.");
        using var noticeWire = new MemoryStream(); Serializer.Serialize(noticeWire, snapshots["ordinary"]); noticeWire.Position = 0;
        var noticePacket = Serializer.Deserialize<CivilizationSnapshotPacket>(noticeWire);
        check(noticePacket.NotificationId == firstNotice.Id && noticePacket.Announcements.Count == 2 && noticePacket.AnnouncementCollection,
            "Multiple announcement tabs and login notification revisions must survive protobuf transport.");

        const string markdown = "## 维护公告\n\n**请携带纸图**\n\n- 尊重建筑\n- 共同建设";
        var markdownRequest = new CivilizationRequestPacket { Action = "announcement-save", Target = firstNotice.Id,
            Name = "维护", Html = markdown, IsMarkdown = true, Notify = true, Revision = 10, RequestId = "markdown-save" };
        using var markdownWire = new MemoryStream(); Serializer.Serialize(markdownWire, markdownRequest); markdownWire.Position = 0;
        Invoke(system, "OnCivilizationRequest", administrator, Serializer.Deserialize<CivilizationRequestPacket>(markdownWire));
        check(snapshots["admin"].AnnouncementSaved && snapshots["ordinary"].Announcements.Single(e => e.Id == firstNotice.Id) is { IsMarkdown: true, Html: markdown },
            "Markdown source and its format must survive request transport, persistence and broadcast.");
        var markdownServer = Server(); Invoke(markdownServer, "LoadState");
        Invoke(markdownServer, "SendCivilizationSnapshot", ordinary, false, "", "", false);
        using var markdownSnapshotWire = new MemoryStream(); Serializer.Serialize(markdownSnapshotWire, snapshots["ordinary"]); markdownSnapshotWire.Position = 0;
        var markdownSnapshot = Serializer.Deserialize<CivilizationSnapshotPacket>(markdownSnapshotWire);
        check(markdownSnapshot.Announcements.Single(e => e.Id == firstNotice.Id) is { IsMarkdown: true, Html: markdown }
            && !markdownSnapshot.Announcements[0].IsMarkdown,
            "Markdown format must survive restart and snapshot transport alongside legacy HTML notices.");
        markdownRequest.Html = new string('中', AnnouncementState.MaxLength + 1); markdownRequest.Revision = 11;
        Invoke(system, "OnCivilizationRequest", administrator, markdownRequest);
        check(!snapshots["admin"].AnnouncementSaved && snapshots["admin"].Announcements.Single(e => e.Id == firstNotice.Id).Html == markdown,
            "Oversized Markdown must be rejected on the server without changing the published source.");

        string markup = AnnouncementHtml.ToNativeMarkup("<h1>标题</h1><p>&lt;纸图&gt; &amp; &quot;公告&quot;</p><ul><li>家园</li></ul><script>hidden</script>");
        check(markup.Contains("weight=\"bold\"") && markup.Contains("&lt;纸图&gt; & \"公告\"") && markup.Contains("• 家园") && !markup.Contains("hidden"),
            "HTML headings, entities and lists must translate to native formatting without executable content.");
        foreach (string hidden in new[] { "<script>incomplete", "<!-- incomplete", "<style>incomplete", "<iframe>incomplete" })
            check(AnnouncementHtml.ToNativeMarkup("safe" + hidden) == "safe", "Unclosed hidden HTML content leaked into the notice.");
        check(!AnnouncementHtml.ToNativeMarkup("<a href='javascript:alert(1)'>text</a><img src='external'>").Contains("href"),
            "Unsupported content and unsafe URI schemes must not become native tags.");
        check(AnnouncementHtml.ToNativeMarkup("<a href='https://example.org/?a=1&amp;b=2'>visit</a>").Contains("https://example.org/?a=1&b=2"),
            "HTTP links must preserve query strings for the game's normal link confirmation.");
        check(AnnouncementHtml.ToNativeMarkup("<b><i>text</b>").EndsWith("</i></strong>"), "Malformed nesting must produce balanced native markup.");
        check(AnnouncementHtml.ToNativeMarkup("<font color='#ffaa00' color='#aabbcc'>text</font>").Contains("#aabbcc"), "Duplicate HTML attributes must not crash rendering.");
        const string paragraphGap = "<vscn-gap em=\"1\" px=\"0\"/>";
        const string blockBreak = "<vscn-gap em=\"0\" px=\"0\"/>";
        check(AnnouncementHtml.ToNativeMarkup("<ul>\n  <li><p>家园</p></li>\n  <li>地图</li>\n</ul>") == "• 家园" + paragraphGap + "• 地图",
            "Paragraphs inside list items must retain their paragraph margin without separating the bullet from its text.");
        check(AnnouncementHtml.ToNativeMarkup("<div>\n <h2>公告</h2>\n <p>第一段</p>\n <div><p>第二段</p></div>\n</div>")
            == "<font size=\"21\" weight=\"bold\">公告</font><vscn-gap em=\"1\" px=\"17.43\"/>第一段" + paragraphGap + "第二段",
            "Heading and paragraph margins must collapse across nested blocks without adding margins at document edges.");
        check(AnnouncementHtml.ToNativeMarkup("<ol><li>甲<ul><li>乙</li></ul></li><li>丙</li></ol>")
            == "1. 甲" + blockBreak + "&nbsp;&nbsp;• 乙" + blockBreak + "2. 丙", "Ordered and nested lists must retain numbering and hierarchy with compact item spacing.");
        check(AnnouncementHtml.ToNativeMarkup("<p>甲</p><p>乙</p>") == "甲" + paragraphGap + "乙"
            && AnnouncementHtml.ToNativeMarkup("<div>甲</div><div>乙</div>") == "甲" + blockBreak + "乙",
            "Paragraphs must have margins while generic divisions only start a new line.");
        check(AnnouncementHtml.ToNativeMarkup("hello <b>bold </b><i>word</i> end") == "hello<strong> bold</strong><i> word</i> end",
            "Collapsed HTML spaces across inline tags must continue separating words.");
        check(AnnouncementHtml.ToNativeMarkup("<p>甲<br><br>乙</p>") == "甲<br><br>乙"
            && AnnouncementHtml.ToNativeMarkup("<pre>甲\n  乙\n\n丙</pre><p>丁</p>") == "甲\n  乙\n\n丙" + paragraphGap + "丁"
            && AnnouncementHtml.ToNativeMarkup("<pre>  甲\n</pre>") == "  甲\n",
            "Explicit blank lines and preformatted code must preserve their author-specified whitespace.");

    }
}
