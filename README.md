# Unnamed Tracking Playnite Plugin

Playnite plugin for integrating [Unnamed Tracking](https://github.com/Rosefall-a/unnamed_tracking_app) with a user's Playnite library.

This repository intentionally starts with only the plugin shell. The first milestone is a clean, buildable, installable Playnite extension so we can establish CI/CD and then add synchronization features incrementally.

## Requirements

- Windows
- Visual Studio 2022 (or another IDE with .NET Framework 4.6.2 support)
- .NET Framework 4.6.2 developer targeting pack
- Playnite 10.x

Playnite 10 plugins target .NET Framework 4.6.2 and use the PlayniteSDK NuGet package.

## Build

```powershell
dotnet restore
dotnet build -c Release
```

The build output is an extension directory containing the DLL and `extension.yaml`.

### Install for local development

Point Playnite's **For developers → External extensions** setting at:

```text
src/UnnamedTrackingPlaynite/bin/Release/net462/
```

Alternatively, use the included packaging script with Playnite's `Toolbox.exe` to create a `.pext` package:

```powershell
./pack.ps1
```

## Current state

The plugin does not upload or modify games yet. It only loads into Playnite and exposes a small test menu entry so installation can be verified.

## Planned direction

- Configure the Unnamed Tracking server URL and API key.
- Read the Playnite library through the Playnite SDK.
- Match games using stable external IDs where possible.
- Add a manual library sync command.
- Add optional automatic synchronization.
- Upload supported artwork/media through the Unnamed Tracking API.

## Playnite compatibility

This initial scaffold targets Playnite 10. Playnite 11 uses a different runtime and extension packaging model, so we will treat Playnite 11 support as a deliberate follow-up rather than mixing both runtimes into the first bootstrap.
