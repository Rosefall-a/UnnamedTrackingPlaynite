# Unnamed Tracking for Playnite

A Playnite 10 companion extension for one-way library and save synchronization
with an Unnamed Tracking account. **0.1.2 is the next, unreleased version.** The
extension keeps the existing `net462` / PlayniteSDK 6.17.0 generic-plugin model.

- Authenticated full/selected-game sync, GUID matching, metadata and artwork.
- Preview, per-game progress/cancellation, ignore tags, startup and game-stop sync.
- Multiple local save folders/files, versioned remote archives, automatic transfers,
  durable restore backups and rollback on ordinary failure/cancellation.
- Existing save sidebar with access to the embedded website through normal sign-in.

See the [user and developer wiki](wiki/docs/index.md) for the complete guide,
[verified plugin-manager API contract](wiki/docs/developer-guide/api-contract.md),
[local versus remote save data](wiki/docs/user-guide/saves.md), and
[known runtime/screenshot limitations](wiki/docs/user-guide/screenshots.md).

## Install and configure

Install a reviewed `.pext` from [releases](https://github.com/Rosefall-a/UnnamedTrackingPlaynite/releases)
or a GitHub Actions build artifact into Windows Playnite. In **Add-ons → Extension
settings → Unnamed Tracking**, enter the server base URL and a user API key starting
with `utk_`. Test the connection, preview, and run the initial upload. API credentials
stay in headers; Playnite's stored extension settings remain sensitive local data.

Sync matches unique Playnite GUIDs first, preserves remote folders on rename, and
never sends arbitrary custom status names to the host enum. Library sync overwrites
mapped remote fields with Playnite values. Save transfers are separately configured
per game. Automatic download pauses the first launch; start the game again after
the success notification. See the wiki before enabling automatic saves.

## Build, test and package

On Windows, use the .NET 8 SDK and .NET Framework 4.6.2 targeting support:

```powershell
dotnet restore UnnamedTrackingPlaynite.sln
dotnet build UnnamedTrackingPlaynite.sln -c Release --no-restore
dotnet build tests/Companion.Tests -c Release -f net462
& ./tests/Companion.Tests/bin/Release/net462/Companion.Tests.exe
if ($LASTEXITCODE -ne 0) { throw "Regression tests failed" }
./pack.ps1 -NoBuild
./tests/test-packaging.ps1
```

The PEXT is written to `artifacts/UnnamedTrackingPlaynite-<version>.pext` and contains exactly
`UnnamedTrackingPlaynite.dll` and `extension.yaml` at its root. Normal development builds
use the project's last stable source version. For a release, the GitHub release tag is the
only version you change (for example `v0.2.0`); CI passes `0.2.0` into MSBuild, stamps the
manifest and assembly, validates the package, and attaches the matching PEXT to the release. Toolbox is optional.
For portable helper tests use `dotnet run --project tests/Companion.Tests -c Release -f net8.0`.
No test framework or new extension runtime dependency is required.

```sh
python -m pip install -r wiki/requirements.txt
python -m mkdocs build --strict -f wiki/mkdocs.yml
```

Both existing build workflows remain active, including package validation.
Main/PR builds produce artifacts; release publication requires an intentional tag
matching the manifest/project version. See [release notes](CHANGELOG.md) and
[release process](wiki/docs/developer-guide/releases.md). This change publishes no release.
Headless tests and builds do not claim full Playnite runtime testing; actual
Playnite screenshots could not be obtained in the Linux environment.
