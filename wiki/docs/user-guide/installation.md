# Installation and configuration

## Requirements

Use Windows and Playnite 10.x, a reachable Unnamed Tracking server, and a user
API key beginning with `utk_`. An application plugin-management token (`utpm_`)
cannot access the game APIs used by this extension. Developing the extension also
requires the .NET 8 SDK and .NET Framework 4.6.2 targeting support; installing a
PEXT does not require the developer tools.

## Install

1. Download a `.pext` from a reviewed [release](https://github.com/Rosefall-a/UnnamedTrackingPlaynite/releases),
   or obtain a build artifact from the repository's GitHub Actions run. An Actions
   artifact is a ZIP wrapper; extract it to obtain the PEXT.
2. Open the PEXT with Playnite or drag it into Playnite. Accept the installation
   prompt and restart if Playnite requests it.
3. Open **Add-ons → Extension settings → Unnamed Tracking**.

Development builds can instead be loaded from the Release output directory using
Playnite's **Settings → For developers → External extensions**. Keep one installed
copy of the extension to avoid confusing duplicate installations.

## Configure

Enter the server's base URL, for example `https://tracking.example.com`. A reverse
proxy base path is supported, such as `https://example.com/tracking`. Use the final
URL: API redirects are rejected. Do not append `/api`, an API key, a username and
password, a query string, or a fragment. HTTPS protects the API key in transit;
HTTP is supported for local deployments.

Create a **user API key** in the Unnamed Tracking application's account settings
and paste it in **API key**. The key is stored in Playnite's extension settings;
it is not encrypted by this extension. Treat the Playnite profile and its backups
as sensitive. The field is masked in the settings view. The key is sent only in
the HTTP `Authorization: Bearer` header, never in page or download URLs.

Leave automatic synchronization disabled until the connection and initial preview
look correct. The default ignore tag is `trackingapp_ignore`. See
[ignored games](synchronization.md#ignored-games) before changing it.

## Test the connection

Click **Test connection**. It sends an authenticated read of one game from
`/api/game/list`; an empty account is valid. Success verifies reachability,
authentication, and a readable game-list response. It does not prove that artwork
or save uploads fit the server's size limits, or establish a browser login.

The test uses the values currently entered in settings, even before saving them.
Save your settings in Playnite to persist changes. See
[troubleshooting](troubleshooting.md) if the test fails.
