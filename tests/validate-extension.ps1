param(
    [Parameter(Mandatory = $true)]
    [string]$ExtensionDirectory,

    [string]$ExpectedVersion = ""
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
$fields = @{}
foreach ($name in @("Id", "Name", "Author", "Version", "Module", "Type")) {
    $pattern = "(?m)^" + [regex]::Escape($name) + ":\s*(\S[^\r\n]*)\r?$"
    $matches = [regex]::Matches($manifest, $pattern)
    if ($matches.Count -ne 1) {
        throw "Manifest must contain exactly one non-empty '$${name}'."
    }
    $fields[$name] = $matches[0].Groups[1].Value.Trim()
}

if ($fields.Id -ne 'UnnamedTrackingPlaynite') { throw "The stable extension ID changed." }
if ($fields.Type -ne 'GenericPlugin') { throw "Manifest Type must remain GenericPlugin." }
if ($fields.Module -ne 'UnnamedTrackingPlaynite.dll') { throw "Unexpected manifest module." }
if ($fields.Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must contain major.minor.patch." }

$modulePath = Join-Path $ExtensionDirectory $fields.Module
if (-not (Test-Path $modulePath -PathType Leaf)) {
    throw "Manifest module was not produced."
}

$privatePlayniteAssemblies = @(
    "Playnite.dll", "Playnite.Common.dll", "Playnite.DesktopApp.dll",
    "Playnite.FullscreenApp.dll", "PlayniteSDK.dll", "Playnite.SDK.dll"
)
foreach ($file in Get-ChildItem $ExtensionDirectory -Recurse -File) {
    if ($file.Name -in $privatePlayniteAssemblies) {
        throw "Extension output must not package Playnite dependency '$($file.Name)'."
    }
}

$project = [xml](Get-Content (Join-Path $PSScriptRoot '../src/UnnamedTrackingPlaynite/UnnamedTrackingPlaynite.csproj') -Raw)
$properties = $project.Project.PropertyGroup
$effectiveVersion = if ($ExpectedVersion) { $ExpectedVersion } else { [string]$properties.Version }

if ($effectiveVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Effective version must contain major.minor.patch."
}
if ($fields.Version -ne $effectiveVersion) {
    throw "Manifest version '$($fields.Version)' does not match effective version '$effectiveVersion'."
}

$assembly = [System.Reflection.AssemblyName]::GetAssemblyName((Resolve-Path $modulePath))
if ($assembly.Name -ne 'UnnamedTrackingPlaynite' -or $assembly.Version.ToString() -ne "$($effectiveVersion).0") {
    throw "Built assembly identity/version does not agree with the effective extension version."
}

$fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path $modulePath))
if ($fileVersion.FileVersion -ne "$($effectiveVersion).0") {
    throw "Built file version does not match the effective extension version."
}

Write-Host "Validated extension $($fields.Id) $($fields.Version)"
