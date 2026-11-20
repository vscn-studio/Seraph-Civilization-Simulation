using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;

namespace SeraphCivilizationSimulation;

internal static class ClientLogFiles
{
    internal const int ChunkBytes = 24 * 1024, FileBytes = 2 * 1024 * 1024;
    internal const int MaxArchiveBytes = 8 * 1024 * 1024, MaxExpandedBytes = 12 * 1024 * 1024;
    internal static readonly string[] LogNames = ["client-crash.txt", "client-crash.log", "client-main.log", "client-debug.log", "client-audit.log"];
    internal static bool AllowedEntry(string name) => name == "client-context.json"
        || new[] { "current/", "previous/" }.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)
            && LogNames.Contains(name[prefix.Length..], StringComparer.Ordinal));

    internal sealed record Snapshot(byte[] Archive, string[] Files);

    // Vintage Story rotates main/debug/audit into Archive/<session> at process startup,
    // but deliberately leaves client-crash.log in the log root. Capture both sessions.
    internal static Snapshot Capture(string logRoot, string context)
    {
        using var output = new MemoryStream();
        var files = new List<string>();
        int contextBytes = Encoding.UTF8.GetByteCount(context);
        if (contextBytes > 256 * 1024) throw new InvalidDataException("Client context is too large.");
        int remaining = MaxExpandedBytes - contextBytes - 16 * 1024;
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("client-context.json").Open(), new UTF8Encoding(false))) writer.Write(context);
            string? previous = null;
            string archive = Path.Combine(logRoot, "Archive");
            if (Directory.Exists(archive))
                previous = Directory.EnumerateDirectories(archive)
                    .Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0
                        && File.Exists(Path.Combine(p, "client-main.log")))
                    .OrderByDescending(p => File.GetLastWriteTimeUtc(Path.Combine(p, "client-main.log")))
                    .FirstOrDefault();
            // Crash stacks first, then the prior session, then current connection context.
            var candidates = LogNames.Take(2).Select(n => (Root: logRoot, Name: n, Prefix: "current/"))
                .Concat(previous == null ? [] : LogNames.Skip(2).Select(n => (Root: previous, Name: n, Prefix: "previous/")))
                .Concat(LogNames.Skip(2).Select(n => (Root: logRoot, Name: n, Prefix: "current/")));
            var details = new List<object>();
            foreach (var candidate in candidates)
            {
                string path = Path.Combine(candidate.Root, candidate.Name);
                try
                {
                    if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || remaining <= 0) continue;
                    using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    long length = input.Length;
                    if (length == 0) continue;
                    int count = (int)Math.Min(length, Math.Min(FileBytes, remaining));
                    long start = length - count;
                    input.Position = start;
                    byte[] data = new byte[count];
                    int read = 0, size;
                    while (read < count && (size = input.Read(data, read, count - read)) > 0) read += size;
                    string name = candidate.Prefix + candidate.Name;
                    using (var entry = zip.CreateEntry(name, CompressionLevel.Fastest).Open()) entry.Write(data, 0, read);
                    remaining -= read;
                    files.Add(name + (start > 0 ? " (tail)" : ""));
                    details.Add(new { file = name, originalBytes = length, uploadedBytes = read, startByte = start,
                        lastWriteUtc = File.GetLastWriteTimeUtc(path) });
                }
                catch (IOException) { /* One locked/missing log must not hide the other evidence. */ }
                catch (UnauthorizedAccessException) { }
            }
            if (files.Count == 0) throw new IOException("没有找到可读取的客户端日志。");
            // Metadata is kept in the context entry, with a reserved bounded allowance.
            using var writer2 = new StreamWriter(zip.CreateEntry("collection.json").Open(), new UTF8Encoding(false));
            writer2.Write(JsonConvert.SerializeObject(details, Formatting.Indented));
        }
        byte[] result = output.ToArray();
        if (result.Length > MaxArchiveBytes) throw new InvalidDataException("日志包超过 8 MiB，请手动联系管理员提交。");
        return new(result, files.ToArray());
    }

    internal static void ValidateArchive(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count is < 2 or > 10) throw new InvalidDataException("Invalid log entry count.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        byte[] buffer = new byte[16 * 1024];
        foreach (var entry in zip.Entries)
        {
            if ((!AllowedEntry(entry.FullName) && entry.FullName != "collection.json") || !names.Add(entry.FullName))
                throw new InvalidDataException("Invalid or duplicate log entry.");
            long limit = entry.FullName.EndsWith(".json", StringComparison.Ordinal) ? 256 * 1024 : FileBytes;
            if (entry.Length > limit) throw new InvalidDataException("Log entry exceeds limit.");
            using var stream = entry.Open();
            long actual = 0;
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                actual += count; total += count;
                if (actual > limit || total > MaxExpandedBytes) throw new InvalidDataException("Expanded logs exceed limit.");
            }
            if (actual != entry.Length) throw new InvalidDataException("Incomplete log entry.");
        }
        if (!names.Contains("client-context.json") || !names.Any(n => n.EndsWith(".log") || n.EndsWith(".txt")))
            throw new InvalidDataException("No client logs.");
    }
}

// A strictly ordered, bounded stream: the sender waits for an acknowledgement after each chunk.
internal sealed class ClientLogIncoming : IDisposable
{
    private readonly FileStream stream;
    private readonly int length;
    internal string Path { get; }
    internal int Offset => checked((int)stream.Length);
    internal ClientLogIncoming(string path, int length)
    {
        if (length is <= 0 or > ClientLogFiles.MaxArchiveBytes) throw new InvalidDataException("Invalid archive size.");
        Path = path; this.length = length;
        stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }
    internal bool Append(int offset, byte[] data)
    {
        if (data == null || data.Length is <= 0 or > ClientLogFiles.ChunkBytes || offset != Offset || (long)offset + data.Length > length)
            throw new InvalidDataException("Invalid log chunk.");
        stream.Write(data);
        return Offset == length;
    }
    public void Dispose() => stream.Dispose();
}
