# Negative tests use a real compiled assembly, not an empty fixture.
param([string]$ExtensionDirectory = 'src/UnnamedTrackingPlaynite/bin/Release/net462')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path ([System.IO.Path]::GetTempPath()) ("unnamed-package-tests-" + [guid]::NewGuid().ToString('N'))
function Assert-Rejected([scriptblock]$Action) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw "Invalid extension/package was accepted." }
}
try {
    New-Item -ItemType Directory -Path $fixture | Out-Null
    $source = Join-Path $root $ExtensionDirectory
    $manifest = Get-Content (Join-Path $source 'extension.yaml') -Raw
    Copy-Item (Join-Path $source 'UnnamedTrackingPlaynite.dll') $fixture
    foreach ($broken in @(
        ($manifest -replace 'Type: GenericPlugin', 'Type: GameLibrary'),
        ($manifest -replace 'Module: UnnamedTrackingPlaynite.dll', 'Module: ../other.dll'),
        ($manifest -replace 'Version: \d+\.\d+\.\d+', 'Version: 99.0.0'),
        ($manifest -replace '(?m)^Author:.*\n', ''),
        ($manifest + "`nId: duplicate`n")
    )) {
        Set-Content (Join-Path $fixture 'extension.yaml') $broken
        Assert-Rejected { & "$PSScriptRoot/validate-extension.ps1" -ExtensionDirectory $fixture }
    }
    Set-Content (Join-Path $fixture 'extension.yaml') $manifest
    New-Item -ItemType Directory -Path (Join-Path $fixture 'nested') | Out-Null
    Set-Content (Join-Path $fixture 'nested/Playnite.SDK.dll') 'forbidden'
    Assert-Rejected { & "$PSScriptRoot/validate-extension.ps1" -ExtensionDirectory $fixture }
    Remove-Item (Join-Path $fixture 'nested') -Recurse
    $version = [regex]::Match($manifest, '(?m)^Version:\s*(.+)$').Groups[1].Value.Trim()
    $zip = Join-Path $fixture 'package.zip'
    Compress-Archive -Path (Join-Path $fixture 'extension.yaml'), (Join-Path $fixture 'UnnamedTrackingPlaynite.dll') -DestinationPath $zip
    $pext = Join-Path $fixture "UnnamedTrackingPlaynite-$version.pext"
    Move-Item $zip $pext
    & "$PSScriptRoot/validate-package.ps1" -PackagePath $pext
    $archive = [System.IO.Compression.ZipFile]::Open($pext, 'Update')
    try { [void]$archive.CreateEntry('save-sync.json') } finally { $archive.Dispose() }
    Assert-Rejected { & "$PSScriptRoot/validate-package.ps1" -PackagePath $pext }
    Write-Host 'Packaging regression checks passed.'
} finally { Remove-Item $fixture -Recurse -Force }
