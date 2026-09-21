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
    private readonly ILogger logger;
    public SaveSyncConfiguration Data { get; private set; }

    public SaveSyncStore(string directory, ILogger logger)
    {
        this.logger = logger;
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "save-sync.json");
        Data = Load();
    }

    public SaveGameConfiguration For(Guid gameId)
    {
        if (!Data.Games.TryGetValue(gameId, out var config))
        {
            config = new SaveGameConfiguration();
            Data.Games[gameId] = config;
        }
        return config;
    }

    public void Save()
    {
        try
        {
            var serializer = new DataContractJsonSerializer(typeof(SaveSyncConfiguration));
            using (var stream = File.Create(path))
                serializer.WriteObject(stream, Data);
        }
        catch (Exception ex)
        {
            logger.Error($"Could not save local save-sync configuration: {ex}");
        }
    }

    private SaveSyncConfiguration Load()
    {
        try
        {
            if (File.Exists(path))
            {
                var serializer = new DataContractJsonSerializer(typeof(SaveSyncConfiguration));
                using (var stream = File.OpenRead(path))
                    return (SaveSyncConfiguration)serializer.ReadObject(stream);
            }
        }
        catch (Exception ex)
        {
            logger.Error($"Could not load local save-sync configuration; using defaults: {ex}");
        }
        return new SaveSyncConfiguration();
    }
}
