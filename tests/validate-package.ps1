param([Parameter(Mandatory = $true)][string]$PackagePath)

$ErrorActionPreference = 'Stop'
$package = Get-Item $PackagePath
$extract = Join-Path ([System.IO.Path]::GetTempPath()) ("unnamed-package-check-" + [guid]::NewGuid().ToString('N'))
try {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $names = @($archive.Entries | ForEach-Object { $_.FullName })
        if ($names.Count -ne 2 -or
            @($names | Where-Object { $_ -ceq 'extension.yaml' }).Count -ne 1 -or
            @($names | Where-Object { $_ -ceq 'UnnamedTrackingPlaynite.dll' }).Count -ne 1) {
            throw "PEXT must contain exactly the extension assembly and manifest at its root."
        }
    } finally { $archive.Dispose() }
    [System.IO.Compression.ZipFile]::ExtractToDirectory($package.FullName, $extract)
    & (Join-Path $PSScriptRoot 'validate-extension.ps1') -ExtensionDirectory $extract
    $manifest = Get-Content (Join-Path $extract 'extension.yaml') -Raw
    $version = [regex]::Match($manifest, '(?m)^Version:\s*(.+)$').Groups[1].Value.Trim()
    if ($package.Name -cne "UnnamedTrackingPlaynite-$version.pext") { throw "PEXT filename does not match its manifest version." }
    Write-Host "Validated PEXT: $($package.Name)"
} finally {
    if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
}
