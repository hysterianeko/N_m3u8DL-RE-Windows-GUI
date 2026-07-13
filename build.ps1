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

if (-not (Test-Path -LiteralPath $compiler)) {
    throw "C# compiler not found: $compiler"
}

New-Item -ItemType Directory -Force -Path $buildDirectory | Out-Null

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
    '/platform:anycpu'
    '/optimize+'
    '/codepage:65001'
    '/utf8output'
    "/reference:$framework\System.dll"
    "/reference:$framework\System.Core.dll"
    "/reference:$framework\System.Drawing.dll"
    "/reference:$framework\System.Windows.Forms.dll"
    "/reference:$framework\System.Web.dll"
    "/reference:$framework\System.Xml.dll"
    (Join-Path $projectDirectory 'Program.cs')
    (Join-Path $projectDirectory 'AppUtilities.cs')
    (Join-Path $projectDirectory 'MainForm.cs')
    (Join-Path $projectDirectory 'HlsKeyDialog.cs')
)

$testPath = Join-Path $buildDirectory 'SelfTests.exe'
$testArguments = @(
    '/target:exe'
    "/out:$testPath"
    '/main:M3u8DownloaderGui.SelfTests'
) + $commonArguments + @((Join-Path $projectDirectory 'SelfTests.cs'))

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

if (-not $SkipDesktopCopy) {
    Copy-Item -LiteralPath $outputPath -Destination $desktopPath -Force
}

Write-Host ''
Write-Host 'Build complete:' -ForegroundColor Green
Write-Host $(if ($SkipDesktopCopy) { $outputPath } else { $desktopPath })
