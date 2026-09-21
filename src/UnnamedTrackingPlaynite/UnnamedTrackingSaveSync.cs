using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualBasic;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace UnnamedTrackingPlaynite;

[DataContract]
internal sealed class PlayniteSaveGameConfig
{
    [DataMember(Name = "game_id")]
    public Guid GameId { get; set; }

    [DataMember(Name = "paths")]
    public List<string> Paths { get; set; } = new List<string>();

    [DataMember(Name = "auto_upload_on_close")]
    public bool AutoUploadOnClose { get; set; }

    [DataMember(Name = "auto_download_on_start")]
    public bool AutoDownloadOnStart { get; set; }

    [DataMember(Name = "archive_id")]
    public Guid? ArchiveId { get; set; }
}

[DataContract]
internal sealed class PlayniteSaveConfigFile
{
    [DataMember(Name = "games")]
    public List<PlayniteSaveGameConfig> Games { get; set; } = new List<PlayniteSaveGameConfig>();
}

[DataContract]
internal sealed class PlayniteSaveArchive
{
    [DataMember(Name = "id")]
    public Guid Id { get; set; }

    [DataMember(Name = "name")]
    public string Name { get; set; } = string.Empty;

    [DataMember(Name = "kind")]
    public string Kind { get; set; } = string.Empty;

    [DataMember(Name = "versions")]
    public List<PlayniteSaveArchiveVersion> Versions { get; set; } = new List<PlayniteSaveArchiveVersion>();
}

[DataContract]
internal sealed class PlayniteSaveArchiveVersion
{
    [DataMember(Name = "id")]
    public Guid Id { get; set; }

    [DataMember(Name = "filename")]
    public string Filename { get; set; } = string.Empty;

    [DataMember(Name = "uploaded_at")]
    public long UploadedAt { get; set; }

    [DataMember(Name = "download_url")]
    public string DownloadUrl { get; set; } = string.Empty;
}

internal sealed class PlayniteSaveSync
{
    private const string SaveKind = "save";
    private const string ArchiveName = "Playnite Save";
    private const string ConfigFileName = "save-sync.json";

    private readonly IPlayniteAPI playniteApi;
    private readonly ILogger logger;
    private readonly string configPath;
    private readonly Dictionary<Guid, PlayniteSaveGameConfig> configs = new Dictionary<Guid, PlayniteSaveGameConfig>();

    public PlayniteSaveSync(IPlayniteAPI playniteApi, ILogger logger, string dataPath)
    {
        this.playniteApi = playniteApi;
        this.logger = logger;
        Directory.CreateDirectory(dataPath);
        configPath = Path.Combine(dataPath, ConfigFileName);
        Load();
    }

    public PlayniteSaveGameConfig GetOrCreate(Game game)
    {
        if (!configs.TryGetValue(game.Id, out var config))
        {
            config = new PlayniteSaveGameConfig { GameId = game.Id };
            configs[game.Id] = config;
        }
        return config;
    }

    public void Configure(Game game)
    {
        var config = GetOrCreate(game);
        var current = string.Join(Environment.NewLine, config.Paths);
        var value = Interaction.InputBox(
            "Enter one local save file or directory per line.\r\nEnvironment variables such as %USERPROFILE% are supported.",
            "Unnamed Tracking - Save locations",
            current);

        if (value == null) return;

        config.Paths = value
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Environment.ExpandEnvironmentVariables(p.Trim().Trim('"')))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Save();

