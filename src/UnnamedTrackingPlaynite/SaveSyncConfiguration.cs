using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using Playnite.SDK;

namespace UnnamedTrackingPlaynite;

[DataContract]
public sealed class SavePathEntry
{
    [DataMember(Name = "name")]
    public string Name { get; set; } = "";
    [DataMember(Name = "is_file")]
    public bool IsFile { get; set; }
    [DataMember(Name = "path")]
    public string Path { get; set; } = "";
}

[DataContract]
internal sealed class SaveGameConfiguration
{
    [DataMember(Name = "save_paths")]
    public List<SavePathEntry> SavePaths { get; set; } = new List<SavePathEntry>();
    [DataMember(Name = "upload_on_game_stop")]
    public bool UploadOnGameStop { get; set; }
    [DataMember(Name = "download_on_game_start")]
    public bool DownloadOnGameStart { get; set; }
    // Remote archive ids are keyed by the configured local path so each save
    // location keeps one durable archive and future uploads become versions.
    [DataMember(Name = "remote_archive_ids")]
    public Dictionary<string, Guid> RemoteArchiveIds { get; set; } = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
    [DataMember(Name = "location_fingerprints")]
    public Dictionary<string, string> LocationFingerprints { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

[DataContract]
internal sealed class SaveSyncConfiguration
{
    [DataMember(Name = "games")]
    public Dictionary<Guid, SaveGameConfiguration> Games { get; set; } = new Dictionary<Guid, SaveGameConfiguration>();
}

internal sealed class SaveSyncStore
{
    private readonly string path;
    private readonly object syncRoot = new object();
    private readonly ILogger logger;
    public SaveSyncConfiguration Data { get; private set; }

    public SaveSyncStore(string directory, ILogger logger)
    {
        this.logger = logger;
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "save-sync.json");
        Data = Load();
    }

    private SaveGameConfiguration For(Guid gameId)
    {
        if (Data.Games == null)
            Data.Games = new Dictionary<Guid, SaveGameConfiguration>();

        if (!Data.Games.TryGetValue(gameId, out var config) || config == null)
        {
            config = new SaveGameConfiguration();
            Data.Games[gameId] = config;
        }

        // DataContractJsonSerializer does not apply property initializers when
        // a member is absent/null in an older save-sync.json. Normalize those
        // collections before any dashboard or sync code touches them.
        config.SavePaths ??= new List<SavePathEntry>();
        config.SavePaths.RemoveAll(entry => entry == null);
        config.RemoteArchiveIds = new Dictionary<string, Guid>(config.RemoteArchiveIds ?? new Dictionary<string, Guid>(), StringComparer.OrdinalIgnoreCase);
        config.LocationFingerprints = new Dictionary<string, string>(config.LocationFingerprints ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        return config;
    }

    public SaveGameConfiguration Snapshot(Guid gameId)
    {
        lock (syncRoot)
        {
            var config = For(gameId);
            return new SaveGameConfiguration
            {
                SavePaths = config.SavePaths.ConvertAll(entry => new SavePathEntry { Name = entry.Name, Path = entry.Path, IsFile = entry.IsFile }),
                UploadOnGameStop = config.UploadOnGameStop,
                DownloadOnGameStart = config.DownloadOnGameStart,
                RemoteArchiveIds = new Dictionary<string, Guid>(config.RemoteArchiveIds, StringComparer.OrdinalIgnoreCase),
                LocationFingerprints = new Dictionary<string, string>(config.LocationFingerprints, StringComparer.OrdinalIgnoreCase)
            };
        }
    }

    public void Commit(Guid gameId, SaveGameConfiguration config)
    {
        lock (syncRoot) { Data.Games[gameId] = config; Save(); }
    }

    private void Save()
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var serializer = new DataContractJsonSerializer(typeof(SaveSyncConfiguration));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                serializer.WriteObject(stream, Data);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        catch (Exception ex)
        {
            logger.Error($"Could not save local save-sync configuration: {ex.Message}");
            throw new IOException("Could not persist save-sync configuration. Check the extension data directory.", ex);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private SaveSyncConfiguration Load()
    {
        try
        {
            if (File.Exists(path))
            {
                var serializer = new DataContractJsonSerializer(typeof(SaveSyncConfiguration));
                using (var stream = File.OpenRead(path))
                    return (SaveSyncConfiguration?)serializer.ReadObject(stream) ?? new SaveSyncConfiguration();
            }
        }
        catch (Exception ex)
        {
            logger.Error($"Could not load local save-sync configuration; preserving the corrupt file: {ex.Message}");
            File.Copy(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N"), false);
        }
        return new SaveSyncConfiguration();
    }
}
