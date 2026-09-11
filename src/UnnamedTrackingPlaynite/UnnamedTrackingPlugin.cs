using System;
using System.Collections.Generic;
using Playnite.SDK;
using Playnite.SDK.Plugins;

namespace UnnamedTrackingPlaynite;

public sealed class UnnamedTrackingPlugin : GenericPlugin
{
    public override Guid Id { get; } = Guid.Parse("4A8D9C7B-2E54-4A6B-9C6D-0A2B8F1E7D43");

    private readonly ILogger _logger;

    public UnnamedTrackingPlugin(IPlayniteAPI api) : base(api)
    {
        _logger = api.CreateLogger();
    }

    public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
    {
        yield return new MainMenuItem
        {
            Description = "Unnamed Tracking",
            MenuSection = "@"
        };
    }

    public override void OnApplicationStarted()
    {
        _logger.Info("Unnamed Tracking plugin loaded.");
    }
}
