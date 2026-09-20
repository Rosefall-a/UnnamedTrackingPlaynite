using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;

namespace UnnamedTrackingPlaynite;

public sealed class UnnamedTrackingSettings : ObservableObject, ISettings
{
    private readonly UnnamedTrackingPlugin? plugin;
    private string apiUrl = string.Empty;
    private string authValue = string.Empty;
    private string editingApiUrl = string.Empty;
    private string editingAuthValue = string.Empty;
    private string ignoreTag = "trackingapp_ignore";
    private string editingIgnoreTag = "trackingapp_ignore";
    private bool syncOnStartup;
    private bool syncOnGameStopped;
    private bool editingSyncOnStartup;
    private bool editingSyncOnGameStopped;

    public string ApiUrl
    {
        get => apiUrl;
        set => SetValue(ref apiUrl, value);
    }

    public string AuthValue
    {
        get => authValue;
        set => SetValue(ref authValue, value);
    }

    public string IgnoreTag
    {
        get => ignoreTag;
        set => SetValue(ref ignoreTag, value);
    }

    public bool SyncOnStartup
    {
        get => syncOnStartup;
        set => SetValue(ref syncOnStartup, value);
    }

    public bool SyncOnGameStopped
    {
        get => syncOnGameStopped;
        set => SetValue(ref syncOnGameStopped, value);
    }

    public UnnamedTrackingSettings()
    {
    }

    public UnnamedTrackingSettings(UnnamedTrackingPlugin plugin)
    {
        this.plugin = plugin;
        try
        {
            var savedSettings = plugin.LoadPluginSettings<UnnamedTrackingSettings>();
            if (savedSettings != null)
            {
                ApiUrl = savedSettings.ApiUrl;
                AuthValue = savedSettings.AuthValue;
                IgnoreTag = string.IsNullOrWhiteSpace(savedSettings.IgnoreTag) ? "trackingapp_ignore" : savedSettings.IgnoreTag;
                SyncOnStartup = savedSettings.SyncOnStartup;
                SyncOnGameStopped = savedSettings.SyncOnGameStopped;
            }
        }
        catch (Exception ex)
        {
            // A corrupt/stale Playnite settings record must never prevent the entire
            // extension from loading. Start from safe defaults and let the user reconfigure.
            LogManager.GetLogger().Error($"Could not load Unnamed Tracking plugin settings; using defaults: {ex}");
        }
    }

    public void BeginEdit()
    {
        editingApiUrl = ApiUrl;
        editingAuthValue = AuthValue;
        editingIgnoreTag = IgnoreTag;
        editingSyncOnStartup = SyncOnStartup;
        editingSyncOnGameStopped = SyncOnGameStopped;
    }

    public void CancelEdit()
    {
        ApiUrl = editingApiUrl;
        AuthValue = editingAuthValue;
        IgnoreTag = editingIgnoreTag;
        SyncOnStartup = editingSyncOnStartup;
        SyncOnGameStopped = editingSyncOnGameStopped;
    }

    public void EndEdit()
    {
        ApiUrl = ApiUrl.Trim().TrimEnd('/');
        AuthValue = AuthValue.Trim();
        IgnoreTag = IgnoreTag.Trim();
        plugin?.SavePluginSettings(this);
    }

    public Task<bool> TestConnectionAsync()
    {
        if (plugin == null)
        {
            throw new InvalidOperationException("The plugin is not initialized.");
        }

        return plugin.TestConnectionAsync();
    }

    public Task<UnnamedTrackingUploadResult> UploadLibraryAsync()
    {
        if (plugin == null)
        {
            throw new InvalidOperationException("The plugin is not initialized.");
        }

        return plugin.UploadLibraryAsync();
    }

    public Task<UnnamedTrackingSyncPreviewResult> PreviewLibraryAsync()
    {
        if (plugin == null) throw new InvalidOperationException("The plugin is not initialized.");
        return plugin.PreviewLibraryAsync();
    }

    public bool VerifySettings(out List<string> errors)
    {
        errors = new List<string>();

        if (!string.IsNullOrWhiteSpace(ApiUrl))
        {
            if (!Uri.TryCreate(ApiUrl.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                string.IsNullOrWhiteSpace(uri.Host))
            {
                errors.Add("API URL must be a valid HTTP or HTTPS URL.");
            }
        }

        if (!string.IsNullOrWhiteSpace(AuthValue) && !AuthValue.Trim().StartsWith("utk_", StringComparison.Ordinal))
        {
            errors.Add("The authentication value must be an Unnamed Tracking API key beginning with utk_.");
        }

        if ((SyncOnStartup || SyncOnGameStopped) &&
            (string.IsNullOrWhiteSpace(ApiUrl) || string.IsNullOrWhiteSpace(AuthValue)))
        {
            errors.Add("An API URL and API key are required when automatic synchronization is enabled.");
        }

        return errors.Count == 0;
    }
}
