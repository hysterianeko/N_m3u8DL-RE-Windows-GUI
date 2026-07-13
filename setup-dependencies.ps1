param(
    [switch]$SkipFfmpeg
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$projectDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$toolsDirectory = Join-Path $projectDirectory 'tools'
$downloaderPath = Join-Path $toolsDirectory 'N_m3u8DL-RE.exe'
$releaseAssetName = 'N_m3u8DL-RE_v0.6.0-beta_win-x64_20260629.zip'
$releaseUrl = 'https://github.com/nilaoda/N_m3u8DL-RE/releases/download/v0.6.0-beta/' + $releaseAssetName
$expectedSha256 = '3825FD42EE502F98A9378F6FDDDB2F7822709F521806214F466DB6935C950F1A'
$expectedExecutableSha256 = '35E7C16983F0315BBA2A3F37DC392FDFA074BE614C8EFFB642B78376B18BA272'
$upstreamLicenseSource = Join-Path $projectDirectory 'LICENSE.N_m3u8DL-RE'
$upstreamLicenseDestination = Join-Path $toolsDirectory 'LICENSE.N_m3u8DL-RE'

New-Item -ItemType Directory -Force -Path $toolsDirectory | Out-Null

$downloaderIsVerified = $false
if (Test-Path -LiteralPath $downloaderPath) {
    $installedHash = (Get-FileHash -LiteralPath $downloaderPath -Algorithm SHA256).Hash
    $downloaderIsVerified = [string]::Equals(
        $installedHash,
        $expectedExecutableSha256,
        [StringComparison]::OrdinalIgnoreCase)
}

if ($downloaderIsVerified) {
    Write-Host "N_m3u8DL-RE is already present: $downloaderPath" -ForegroundColor Green
} else {
    if (Test-Path -LiteralPath $downloaderPath) {
        Write-Warning 'The existing N_m3u8DL-RE.exe does not match the tested release and will be replaced after a verified download.'
    }

    $temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('M3u8GuiSetup_' + [Guid]::NewGuid().ToString('N'))
    $archivePath = Join-Path $temporaryDirectory $releaseAssetName
    $extractDirectory = Join-Path $temporaryDirectory 'extracted'
    New-Item -ItemType Directory -Force -Path $temporaryDirectory | Out-Null

    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Write-Host 'Downloading N_m3u8DL-RE from the official GitHub Release...'
        Invoke-WebRequest -UseBasicParsing -Uri $releaseUrl -OutFile $archivePath

        $actualSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
        if (-not [string]::Equals($actualSha256, $expectedSha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw "N_m3u8DL-RE archive checksum mismatch. Expected $expectedSha256, got $actualSha256."
        }

        Expand-Archive -LiteralPath $archivePath -DestinationPath $extractDirectory -Force
        $downloadedExecutable = Get-ChildItem -LiteralPath $extractDirectory -Filter 'N_m3u8DL-RE.exe' -File -Recurse |
            Select-Object -First 1
        if (-not $downloadedExecutable) {
            throw 'The official archive did not contain N_m3u8DL-RE.exe.'
        }

        Copy-Item -LiteralPath $downloadedExecutable.FullName -Destination $downloaderPath -Force
        Unblock-File -LiteralPath $downloaderPath -ErrorAction SilentlyContinue
        Write-Host "Installed N_m3u8DL-RE: $downloaderPath" -ForegroundColor Green
    } finally {
        if (Test-Path -LiteralPath $temporaryDirectory) {
            Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

if (-not (Test-Path -LiteralPath $upstreamLicenseSource)) {
    throw "Required upstream license file is missing: $upstreamLicenseSource"
}
Copy-Item -LiteralPath $upstreamLicenseSource -Destination $upstreamLicenseDestination -Force

if (-not $SkipFfmpeg) {
    $ffmpeg = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
    $winGetLink = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\ffmpeg.exe'
    if (-not $ffmpeg -and (Test-Path -LiteralPath $winGetLink)) {
        $ffmpeg = Get-Item -LiteralPath $winGetLink
    }

    if ($ffmpeg) {
        $ffmpegPath = if ($ffmpeg.Source) { $ffmpeg.Source } else { $ffmpeg.FullName }
        Write-Host "FFmpeg is already available: $ffmpegPath" -ForegroundColor Green
    } else {
        $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
        if (-not $winget) {
            throw 'FFmpeg was not found and WinGet is unavailable. Install FFmpeg manually from https://ffmpeg.org/download.html.'
        }

        Write-Host 'Installing FFmpeg through WinGet...'
        & $winget.Source install --id Gyan.FFmpeg -e --accept-source-agreements --accept-package-agreements
        if ($LASTEXITCODE -ne 0) {
            throw "WinGet could not install FFmpeg (exit code $LASTEXITCODE)."
        }

        Write-Host 'FFmpeg installation completed. Reopen the GUI if its path is not detected immediately.' -ForegroundColor Green
    }
}

Write-Host ''
Write-Host 'Dependencies are ready. Start M3U8-Video-Downloader.exe.' -ForegroundColor Cyan
