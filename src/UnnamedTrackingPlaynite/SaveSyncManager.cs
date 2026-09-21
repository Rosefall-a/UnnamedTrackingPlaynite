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
using Playnite.SDK;
using Playnite.SDK.Models;

namespace UnnamedTrackingPlaynite;

[DataContract]
internal sealed class SaveRemoteGame { [DataMember(Name="id")] public Guid Id { get; set; } [DataMember(Name="playnite_guid")] public Guid? PlayniteGuid { get; set; } }

[DataContract]
internal sealed class SaveRemoteArchive { [DataMember(Name="id")] public Guid Id { get; set; } [DataMember(Name="name")] public string Name { get; set; } = ""; [DataMember(Name="updated_at")] public long UpdatedAt { get; set; } [DataMember(Name="versions")] public List<SaveRemoteVersion> Versions { get; set; } = new List<SaveRemoteVersion>(); }

[DataContract]
internal sealed class SaveRemoteVersion { [DataMember(Name="id")] public Guid Id { get; set; } [DataMember(Name="filename")] public string Filename { get; set; } = ""; [DataMember(Name="uploaded_at")] public long UploadedAt { get; set; } [DataMember(Name="url")] public string Url { get; set; } = ""; }

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

    public void SaveConfiguration(Guid gameId, IEnumerable<string> paths, bool uploadOnStop, bool downloadOnStart)
    {
        var c = store.For(gameId);
        c.SavePaths = paths.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Environment.ExpandEnvironmentVariables).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        c.UploadOnGameStop = uploadOnStop;
        c.DownloadOnGameStart = downloadOnStart;
        store.Save();
    }

    public Task UploadAsync(Game game, string apiUrl, string apiKey) => Task.Run(async () =>
    {
        var c = store.For(game.Id);
        if (c.SavePaths.Count == 0) { logger.Info($"No save paths configured for '{game.Name}'."); return; }
        var files = CollectFiles(c.SavePaths);
        if (files.Count == 0) { logger.Info($"No save files found for '{game.Name}'."); return; }
        var remoteGameId = await ResolveGameId(apiUrl, apiKey, game.Id).ConfigureAwait(false);
        if (remoteGameId == Guid.Empty) throw new InvalidOperationException($"'{game.Name}' is not linked to an Unnamed Tracking game yet.");
        var zip = Path.Combine(Path.GetTempPath(), "unnamed-tracking-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            CreateZip(files, zip);
            await UploadArchive(apiUrl, apiKey, remoteGameId, game.Name, zip).ConfigureAwait(false);
            logger.Info($"Uploaded {files.Count} save file(s) for '{game.Name}'.");
        }
        finally { TryDelete(zip); }
    });

    public Task DownloadAsync(Game game, string apiUrl, string apiKey) => Task.Run(async () =>
    {
        var c = store.For(game.Id);
        if (c.SavePaths.Count == 0) return;
        var remoteGameId = await ResolveGameId(apiUrl, apiKey, game.Id).ConfigureAwait(false);
        if (remoteGameId == Guid.Empty) throw new InvalidOperationException($"'{game.Name}' is not linked to an Unnamed Tracking game yet.");
        var archives = Deserialize<List<SaveRemoteArchive>>(await Send(apiUrl.TrimEnd('/') + $"/api/game/{remoteGameId}/archives/save", apiKey, "GET", (string)null).ConfigureAwait(false)) ?? new List<SaveRemoteArchive>();
        var archive = archives.OrderByDescending(x => x.UpdatedAt).FirstOrDefault(x => x.Versions != null && x.Versions.Count > 0);
        var version = archive?.Versions?.OrderByDescending(x => x.UploadedAt).FirstOrDefault();
        if (version == null) { logger.Info($"No cloud save exists for '{game.Name}'."); return; }
        var zip = Path.Combine(Path.GetTempPath(), "unnamed-tracking-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var bytes = await Download(apiUrl.TrimEnd('/') + version.Url, apiKey).ConfigureAwait(false);
            File.WriteAllBytes(zip, bytes);
            BackupExisting(c.SavePaths, game.Id);
            ExtractZip(zip, c.SavePaths);
            logger.Info($"Downloaded latest cloud save for '{game.Name}'.");
        }
        finally { TryDelete(zip); }
    });

    private static List<string> CollectFiles(IEnumerable<string> roots)
    {
        var result = new List<string>();
        foreach (var root in roots.Select(Environment.ExpandEnvironmentVariables))
        {
            if (File.Exists(root)) result.Add(root);
            else if (Directory.Exists(root)) result.AddRange(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void CreateZip(List<string> files, string zip)
    {
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var roots = files.Select(Path.GetDirectoryName).Where(x => x != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var file in files)
            {
                var root = roots.FirstOrDefault(x => file.StartsWith(x + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) ?? Path.GetDirectoryName(file);
                var entry = root == null ? Path.GetFileName(file) : file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                archive.CreateEntryFromFile(file, entry, CompressionLevel.Optimal);
            }
        }
    }

    private static void ExtractZip(string zip, IEnumerable<string> roots)
    {
        var target = roots.Select(Environment.ExpandEnvironmentVariables).FirstOrDefault(Directory.Exists) ?? roots.Select(Environment.ExpandEnvironmentVariables).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(target)) return;
        Directory.CreateDirectory(target);
        using (var archive = ZipFile.OpenRead(zip))
        {
            foreach (var entry in archive.Entries)
            {
                var destination = Path.GetFullPath(Path.Combine(target, entry.FullName));
                if (!destination.StartsWith(Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe save archive path.");
                if (string.IsNullOrEmpty(entry.Name)) Directory.CreateDirectory(destination);
                else { Directory.CreateDirectory(Path.GetDirectoryName(destination)); entry.ExtractToFile(destination, true); }
            }
        }
    }

    private static void BackupExisting(IEnumerable<string> roots, Guid gameId)
    {
        var files = CollectFiles(roots);
        if (files.Count == 0) return;
        var backupRoot = Path.Combine(Path.GetTempPath(), "UnnamedTrackingSaveBackups", gameId.ToString("N"), DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        foreach (var file in files)
        {
            var dest = Path.Combine(backupRoot, Path.GetFileName(file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            File.Copy(file, dest, true);
        }
    }

    private async Task<Guid> ResolveGameId(string apiUrl, string apiKey, Guid playniteGuid)
    {
        var response = await Send(apiUrl.TrimEnd('/') + "/api/game/list?skip=0&limit=200", apiKey, "GET", null).ConfigureAwait(false);
        var games = Deserialize<List<SaveRemoteGame>>(response) ?? new List<SaveRemoteGame>();
        return games.FirstOrDefault(x => x.PlayniteGuid == playniteGuid)?.Id ?? Guid.Empty;
    }

    private static async Task UploadArchive(string apiUrl, string key, Guid gameId, string gameName, string zip)
    {
        var boundary = "----------------" + Guid.NewGuid().ToString("N");
        var bytes = File.ReadAllBytes(zip);
        var name = Uri.EscapeDataString(gameName);
        var header = Encoding.UTF8.GetBytes($"--{boundary}\r\nContent-Disposition: form-data; name=\"name\"\r\n\r\nPlaynite Save\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"playnite-save.zip\"\r\nContent-Type: application/zip\r\n\r\n");
        var tail = Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n");
        var body = new byte[header.Length + bytes.Length + tail.Length];
        Buffer.BlockCopy(header,0,body,0,header.Length); Buffer.BlockCopy(bytes,0,body,header.Length,bytes.Length); Buffer.BlockCopy(tail,0,body,header.Length+bytes.Length,tail.Length);
        await Send(apiUrl.TrimEnd('/') + $"/api/game/{gameId}/archives/save", key, "POST", body, "multipart/form-data; boundary=" + boundary).ConfigureAwait(false);
    }

    private static async Task<byte[]> Download(string url, string key)
    {
        var request = (HttpWebRequest)WebRequest.Create(url); request.Method = "GET"; request.Headers[HttpRequestHeader.Authorization] = "Bearer " + key;
        using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
        using (var stream = response.GetResponseStream())
        using (var memory = new MemoryStream()) { await stream.CopyToAsync(memory).ConfigureAwait(false); return memory.ToArray(); }
    }

    private static async Task<string> Send(string url, string key, string method, string body, string contentType = "application/json")
    {
        return Encoding.UTF8.GetString(await Send(url,key,method,body == null ? null : Encoding.UTF8.GetBytes(body),contentType).ConfigureAwait(false));
    }

    private static async Task<byte[]> Send(string url, string key, string method, byte[] body, string contentType = "application/json")
    {
        var request=(HttpWebRequest)WebRequest.Create(url); request.Method=method; request.Headers[HttpRequestHeader.Authorization]="Bearer "+key;
        if(body!=null){request.ContentType=contentType;request.ContentLength=body.Length;using(var s=await request.GetRequestStreamAsync().ConfigureAwait(false))await s.WriteAsync(body,0,body.Length).ConfigureAwait(false);}
        using(var response=(HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
        using(var stream=response.GetResponseStream()) using(var memory=new MemoryStream()){await stream.CopyToAsync(memory).ConfigureAwait(false);return memory.ToArray();}
    }

    private static T Deserialize<T>(string json) => Deserialize<T>(Encoding.UTF8.GetBytes(json));
    private static T Deserialize<T>(byte[] bytes){var serializer=new DataContractJsonSerializer(typeof(T));using(var stream=new MemoryStream(bytes))return (T)serializer.ReadObject(stream);}

    private static void TryDelete(string path){try{if(File.Exists(path))File.Delete(path);}catch{}}
}
