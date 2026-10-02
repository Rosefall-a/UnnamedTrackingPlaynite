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
using System.Threading;
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
    [DataMember(Name = "updated_at")] public double UpdatedAt { get; set; }
    [DataMember(Name = "versions")] public List<SaveRemoteVersion> Versions { get; set; } = new List<SaveRemoteVersion>();
}

[DataContract]
internal sealed class SaveRemoteVersion
{
    [DataMember(Name = "id")] public Guid Id { get; set; }
    [DataMember(Name = "filename")] public string Filename { get; set; } = "";
    [DataMember(Name = "uploaded_at")] public double UploadedAt { get; set; }
    [DataMember(Name = "url")] public string Url { get; set; } = "";
}

[DataContract]
internal sealed class SaveArchiveName
{
    [DataMember(Name = "name")] public string Name { get; set; } = string.Empty;
}

internal sealed class SaveSyncManager
{
    private readonly ILogger logger;
    private readonly SaveSyncStore store;
    private readonly string backupDirectory;
    private readonly SemaphoreSlim operationGate = new SemaphoreSlim(1, 1);
    private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
    internal void Cancel() => shutdown.Cancel();
    internal bool IsBusy => operationGate.CurrentCount == 0;

    private async Task RunAsync(Func<Task> action, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        await operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            using (ApiConnection.UseCancellation(linked.Token))
            {
                await Task.Run(action, linked.Token).ConfigureAwait(false);
            }
        }
        catch (WebException) when (linked.IsCancellationRequested) { throw new OperationCanceledException(linked.Token); }
        finally { operationGate.Release(); }
    }

    public SaveSyncManager(string dataPath, ILogger logger)
    {
        this.logger = logger;
        store = new SaveSyncStore(dataPath, logger);
        backupDirectory = Path.Combine(dataPath, "save-backups");
    }

    public SaveGameConfiguration Configuration(Guid gameId) => store.Snapshot(gameId);

    public void SaveConfiguration(Guid gameId, IEnumerable<SavePathEntry> paths, bool uploadOnStop, bool downloadOnStart)
    {
        if (IsBusy) throw new InvalidOperationException("Wait for the active save synchronization before editing save locations.");
        var validated = SaveArchive.ValidatePaths(paths);
        var config = store.Snapshot(gameId);
        foreach (var old in config.SavePaths)
        {
            var current = validated.FirstOrDefault(path => string.Equals(path.Path, old.Path, StringComparison.OrdinalIgnoreCase));
            if (current == null || !string.Equals(current.Name, old.Name, StringComparison.Ordinal))
            {
                config.RemoteArchiveIds.Remove(old.Path);
                config.LocationFingerprints.Remove(old.Path);
            }
        }
        config.SavePaths = validated;
        config.UploadOnGameStop = uploadOnStop;
        config.DownloadOnGameStart = downloadOnStart;
        store.Commit(gameId, config);
    }

    public Task UploadAsync(Game game, string apiUrl, string apiKey, bool onlyIfChanged = false, CancellationToken token = default(CancellationToken))
        => UploadAsync(game.Id, game.Name ?? "<unnamed>", apiUrl, apiKey, onlyIfChanged, token);

    internal Task UploadAsync(Guid gameId, string gameName, string apiUrl, string apiKey, bool onlyIfChanged = false, CancellationToken token = default(CancellationToken)) => RunAsync(async () =>
    {
        apiUrl = ApiConnection.ValidateBaseUrl(apiUrl);
        ApiConnection.ValidateKey(apiKey);
        var config = store.Snapshot(gameId);
        var locations = SaveArchive.ValidatePaths(config.SavePaths);
        if (config.SavePaths.Count == 0)
        {
            logger.Info($"No save paths configured for '{gameName}'.");
            return;
        }

        var remoteGameId = await ResolveGameId(apiUrl, apiKey, gameId).ConfigureAwait(false);
        if (remoteGameId == Guid.Empty)
            throw new InvalidOperationException($"'{gameName}' is not linked to an Unnamed Tracking game yet.");

        var archivesJson = await SendText(
            apiUrl.TrimEnd('/') + $"/api/game/{remoteGameId}/archives/save",
            apiKey,
            "GET").ConfigureAwait(false);
        var remoteArchives = Deserialize<List<SaveRemoteArchive>>(archivesJson) ?? throw new InvalidDataException("The server returned a null archive list.");

        ValidateArchives(remoteArchives);
        var uploadedFiles = 0;
        foreach (var location in locations)
        {
            ApiConnection.Token.ThrowIfCancellationRequested();
            var files = CollectFiles(new[] { location.Path });
            if (files.Count == 0)
            {
                logger.Info($"No save files found for '{location.Name}' at '{location.Path}'.");
                continue;
            }

            var archiveId = ResolveArchiveId(config, location, remoteArchives);
            var fingerprint = FingerprintLocation(location);
            if (onlyIfChanged && archiveId != Guid.Empty &&
                config.LocationFingerprints.TryGetValue(location.Path, out var previousFingerprint) &&
                string.Equals(previousFingerprint, fingerprint, StringComparison.Ordinal))
            {
                logger.Info($"No changes detected for save location '{location.Name}'; automatic upload skipped.");
                continue;
            }

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
                if (!string.Equals(fingerprint, FingerprintLocation(location), StringComparison.Ordinal))
                    throw new IOException("Save files changed while the archive was being created. Stop the game and retry.");

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
                store.Commit(gameId, config);
                uploadedFiles += files.Count;
                logger.Info($"Uploaded {files.Count} save file(s) for '{location.Name}'.");
            }
            finally
            {
                TryDelete(zip);
            }
        }

        store.Commit(gameId, config);
        if (uploadedFiles == 0)
            logger.Info($"No changed save locations were uploaded for '{gameName}'.");
    }, token);

    public Task DownloadAsync(Game game, string apiUrl, string apiKey, CancellationToken token = default(CancellationToken))
        => DownloadAsync(game.Id, game.Name ?? "<unnamed>", apiUrl, apiKey, token);

    internal Task DownloadAsync(Guid gameId, string gameName, string apiUrl, string apiKey, CancellationToken token = default(CancellationToken)) => RunAsync(async () =>
    {
        apiUrl = ApiConnection.ValidateBaseUrl(apiUrl);
        ApiConnection.ValidateKey(apiKey);
        var config = store.Snapshot(gameId);
        var locations = SaveArchive.ValidatePaths(config.SavePaths);
        if (config.SavePaths.Count == 0) return;

        var remoteGameId = await ResolveGameId(apiUrl, apiKey, gameId).ConfigureAwait(false);
        if (remoteGameId == Guid.Empty)
            throw new InvalidOperationException($"'{gameName}' is not linked to an Unnamed Tracking game yet.");

        var archiveJson = await SendText(
            apiUrl.TrimEnd('/') + $"/api/game/{remoteGameId}/archives/save",
            apiKey,
            "GET").ConfigureAwait(false);
        var archives = Deserialize<List<SaveRemoteArchive>>(archiveJson) ?? throw new InvalidDataException("The server returned a null archive list.");

        ValidateArchives(archives);
        foreach (var location in locations)
        {
            ApiConnection.Token.ThrowIfCancellationRequested();
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
                await DownloadToFile(ApiConnection.DownloadUrl(apiUrl, version.Url, remoteGameId, archiveId, version.Id), apiKey, zip).ConfigureAwait(false);
                var backup = SaveArchive.Restore(zip, location, Path.Combine(backupDirectory, gameId.ToString("N")), ApiConnection.Token);
                logger.Info("Save originals and recovery manifest retained in " + backup);
                config.LocationFingerprints[location.Path] = FingerprintLocation(location);
                store.Commit(gameId, config);
                logger.Info($"Downloaded latest cloud save for '{location.Name}'.");
            }
            finally
            {
                TryDelete(zip);
            }
        }

        store.Commit(gameId, config);
    }, token);

    private static List<string> CollectFiles(IEnumerable<string> roots)
        => roots.SelectMany(root => SaveArchive.CollectFiles(Environment.ExpandEnvironmentVariables(root)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static void CreateZip(SavePathEntry item, string zip)
    {
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var root = Environment.ExpandEnvironmentVariables(item.Path);
            foreach (var file in CollectFiles(new[] { root }))
            {
                ApiConnection.Token.ThrowIfCancellationRequested();
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

    private static string FingerprintLocation(SavePathEntry item)
    {
        using (var sha = SHA256.Create())
        {
            var lines = new List<string>();
            foreach (var file in CollectFiles(new[] { item.Path }).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(file);
                ApiConnection.Token.ThrowIfCancellationRequested();
                using var content = File.OpenRead(file);
                lines.Add(item.Name + "|" + file + "|" + info.Length + "|" + Convert.ToBase64String(sha.ComputeHash(content)));
            }
            return Convert.ToBase64String(
                sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
        }
    }

    internal SaveSyncStatus GetStatus(SaveGameConfiguration config)
    {
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
        var matches = new List<SaveRemoteGame>();
        var seen = new HashSet<Guid>();
        for (var skip = 0; ; skip += 200)
        {
            ApiConnection.Token.ThrowIfCancellationRequested();
            var response = await SendText(apiUrl.TrimEnd('/') + $"/api/game/list?skip={skip}&limit=200", apiKey, "GET").ConfigureAwait(false);
            var games = Deserialize<List<SaveRemoteGame>>(response);
            if (games == null || games.Any(game => game == null || game.Id == Guid.Empty || !seen.Add(game.Id)))
                throw new InvalidDataException("The server returned an invalid or repeated game list.");
            matches.AddRange(games.Where(game => game.PlayniteGuid == playniteGuid));
            if (games.Count < 200) break;
        }
        if (matches.Count > 1) throw new InvalidDataException("More than one remote game has this Playnite GUID.");
        return matches.SingleOrDefault()?.Id ?? Guid.Empty;
    }

    private static void ValidateArchives(List<SaveRemoteArchive> archives)
    {
        if (archives.Any(archive => archive == null || archive.Id == Guid.Empty || string.IsNullOrWhiteSpace(archive.Name) ||
            archive.Versions == null || archive.Versions.Any(version => version == null || version.Id == Guid.Empty || string.IsNullOrWhiteSpace(version.Url))) ||
            archives.Select(archive => archive.Id).Distinct().Count() != archives.Count)
            throw new InvalidDataException("The server returned an invalid save archive list.");
    }

    private static Guid ResolveArchiveId(
        SaveGameConfiguration config,
        SavePathEntry location,
        IEnumerable<SaveRemoteArchive> archives)
    {
        if (config.RemoteArchiveIds.TryGetValue(location.Path, out var storedId))
        {
            if (archives.Any(archive => archive.Id == storedId)) return storedId;
            config.RemoteArchiveIds.Remove(location.Path);
        }

        var matches = archives.Where(x => string.Equals(x.Name, location.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1) throw new InvalidDataException("Multiple remote save archives share this location name.");
        return matches.SingleOrDefault()?.Id ?? Guid.Empty;
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

        var request = ApiConnection.CreateRequest(
            apiUrl.TrimEnd('/') + $"/api/game/{gameId}/archives/save");
        using var registration = ApiConnection.RegisterRequest(request);
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
                while ((read = await input.ReadAsync(buffer, 0, buffer.Length, ApiConnection.Token).ConfigureAwait(false)) > 0)
                    await requestStream.WriteAsync(buffer, 0, read).ConfigureAwait(false);
            }
            await requestStream.WriteAsync(tail, 0, tail.Length).ConfigureAwait(false);
        }

        using (var response = ApiConnection.EnsureSuccess((HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false)))
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
        byte[] body;
        using (var memory = new MemoryStream())
        {
            new DataContractJsonSerializer(typeof(SaveArchiveName)).WriteObject(memory, new SaveArchiveName { Name = name });
            body = memory.ToArray();
        }

        var request = ApiConnection.CreateRequest(
            apiUrl.TrimEnd('/') + $"/api/game/{gameId}/archives/{archiveId}");
        using var registration = ApiConnection.RegisterRequest(request);
        request.Method = "PATCH";
        request.Headers[HttpRequestHeader.Authorization] = "Bearer " + key;
        request.ContentType = "application/json";
        request.ContentLength = body.LongLength;

        using (var requestStream = await request.GetRequestStreamAsync().ConfigureAwait(false))
            await requestStream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);

        using (var response = ApiConnection.EnsureSuccess((HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false)))
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

        var request = ApiConnection.CreateRequest(
            apiUrl.TrimEnd('/') + $"/api/game/{gameId}/archives/{archiveId}/versions");
        using var registration = ApiConnection.RegisterRequest(request);
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
                while ((read = await input.ReadAsync(buffer, 0, buffer.Length, ApiConnection.Token).ConfigureAwait(false)) > 0)
                    await requestStream.WriteAsync(buffer, 0, read).ConfigureAwait(false);
            }
            await requestStream.WriteAsync(tail, 0, tail.Length).ConfigureAwait(false);
        }

        using (var response = ApiConnection.EnsureSuccess((HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false)))
        using (var stream = response.GetResponseStream() ?? Stream.Null)
        {
            await stream.CopyToAsync(Stream.Null).ConfigureAwait(false);
        }
    }

    private static async Task DownloadToFile(string url, string key, string destination)
    {
        var request = ApiConnection.CreateRequest(url);
        using var registration = ApiConnection.RegisterRequest(request);
        request.Method = "GET";
        request.Headers[HttpRequestHeader.Authorization] = "Bearer " + key;
        using (var response = ApiConnection.EnsureSuccess((HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false)))
        using (var stream = response.GetResponseStream() ?? Stream.Null)
        using (var output = File.Create(destination))
        {
            await stream.CopyToAsync(output, 81920, ApiConnection.Token).ConfigureAwait(false);
        }
    }

    private static async Task<string> SendText(string url, string key, string method)
    {
        var bytes = await SendBytes(url, key, method, new byte[0], "application/json").ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    private static async Task<byte[]> SendBytes(string url, string key, string method, byte[] body, string contentType)
    {
        var request = ApiConnection.CreateRequest(url);
        using var registration = ApiConnection.RegisterRequest(request);
        request.Method = method;
        request.Headers[HttpRequestHeader.Authorization] = "Bearer " + key;
        request.ContentLength = body.Length;
        if (body.Length > 0)
        {
            request.ContentType = contentType;
            using (var stream = await request.GetRequestStreamAsync().ConfigureAwait(false))
                await stream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
        }

        using (var response = ApiConnection.EnsureSuccess((HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false)))
        using (var stream = response.GetResponseStream() ?? Stream.Null)
        using (var memory = new MemoryStream())
        {
            await stream.CopyToAsync(memory).ConfigureAwait(false);
            return memory.ToArray();
        }
    }

    private static T Deserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidDataException("The server returned an empty JSON response.");
        if (typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(List<>) && !json.TrimStart().StartsWith("[", StringComparison.Ordinal))
            throw new InvalidDataException("The server returned an object where a JSON array was required.");
        var serializer = new DataContractJsonSerializer(typeof(T));
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            return (T)serializer.ReadObject(stream)!;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
