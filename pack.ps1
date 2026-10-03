param(
    [string]$Configuration = "Release",
    [string]$ToolboxPath = "",
    [string]$Version = "",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "src/UnnamedTrackingPlaynite/UnnamedTrackingPlaynite.csproj"
$output = Join-Path $root "src/UnnamedTrackingPlaynite/bin/$Configuration/net462"
$artifacts = Join-Path $root "artifacts"

if ($Version -and $Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must be major.minor.patch, got '$Version'."
}

if (-not $NoBuild) {
    if ($Version) {
        dotnet build $project -c $Configuration -p:Version=$Version
    } else {
        dotnet build $project -c $Configuration
    }
    if ($LASTEXITCODE -ne 0) { throw "Extension build failed with exit code $LASTEXITCODE." }
}

& (Join-Path $root "tests/validate-extension.ps1") -ExtensionDirectory $output
if ($Version) {
    $manifest = Get-Content (Join-Path $output "extension.yaml") -Raw
    $builtVersion = [regex]::Match($manifest, '(?m)^Version:\s*(.+)$').Groups[1].Value.Trim()
    if ($builtVersion -ne $Version) {
        throw "Built extension version '$builtVersion' does not match requested version '$Version'."
    }
}

$manifest = Get-Content (Join-Path $output "extension.yaml") -Raw
$version = [regex]::Match($manifest, '(?m)^Version:\s*(.+)$').Groups[1].Value.Trim()
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$packagePath = Join-Path $artifacts "UnnamedTrackingPlaynite-$version.pext"
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("unnamed-pext-" + [guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Path $staging | Out-Null
    Copy-Item (Join-Path $output "UnnamedTrackingPlaynite.dll") $staging
    Copy-Item (Join-Path $output "extension.yaml") $staging
    if ($ToolboxPath) {
        if (-not (Test-Path $ToolboxPath -PathType Leaf)) { throw "Playnite Toolbox not found: $ToolboxPath" }
        $toolboxOutput = Join-Path $staging "toolbox-output"
        New-Item -ItemType Directory -Path $toolboxOutput | Out-Null
        & $ToolboxPath pack $output $toolboxOutput
        if ($LASTEXITCODE -ne 0) { throw "Playnite Toolbox failed with exit code $LASTEXITCODE." }
        $packages = @(Get-ChildItem $toolboxOutput -Filter '*.pext')
        if ($packages.Count -ne 1) { throw "Playnite Toolbox must produce exactly one PEXT." }
        Copy-Item $packages[0].FullName $packagePath -Force
    } else {
        # .pext is ZIP, but Compress-Archive only accepts .zip consistently.
        $zip = Join-Path $staging "package.zip"
        Compress-Archive -LiteralPath @(
            (Join-Path $staging "UnnamedTrackingPlaynite.dll"),
            (Join-Path $staging "extension.yaml")
        ) -DestinationPath $zip
        Move-Item $zip $packagePath -Force
    }
    & (Join-Path $root "tests/validate-package.ps1") -PackagePath $packagePath
    Write-Host "Created $packagePath"
} finally {
    Remove-Item $staging -Recurse -Force
}
