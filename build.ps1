param(
    [switch]$SkipDesktopCopy
)

$ErrorActionPreference = 'Stop'

$projectDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildDirectory = Join-Path $projectDirectory 'build'
$desktopDirectory = [Environment]::GetFolderPath('Desktop')
$outputName = 'M3U8' + [char]0x89C6 + [char]0x9891 + [char]0x4E0B + [char]0x8F7D + [char]0x5668 + '.exe'
$outputPath = Join-Path $buildDirectory $outputName
$desktopPath = Join-Path $desktopDirectory $outputName
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'

function Copy-FileIfChanged {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (Test-Path -LiteralPath $Destination) {
        $sourceInfo = Get-Item -LiteralPath $Source
        $destinationInfo = Get-Item -LiteralPath $Destination
        if ($sourceInfo.Length -eq $destinationInfo.Length) {
            $sourceHash = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
            $destinationHash = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash
            if ($sourceHash -eq $destinationHash) {
                return
            }
        }
    }

    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

if (-not (Test-Path -LiteralPath $compiler)) {
    throw "C# compiler not found: $compiler"
}

New-Item -ItemType Directory -Force -Path $buildDirectory | Out-Null

& (Join-Path $projectDirectory 'restore-webview2.ps1')

$iconPath = Join-Path $buildDirectory 'app.ico'
Add-Type -AssemblyName System.Drawing
$icon = [System.Drawing.SystemIcons]::Application

$iconStream = [System.IO.File]::Create($iconPath)
try {
    $icon.Save($iconStream)
} finally {
    $iconStream.Dispose()
}

$commonArguments = @(
    '/nologo'
    '/platform:x64'
    '/optimize+'
    '/codepage:65001'
    '/utf8output'
    "/reference:$framework\System.dll"
    "/reference:$framework\System.Core.dll"
    "/reference:$framework\System.Drawing.dll"
    "/reference:$framework\System.IO.Compression.dll"
    "/reference:$framework\System.IO.Compression.FileSystem.dll"
    "/reference:$framework\System.Security.dll"
    "/reference:$framework\System.Windows.Forms.dll"
    "/reference:$framework\System.Web.dll"
    "/reference:$framework\System.Xml.dll"
    "/reference:$(Join-Path $projectDirectory 'libs\Microsoft.Web.WebView2.Core.dll')"
    "/reference:$(Join-Path $projectDirectory 'libs\Microsoft.Web.WebView2.WinForms.dll')"
    (Join-Path $projectDirectory 'Program.cs')
    (Join-Path $projectDirectory 'AppUtilities.cs')
    (Join-Path $projectDirectory 'DependencyInstaller.cs')
    (Join-Path $projectDirectory 'MainForm.cs')
    (Join-Path $projectDirectory 'HlsKeyDialog.cs')
    (Join-Path $projectDirectory 'M3u8SizeProbe.cs')
    (Join-Path $projectDirectory 'HlsPlaylistInspector.cs')
    (Join-Path $projectDirectory 'WebView2Runtime.cs')
    (Join-Path $projectDirectory 'CaptureBrowserForm.cs')
    (Join-Path $projectDirectory 'LocalPlayer.cs')
    (Join-Path $projectDirectory 'CurlMediaProxy.cs')
    (Join-Path $projectDirectory 'DownloadResumeStore.cs')
)

$testPath = Join-Path $buildDirectory 'SelfTests.exe'
$testArguments = @(
    '/target:exe'
    "/out:$testPath"
    '/main:M3u8DownloaderGui.SelfTests'
) + $commonArguments + @(
    (Join-Path $projectDirectory 'SelfTests.cs')
    (Join-Path $projectDirectory 'DownloadResumeStoreSelfTests.cs')
)

& $compiler @testArguments
if ($LASTEXITCODE -ne 0) {
    throw "Test compilation failed with exit code $LASTEXITCODE"
}

& $testPath
if ($LASTEXITCODE -ne 0) {
    throw "Self-tests failed with exit code $LASTEXITCODE"
}

$applicationArguments = @(
    '/target:winexe'
    "/out:$outputPath"
    "/win32manifest:$(Join-Path $projectDirectory 'app.manifest')"
    "/win32icon:$iconPath"
    '/main:M3u8DownloaderGui.Program'
) + $commonArguments

& $compiler @applicationArguments
if ($LASTEXITCODE -ne 0) {
    throw "Application compilation failed with exit code $LASTEXITCODE"
}

# WebView2 assemblies are referenced, not merged, so all three DLLs must sit
# next to the exe at run time: the two managed assemblies and the native loader.
$webViewDlls = @(
    'Microsoft.Web.WebView2.Core.dll',
    'Microsoft.Web.WebView2.WinForms.dll',
    'WebView2Loader.dll'
)
foreach ($dll in $webViewDlls) {
    $dllSource = Join-Path $projectDirectory (Join-Path 'libs' $dll)
    if (-not (Test-Path -LiteralPath $dllSource)) {
        throw "WebView2 dependency not found: $dllSource"
    }
    Copy-FileIfChanged -Source $dllSource -Destination (Join-Path $buildDirectory $dll)
}

if (-not $SkipDesktopCopy) {
    Copy-Item -LiteralPath $outputPath -Destination $desktopPath -Force
    foreach ($dll in $webViewDlls) {
        Copy-FileIfChanged `
            -Source (Join-Path $projectDirectory (Join-Path 'libs' $dll)) `
            -Destination (Join-Path $desktopDirectory $dll)
    }
}

Write-Host ''
Write-Host 'Build complete:' -ForegroundColor Green
Write-Host $(if ($SkipDesktopCopy) { $outputPath } else { $desktopPath })
