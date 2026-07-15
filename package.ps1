param(
    [string]$Version = '1.2.3',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$projectDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildDirectory = Join-Path $projectDirectory 'build'
$distDirectory = Join-Path $projectDirectory 'dist'
$applicationName = 'M3U8' + [char]0x89C6 + [char]0x9891 + [char]0x4E0B + [char]0x8F7D + [char]0x5668 + '.exe'
$builtApplication = Join-Path $buildDirectory $applicationName
$packageApplicationName = 'M3U8-Video-Downloader.exe'
$packageName = "M3U8-Video-Downloader-v$Version-win-x64"
$packageDirectory = Join-Path $distDirectory $packageName
$zipPath = Join-Path $distDirectory ($packageName + '.zip')
$standalonePath = Join-Path $distDirectory ("M3U8-Video-Downloader-v$Version-win-x64.exe")

$programSource = [System.IO.File]::ReadAllText((Join-Path $projectDirectory 'Program.cs'))
$versionMatch = [regex]::Match($programSource, 'AssemblyFileVersion\("(?<version>\d+\.\d+\.\d+)\.\d+"\)')
if (-not $versionMatch.Success) {
    throw 'Could not read AssemblyFileVersion from Program.cs.'
}
$requestedVersionCore = ($Version -split '-', 2)[0]
if ($requestedVersionCore -ne $versionMatch.Groups['version'].Value) {
    throw "Package version $Version does not match AssemblyFileVersion $($versionMatch.Groups['version'].Value)."
}

if (-not $SkipBuild) {
    & (Join-Path $projectDirectory 'build.ps1') -SkipDesktopCopy
}

if (-not (Test-Path -LiteralPath $builtApplication)) {
    throw "Application build was not found: $builtApplication"
}

New-Item -ItemType Directory -Force -Path $distDirectory | Out-Null
if (Test-Path -LiteralPath $packageDirectory) {
    Remove-Item -LiteralPath $packageDirectory -Recurse -Force
}
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

New-Item -ItemType Directory -Force -Path $packageDirectory | Out-Null
Copy-Item -LiteralPath $builtApplication -Destination (Join-Path $packageDirectory $packageApplicationName)
Copy-Item -LiteralPath $builtApplication -Destination $standalonePath -Force

$packageFiles = @(
    'QUICKSTART.zh-CN.md',
    'setup-dependencies.ps1',
    'Setup-dependencies.cmd',
    'LICENSE',
    'LICENSE.N_m3u8DL-RE',
    'THIRD_PARTY_NOTICES.md'
)
foreach ($file in $packageFiles) {
    Copy-Item -LiteralPath (Join-Path $projectDirectory $file) -Destination (Join-Path $packageDirectory $file)
}

Compress-Archive -Path (Join-Path $packageDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal

$hashFiles = @($standalonePath, $zipPath)
$hashLines = foreach ($file in $hashFiles) {
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path -Leaf $file)"
}
$hashPath = Join-Path $distDirectory 'SHA256SUMS.txt'
$utf8WithoutBom = New-Object System.Text.UTF8Encoding -ArgumentList $false
[System.IO.File]::WriteAllLines($hashPath, $hashLines, $utf8WithoutBom)

Write-Host ''
Write-Host 'Release package complete:' -ForegroundColor Green
Write-Host $standalonePath
Write-Host $zipPath
Write-Host $hashPath