        var autoUpload = MessageBox.Show(
            "Automatically upload these saves when this game closes?",
            "Unnamed Tracking - Save synchronization",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (autoUpload == MessageBoxResult.Cancel) return;
        config.AutoUploadOnClose = autoUpload == MessageBoxResult.Yes;

        var autoDownload = MessageBox.Show(
            "Automatically download the latest cloud save when this game starts?",
            "Unnamed Tracking - Save synchronization",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        config.AutoDownloadOnStart = autoDownload == MessageBoxResult.Yes;
        Save();

        PlayniteApiNotification($"Save synchronization configured for {game.Name}.");
    }

    public async Task UploadAsync(Game game, string apiUrl, string authValue)
    {
        var config = GetOrCreate(game);
        if (config.Paths.Count == 0)
        {
            PlayniteApi.Dialogs.ShowMessage($"No save location is configured for {game.Name}.", "Unnamed Tracking");
            return;
        }

        var remoteGame = await FindRemoteGameAsync(apiUrl, authValue, game).ConfigureAwait(false);
        if (remoteGame == null)
        {
            logger.Warn($"Cannot upload save for '{game.Name}': no linked Unnamed Tracking game was found.");
            return;
        }

        var archive = await GetOrCreateArchiveAsync(apiUrl, authValue, remoteGame.Id, config).ConfigureAwait(false);
        var tempZip = Path.Combine(Path.GetTempPath(), "unnamed-tracking-save-" + Guid.NewGuid().ToString("N") + ".zip");

        try
        {
            CreateSaveArchive(tempZip, config.Paths);
            var filename = $"playnite-save-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";
            await UploadArchiveVersionAsync(apiUrl, authValue, remoteGame.Id, archive.Id, tempZip, filename).ConfigureAwait(false);
            config.ArchiveId = archive.Id;
            Save();
            logger.Info($"Uploaded Playnite save for '{game.Name}' to archive {archive.Id}.");
        }
        finally
        {
            try { File.Delete(tempZip); } catch { }
        }
    }

    public async Task DownloadAsync(Game game, string apiUrl, string authValue)
    {
        var config = GetOrCreate(game);
        if (config.Paths.Count == 0)
        {
            PlayniteApi.Dialogs.ShowMessage($"No save location is configured for {game.Name}.", "Unnamed Tracking");
            return;
        }

        var remoteGame = await FindRemoteGameAsync(apiUrl, authValue, game).ConfigureAwait(false);
        if (remoteGame == null) return;

        var archives = await GetArchivesAsync(apiUrl, authValue, remoteGame.Id).ConfigureAwait(false);
        var archive = config.ArchiveId.HasValue
            ? archives.FirstOrDefault(a => a.Id == config.ArchiveId.Value)
            : null;
        archive ??= archives.FirstOrDefault(a => string.Equals(a.Name, ArchiveName, StringComparison.OrdinalIgnoreCase));
        archive ??= archives.FirstOrDefault();

        var version = archive?.Versions?.OrderByDescending(v => v.UploadedAt).FirstOrDefault();
        if (version == null || string.IsNullOrWhiteSpace(version.DownloadUrl)) return;

        var tempZip = Path.Combine(Path.GetTempPath(), "unnamed-tracking-save-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await DownloadFileAsync(version.DownloadUrl, authValue, tempZip).ConfigureAwait(false);
            ExtractSaveArchive(tempZip, config.Paths);
            config.ArchiveId = archive.Id;
            Save();
            logger.Info($"Downloaded Playnite save for '{game.Name}' from archive {archive.Id}.");
        }
        finally
        {
            try { File.Delete(tempZip); } catch { }
        }
    }

    public async Task OnGameStartedAsync(Game game, string apiUrl, string authValue)
    {
        var config = GetOrCreate(game);
        if (!config.AutoDownloadOnStart) return;
        try { await DownloadAsync(game, apiUrl, authValue).ConfigureAwait(false); }
        catch (Exception ex) { logger.Error($"Automatic save download failed for '{game.Name}': {ex}"); }
    }

    public async Task OnGameStoppedAsync(Game game, string apiUrl, string authValue)
    {
        var config = GetOrCreate(game);
        if (!config.AutoUploadOnClose) return;
        try { await UploadAsync(game, apiUrl, authValue).ConfigureAwait(false); }
        catch (Exception ex) { logger.Error($"Automatic save upload failed for '{game.Name}': {ex}"); }
    }

    private async Task<UnnamedTrackingSyncExistingGame> FindRemoteGameAsync(string apiUrl, string authValue, Game game)
    {
        var client = new UnnamedTrackingSyncClient(logger, playniteApi);
        var games = await client.GetExistingGamesForSaveSyncAsync(apiUrl, authValue).ConfigureAwait(false);
        var folder = UnnamedTrackingSyncClient.GetFolderLocationForSaveSync(game);
        return games.FirstOrDefault(x => string.Equals(x.FolderLocation, folder, StringComparison.OrdinalIgnoreCase))
            ?? games.FirstOrDefault(x => x.PlayniteGuid == game.Id);
    }

    private async Task<PlayniteSaveArchive> GetOrCreateArchiveAsync(string apiUrl, string authValue, Guid gameId, PlayniteSaveGameConfig config)
    {
        var archives = await GetArchivesAsync(apiUrl, authValue, gameId).ConfigureAwait(false);
        var existing = config.ArchiveId.HasValue
            ? archives.FirstOrDefault(a => a.Id == config.ArchiveId.Value)
            : archives.FirstOrDefault(a => string.Equals(a.Name, ArchiveName, StringComparison.OrdinalIgnoreCase));

        if (existing != null) return existing;

        var response = await SendMultipartAsync(
            apiUrl.TrimEnd('/') + $"/api/game/{gameId}/archives/{SaveKind}",
            authValue,
            new Dictionary<string, string> { ["name"] = ArchiveName },
            null,
            null).ConfigureAwait(false);

        var created = Deserialize<PlayniteSaveArchive>(response);
        if (created == null || created.Id == Guid.Empty)
            throw new InvalidOperationException("Save archive creation did not return an archive ID.");

        return created;
    }

    private async Task<List<PlayniteSaveArchive>> GetArchivesAsync(string apiUrl, string authValue, Guid gameId)
    {
        var response = await SendJsonAsync(
            apiUrl.TrimEnd('/') + $"/api/game/{gameId}/archives/{SaveKind}",
            authValue,
            "GET",
            null).ConfigureAwait(false);
        return Deserialize<List<PlayniteSaveArchive>>(response) ?? new List<PlayniteSaveArchive>();
    }

    private async Task UploadArchiveVersionAsync(string apiUrl, string authValue, Guid gameId, Guid archiveId, string filePath, string filename)
    {
        await SendMultipartAsync(
            apiUrl.TrimEnd('/') + $"/api/game/{gameId}/archives/{archiveId}/versions",
            authValue,
            new Dictionary<string, string>(),
            filePath,
            filename).ConfigureAwait(false);
    }

    private static void CreateSaveArchive(string zipPath, IEnumerable<string> paths)
    {
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var index = 0;
            foreach (var rawPath in paths)
            {
                var path = Environment.ExpandEnvironmentVariables(rawPath);
                if (File.Exists(path))
                {
                    archive.CreateEntryFromFile(path, $"root{index}/{Path.GetFileName(path)}", CompressionLevel.Fastest);
                }
                else if (Directory.Exists(path))
                {
                    var root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        var relative = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        archive.CreateEntryFromFile(file, $"root{index}/{relative.Replace('\\', '/')}", CompressionLevel.Fastest);
                    }
                }
                index++;
            }
        }
    }

    private static void ExtractSaveArchive(string zipPath, IReadOnlyList<string> paths)
    {
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Name)) continue;
                var slash = entry.FullName.IndexOf('/');
                if (slash <= 4 || !entry.FullName.StartsWith("root", StringComparison.OrdinalIgnoreCase)) continue;

                if (!int.TryParse(entry.FullName.Substring(4, slash - 4), out var index) || index < 0 || index >= paths.Count) continue;
                var root = Environment.ExpandEnvironmentVariables(paths[index]);
                Directory.CreateDirectory(root);

                var relative = entry.FullName.Substring(slash + 1).Replace('/', Path.DirectorySeparatorChar);
                var destination = Path.GetFullPath(Path.Combine(root, relative));
                var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!destination.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                entry.ExtractToFile(destination, true);
            }
        }
    }

    private static async Task DownloadFileAsync(string url, string authValue, string destination)
    {
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "GET";
        request.Headers[HttpRequestHeader.Authorization] = "Bearer " + authValue;
        using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
        using (var input = response.GetResponseStream())
        using (var output = File.Create(destination))
        {
            await input.CopyToAsync(output).ConfigureAwait(false);
        }
    }

    private static async Task<string> SendJsonAsync(string endpoint, string authValue, string method, string body)
    {
        var request = (HttpWebRequest)WebRequest.Create(endpoint);
        request.Method = method;
        request.Accept = "application/json";
        request.Headers[HttpRequestHeader.Authorization] = "Bearer " + authValue;
        if (body != null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            request.ContentType = "application/json; charset=utf-8";
            request.ContentLength = bytes.Length;
            using (var stream = await request.GetRequestStreamAsync().ConfigureAwait(false))
                await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }

        using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
        using (var reader = new StreamReader(response.GetResponseStream() ?? Stream.Null, Encoding.UTF8))
            return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private static async Task<string> SendMultipartAsync(string endpoint, string authValue, Dictionary<string, string> fields, string filePath, string filename)
    {
        var boundary = "----UnnamedTracking" + Guid.NewGuid().ToString("N");
        var request = (HttpWebRequest)WebRequest.Create(endpoint);
        request.Method = "POST";
        request.Headers[HttpRequestHeader.Authorization] = "Bearer " + authValue;
        request.ContentType = "multipart/form-data; boundary=" + boundary;

        using (var stream = await request.GetRequestStreamAsync().ConfigureAwait(false))
        {
            foreach (var field in fields)
            {
                var header = $"--{boundary}\r\nContent-Disposition: form-data; name=\"{field.Key}\"\r\n\r\n{field.Value}\r\n";
                var bytes = Encoding.UTF8.GetBytes(header);
                await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(filePath))
            {
                var header = $"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{filename}\"\r\nContent-Type: application/zip\r\n\r\n";
                var bytes = Encoding.UTF8.GetBytes(header);
                await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                using (var file = File.OpenRead(filePath))
                    await file.CopyToAsync(stream).ConfigureAwait(false);
                var end = Encoding.UTF8.GetBytes("\r\n");
                await stream.WriteAsync(end, 0, end.Length).ConfigureAwait(false);
            }

            var closing = Encoding.UTF8.GetBytes($"--{boundary}--\r\n");
            await stream.WriteAsync(closing, 0, closing.Length).ConfigureAwait(false);
        }

        using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
        using (var reader = new StreamReader(response.GetResponseStream() ?? Stream.Null, Encoding.UTF8))
            return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private static T Deserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default(T);
        var serializer = new DataContractJsonSerializer(typeof(T));
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            return (T)serializer.ReadObject(stream);
    }

    private void Load()
    {
        if (!File.Exists(configPath)) return;
        try
        {
            var data = Deserialize<PlayniteSaveConfigFile>(File.ReadAllText(configPath));
            foreach (var item in data?.Games ?? new List<PlayniteSaveGameConfig>())
                configs[item.GameId] = item;
        }
        catch (Exception ex) { logger.Warn($"Could not load save synchronization settings: {ex.Message}"); }
    }

    private void Save()
    {
        var data = new PlayniteSaveConfigFile { Games = configs.Values.ToList() };
        var serializer = new DataContractJsonSerializer(typeof(PlayniteSaveConfigFile));
        using (var stream = new MemoryStream())
        {
            serializer.WriteObject(stream, data);
            File.WriteAllText(configPath, Encoding.UTF8.GetString(stream.ToArray()));
        }
    }

    private void PlayniteApiNotification(string message)
    {
        playniteApi.Notifications.Add(new NotificationMessage(
            "UnnamedTrackingPlaynite",
            message,
            NotificationType.Info));
    }
}
