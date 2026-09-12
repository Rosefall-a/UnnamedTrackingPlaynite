using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;

namespace UnnamedTrackingPlaynite;

public sealed class UnnamedTrackingPlugin : GenericPlugin
{
    public override Guid Id { get; } = Guid.Parse("4A8D9C7B-2E54-4A6B-9C6D-0A2B8F1E7D43");

    private readonly ILogger _logger;
    private readonly UnnamedTrackingSyncClient _syncClient;
    private readonly UnnamedTrackingAfkClient _afkClient;
    private readonly AfkTracker _afkTracker;
    private Guid? _activeGameId;

    public UnnamedTrackingSettings Settings { get; }

    public UnnamedTrackingPlugin(IPlayniteAPI api) : base(api)
    {
        Properties = new GenericPluginProperties
        {
            HasSettings = true
        };

        _logger = LogManager.GetLogger();
        _syncClient = new UnnamedTrackingSyncClient(_logger, api);
        _afkClient = new UnnamedTrackingAfkClient(_logger);
        _afkTracker = new AfkTracker();
        Settings = new UnnamedTrackingSettings(this);
    }

    public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
    {
        yield return new MainMenuItem
        {
            Description = "Unnamed Tracking",
            MenuSection = "@"
        };
    }

    public override ISettings GetSettings(bool firstRunSettings)
    {
        return Settings;
    }

    public override UserControl GetSettingsView(bool firstRunView)
    {
        return new UnnamedTrackingSettingsView();
    }

    public Task<UnnamedTrackingUploadResult> UploadLibraryAsync()
    {
        return _syncClient.UploadLibraryAsync(
            Settings.ApiUrl,
            Settings.AuthValue,
            PlayniteApi.Database.Games);
    }

    public override void OnGameStarted(OnGameStartedEventArgs args)
    {
        _activeGameId = args.Game.Id;
        _afkTracker.Start();
        _logger.Info($"Unnamed Tracking AFK tracking started for '{args.Game.Name}' ({args.Game.Id}).");
    }

    public override void OnGameStopped(OnGameStoppedEventArgs args)
    {
        var gameId = _activeGameId;
        _activeGameId = null;
        var afkSeconds = _afkTracker.Stop();
        if (afkSeconds <= 0 || !gameId.HasValue) return;

        var game = args.Game;
        _logger.Info($"Unnamed Tracking recorded {afkSeconds} seconds of AFK time for '{game.Name}' ({game.Id}).");
        _ = _afkClient.RecordAsync(Settings.ApiUrl, Settings.AuthValue, gameId.Value, afkSeconds);
    }

    public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
    {
        _logger.Info("Unnamed Tracking plugin loaded.");
    }

    public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
    {
        var gameId = _activeGameId;
        _activeGameId = null;
        var afkSeconds = _afkTracker.Stop();
        if (afkSeconds > 0 && gameId.HasValue)
        {
            _logger.Info($"Unnamed Tracking recorded {afkSeconds} seconds of AFK time while Playnite was shutting down.");
            _ = _afkClient.RecordAsync(Settings.ApiUrl, Settings.AuthValue, gameId.Value, afkSeconds);
        }
        _afkTracker.Dispose();
    }
}
