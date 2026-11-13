param(
    [Parameter(Mandatory)] [string] $IdentityName,
    [Parameter(Mandatory)] [string] $Publisher,
    [Parameter(Mandatory)] [string] $PublisherDisplayName,
    [Parameter(Mandatory)] [string] $Version
)

$ErrorActionPreference = 'Stop'
$parsedVersion = [version]$Version
if ($parsedVersion.Major -lt 1 -or $parsedVersion.Revision -ne 0 -or
    ($parsedVersion.Major, $parsedVersion.Minor, $parsedVersion.Build | Where-Object { $_ -gt 65535 })) {
    throw 'Use a Store package version such as 1.0.0.0 (final component must be 0).'
}

$publish = Join-Path $PSScriptRoot '../../publish'
$stage = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString())
$sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$compiler = Get-ChildItem -Path "$sdk\10.*\x64\makeappx.exe" -File |
    Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
if (-not $compiler) { throw 'Install the Windows SDK to use MakeAppx.' }

try {
    New-Item -ItemType Directory -Path $stage | Out-Null
    Copy-Item -LiteralPath (Join-Path $publish 'StreamEnded.exe') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets') -Destination $stage -Recurse
    $manifest = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw -Encoding utf8)
    $manifest.Package.Identity.SetAttribute('Name', $IdentityName)
    $manifest.Package.Identity.SetAttribute('Publisher', $Publisher)
    $manifest.Package.Identity.SetAttribute('Version', $parsedVersion.ToString())
    $manifest.Package.Properties.PublisherDisplayName = $PublisherDisplayName
    $manifest.Save((Join-Path $stage 'AppxManifest.xml'))

    & $compiler.FullName pack /d $stage /p (Join-Path $publish 'StreamEnded-Store-x64.msix') /o
    if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed (exit code $LASTEXITCODE)." }
} finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
