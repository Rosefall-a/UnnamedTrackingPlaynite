using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
    private CancellationTokenSource? syncCancellation;
    private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
    private CancellationTokenSource? saveCancellation;
    private readonly HashSet<Guid> preparedDownloads = new HashSet<Guid>();
    private readonly HashSet<Guid> pendingDownloads = new HashSet<Guid>();

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
        yield return new MainMenuItem { Description = "Cancel Unnamed Tracking synchronization", MenuSection = "@", Action = args => CancelSync() };
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

    private System.Windows.Controls.Control OpenSaveSyncDashboard() => new SaveSyncDashboard(
        PlayniteApi,
        _saveSync,
        ConfigureSaveGame,
        async (game, upload) => await SyncSaveNow(game, upload).ConfigureAwait(true),
        OpenApplicationView, CancelSync);

    private void OpenApplicationView()
    {
        try
        {
            var url = ApiConnection.ValidateBaseUrl(Settings.ApiUrl) + "/";
            PlayniteApi.Dialogs.ShowMessage("The embedded view uses the website's normal sign-in. Your API key authenticates synchronization only; it is never passed to the browser. If sign-in is unavailable here, use your normal browser.", "Unnamed Tracking");
            if (_applicationView == null)
                _applicationView = PlayniteApi.WebViews.CreateView(new WebViewSettings
                {
                    JavaScriptEnabled = true,
                    WindowWidth = 1280,
                    WindowHeight = 800
                });
            _applicationView.Navigate(url);
            _applicationView.Open();
        }
        catch (Exception ex)
        {
            _logger.Error("Could not open the embedded Unnamed Tracking view: " + ex.Message);
            PlayniteApi.Dialogs.ShowErrorMessage("The embedded view is unavailable. Check the server URL and open Unnamed Tracking in your normal browser. " + ex.Message, "Unnamed Tracking");
        }
    }

    public override ISettings GetSettings(bool firstRunSettings)
    {
        return Settings;
    }

    public override UserControl GetSettingsView(bool firstRunView)
    {
        return new UnnamedTrackingSettingsView();
    }

    private CancellationTokenSource BeginSync()
    {
        if (syncCancellation != null) throw new InvalidOperationException("A synchronization or preview is already running. Wait for it or cancel it first.");
        syncCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        return syncCancellation;
    }

    public Task<UnnamedTrackingUploadResult> UploadLibraryAsync(Action<int, int, string>? progress = null)
        => UploadGamesAsync(PlayniteApi.Database.Games.ToList(), progress);

    private async Task<UnnamedTrackingUploadResult> UploadGamesAsync(IEnumerable<Game> games, Action<int, int, string>? progress = null)
    {
        var cancellation = BeginSync();
        try
        {
            return await _syncClient.UploadLibraryAsync(Settings.ApiUrl, Settings.AuthValue, games,
                Settings.IgnoreTag, cancellation.Token, progress).ConfigureAwait(true);
        }
        finally { syncCancellation = null; cancellation.Dispose(); }
    }

    public async Task<UnnamedTrackingSyncPreviewResult> PreviewLibraryAsync()
    {
        var cancellation = BeginSync();
        try
        {
            return await _syncClient.PreviewLibraryAsync(Settings.ApiUrl, Settings.AuthValue,
                PlayniteApi.Database.Games.ToList(), Settings.IgnoreTag, cancellation.Token).ConfigureAwait(true);
        }
        finally { syncCancellation = null; cancellation.Dispose(); }
    }

    public void CancelSync()
    {
        syncCancellation?.Cancel();
        saveCancellation?.Cancel();
    }

    public Task<bool> TestConnectionAsync()
    {
        return _syncClient.TestConnectionAsync(Settings.ApiUrl, Settings.AuthValue, lifetime.Token);
    }

    public override void OnGameStarting(OnGameStartingEventArgs args)
    {
        var game = args?.Game;
        if (game == null) return;
        if (pendingDownloads.Contains(game.Id)) { args!.CancelStartup = true; Notify("Game launch paused while its saves are downloading."); return; }
        var config = _saveSync.Configuration(game.Id);
        if (!config.DownloadOnGameStart || config.SavePaths.Count == 0) return;
        if (UnnamedTrackingSyncClient.HasIgnoreTag(game, Settings.IgnoreTag)) return;
        if (preparedDownloads.Remove(game.Id)) return;
        args!.CancelStartup = true;
        if (!HasCredentials)
        {
            Notify("Game launch paused. Configure the Unnamed Tracking server URL and API key, or disable download on game start.", true);
            return;
        }
        if (!pendingDownloads.Add(game.Id)) return;
        if (saveCancellation != null || _saveSync.IsBusy) { pendingDownloads.Remove(game.Id); Notify("Game launch paused while another save synchronization is running. Try again when it finishes."); return; }
        var id = game.Id;
        var name = game.Name ?? "<unnamed>";
        _ = PrepareSavesAsync(id, name);
    }

    private async Task PrepareSavesAsync(Guid id, string name)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        saveCancellation = cancellation;
        Notify("Downloading saves for '" + name + "'. Launch is paused; Cancel in the Unnamed Tracking sidebar stops the download.");
        try
        {
            await _saveSync.DownloadAsync(id, name, Settings.ApiUrl, Settings.AuthValue, cancellation.Token).ConfigureAwait(true);
            if (lifetime.IsCancellationRequested) return;
            preparedDownloads.Add(id);
            Notify("Saves checked for '" + name + "'. Start the game again to play.");
        }
        catch (OperationCanceledException) { Notify("Save download cancelled. Game launch remains paused."); }
        catch (Exception ex) { Notify("Save download failed for '" + name + "': " + ex.Message + " Game launch remains paused.", true); }
        finally { pendingDownloads.Remove(id); if (saveCancellation == cancellation) saveCancellation = null; }
    }

    private void Notify(string text, bool error = false)
    {
        if (lifetime.IsCancellationRequested) return;
        PlayniteApi.MainView.UIDispatcher.BeginInvoke(new Action(() =>
        {
            if (!lifetime.IsCancellationRequested)
                PlayniteApi.Notifications.Add(new NotificationMessage("unnamed-tracking-sync", text, error ? NotificationType.Error : NotificationType.Info));
        }));
    }

    public override void OnGameStopped(OnGameStoppedEventArgs args)
    {
        var game = args?.Game;
        if (game == null) return;
        preparedDownloads.Remove(game.Id);
        if (UnnamedTrackingSyncClient.HasIgnoreTag(game, Settings.IgnoreTag)) return;

        if (Settings.SyncOnGameStopped)
            _ = SyncStoppedGameAsync(game);

        var config = _saveSync.Configuration(game.Id);
        if (config.UploadOnGameStop && HasCredentials)
            _ = RunSaveSyncAsync(() => _saveSync.UploadAsync(game, Settings.ApiUrl, Settings.AuthValue, true, saveCancellation?.Token ?? lifetime.Token), $"upload save for '{game.Name}'");
    }

    private bool HasCredentials => !string.IsNullOrWhiteSpace(Settings.ApiUrl) && !string.IsNullOrWhiteSpace(Settings.AuthValue);

    private async Task RunSaveSyncAsync(Func<Task> action, string operation)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        if (saveCancellation != null || _saveSync.IsBusy) { Notify("Skipped " + operation + ": another save synchronization is running.", true); return; }
        saveCancellation = cancellation;
        try { using (ApiConnection.UseCancellation(cancellation.Token)) await action().ConfigureAwait(true); }
        catch (OperationCanceledException) { Notify("Cancelled: " + operation); }
        catch (Exception ex) { _logger.Error($"Could not {operation}: {ex.Message}"); Notify("Could not " + operation + ": " + ex.Message, true); }
        finally { if (saveCancellation == cancellation) saveCancellation = null; }
    }

    private async Task SyncStoppedGameAsync(Game game)
    {
        var name = game.Name;
        var id = game.Id;
        try
        {
            var updated = await _syncClient.UpdateGameAsync(Settings.ApiUrl, Settings.AuthValue, game, Settings.IgnoreTag, lifetime.Token).ConfigureAwait(false);
            if (!updated) { _logger.Info("Game-stop sync skipped unlinked or ignored game: " + name); return; }
            _logger.Info($"Synced stopped game '{name}' ({id}) to Unnamed Tracking.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Notify("Game-stop synchronization failed: " + ex.Message, true);
            _logger.Error($"Could not sync stopped game '{name}' ({id}): {ex}");
        }
    }

    public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
    {
        _logger.Info("Unnamed Tracking plugin loaded.");

        if (Settings.SyncOnStartup && !string.IsNullOrWhiteSpace(Settings.ApiUrl) && !string.IsNullOrWhiteSpace(Settings.AuthValue))
        {
            _ = SyncStartupAsync();
        }
    }

    private async Task SyncStartupAsync()
    {
        try
        {
            var result = await UploadLibraryAsync().ConfigureAwait(true);
            Notify($"Startup sync: {result.SucceededGames}/{result.TotalGames} succeeded, {result.FailedGames} failed, {result.WarningCount} artwork warnings.", result.FailedGames > 0);
        }
        catch (OperationCanceledException) { Notify("Startup synchronization cancelled."); }
        catch (Exception ex) { Notify("Startup synchronization failed: " + ex.Message, true); }
    }

    public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
    {
        lifetime.Cancel();
        CancelSync();
        _saveSync.Cancel();
        try { _applicationView?.Dispose(); } catch (Exception ex) { _logger.Warn(ex.Message); }
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
        try { _saveSync.SaveConfiguration(game.Id, dialog.Entries, current.UploadOnGameStop, current.DownloadOnGameStart); }
        catch (Exception ex) { PlayniteApi.Dialogs.ShowErrorMessage(ex.Message, "Unnamed Tracking"); return; }
        preparedDownloads.Remove(game.Id);
        PlayniteApi.Dialogs.ShowMessage("Saved " + dialog.Entries.Length + " local save path(s) for " + game.Name + ".", "Unnamed Tracking");
    }

    private async Task SyncSaveNow(Game game, bool upload)
    {
        if (!HasCredentials)
        {
            PlayniteApi.Dialogs.ShowErrorMessage("Configure the Unnamed Tracking API URL and API key first.", "Unnamed Tracking");
            return;
        }

        if (_saveSync.Configuration(game.Id).SavePaths.Count == 0)
        {
            PlayniteApi.Dialogs.ShowErrorMessage("Configure at least one save location first.", "Unnamed Tracking");
            return;
        }
        if (!upload && game.IsRunning)
        {
            PlayniteApi.Dialogs.ShowErrorMessage("Stop the game before downloading its saves.", "Unnamed Tracking");
            return;
        }
        if (UnnamedTrackingSyncClient.HasIgnoreTag(game, Settings.IgnoreTag))
        {
            PlayniteApi.Dialogs.ShowMessage("This game has the configured ignore tag. Remove it before synchronizing.", "Unnamed Tracking");
            return;
        }
        if (saveCancellation != null || _saveSync.IsBusy)
        {
            PlayniteApi.Dialogs.ShowMessage("A save synchronization is already running. Wait or cancel it from the sidebar.", "Unnamed Tracking");
            return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        saveCancellation = cancellation;
        if (!upload) pendingDownloads.Add(game.Id);
        try
        {
            var linked = await _syncClient.IsGameLinkedAsync(Settings.ApiUrl, Settings.AuthValue, game.Id, cancellation.Token).ConfigureAwait(true);
            if (!linked)
            {
                if (!ConfirmSyncGame(game)) return;

                var result = await _syncClient.UploadLibraryAsync(
                    Settings.ApiUrl,
                    Settings.AuthValue,
                    new[] { game },
                    Settings.IgnoreTag, cancellation.Token).ConfigureAwait(true);

                if (result.SucceededGames != 1)
                {
                    var failure = result.Failures.FirstOrDefault();
                    var failureBody = failure?.ResponseBody;
                    throw new InvalidOperationException(
                        "Could not sync the game to Unnamed Tracking." +
                        (string.IsNullOrWhiteSpace(failureBody) ? string.Empty : " " + failureBody));
                }
            }

            if (upload) await _saveSync.UploadAsync(game, Settings.ApiUrl, Settings.AuthValue, false, cancellation.Token);
            else await _saveSync.DownloadAsync(game, Settings.ApiUrl, Settings.AuthValue, cancellation.Token);
            PlayniteApi.Dialogs.ShowMessage(upload ? "Save upload complete. Empty locations are skipped." : "Cloud save check complete. Available versions were restored; locations without a cloud version were skipped.", "Unnamed Tracking");
        }
        catch (OperationCanceledException) { PlayniteApi.Dialogs.ShowMessage("Save synchronization cancelled.", "Unnamed Tracking"); }
        catch (Exception ex)
        {
            _logger.Error("Manual save sync failed for '" + game.Name + "': " + ex);
            PlayniteApi.Dialogs.ShowErrorMessage(ex.Message, "Unnamed Tracking");
        }
        finally { if (!upload) pendingDownloads.Remove(game.Id); if (saveCancellation == cancellation) saveCancellation = null; }
    }

    private bool ConfirmSyncGame(Game game)
    {
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = "'" + game.Name + "' is not linked to an Unnamed Tracking game yet. Sync the game to Unnamed Tracking first, then continue with the save sync?",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        var sync = new Button { Content = "Sync game", MinWidth = 100, Padding = new Thickness(12, 6, 12, 6) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(sync);
        panel.Children.Add(buttons);

        var window = PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions
        {
            ShowMinimizeButton = false,
            ShowMaximizeButton = false,
            ShowCloseButton = true
        });
        window.Title = "Unnamed Tracking — Sync game";
        window.Content = panel;
        window.Width = 580;
        window.SizeToContent = SizeToContent.Height;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.Owner = PlayniteApi.Dialogs.GetCurrentAppWindow();

        var shouldSync = false;
        sync.Click += (sender, args) => { shouldSync = true; window.DialogResult = true; };
        cancel.Click += (sender, args) => { shouldSync = false; window.DialogResult = false; };
        window.ShowDialog();
        return shouldSync;
    }

    public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
    {
        yield return new GameMenuItem
        {
            MenuSection = "Unnamed Tracking",
            Description = "Sync selected games to Unnamed Tracking",
            Action = menuArgs => { _ = SyncLibraryAsync(menuArgs.Games.ToList()); }
        };
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
        if (enabled && current.SavePaths.Count == 0)
        {
            ConfigureSaveGame(game);
            current = _saveSync.Configuration(game.Id);
            if (current.SavePaths.Count == 0) return;
        }

        try
        {
            _saveSync.SaveConfiguration(
            game.Id,
            current.SavePaths,
            upload ? enabled : current.UploadOnGameStop,
            upload ? current.DownloadOnGameStart : enabled);
        }
        catch (Exception ex) { PlayniteApi.Dialogs.ShowErrorMessage(ex.Message, "Unnamed Tracking"); }
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

            var result = await UploadGamesAsync(games, (completed, total, name) =>
                Notify($"Syncing {completed}/{total}: {name}")).ConfigureAwait(true);
            var message = $"Library sync complete. {result.SucceededGames}/{result.TotalGames} games succeeded.";
            if (result.FailedGames > 0) message += $" {result.FailedGames} failed.";
            if (result.WarningCount > 0) message += $" {result.WarningCount} artwork warnings.";
            PlayniteApi.Dialogs.ShowMessage(message, "Unnamed Tracking");
        }
        catch (OperationCanceledException) { PlayniteApi.Dialogs.ShowMessage("Library synchronization cancelled.", "Unnamed Tracking"); }
        catch (Exception ex)
        {
            _logger.Error($"Manual Unnamed Tracking sync failed: {ex}");
            PlayniteApi.Dialogs.ShowErrorMessage(ex.Message, "Unnamed Tracking");
        }
    }
}
