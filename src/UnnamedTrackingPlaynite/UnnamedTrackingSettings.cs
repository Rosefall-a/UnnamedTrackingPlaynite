using System;
using System.Collections.Generic;
using Playnite.SDK;

namespace UnnamedTrackingPlaynite;

public sealed class UnnamedTrackingSettings : ObservableObject, ISettings
{
    private readonly UnnamedTrackingPlugin? plugin;
    private string apiUrl = string.Empty;
    private string authValue = string.Empty;
    private string editingApiUrl = string.Empty;
    private string editingAuthValue = string.Empty;

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

    public UnnamedTrackingSettings()
    {
    }

    public UnnamedTrackingSettings(UnnamedTrackingPlugin plugin)
    {
        this.plugin = plugin;
        var savedSettings = plugin.LoadPluginSettings<UnnamedTrackingSettings>();
        if (savedSettings != null)
        {
            ApiUrl = savedSettings.ApiUrl;
            AuthValue = savedSettings.AuthValue;
        }
    }

    public void BeginEdit()
    {
        editingApiUrl = ApiUrl;
        editingAuthValue = AuthValue;
    }

    public void CancelEdit()
    {
        ApiUrl = editingApiUrl;
        AuthValue = editingAuthValue;
    }

    public void EndEdit()
    {
        plugin?.SavePluginSettings(this);
    }

    public bool VerifySettings(out List<string> errors)
    {
        errors = new List<string>();

        if (!string.IsNullOrWhiteSpace(ApiUrl))
        {
            if (!Uri.TryCreate(ApiUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                errors.Add("API URL must be a valid HTTP or HTTPS URL.");
            }
        }

        return errors.Count == 0;
    }
}
