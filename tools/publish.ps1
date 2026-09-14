param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.0'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $projectRoot 'release'
$packageName = 'TLO-win-x64'
$archive = Join-Path $releaseRoot "$packageName.zip"
if (Test-Path -LiteralPath $archive) { throw "Release already exists: $archive. Move it elsewhere before rebuilding." }
foreach ($asset in @('tiles/maps.json', 'data/icons.idx')) {
    if (!(Test-Path -LiteralPath (Join-Path $projectRoot $asset))) { throw "Missing $asset. Generate map tiles before packaging." }
}
$stage = Join-Path $projectRoot ('artifacts/package-' + [guid]::NewGuid().ToString('N'))
$package = Join-Path $stage $packageName
New-Item -ItemType Directory -Path $package -Force | Out-Null
& dotnet publish (Join-Path $projectRoot 'src/TarkovOverlay/TarkovOverlay.csproj') -c Release -r win-x64 --self-contained true --artifacts-path (Join-Path $projectRoot 'artifacts/dotnet') -o $package "-p:Version=$Version" --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'tiles') -Destination $package -Recurse
New-Item -ItemType Directory -Path (Join-Path $package 'data') | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'data/icons.idx') -Destination (Join-Path $package 'data')
foreach ($file in @('README.md', 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination $package
}
if (!(Test-Path -LiteralPath (Join-Path $package 'TLO.exe'))) { throw 'Executable missing from publish output.' }
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $archive, [System.IO.Compression.CompressionLevel]::Fastest, $false)
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath (Join-Path $releaseRoot "$packageName.sha256") -Value "$hash  $packageName.zip" -Encoding ascii
Write-Host "Ready: $archive"
Write-Host "Executable: $(Join-Path $package 'TLO.exe')"
