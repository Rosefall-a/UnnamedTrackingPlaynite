using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace UnnamedTrackingPlaynite;

[DataContract]
internal sealed class SaveRemoteGame
{
    [DataMember(Name = "id")] public Guid Id { get; set; }
    [DataMember(Name = "playnite_guid")] public Guid? PlayniteGuid { get; set; }
}

[DataContract]
internal sealed class SaveRemoteArchive
{
    [DataMember(Name = "id")] public Guid Id { get; set; }
    [DataMember(Name = "name")] public string Name { get; set; } = "";
    [DataMember(Name = "updated_at")] public long UpdatedAt { get; set; }
    [DataMember(Name = "versions")] public List<SaveRemoteVersion> Versions { get; set; } = new List<SaveRemoteVersion>();
}

[DataContract]
internal sealed class SaveRemoteVersion
{
    [DataMember(Name = "id")] public Guid Id { get; set; }
    [DataMember(Name = "filename")] public string Filename { get; set; } = "";
    [DataMember(Name = "uploaded_at")] public long UploadedAt { get; set; }
    [DataMember(Name = "url")] public string Url { get; set; } = "";
}

internal sealed class SaveSyncManager
{
    private readonly ILogger logger;
    private readonly SaveSyncStore store;

    public SaveSyncManager(string dataPath, ILogger logger)
    {
        this.logger = logger;
        store = new SaveSyncStore(dataPath, logger);
    }

    public SaveGameConfiguration Configuration(Guid gameId) => store.For(gameId);

