# Build, packaging and CI

## Fresh checkout

On Windows with the .NET 8 SDK and .NET Framework 4.6.2 targeting support:

```powershell
dotnet restore UnnamedTrackingPlaynite.sln
dotnet build UnnamedTrackingPlaynite.sln -c Release --no-restore
dotnet build tests/Companion.Tests -c Release -f net462
& ./tests/Companion.Tests/bin/Release/net462/Companion.Tests.exe
if ($LASTEXITCODE -ne 0) { throw "Regression tests failed" }
./pack.ps1 -NoBuild
./tests/test-packaging.ps1
```

The no-framework console harness fails with a nonzero exit code on any assertion.
It links the actual production helpers/client source and uses the same SDK
reference, loopback HTTP fixtures and disposable files. It adds no runtime or
test-framework dependency. Windows CI runs it on the extension's `net462` CLR.
For a portable headless run during development:

```sh
dotnet run --project tests/Companion.Tests -c Release -f net8.0
```

Only the portable test target suppresses WebRequest deprecation diagnostics;
the existing .NET Framework SDK-reference compatibility warning is scoped to the
test dependency. The extension's warning-as-error policy remains intact. Headless
tests do not instantiate Playnite/WPF/CEF or prove runtime integration.

The extension output is `src/UnnamedTrackingPlaynite/bin/Release/net462/`.
`./pack.ps1` builds and validates it, then stages exactly the assembly and manifest
and writes `artifacts/UnnamedTrackingPlaynite-0.1.2.pext`. No Playnite Toolbox
installation is required. An explicit `-ToolboxPath` retains Toolbox support;
its output must pass the same package validator. Build/Toolbox failures fail the
script. `-NoBuild` is for already-built output; version validation rejects stale
assemblies/manifests.

PEXT is ZIP. The script compresses to a `.zip` then renames it, avoiding the
old assumption that PowerShell archive cmdlets accept `.pext` directly. Package
validation reads actual ZIP entries, requires exactly the two root files,
extracts them in a unique temporary directory, and checks manifest/module/version
consistency. Private Playnite assemblies are rejected recursively. Negative tests
exercise missing/duplicate manifest fields, wrong type/module/version, forbidden
SDK binaries, and an extra local save configuration in a PEXT.

## Workflows

**Build plugin** retains solution restore/build/output validation and artifacts,
and adds the production helper harness on .NET Framework plus strict wiki build.
**Build Playnite extension** retains extension restore/build, PEXT packaging,
package validation and artifact upload, using the same packaging script as local
builds. Both workflows remain enabled for PRs. Tags additionally build packages;
release publication requires an intentional matching `v<version>` tag. Main/PR
builds produce artifacts without publishing a release.

Generated `bin/`, `obj/`, `artifacts/`, PEXTs and wiki `site/` output are ignored.
They are rebuilt from source; no prebuilt binary is needed for restore or testing.
The obsolete Backup solution and build marker were removed only after checking
references and their contents. See [repository audit](audit.md).

## Wiki validation

```sh
python -m pip install -r wiki/requirements.txt
python -m mkdocs build --strict -f wiki/mkdocs.yml
python -m mkdocs serve -f wiki/mkdocs.yml
```

The Material theme and MkDocs are documentation-only dependencies, pinned in the
wiki requirements. There is no website deployment workflow in this change.
