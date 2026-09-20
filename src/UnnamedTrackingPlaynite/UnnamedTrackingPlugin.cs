using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace UnnamedTrackingPlaynite;

public sealed class UnnamedTrackingPlugin : GenericPlugin
{
    public override Guid Id { get; } = Guid.Parse("4A8D9C7B-2E54-4A6B-9C6D-0A2B8F1E7D43");

    private readonly ILogger _logger;
    private readonly UnnamedTrackingSyncClient _syncClient;
    private CancellationTokenSource? syncCancellation;

    public UnnamedTrackingSettings Settings { get; }

    public UnnamedTrackingPlugin(IPlayniteAPI api) : base(api)
    {
        Properties = new GenericPluginProperties
        {
            HasSettings = true
        };

        _logger = LogManager.GetLogger();
        _syncClient = new UnnamedTrackingSyncClient(_logger, api);
        Settings = new UnnamedTrackingSettings(this);
    }

    public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
    {
        yield return new MainMenuItem
        {
            Description = "Sync library to Unnamed Tracking",
            MenuSection = "@",
            Action = args => { _ = SyncLibraryAsync(PlayniteApi.Database.Games.ToList()); }
        };
    }

    public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
    {
        yield return new GameMenuItem
        {
            Description = "Sync selected games to Unnamed Tracking",
            Action = menuArgs => { _ = SyncLibraryAsync(menuArgs.Games.ToList()); }
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
            PlayniteApi.Database.Games.ToList(),
            Settings.IgnoreTag);
    }

    public Task<UnnamedTrackingSyncPreviewResult> PreviewLibraryAsync()
    {
        return _syncClient.PreviewLibraryAsync(Settings.ApiUrl, Settings.AuthValue, PlayniteApi.Database.Games.ToList(), Settings.IgnoreTag);
    }

    public void CancelSync()
    {
        syncCancellation?.Cancel();
    }

    public Task<bool> TestConnectionAsync()
    {
        return _syncClient.TestConnectionAsync(Settings.ApiUrl, Settings.AuthValue);
    }

    public override void OnGameStopped(OnGameStoppedEventArgs args)
    {
        var game = args?.Game;
        if (!Settings.SyncOnGameStopped || game == null) return;

        _ = SyncStoppedGameAsync(game);
    }

    private async Task SyncStoppedGameAsync(Game game)
    {
        try
        {
            await _syncClient.UpdateGameAsync(Settings.ApiUrl, Settings.AuthValue, game).ConfigureAwait(false);
            _logger.Info($"Synced stopped game '{game.Name}' ({game.Id}) to Unnamed Tracking.");
        }
        catch (Exception ex)
        {
            _logger.Error($"Could not sync stopped game '{game.Name}' ({game.Id}): {ex}");
        }
    }

    public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
    {
        _logger.Info("Unnamed Tracking plugin loaded.");

        if (Settings.SyncOnStartup && !string.IsNullOrWhiteSpace(Settings.ApiUrl) && !string.IsNullOrWhiteSpace(Settings.AuthValue))
        {
            var games = PlayniteApi.Database.Games.ToList();
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await _syncClient.UploadLibraryAsync(Settings.ApiUrl, Settings.AuthValue, games, Settings.IgnoreTag).ConfigureAwait(false);
                    _logger.Info($"Automatic Unnamed Tracking startup sync finished: {result.SucceededGames}/{result.TotalGames} succeeded, {result.FailedGames} failed, {result.WarningCount} warnings.");
                }
                catch (Exception ex)
                {
                    _logger.Error($"Automatic Unnamed Tracking startup sync failed: {ex}");
                }
            });
        }
    }

    private async Task SyncLibraryAsync(System.Collections.Generic.IEnumerable<Game> games)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Settings.ApiUrl) || string.IsNullOrWhiteSpace(Settings.AuthValue))
            {
                PlayniteApi.Dialogs.ShowErrorMessage("Configure the Unnamed Tracking API URL and API key in Add-ons > Extension settings first.", "Unnamed Tracking");
                return;
            }

            syncCancellation?.Dispose();
            syncCancellation = new CancellationTokenSource();
            var result = await _syncClient.UploadLibraryAsync(Settings.ApiUrl, Settings.AuthValue, games, Settings.IgnoreTag, syncCancellation.Token).ConfigureAwait(true);
            var message = $"Library sync complete. {result.SucceededGames}/{result.TotalGames} games succeeded.";
            if (result.FailedGames > 0) message += $" {result.FailedGames} failed.";
            if (result.WarningCount > 0) message += $" {result.WarningCount} artwork warnings.";
            syncCancellation?.Dispose();
            syncCancellation = null;
            PlayniteApi.Dialogs.ShowMessage(message, "Unnamed Tracking");
        }
        catch (Exception ex)
        {
            _logger.Error($"Manual Unnamed Tracking sync failed: {ex}");
            PlayniteApi.Dialogs.ShowErrorMessage(ex.Message, "Unnamed Tracking");
        }
    }
}
