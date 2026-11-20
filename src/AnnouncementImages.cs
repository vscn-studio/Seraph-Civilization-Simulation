using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Cairo;
using SkiaSharp;

namespace SeraphCivilizationSimulation;

// Only bytes are shared across windows. Native surfaces belong to the GUI that uses them.
internal static class AnnouncementImages
{
    internal const int MaxBytes = 4 * 1024 * 1024;
    internal const int MaxPixels = 2 * 1024 * 1024;
    internal const int MaxImages = 16;
    private const int MaxCacheBytes = 32 * 1024 * 1024;
    private static readonly object Sync = new();
    internal sealed class CachedRequest(Task<byte[]?> task)
    {
        internal Task<byte[]?> Task { get; } = task;
        internal byte[]? Bytes;
        internal DateTimeOffset LastUsed = DateTimeOffset.UtcNow, Expires = DateTimeOffset.MaxValue;
    }
    private static readonly Dictionary<string, CachedRequest> Cache = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim Downloads = new(2);
    private static CancellationTokenSource lifetime = new();
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = async (context, cancellation) =>
        {
            // Check the actual DNS result at connection time, including every redirect.
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellation);
            foreach (var address in addresses.Where(IsPublicAddress))
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellation);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); }
            }
            throw new HttpRequestException("No public image host is reachable.");
        }
    }) { Timeout = TimeSpan.FromSeconds(30) };

    internal static string? NormalizeSource(string? source)
    {
        source = source?.Trim();
        if (string.IsNullOrEmpty(source) || source.Length > 2048 || source.Any(char.IsControl)) return null;
        if (source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Host.Length == 0
                || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
                || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && !IsPublicAddress(address))) return null;
            return new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri;
        }
        // Match the game asset convention; never read an arbitrary disk path.
        string[] parts = source.Split(':');
        string domain = parts.Length == 1 ? "game" : parts[0];
        string path = parts[^1];
        if (parts.Length > 2 || domain.Length == 0 || !domain.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            || !path.StartsWith("textures/", StringComparison.Ordinal) || path.Contains('\\')
            || path.Split('/').Any(p => p.Length == 0 || p is "." or "..")
            || !path.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '.')) return null;
        string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return extension is ".png" or ".jpg" or ".jpeg" or ".webp" ? domain + ":" + path : null;
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        byte[] b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return b[0] is not (0 or 10 or 127) && b[0] < 224
                && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31)
                && !(b[0] == 192 && (b[1] == 168 || (b[1] == 0 && b[2] is 0 or 2)))
                && !(b[0] == 100 && b[1] is >= 64 and <= 127) && !(b[0] == 198 && b[1] is 18 or 19)
                && !(b[0] == 198 && b[1] == 51 && b[2] == 100) && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
        // Allow ordinary global unicast; block local, multicast, and transition tunnels.
        return b.Length == 16 && (b[0] & 0xe0) == 0x20
            && !(b[0] == 0x20 && b[1] == 0x02)
            && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0)
            && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8);
    }

    internal static Task<byte[]?> Request(string source, HttpClient? client = null)
    {
        lock (Sync)
        {
            foreach (string expired in Cache.Where(e => e.Value.Expires <= DateTimeOffset.UtcNow).Select(e => e.Key).ToArray()) Cache.Remove(expired);
            if (Cache.TryGetValue(source, out var cached)) { cached.LastUsed = DateTimeOffset.UtcNow; return cached.Task; }
            // Evict completed byte buffers; never multiply active requests for the same URL.
            if (Cache.Count >= 48)
                foreach (string key in Cache.Where(e => e.Value.Task.IsCompleted).Select(e => e.Key).ToArray()) Cache.Remove(key);
            if (Cache.Count >= 48) return Task.FromResult<byte[]?>(null);
            var cancellation = lifetime.Token;
            var completion = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entry = new CachedRequest(completion.Task); Cache[source] = entry;
            _ = Task.Run(async () =>
            {
                byte[]? bytes = await Download(source, cancellation, client);
                lock (Sync)
                {
                    if (cancellation.IsCancellationRequested) bytes = null;
                    entry.Bytes = bytes;
                    entry.Expires = DateTimeOffset.UtcNow.Add(bytes == null ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(10));
                    completion.TrySetResult(bytes);
                    // Enforce the byte budget on completion, including queued downloads.
                    foreach (string key in Cache.Where(e => e.Value.Task.IsCompleted).OrderBy(e => e.Value.LastUsed).Select(e => e.Key).ToArray())
                    {
                        if (Cache.Values.Sum(e => e.Bytes?.Length ?? 0) <= MaxCacheBytes) break;
                        Cache.Remove(key);
                    }
                }
            });
            return completion.Task;
        }
    }

    internal static async Task<byte[]?> Download(string source, CancellationToken cancellation, HttpClient? client = null)
    {
        bool entered = false;
        try
        {
            await Downloads.WaitAsync(cancellation); entered = true;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    string? url = source;
                    for (int redirect = 0; redirect <= 3; redirect++)
                    {
                        if (NormalizeSource(url) is not string normalized || !normalized.StartsWith("https://", StringComparison.Ordinal)) return null;
                        using var message = new HttpRequestMessage(HttpMethod.Get, normalized);
                        message.Headers.UserAgent.ParseAdd("Seraph-Civilization-Simulation-Announcements/1.0");
                        using var response = await (client ?? Client).SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                        if ((int)response.StatusCode is >= 300 and < 400)
                        {
                            var location = response.Headers.Location;
                            if (location == null || redirect == 3) return null;
                            url = new Uri(new Uri(normalized), location).AbsoluteUri; continue;
                        }
                        if ((int)response.StatusCode is 408 or 429 or >= 500) break;
                        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes) return null;
                        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                        return await ReadBounded(stream, deadline.Token);
                    }
                }
                catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
                { if (cancellation.IsCancellationRequested) return null; }
                if (attempt == 0) await Task.Delay(250, cancellation);
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or UriFormatException) { }
        finally { if (entered) Downloads.Release(); }
        return null;
    }

    internal static async Task<byte[]?> ReadBounded(Stream stream, CancellationToken cancellation = default)
    {
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellation)) > 0)
        {
            if (bytes.Length + count > MaxBytes) return null;
            bytes.Write(buffer, 0, count);
        }
        return bytes.ToArray();
    }

    internal static ImageSurface? Decode(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length > MaxBytes) return null;
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec == null || codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp)) return null;
            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0 || info.Width > 4096 || info.Height > 4096 || (long)info.Width * info.Height > MaxPixels) return null;
            info.ColorType = SKColorType.Bgra8888; info.AlphaType = SKAlphaType.Premul;
            using var bitmap = SKBitmap.Decode(codec, info);
            if (bitmap == null) return null;
            var surface = new ImageSurface(Format.Argb32, info.Width, info.Height);
            byte[] pixels = new byte[info.Width * 4];
            for (int y = 0; y < info.Height; y++)
            {
                Marshal.Copy(bitmap.GetPixels() + y * bitmap.RowBytes, pixels, 0, pixels.Length);
                Marshal.Copy(pixels, 0, surface.DataPtr + y * surface.Stride, pixels.Length);
            }
            surface.MarkDirty(); return surface;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return null; }
    }

    internal static void Clear()
    {
        lock (Sync)
        {
            lifetime.Cancel(); lifetime.Dispose(); lifetime = new(); Cache.Clear();
        }
    }
}