    public void SaveConfiguration(Guid gameId, IEnumerable<SavePathEntry> paths, bool uploadOnStop, bool downloadOnStart)
    {
        var config = store.For(gameId);
        config.SavePaths = paths
            .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Path))
            .Select(x => new SavePathEntry { Name = string.IsNullOrWhiteSpace(x.Name) ? "Save location" : x.Name.Trim(), Path = Environment.ExpandEnvironmentVariables(x.Path.Trim()) })
            .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();
        config.UploadOnGameStop = uploadOnStop;
        config.DownloadOnGameStart = downloadOnStart;
        store.Save();
    }

    public Task UploadAsync(Game game, string apiUrl, string apiKey, bool onlyIfChanged = false) => Task.Run(async () =>
    {
        var config = store.For(game.Id);
        if (config.SavePaths.Count == 0)
        {
            logger.Info($"No save paths configured for '{game.Name}'.");
            return;
        }

        var files = CollectFiles(config.SavePaths.Select(x => x.Path));
        if (files.Count == 0)
        {
            logger.Info($"No save files found for '{game.Name}'.");
            return;
        }

        var fingerprint = Fingerprint(game.Id, config.SavePaths);
        if (onlyIfChanged && string.Equals(ReadFingerprint(game.Id), fingerprint, StringComparison.Ordinal))
        {
            logger.Info("No save changes detected; automatic upload skipped.");
            return;
        }

        var remoteGameId = await ResolveGameId(apiUrl, apiKey, game.Id).ConfigureAwait(false);
        if (remoteGameId == Guid.Empty)
            throw new InvalidOperationException($"'{game.Name}' is not linked to an Unnamed Tracking game yet.");

        var zip = Path.Combine(Path.GetTempPath(), "unnamed-tracking-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            CreateZip(config.SavePaths, zip);
            await UploadArchive(apiUrl, apiKey, remoteGameId, zip).ConfigureAwait(false);
            WriteFingerprint(game.Id, fingerprint);
            logger.Info($"Uploaded {files.Count} save file(s) for '{game.Name}'.");
        }
        finally
        {
            TryDelete(zip);
        }
    });

    public Task DownloadAsync(Game game, string apiUrl, string apiKey) => Task.Run(async () =>
    {
        var config = store.For(game.Id);
        if (config.SavePaths.Count == 0) return;

        var remoteGameId = await ResolveGameId(apiUrl, apiKey, game.Id).ConfigureAwait(false);
        if (remoteGameId == Guid.Empty)
            throw new InvalidOperationException($"'{game.Name}' is not linked to an Unnamed Tracking game yet.");

        var archiveJson = await SendText(apiUrl.TrimEnd('/') + $"/api/game/{remoteGameId}/archives/save", apiKey, "GET").ConfigureAwait(false);
        var archives = Deserialize<List<SaveRemoteArchive>>(archiveJson) ?? new List<SaveRemoteArchive>();
        var archive = archives
            .OrderByDescending(x => x.UpdatedAt)
            .FirstOrDefault(x => x.Versions != null && x.Versions.Count > 0);
        var version = archive?.Versions?.OrderByDescending(x => x.UploadedAt).FirstOrDefault();
        if (version == null || string.IsNullOrWhiteSpace(version.Url))
        {
            logger.Info($"No cloud save exists for '{game.Name}'.");
            return;
        }

        var zip = Path.Combine(Path.GetTempPath(), "unnamed-tracking-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var bytes = await Download(apiUrl.TrimEnd('/') + version.Url, apiKey).ConfigureAwait(false);
            File.WriteAllBytes(zip, bytes);
            BackupExisting(config.SavePaths, game.Id);
            ExtractZip(zip, config.SavePaths);
            WriteFingerprint(game.Id, Fingerprint(game.Id, config.SavePaths));
            logger.Info($"Downloaded latest cloud save for '{game.Name}'.");
        }
        finally
        {
            TryDelete(zip);
        }
    });

    private static List<string> CollectFiles(IEnumerable<string> roots)
    {
        var result = new List<string>();
        foreach (var root in roots.Select(Environment.ExpandEnvironmentVariables))
        {
            if (File.Exists(root))
                result.Add(root);
            else if (Directory.Exists(root))
                result.AddRange(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void CreateZip(IEnumerable<SavePathEntry> configured, string zip)
    {
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var item in configured)
            {
                var root = Environment.ExpandEnvironmentVariables(item.Path);
                foreach (var file in CollectFiles(new[] { root }))
                {
                    var relative = Directory.Exists(root) ? file.Substring(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : Path.GetFileName(file);
                    var safeName = string.IsNullOrWhiteSpace(item.Name) ? "Save location" : item.Name;
                    foreach (var invalid in Path.GetInvalidFileNameChars())
                        safeName = safeName.Replace(invalid, '_');
                    archive.CreateEntryFromFile(file, (safeName + "/" + relative).Replace('\\', '/'), CompressionLevel.Optimal);
                }
            }
        }
    }

    private static void ExtractZip(string zip, IEnumerable<SavePathEntry> configured)
    {
        var targets = configured.ToDictionary(
            x => string.IsNullOrWhiteSpace(x.Name) ? "Save location" : x.Name,
            x => Environment.ExpandEnvironmentVariables(x.Path),
            StringComparer.OrdinalIgnoreCase);

        using (var archive = ZipFile.OpenRead(zip))
        {
            foreach (var entry in archive.Entries)
            {
                var parts = entry.FullName.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !targets.TryGetValue(parts[0], out var target))
                    throw new InvalidDataException("Save archive contains an unknown save location.");

                var relative = string.Join(Path.DirectorySeparatorChar.ToString(), parts.Skip(1));
                var destination = Path.GetFullPath(Path.Combine(target, relative));
                var root = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Unsafe save archive path.");

                if (string.IsNullOrEmpty(entry.Name))
                    Directory.CreateDirectory(destination);
                else
                {
                    var parent = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
                    entry.ExtractToFile(destination, true);
                }
            }
        }
    }

    private static void BackupExisting(IEnumerable<SavePathEntry> configured, Guid gameId)
    {
        var files = CollectFiles(configured.Select(x => x.Path));
        if (files.Count == 0) return;

        var backupRoot = Path.Combine(Path.GetTempPath(), "UnnamedTrackingSaveBackups", gameId.ToString("N"), DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        foreach (var file in files)
        {
            var sourceRoot = configured.FirstOrDefault(x => CollectFiles(new[] { x.Path }).Contains(file, StringComparer.OrdinalIgnoreCase));
            var locationName = sourceRoot == null ? "Save location" : (string.IsNullOrWhiteSpace(sourceRoot.Name) ? "Save location" : sourceRoot.Name);
            foreach (var invalid in Path.GetInvalidFileNameChars()) locationName = locationName.Replace(invalid, '_');
            var root = Environment.ExpandEnvironmentVariables(sourceRoot?.Path ?? Path.GetDirectoryName(file) ?? backupRoot);
            var relative = Directory.Exists(root) ? file.Substring(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : Path.GetFileName(file);
            var destination = Path.Combine(backupRoot, locationName, relative);
            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
            File.Copy(file, destination, true);
        }
    }

    private string Fingerprint(Guid gameId, IEnumerable<SavePathEntry> configured)
    {
        using (var sha = SHA256.Create())
        {
            var lines = new List<string>();
            foreach (var item in configured)
            {
                foreach (var file in CollectFiles(new[] { item.Path }).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var info = new FileInfo(file);
                    lines.Add(item.Name + "|" + file + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks);
                }
            }
            return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
        }
    }

    private string ReadFingerprint(Guid gameId)
    {
        var path = Path.Combine(Path.GetTempPath(), "UnnamedTrackingSaveSync", gameId.ToString("N") + ".txt");
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; } catch { return ""; }
    }

    private void WriteFingerprint(Guid gameId, string value)
    {
        var path = Path.Combine(Path.GetTempPath(), "UnnamedTrackingSaveSync", gameId.ToString("N") + ".txt");
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, value); } catch { }
    }

    private static async Task<Guid> ResolveGameId(string apiUrl, string apiKey, Guid playniteGuid)
    {
        var response = await SendText(
            apiUrl.TrimEnd('/') + "/api/game/list?skip=0&limit=200",
            apiKey,
            "GET").ConfigureAwait(false);
        var games = Deserialize<List<SaveRemoteGame>>(response) ?? new List<SaveRemoteGame>();
        return games.FirstOrDefault(x => x.PlayniteGuid == playniteGuid)?.Id ?? Guid.Empty;
    }

    private static async Task UploadArchive(string apiUrl, string key, Guid gameId, string zip)
    {
        var boundary = "----------------" + Guid.NewGuid().ToString("N");
        var zipBytes = File.ReadAllBytes(zip);
        var header = Encoding.UTF8.GetBytes(
            $"--{boundary}\r\nContent-Disposition: form-data; name=name\r\n\r\nPlaynite Save\r\n" +
            $"--{boundary}\r\nContent-Disposition: form-data; name=file; filename=playnite-save.zip\r\n" +
            "Content-Type: application/zip\r\n\r\n");
        var tail = Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n");
        var body = new byte[header.Length + zipBytes.Length + tail.Length];
        Buffer.BlockCopy(header, 0, body, 0, header.Length);
        Buffer.BlockCopy(zipBytes, 0, body, header.Length, zipBytes.Length);
        Buffer.BlockCopy(tail, 0, body, header.Length + zipBytes.Length, tail.Length);

        await SendBytes(
            apiUrl.TrimEnd('/') + $"/api/game/{gameId}/archives/save",
            key,
            "POST",
            body,
            "multipart/form-data; boundary=" + boundary).ConfigureAwait(false);
    }

    private static async Task<byte[]> Download(string url, string key)
    {
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "GET";
        request.Headers[HttpRequestHeader.Authorization] = "Bearer " + key;
        using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
        using (var stream = response.GetResponseStream() ?? Stream.Null)
        using (var memory = new MemoryStream())
        {
            await stream.CopyToAsync(memory).ConfigureAwait(false);
            return memory.ToArray();
        }
    }

    private static async Task<string> SendText(string url, string key, string method)
    {
        var bytes = await SendBytes(url, key, method, new byte[0], "application/json").ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    private static async Task<byte[]> SendBytes(string url, string key, string method, byte[] body, string contentType)
    {
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = method;
        request.Headers[HttpRequestHeader.Authorization] = "Bearer " + key;
        request.ContentLength = body.Length;
        if (body.Length > 0)
        {
            request.ContentType = contentType;
            using (var stream = await request.GetRequestStreamAsync().ConfigureAwait(false))
                await stream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
        }

        using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
        using (var stream = response.GetResponseStream() ?? Stream.Null)
        using (var memory = new MemoryStream())
        {
            await stream.CopyToAsync(memory).ConfigureAwait(false);
            return memory.ToArray();
        }
    }

    private static T Deserialize<T>(string json)
    {
        var serializer = new DataContractJsonSerializer(typeof(T));
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            return (T)serializer.ReadObject(stream)!;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
