param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.0'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $projectRoot 'release'
$packageName = 'TLO-win-x64'
$archive = Join-Path $releaseRoot "$packageName.zip"
$readyFolder = Join-Path $releaseRoot $packageName
if (Test-Path -LiteralPath $archive) { throw "Release already exists: $archive. Move it elsewhere before rebuilding." }
if (Test-Path -LiteralPath $readyFolder) { throw "Published folder already exists: $readyFolder. Move it elsewhere before rebuilding." }
foreach ($asset in @('tiles/maps.json', 'data/icons.idx')) {
    if (!(Test-Path -LiteralPath (Join-Path $projectRoot $asset))) { throw "Missing $asset. Generate map tiles before packaging." }
}
$stage = Join-Path $projectRoot ('artifacts/package-' + [guid]::NewGuid().ToString('N'))
$package = Join-Path $stage $packageName
New-Item -ItemType Directory -Path $package -Force | Out-Null
& dotnet publish (Join-Path $projectRoot 'src/TarkovOverlay/TarkovOverlay.csproj') -c Release -r win-x64 --self-contained true --artifacts-path (Join-Path $projectRoot 'artifacts/dotnet') -o $package "-p:Version=$Version" --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
# Runtime publishing does not automatically copy the NuGet packages' licenses.
$assets = Get-Content -LiteralPath (Join-Path $projectRoot 'artifacts/dotnet/obj/TarkovOverlay/project.assets.json') -Raw | ConvertFrom-Json
$runtime = Get-Content -LiteralPath (Join-Path $package 'TLO.runtimeconfig.json') -Raw | ConvertFrom-Json
$coreVersion = ($runtime.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
$desktopVersion = ($runtime.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.WindowsDesktop.App').version
$webViewPath = ($assets.libraries.PSObject.Properties | Where-Object Name -Like 'Microsoft.Web.WebView2/*').Value.path
$licenses = @{
    'dotnet-LICENSE.txt' = "microsoft.netcore.app.runtime.win-x64/$coreVersion/LICENSE.TXT"
    'dotnet-THIRD-PARTY-NOTICES.txt' = "microsoft.netcore.app.runtime.win-x64/$coreVersion/THIRD-PARTY-NOTICES.TXT"
    'windowsdesktop-LICENSE.txt' = "microsoft.windowsdesktop.app.runtime.win-x64/$desktopVersion/LICENSE"
    'webview2-LICENSE.txt' = "$webViewPath/LICENSE.txt"
    'webview2-NOTICE.txt' = "$webViewPath/NOTICE.txt"
}
$licenseFolder = Join-Path $package 'licenses'
New-Item -ItemType Directory -Path $licenseFolder | Out-Null
foreach ($entry in $licenses.GetEnumerator()) {
    $source = $assets.packageFolders.PSObject.Properties.Name | ForEach-Object { Join-Path $_ $entry.Value } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (!$source) { throw "Missing dependency license: $($entry.Value)" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $licenseFolder $entry.Key)
}
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
# Both paths are constructed inside this workspace; never overwrite an existing build.
Move-Item -LiteralPath $package -Destination $readyFolder
Write-Host "Ready: $archive"
Write-Host "Executable: $(Join-Path $readyFolder 'TLO.exe')"
