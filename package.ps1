param(
    [string]$Version,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$projectDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildDirectory = Join-Path $projectDirectory 'build'
$distDirectory = Join-Path $projectDirectory 'dist'
$applicationName = 'M3U8' + [char]0x89C6 + [char]0x9891 + [char]0x4E0B + [char]0x8F7D + [char]0x5668 + '.exe'
$builtApplication = Join-Path $buildDirectory $applicationName
$packageApplicationName = 'M3U8-Video-Downloader.exe'
$stagingMarkerName = '.m3u8-gui-release-staging'
$stagingMarkerValue = 'M3u8DownloaderGui release staging v1'

function Assert-OrdinaryDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $info = Get-Item -LiteralPath $Path -Force
    if (-not $info.PSIsContainer -or
        ($info.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to clean a non-directory or reparse-point release path: $Path"
    }
}

function Remove-OwnedStagingDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    Assert-OrdinaryDirectory -Path $Path
    $marker = Join-Path $Path $stagingMarkerName
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf) -or
        [IO.File]::ReadAllText($marker) -ne $stagingMarkerValue) {
        throw "Refusing to delete an unowned release staging directory: $Path"
    }

    Remove-Item -LiteralPath $Path -Recurse -Force
}

function Assert-ContainedPath {
    param(
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$Child
    )

    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $childFull = [IO.Path]::GetFullPath($Child)
    if (-not $childFull.StartsWith($parentFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release path escapes its managed directory: $childFull"
    }
}

$programSource = [System.IO.File]::ReadAllText((Join-Path $projectDirectory 'Program.cs'))
$versionMatch = [regex]::Match($programSource, 'AssemblyFileVersion\("(?<version>\d+\.\d+\.\d+)\.\d+"\)')
if (-not $versionMatch.Success) {
    throw 'Could not read AssemblyFileVersion from Program.cs.'
}
$sourceVersion = $versionMatch.Groups['version'].Value
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $sourceVersion
}
$Version = $Version.Trim()
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') {
    throw "Package version is not a safe semantic version: $Version"
}
$requestedVersionCore = ($Version -split '-', 2)[0]
if ($requestedVersionCore -ne $sourceVersion) {
    throw "Package version $Version does not match AssemblyFileVersion $sourceVersion."
}

$packageName = "M3U8-Video-Downloader-v$Version-win-x64"
$stagingRoot = Join-Path $buildDirectory 'release-staging'
$packageDirectory = Join-Path $stagingRoot $packageName
$zipPath = Join-Path $distDirectory ($packageName + '.zip')
Assert-ContainedPath -Parent $stagingRoot -Child $packageDirectory
Assert-ContainedPath -Parent $distDirectory -Child $zipPath

if (-not $SkipBuild) {
    & (Join-Path $projectDirectory 'build.ps1') -SkipDesktopCopy
}

if (-not (Test-Path -LiteralPath $builtApplication)) {
    throw "Application build was not found: $builtApplication"
}

if (Test-Path -LiteralPath $distDirectory) {
    Assert-OrdinaryDirectory -Path $distDirectory
    $managedReleasePattern = '^M3U8-Video-Downloader-v[0-9A-Za-z._-]+-win-x64(?:\.exe|\.zip)?$'
    foreach ($entry in Get-ChildItem -LiteralPath $distDirectory -Force) {
        if ($entry.Name -ne 'SHA256SUMS.txt' -and
            $entry.Name -notmatch $managedReleasePattern) {
            continue
        }
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to delete a reparse-point release artifact: $($entry.FullName)"
        }

        Remove-Item -LiteralPath $entry.FullName -Recurse -Force
    }
}

Remove-OwnedStagingDirectory -Path $stagingRoot
New-Item -ItemType Directory -Force -Path $distDirectory | Out-Null
New-Item -ItemType Directory -Path $stagingRoot | Out-Null
[IO.File]::WriteAllText((Join-Path $stagingRoot $stagingMarkerName), $stagingMarkerValue)
New-Item -ItemType Directory -Path $packageDirectory | Out-Null
Copy-Item -LiteralPath $builtApplication -Destination (Join-Path $packageDirectory $packageApplicationName)

# WebView2 assemblies are referenced, not merged, so all three DLLs must ship
# beside the exe: the two managed assemblies and the native loader.
$webViewDlls = @(
    'Microsoft.Web.WebView2.Core.dll',
    'Microsoft.Web.WebView2.WinForms.dll',
    'WebView2Loader.dll'
)
foreach ($dll in $webViewDlls) {
    $dllSource = Join-Path $buildDirectory $dll
    if (-not (Test-Path -LiteralPath $dllSource)) {
        throw "WebView2 dependency not found in build output: $dllSource"
    }
    Copy-Item -LiteralPath $dllSource -Destination (Join-Path $packageDirectory $dll) -Force
}

$packageFiles = @(
    'QUICKSTART.zh-CN.md',
    'setup-dependencies.ps1',
    'Setup-dependencies.cmd',
    'LICENSE',
    'LICENSE.N_m3u8DL-RE',
    'LICENSE.WebView2',
    'NOTICE.WebView2',
    'THIRD_PARTY_NOTICES.md'
)
foreach ($file in $packageFiles) {
    Copy-Item -LiteralPath (Join-Path $projectDirectory $file) -Destination (Join-Path $packageDirectory $file)
}

try {
    Compress-Archive -Path (Join-Path $packageDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal
} finally {
    Remove-OwnedStagingDirectory -Path $stagingRoot
}

$hashFiles = @($zipPath)
$hashLines = foreach ($file in $hashFiles) {
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path -Leaf $file)"
}
$hashPath = Join-Path $distDirectory 'SHA256SUMS.txt'
$utf8WithoutBom = New-Object System.Text.UTF8Encoding -ArgumentList $false
[System.IO.File]::WriteAllLines($hashPath, $hashLines, $utf8WithoutBom)

Write-Host ''
Write-Host 'Release package complete:' -ForegroundColor Green
Write-Host $zipPath
Write-Host $hashPath
