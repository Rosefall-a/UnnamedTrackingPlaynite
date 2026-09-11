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
    private readonly UnnamedTrackingApiClient _apiClient;

    public UnnamedTrackingSettings Settings { get; }

    public UnnamedTrackingPlugin(IPlayniteAPI api) : base(api)
    {
        Properties = new GenericPluginProperties
        {
            HasSettings = true
        };

        _logger = LogManager.GetLogger();
        _apiClient = new UnnamedTrackingApiClient(_logger);
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
        return _apiClient.UploadLibraryAsync(
            Settings.ApiUrl,
            Settings.AuthValue,
            PlayniteApi.Database.Games);
    }

    public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
    {
        _logger.Info("Unnamed Tracking plugin loaded.");
    }
}
