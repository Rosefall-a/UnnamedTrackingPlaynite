using System;
using System.Collections.Generic;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;

namespace UnnamedTrackingPlaynite;

public sealed class UnnamedTrackingPlugin : GenericPlugin
{
    public override Guid Id { get; } = Guid.Parse("4A8D9C7B-2E54-4A6B-9C6D-0A2B8F1E7D43");

    private readonly ILogger _logger;

    public UnnamedTrackingSettings Settings { get; }

    public UnnamedTrackingPlugin(IPlayniteAPI api) : base(api)
    {
        Properties = new GenericPluginProperties
        {
            HasSettings = true
        };

        _logger = LogManager.GetLogger();
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

    public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
    {
        _logger.Info("Unnamed Tracking plugin loaded.");
    }
}
