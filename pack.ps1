param(
    [string]$Configuration = "Release",
    [string]$ToolboxPath = ""
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src/UnnamedTrackingPlaynite/UnnamedTrackingPlaynite.csproj"
$output = Join-Path $root "src/UnnamedTrackingPlaynite/bin/$Configuration/net462"
$packageDir = Join-Path $root "artifacts"

if (-not (Test-Path $project)) {
    throw "Plugin project not found: $project"
}

dotnet build $project -c $Configuration

if (-not $ToolboxPath) {
    $candidates = @(
        "$env:ProgramFiles\Playnite\Toolbox.exe",
        "$env:LOCALAPPDATA\Playnite\Toolbox.exe"
    )
    $ToolboxPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $ToolboxPath -or -not (Test-Path $ToolboxPath)) {
    Write-Warning "Playnite Toolbox.exe was not found. The plugin was built successfully, but no .pext package was created."
    Write-Host "Build output: $output"
    exit 0
}

New-Item -ItemType Directory -Force -Path $packageDir | Out-Null
& $ToolboxPath pack $output $packageDir

if ($LASTEXITCODE -ne 0) {
    throw "Playnite Toolbox failed with exit code $LASTEXITCODE."
}

Write-Host "Package created in $packageDir"
