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

        var remoteGameId = await ResolveGameId(apiUrl, apiKey, game.Id).ConfigureAwait(false);
        if (remoteGameId == Guid.Empty)
            throw new InvalidOperationException($"'{game.Name}' is not linked to an Unnamed Tracking game yet.");

        var archivesJson = await SendText(
            apiUrl.TrimEnd('/') + $"/api/game/{remoteGameId}/archives/save",
            apiKey,
            "GET").ConfigureAwait(false);
        var remoteArchives = Deserialize<List<SaveRemoteArchive>>(archivesJson) ?? new List<SaveRemoteArchive>();

        var uploadedFiles = 0;
        foreach (var location in config.SavePaths)
        {
            var files = CollectFiles(new[] { location.Path });
            if (files.Count == 0)
            {
                logger.Info($"No save files found for '{location.Name}' at '{location.Path}'.");
                continue;
            }

            var fingerprint = FingerprintLocation(location);
            if (onlyIfChanged &&
                config.LocationFingerprints.TryGetValue(location.Path, out var previousFingerprint) &&
                string.Equals(previousFingerprint, fingerprint, StringComparison.Ordinal))
            {
                logger.Info($"No changes detected for save location '{location.Name}'; automatic upload skipped.");
                continue;
            }

            var archiveId = ResolveArchiveId(config, location, remoteArchives);
            // Migrate the archive produced by the earlier implementation when
            // there was only one configured location. This preserves its history
            // while giving the archive its real save-location name.
            if (archiveId == Guid.Empty &&
                config.SavePaths.Count == 1 &&
                remoteArchives.Count == 1 &&
                string.Equals(remoteArchives[0].Name, "Playnite Save", StringComparison.OrdinalIgnoreCase))
            {
                archiveId = remoteArchives[0].Id;
                await RenameArchive(apiUrl, apiKey, remoteGameId, archiveId, location.Name).ConfigureAwait(false);
                remoteArchives[0].Name = location.Name;
                config.RemoteArchiveIds[location.Path] = archiveId;
            }

            var zip = Path.Combine(
                Path.GetTempPath(),
                "unnamed-tracking-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                CreateZip(location, zip);

                if (archiveId == Guid.Empty)
                {
                    archiveId = await CreateArchive(
                        apiUrl,
                        apiKey,
                        remoteGameId,
                        location.Name,
                        zip).ConfigureAwait(false);
                    if (archiveId == Guid.Empty)
                        throw new InvalidOperationException($"The server did not return an archive ID for save location '{location.Name}'.");
                    config.RemoteArchiveIds[location.Path] = archiveId;
                }
                else
                {
                    await UploadArchiveVersion(apiUrl, apiKey, remoteGameId, archiveId, zip).ConfigureAwait(false);
                }

                config.LocationFingerprints[location.Path] = fingerprint;
                uploadedFiles += files.Count;
                logger.Info($"Uploaded {files.Count} save file(s) for '{location.Name}'.");
            }
            finally
            {
                TryDelete(zip);
            }
        }

        store.Save();
        if (uploadedFiles == 0)
            logger.Info($"No changed save locations were uploaded for '{game.Name}'.");
    });

    public Task DownloadAsync(Game game, string apiUrl, string apiKey) => Task.Run(async () =>
    {
        var config = store.For(game.Id);
        if (config.SavePaths.Count == 0) return;

        var remoteGameId = await ResolveGameId(apiUrl, apiKey, game.Id).ConfigureAwait(false);
        if (remoteGameId == Guid.Empty)
            throw new InvalidOperationException($"'{game.Name}' is not linked to an Unnamed Tracking game yet.");

        var archiveJson = await SendText(
            apiUrl.TrimEnd('/') + $"/api/game/{remoteGameId}/archives/save",
            apiKey,
            "GET").ConfigureAwait(false);
        var archives = Deserialize<List<SaveRemoteArchive>>(archiveJson) ?? new List<SaveRemoteArchive>();

        foreach (var location in config.SavePaths)
        {
            var archiveId = ResolveArchiveId(config, location, archives);
            var archive = archiveId == Guid.Empty
                ? archives.FirstOrDefault(x => string.Equals(x.Name, location.Name, StringComparison.OrdinalIgnoreCase))
                : archives.FirstOrDefault(x => x.Id == archiveId);

            var version = archive?.Versions?
                .OrderByDescending(x => x.UploadedAt)
                .FirstOrDefault();

            if (version == null || string.IsNullOrWhiteSpace(version.Url))
            {
                logger.Info($"No cloud save exists for '{location.Name}'.");
                continue;
            }

            if (archiveId != Guid.Empty)
                config.RemoteArchiveIds[location.Path] = archiveId;

            var zip = Path.Combine(
                Path.GetTempPath(),
                "unnamed-tracking-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                await DownloadToFile(apiUrl.TrimEnd('/') + version.Url, apiKey, zip).ConfigureAwait(false);
                BackupExisting(new[] { location }, game.Id);
                ExtractZip(zip, new[] { location });
                config.LocationFingerprints[location.Path] = FingerprintLocation(location);
                logger.Info($"Downloaded latest cloud save for '{location.Name}'.");
            }
            finally
            {
                TryDelete(zip);
            }
        }

        store.Save();
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

    private static void CreateZip(SavePathEntry item, string zip)
    {
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var root = Environment.ExpandEnvironmentVariables(item.Path);
            foreach (var file in CollectFiles(new[] { root }))
            {
                var relative = Directory.Exists(root)
                    ? file.Substring(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    : Path.GetFileName(file);
                var safeName = string.IsNullOrWhiteSpace(item.Name) ? "Save location" : item.Name;
                foreach (var invalid in Path.GetInvalidFileNameChars())
                    safeName = safeName.Replace(invalid, '_');
                archive.CreateEntryFromFile(
                    file,
                    (safeName + "/" + relative).Replace('\\', '/'),
                    CompressionLevel.Optimal);
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

    private static string FingerprintLocation(SavePathEntry item)
    {
        using (var sha = SHA256.Create())
        {
            var lines = new List<string>();
            foreach (var file in CollectFiles(new[] { item.Path }).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(file);
                lines.Add(item.Name + "|" + file + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks);
            }
            return Convert.ToBase64String(
                sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
        }
    }

    internal SaveSyncStatus GetStatus(Game game)
    {
        var config = store.For(game.Id);
        var files = CollectFiles(config.SavePaths.Select(x => x.Path));
        if (config.SavePaths.Count == 0)
            return new SaveSyncStatus("Not configured", 0, config.UploadOnGameStop, config.DownloadOnGameStart);
        if (files.Count == 0)
            return new SaveSyncStatus("No local save files", 0, config.UploadOnGameStop, config.DownloadOnGameStart);
        var changed = config.SavePaths.Any(location =>
            !config.LocationFingerprints.TryGetValue(location.Path, out var savedFingerprint) ||
            !string.Equals(savedFingerprint, FingerprintLocation(location), StringComparison.Ordinal));
        return new SaveSyncStatus(changed ? "Local changes pending" : "Up to date", files.Count, config.UploadOnGameStop, config.DownloadOnGameStart);
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

    private static Guid ResolveArchiveId(
        SaveGameConfiguration config,
        SavePathEntry location,
        IEnumerable<SaveRemoteArchive> archives)
    {
        if (config.RemoteArchiveIds.TryGetValue(location.Path, out var storedId))
            return storedId;

        var byName = archives.FirstOrDefault(
            x => string.Equals(x.Name, location.Name, StringComparison.OrdinalIgnoreCase));
        return byName?.Id ?? Guid.Empty;
    }

    private static async Task<Guid> CreateArchive(
        string apiUrl,
        string key,
        Guid gameId,
        string name,
        string zip)
    {
        var boundary = "----------------" + Guid.NewGuid().ToString("N");
        var zipInfo = new FileInfo(zip);
        var header = Encoding.UTF8.GetBytes(
            $"--{boundary}\r\nContent-Disposition: form-data; name=name\r\n\r\n{name}\r\n" +
            $"--{boundary}\r\nContent-Disposition: form-data; name=file; filename=save.zip\r\n" +
            "Content-Type: application/zip\r\n\r\n");
        var tail = Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n");

        var request = (HttpWebRequest)WebRequest.Create(
            apiUrl.TrimEnd('/') + $"/api/game/{gameId}/archives/save");
        request.Method = "POST";
        request.Headers[HttpRequestHeader.Authorization] = "Bearer " + key;
        request.ContentType = "multipart/form-data; boundary=" + boundary;
        request.ContentLength = header.LongLength + zipInfo.Length + tail.LongLength;

        using (var requestStream = await request.GetRequestStreamAsync().ConfigureAwait(false))
        {
            await requestStream.WriteAsync(header, 0, header.Length).ConfigureAwait(false);
            using (var input = File.OpenRead(zip))
            {
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                    await requestStream.WriteAsync(buffer, 0, read).ConfigureAwait(false);
            }
            await requestStream.WriteAsync(tail, 0, tail.Length).ConfigureAwait(false);
        }

        using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
        using (var stream = response.GetResponseStream() ?? Stream.Null)
        using (var memory = new MemoryStream())
        {
            await stream.CopyToAsync(memory).ConfigureAwait(false);
            var created = Deserialize<SaveRemoteArchive>(Encoding.UTF8.GetString(memory.ToArray()));
            return created?.Id ?? Guid.Empty;
        }
    }

    private static async Task RenameArchive(
        string apiUrl,
        string key,
        Guid gameId,
        Guid archiveId,
        string name)
    {
        var body = Encoding.UTF8.GetBytes(
            "{\\"name\\":\\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\\"}");

        var request = (HttpWebRequest)WebRequest.Create(
            apiUrl.TrimEnd('/') + $"/api/game/{gameId}/archives/{archiveId}");
        request.Method = "PATCH";
        request.Headers[HttpRequestHeader.Authorization] = "Bearer " + key;
        request.ContentType = "application/json";
        request.ContentLength = body.LongLength;

        using (var requestStream = await request.GetRequestStreamAsync().ConfigureAwait(false))
            await requestStream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);

        using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
        using (var stream = response.GetResponseStream() ?? Stream.Null)
            await stream.CopyToAsync(Stream.Null).ConfigureAwait(false);
    }

    private static async Task UploadArchiveVersion(
        string apiUrl,
        string key,
        Guid gameId,
        Guid archiveId,
        string zip)
    {
        var boundary = "----------------" + Guid.NewGuid().ToString("N");
        var zipInfo = new FileInfo(zip);
        var header = Encoding.UTF8.GetBytes(
            $"--{boundary}\r\nContent-Disposition: form-data; name=file; filename=save.zip\r\n" +
            "Content-Type: application/zip\r\n\r\n");
        var tail = Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n");

        var request = (HttpWebRequest)WebRequest.Create(
            apiUrl.TrimEnd('/') + $"/api/game/{gameId}/archives/{archiveId}/versions");
        request.Method = "POST";
        request.Headers[HttpRequestHeader.Authorization] = "Bearer " + key;
        request.ContentType = "multipart/form-data; boundary=" + boundary;
        request.ContentLength = header.LongLength + zipInfo.Length + tail.LongLength;

        using (var requestStream = await request.GetRequestStreamAsync().ConfigureAwait(false))
        {
            await requestStream.WriteAsync(header, 0, header.Length).ConfigureAwait(false);
            using (var input = File.OpenRead(zip))
            {
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                    await requestStream.WriteAsync(buffer, 0, read).ConfigureAwait(false);
            }
            await requestStream.WriteAsync(tail, 0, tail.Length).ConfigureAwait(false);
        }

        using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
        using (var stream = response.GetResponseStream() ?? Stream.Null)
        {
            await stream.CopyToAsync(Stream.Null).ConfigureAwait(false);
        }
    }

    private static async Task DownloadToFile(string url, string key, string destination)
    {
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "GET";
        request.Headers[HttpRequestHeader.Authorization] = "Bearer " + key;
        using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
        using (var stream = response.GetResponseStream() ?? Stream.Null)
        using (var output = File.Create(destination))
        {
            await stream.CopyToAsync(output).ConfigureAwait(false);
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
