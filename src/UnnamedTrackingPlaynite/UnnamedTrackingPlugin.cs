using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
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
    private readonly SaveSyncManager _saveSync;
    private IWebView? _applicationView;

    public UnnamedTrackingSettings Settings { get; }

    public UnnamedTrackingPlugin(IPlayniteAPI api) : base(api)
    {
        Properties = new GenericPluginProperties
        {
            HasSettings = true
        };

        _logger = LogManager.GetLogger();
        _syncClient = new UnnamedTrackingSyncClient(_logger, api);
        _saveSync = new SaveSyncManager(GetPluginUserDataPath(), _logger);
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

    public override IEnumerable<SidebarItem> GetSidebarItems()
    {
        yield return new SidebarItem
        {
            Title = "Unnamed Tracking",
            Type = SiderbarItemType.View,
            Opened = OpenSaveSyncDashboard,
            Closed = () => { }
        };
    }

    private System.Windows.Controls.Control OpenSaveSyncDashboard() => new SaveSyncDashboard(PlayniteApi, _saveSync);

    private void OpenApplicationView()
    {
        if (_applicationView == null)
        {
            _applicationView = PlayniteApi.WebViews.CreateView(new WebViewSettings
            {
                JavaScriptEnabled = true,
                WindowWidth = 1280,
                WindowHeight = 800
            });
        }

        if (Uri.TryCreate(Settings.ApiUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri))
            _applicationView.Navigate(uri.ToString());
        _applicationView.Open();
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
            PlayniteApi.Database.Games.ToList());
    }

    public Task<bool> TestConnectionAsync()
    {
        return _syncClient.TestConnectionAsync(Settings.ApiUrl, Settings.AuthValue);
    }

    public override void OnGameStarting(OnGameStartingEventArgs args)
    {
        var game = args?.Game;
        if (game == null) return;
        var config = _saveSync.Configuration(game.Id);
        if (config.DownloadOnGameStart && HasCredentials)
            _ = RunSaveSyncAsync(() => _saveSync.DownloadAsync(game, Settings.ApiUrl, Settings.AuthValue), $"download save for '{game.Name}'");
    }

    public override void OnGameStopped(OnGameStoppedEventArgs args)
    {
        var game = args?.Game;
        if (game == null) return;

        if (Settings.SyncOnGameStopped)
            _ = SyncStoppedGameAsync(game);

        var config = _saveSync.Configuration(game.Id);
        if (config.UploadOnGameStop && HasCredentials)
            _ = RunSaveSyncAsync(() => _saveSync.UploadAsync(game, Settings.ApiUrl, Settings.AuthValue, true), $"upload save for '{game.Name}'");
    }

    private bool HasCredentials => !string.IsNullOrWhiteSpace(Settings.ApiUrl) && !string.IsNullOrWhiteSpace(Settings.AuthValue);

    private async Task RunSaveSyncAsync(Func<Task> action, string operation)
    {
        try { await action().ConfigureAwait(false); }
        catch (Exception ex) { _logger.Error($"Could not {operation}: {ex}"); }
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
                    var result = await _syncClient.UploadLibraryAsync(Settings.ApiUrl, Settings.AuthValue, games).ConfigureAwait(false);
                    _logger.Info($"Automatic Unnamed Tracking startup sync finished: {result.SucceededGames}/{result.TotalGames} succeeded, {result.FailedGames} failed, {result.WarningCount} warnings.");
                }
                catch (Exception ex)
                {
                    _logger.Error($"Automatic Unnamed Tracking startup sync failed: {ex}");
                }
            });
        }
    }

    private void ConfigureSaveGame(Game game)
    {
        var current = _saveSync.Configuration(game.Id);
        var dialog = new SavePathDialog(current.SavePaths, game.InstallDirectory, dir => PlayniteApi.Dialogs.SelectFolder(dir));
        var window = PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions
        {
            ShowMinimizeButton = false,
            ShowMaximizeButton = false,
            ShowCloseButton = true
        });
        window.Title = "Unnamed Tracking saves — " + game.Name;
        window.Content = dialog;
        window.Width = 680;
        window.Height = 360;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.Owner = PlayniteApi.Dialogs.GetCurrentAppWindow();
        window.ShowDialog();

        if (!dialog.Saved) return;
        _saveSync.SaveConfiguration(game.Id, dialog.Entries, current.UploadOnGameStop, current.DownloadOnGameStart);
        PlayniteApi.Dialogs.ShowMessage("Saved " + dialog.Entries.Length + " local save path(s) for " + game.Name + ".", "Unnamed Tracking");
    }

    private void ToggleSaveSync(Game game, bool upload, bool download)
    {
        var current = _saveSync.Configuration(game.Id);
        if (current.SavePaths.Count == 0)
        {
            ConfigureSaveGame(game);
            current = _saveSync.Configuration(game.Id);
        }
        _saveSync.SaveConfiguration(game.Id, current.SavePaths, upload, download);
        PlayniteApi.Dialogs.ShowMessage("Save sync updated for " + game.Name + ". Upload on stop: " + upload + ". Download on start: " + download + ".", "Unnamed Tracking");
    }

    private async Task SyncSaveNow(Game game, bool upload)
    {
        if (!HasCredentials)
        {
            PlayniteApi.Dialogs.ShowErrorMessage("Configure the Unnamed Tracking API URL and API key first.", "Unnamed Tracking");
            return;
        }
        try
        {
            if (upload) await _saveSync.UploadAsync(game, Settings.ApiUrl, Settings.AuthValue);
            else await _saveSync.DownloadAsync(game, Settings.ApiUrl, Settings.AuthValue);
            PlayniteApi.Dialogs.ShowMessage(upload ? "Save uploaded successfully." : "Latest cloud save downloaded successfully.", "Unnamed Tracking");
        }
        catch (Exception ex)
        {
            _logger.Error("Manual save sync failed for '" + game.Name + "': " + ex);
            PlayniteApi.Dialogs.ShowErrorMessage(ex.Message, "Unnamed Tracking");
        }
    }

    public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
    {
        foreach (var game in args.Games)
        {
            var config = _saveSync.Configuration(game.Id);
            yield return new GameMenuItem
            {
                Description = "Upload save now",
                Action = _ => { var ignored = SyncSaveNow(game, true); }
            };
            yield return new GameMenuItem
            {
                MenuSection = "Save sync",
                Description = "Configure save locations",
                Action = _ => ConfigureSaveGame(game)
            };
            yield return new GameMenuItem
            {
                MenuSection = "Save sync",
                Description = config.UploadOnGameStop ? "Disable upload on game stop" : "Enable upload on game stop",
                Action = _ => SetSaveSyncDirection(game, true, !config.UploadOnGameStop)
            };
            yield return new GameMenuItem
            {
                MenuSection = "Save sync",
                Description = config.DownloadOnGameStart ? "Disable download on game start" : "Enable download on game start",
                Action = _ => SetSaveSyncDirection(game, false, !config.DownloadOnGameStart)
            };
            yield return new GameMenuItem
            {
                MenuSection = "Save sync",
                Description = "Download latest save now",
                Action = _ => { var ignored = SyncSaveNow(game, false); }
            };
        }
    }

    private void SetSaveSyncDirection(Game game, bool upload, bool enabled)
    {
        var current = _saveSync.Configuration(game.Id);
        if (current.SavePaths.Count == 0)
        {
            ConfigureSaveGame(game);
            current = _saveSync.Configuration(game.Id);
        }

        _saveSync.SaveConfiguration(
            game.Id,
            current.SavePaths,
            upload ? enabled : current.UploadOnGameStop,
            upload ? current.DownloadOnGameStart : enabled);
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

            var result = await _syncClient.UploadLibraryAsync(Settings.ApiUrl, Settings.AuthValue, games).ConfigureAwait(true);
            var message = $"Library sync complete. {result.SucceededGames}/{result.TotalGames} games succeeded.";
            if (result.FailedGames > 0) message += $" {result.FailedGames} failed.";
            if (result.WarningCount > 0) message += $" {result.WarningCount} artwork warnings.";
            PlayniteApi.Dialogs.ShowMessage(message, "Unnamed Tracking");
        }
        catch (Exception ex)
        {
            _logger.Error($"Manual Unnamed Tracking sync failed: {ex}");
            PlayniteApi.Dialogs.ShowErrorMessage(ex.Message, "Unnamed Tracking");
        }
    }
}
