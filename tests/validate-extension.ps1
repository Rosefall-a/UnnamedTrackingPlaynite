param(
    [Parameter(Mandatory = $true)]
    [string]$ExtensionDirectory
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $ExtensionDirectory -PathType Container)) {
    throw "Extension directory does not exist: $ExtensionDirectory"
}

$manifestPath = Join-Path $ExtensionDirectory "extension.yaml"
if (-not (Test-Path $manifestPath -PathType Leaf)) {
    throw "extension.yaml was not produced."
}

$manifest = Get-Content $manifestPath -Raw

$requiredFields = @("Id", "Name", "Author", "Version", "Module", "Type")
foreach ($field in $requiredFields) {
    if ($manifest -notmatch "(?m)^$field:\s*\S.+$") {
        throw "extension.yaml is missing required field '$field'."
    }
}

$versionMatch = [regex]::Match($manifest, "(?m)^Version:\s*(.+)$")
$version = $versionMatch.Groups[1].Value.Trim()
try {
    [void][System.Version]::Parse($version)
} catch {
    throw "extension.yaml Version '$version' is not a valid .NET version."
}

$moduleMatch = [regex]::Match($manifest, "(?m)^Module:\s*(.+)$")
$module = $moduleMatch.Groups[1].Value.Trim()
$modulePath = Join-Path $ExtensionDirectory $module
if (-not (Test-Path $modulePath -PathType Leaf)) {
    throw "Manifest module '$module' was not produced."
}

$privatePlayniteAssemblies = @(
    "Playnite.dll",
    "Playnite.Common.dll",
    "Playnite.DesktopApp.dll",
    "Playnite.FullscreenApp.dll",
    "PlayniteSDK.dll"
)
foreach ($assembly in $privatePlayniteAssemblies) {
    if (Test-Path (Join-Path $ExtensionDirectory $assembly)) {
        throw "Extension output must not package Playnite dependency '$assembly'."
    }
}

Write-Host "Validated Playnite extension:"
Write-Host "  Manifest: $manifestPath"
Write-Host "  Module:   $modulePath"
Write-Host "  Version:  $version"
