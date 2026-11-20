using System.Net;
using SeraphCivilizationSimulation;

internal static class AnnouncementImageChecks
{
    internal const string PublicModCardsHtml = """
        <h2>服务器维护模组</h2>
        <div class="card">
          <article>
            <a href="https://mods.vintagestory.at/vsscsmod">
              <img src="https://mods.vintagestory.at/web/img/mod-default.png"
                   alt="VSCN 文明模拟主模组">
              <h3>VSCN 文明模拟主模组</h3>
            </a>
            <p>For Chinese community servers only (Civilization Simulation)</p>
            <p><a href="https://mods.vintagestory.at/vsscsmod">查看模组详情</a></p>
          </article>
        </div>
        <div class="card">
          <article>
            <a href="https://mods.vintagestory.at/vscnservermodfixmod">
              <img src="https://mods.vintagestory.at/web/img/mod-default.png"
                   alt="VSCN 社区服务器改进模组">
              <h3>VSCN 社区服务器改进模组</h3>
            </a>
            <p>VSCN community server improvements and compatibility fixes.</p>
            <p><a href="https://mods.vintagestory.at/vscnservermodfixmod">查看模组详情</a></p>
          </article>
        </div>
        """;

    internal static void Run(Action<bool, string> check)
    {
        foreach (string address in new[] { "127.0.0.1", "10.0.0.1", "169.254.169.254", "172.16.2.1", "192.168.1.1", "100.64.0.1", "0.0.0.0", "224.0.0.1", "::1", "fc00::1", "fe80::1", "::ffff:127.0.0.1", "2002:7f00:1::1" })
            check(!AnnouncementImages.IsPublicAddress(IPAddress.Parse(address)), "Announcement image requests must reject local or tunneled addresses: " + address);
        foreach (string address in new[] { "8.8.8.8", "1.1.1.1", "2606:4700:4700::1111" })
            check(AnnouncementImages.IsPublicAddress(IPAddress.Parse(address)), "Public image hosts must remain usable: " + address);
        foreach (string source in new[] { "file:///c:/picture.png", "data:image/png;base64,aaa", "http://example.org/a.png", "https://127.0.0.1/a.png", "https://[::1]/a.png", "https://localhost/a.png", "https://printer.local/a.png", "https://user:password@example.org/a.png", "textures/../a.png", "game:textures/%2e%2e/a.png", "C:\\picture.png", "game:textures/a.svg", "game:config/password.png" })
            check(AnnouncementImages.NormalizeSource(source) == null, "Unsafe image sources must not reach a downloader or file access: " + source);
        check(AnnouncementImages.NormalizeSource("textures/gui/logo.png") == "game:textures/gui/logo.png"
            && AnnouncementImages.NormalizeSource("example:textures/announcement/photo.jpg") == "example:textures/announcement/photo.jpg"
            && AnnouncementImages.NormalizeSource("https://example.org/image?id=1&size=2#fragment") == "https://example.org/image?id=1&size=2",
            "Native resources and HTTPS images must preserve their usable source and query parameters.");
        string markup = AnnouncementHtml.ToNativeMarkup("<article style='background-color:#abc;border-color:#776644'><div><p>甲</p></div><p>乙</p><img src='game:textures/gui/logo.png' alt='图 > &quot;标题&quot;' style='width:100%;height:auto'></article><p>丙</p>");
        check(markup.Contains("bg=\"#aabbcc\"") && markup.Contains("border=\"#776644\"") && markup.Contains("width=\"100%\"")
            && markup.IndexOf("乙", StringComparison.Ordinal) < markup.IndexOf("</vscn-card>", StringComparison.Ordinal)
            && markup.IndexOf("丙", StringComparison.Ordinal) > markup.IndexOf("</vscn-card>", StringComparison.Ordinal),
            "A card must contain generic nested divisions and safe image attributes, including a greater-than sign inside quotes.");
        string fallback = AnnouncementHtml.ToNativeMarkup("<img src='data:bad' alt='替代内容'><script>alert(1)</script><div class='card' onclick='bad()'><p>卡片</p></div>");
        check(fallback.Contains("替代内容") && fallback.Contains("vscn-card") && !fallback.Contains("onclick") && !fallback.Contains("alert"),
            "Rejected images must retain alt text and cards must never execute event attributes or scripts.");
        using var small = new MemoryStream([1, 2, 3, 4]);
        check(AnnouncementImages.ReadBounded(small).GetAwaiter().GetResult()!.SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Image downloads must preserve bytes.");
        using var oversized = new MemoryStream(new byte[AnnouncementImages.MaxBytes + 1]);
        check(AnnouncementImages.ReadBounded(oversized).GetAwaiter().GetResult() == null, "Chunked responses without Content-Length must stop at the byte limit.");

        int requests = 0;
        using var redirectClient = new HttpClient(new Handler(request =>
        {
            requests++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://169.254.169.254/metadata"); return response;
        }));
        check(AnnouncementImages.Download("https://example.org/photo.png", default, redirectClient).GetAwaiter().GetResult() == null && requests == 1,
            "A redirect into a private host must be rejected before issuing a second request.");
        requests = 0;
        using var publicClient = new HttpClient(new Handler(request =>
        {
            requests++;
            if (requests == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("/image.png?size=2", UriKind.Relative); return response;
            }
            check(request.RequestUri!.AbsoluteUri == "https://example.org/image.png?size=2", "Relative redirects must preserve the public origin and query.");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        }));
        check(AnnouncementImages.Download("https://example.org/start", default, publicClient).GetAwaiter().GetResult()!.Length == 3 && requests == 2,
            "Public image redirects must remain supported.");
        requests = 0;
        using var loopingClient = new HttpClient(new Handler(request =>
        {
            requests++; var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = request.RequestUri; return response;
        }));
        check(AnnouncementImages.Download("https://example.org/loop", default, loopingClient).GetAwaiter().GetResult() == null && requests == 4,
            "Redirect loops must stop after a bounded number of requests.");
        requests = 0;
        using var recoveringClient = new HttpClient(new Handler(request =>
        {
            requests++;
            return requests == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([4, 5, 6]) };
        }));
        check(AnnouncementImages.Download("https://example.org/recover.png", default, recoveringClient).GetAwaiter().GetResult()!.SequenceEqual(new byte[] { 4, 5, 6 }) && requests == 2,
            "A temporary image-server failure must retry once and publish the successful payload.");
        requests = 0;
        using var interruptedClient = new HttpClient(new Handler(request =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = requests == 1
                ? new StreamContent(new InterruptedStream()) : new ByteArrayContent([7, 8, 9]) };
        }));
        check(AnnouncementImages.Download("https://example.org/interrupted.png", default, interruptedClient).GetAwaiter().GetResult()!.SequenceEqual(new byte[] { 7, 8, 9 }) && requests == 2,
            "A download interrupted midway must retry from the start instead of caching truncated image bytes.");
        requests = 0;
        using var missingClient = new HttpClient(new Handler(request => { requests++; return new HttpResponseMessage(HttpStatusCode.NotFound); }));
        check(AnnouncementImages.Download("https://example.org/missing.png", default, missingClient).GetAwaiter().GetResult() == null && requests == 1,
            "A permanent missing-image response must not repeat network requests.");
        using var leaving = new CancellationTokenSource(); requests = 0;
        using var leavingClient = new HttpClient(new Handler(request => { requests++; leaving.Cancel(); throw new HttpRequestException("Connection interrupted while leaving."); }));
        check(AnnouncementImages.Download("https://example.org/leaving.png", leaving.Token, leavingClient).GetAwaiter().GetResult() == null && requests == 1,
            "World cancellation must stop transient-failure retries immediately.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        check(AnnouncementImages.Download("https://example.org/photo.png", cancelled.Token, publicClient).GetAwaiter().GetResult() == null,
            "Leaving a world must be able to cancel pending image loads safely.");
        int cachedRequests = 0;
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cachedClient = new HttpClient(new AsyncHandler(async request =>
        {
            Interlocked.Increment(ref cachedRequests); return await release.Task;
        }));
        string cachedSource = "https://example.org/cached.png";
        var first = AnnouncementImages.Request(cachedSource, cachedClient);
        var second = AnnouncementImages.Request(cachedSource, cachedClient);
        check(ReferenceEquals(first, second) && !first.IsCompleted, "Repeated image requests must share a nonblocking download task.");
        release.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        check(first.GetAwaiter().GetResult()!.Length == 3 && cachedRequests == 1 && ReferenceEquals(first, AnnouncementImages.Request(cachedSource, cachedClient)),
            "Opening a preview must reuse completed image bytes without another download.");
        AnnouncementImages.Clear();
    }
    private sealed class Handler(System.Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }
    private sealed class AsyncHandler(System.Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request);
    }
    private sealed class InterruptedStream() : MemoryStream(new byte[] { 1, 2 })
    {
        private bool started;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (started) return ValueTask.FromException<int>(new IOException("Incomplete image body."));
            started = true; return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
