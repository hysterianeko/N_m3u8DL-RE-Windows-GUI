param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$projectDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildDirectory = Join-Path $projectDirectory 'build'
$cacheDirectory = Join-Path $buildDirectory 'dependency-cache'
$libsDirectory = Join-Path $projectDirectory 'libs'
$backupDirectory = Join-Path $projectDirectory '.webview2-backup'
$backupMarkerName = '.m3u8-gui-webview2-backup'
$backupMarkerValue = 'M3u8DownloaderGui WebView2 backup v1'
$packageVersion = '1.0.2957.106'
$packageName = "microsoft.web.webview2.$packageVersion.nupkg"
$packageUrl = "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$packageVersion/$packageName"
$packageSha256 = '4C35A54835B63954159EAC1D5B7A60AE617A41DBB5B73BFDB11C4870A891080A'
$packagePath = Join-Path $cacheDirectory $packageName
$lockPath = Join-Path $cacheDirectory 'webview2.restore.lock'

$dependencies = @(
    @{
        Name = 'Microsoft.Web.WebView2.Core.dll'
        Entry = 'lib/net462/Microsoft.Web.WebView2.Core.dll'
        Sha256 = 'CB8C852FCC4EF55D630B64D171DC11538BB25258041ED22CF31735982A2E09E3'
    },
    @{
        Name = 'Microsoft.Web.WebView2.WinForms.dll'
        Entry = 'lib/net462/Microsoft.Web.WebView2.WinForms.dll'
        Sha256 = 'E62056BEE28AB094071144B47371009A6FBC162F9AEA184719F2E86ED515F7F8'
    },
    @{
        Name = 'WebView2Loader.dll'
        Entry = 'runtimes/win-x64/native/WebView2Loader.dll'
        Sha256 = '271B57E3EC03C436A15D80CAFEB9FD1618A43793233D8B05C9446F8DE0A51BE4'
    }
)

function Test-ExpectedHash {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedHash
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $ExpectedHash
}

function Test-DependenciesAt {
    param([Parameter(Mandatory = $true)][string]$Directory)

    foreach ($dependency in $dependencies) {
        $path = Join-Path $Directory $dependency.Name
        if (-not (Test-ExpectedHash -Path $path -ExpectedHash $dependency.Sha256)) {
            return $false
        }
    }

    return $true
}

function Assert-OrdinaryDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $info = Get-Item -LiteralPath $Path -Force
    if (-not $info.PSIsContainer) {
        throw "Expected a dependency directory but found a file: $Path"
    }
    if (($info.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to modify a reparse-point dependency directory: $Path"
    }
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
        throw "Dependency path escapes the project directory: $childFull"
    }
}

function Test-OwnedBackup {
    param([Parameter(Mandatory = $true)][string]$Directory)

    $marker = Join-Path $Directory $backupMarkerName
    return (Test-Path -LiteralPath $marker -PathType Leaf) -and
        [IO.File]::ReadAllText($marker) -eq $backupMarkerValue
}

# The common path is intentionally lock-free and performs no network access.
if (-not $Force -and
    -not (Test-Path -LiteralPath $backupDirectory) -and
    -not (Test-Path -LiteralPath (Join-Path $libsDirectory $backupMarkerName)) -and
    (Test-DependenciesAt -Directory $libsDirectory)) {
    return
}

Assert-ContainedPath -Parent $projectDirectory -Child $buildDirectory
Assert-ContainedPath -Parent $projectDirectory -Child $cacheDirectory
Assert-ContainedPath -Parent $cacheDirectory -Child $packagePath
Assert-ContainedPath -Parent $cacheDirectory -Child $lockPath
Assert-OrdinaryDirectory -Path $buildDirectory
if (-not (Test-Path -LiteralPath $buildDirectory)) {
    New-Item -ItemType Directory -Path $buildDirectory | Out-Null
}
Assert-OrdinaryDirectory -Path $buildDirectory
Assert-OrdinaryDirectory -Path $cacheDirectory
if (-not (Test-Path -LiteralPath $cacheDirectory)) {
    New-Item -ItemType Directory -Path $cacheDirectory | Out-Null
}
Assert-OrdinaryDirectory -Path $cacheDirectory

$lockStream = $null
for ($attempt = 0; $attempt -lt 120 -and $null -eq $lockStream; $attempt++) {
    try {
        $lockStream = [IO.File]::Open(
            $lockPath,
            [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::None)
    } catch [IO.IOException] {
        if ($attempt -eq 119) {
            throw 'Timed out waiting for another WebView2 dependency restore to finish.'
        }
        Start-Sleep -Milliseconds 250
    }
}

$downloadPath = $null
$stagingDirectory = $null

try {
    Assert-OrdinaryDirectory -Path $libsDirectory
    Assert-OrdinaryDirectory -Path $backupDirectory

    # Recover a process interruption between the two atomic directory renames.
    if (Test-Path -LiteralPath $backupDirectory) {
        if (-not (Test-OwnedBackup -Directory $backupDirectory)) {
            throw "An unowned WebView2 backup requires inspection: $backupDirectory"
        }

        if (Test-DependenciesAt -Directory $libsDirectory) {
            try {
                Remove-Item -LiteralPath $backupDirectory -Recurse -Force
            } catch {
                Write-Warning "Could not remove the completed WebView2 backup: $($_.Exception.Message)"
            }
        } elseif (Test-DependenciesAt -Directory $backupDirectory) {
            $failedDirectory = Join-Path $projectDirectory `
                ('.webview2-failed-' + [Guid]::NewGuid().ToString('N'))
            if (Test-Path -LiteralPath $libsDirectory) {
                Move-Item -LiteralPath $libsDirectory -Destination $failedDirectory
            }
            try {
                Move-Item -LiteralPath $backupDirectory -Destination $libsDirectory
            } catch {
                if ((Test-Path -LiteralPath $failedDirectory) -and
                    -not (Test-Path -LiteralPath $libsDirectory)) {
                    Move-Item -LiteralPath $failedDirectory -Destination $libsDirectory
                }
                throw
            }
            Remove-Item -LiteralPath (Join-Path $libsDirectory $backupMarkerName) -Force
            if (Test-Path -LiteralPath $failedDirectory) {
                Remove-Item -LiteralPath $failedDirectory -Recurse -Force
            }
        } else {
            throw "An invalid interrupted WebView2 backup requires inspection: $backupDirectory"
        }
    }

    $liveBackupMarker = Join-Path $libsDirectory $backupMarkerName
    if (-not (Test-Path -LiteralPath $backupDirectory) -and
        (Test-DependenciesAt -Directory $libsDirectory) -and
        (Test-Path -LiteralPath $liveBackupMarker -PathType Leaf)) {
        Remove-Item -LiteralPath $liveBackupMarker -Force
    }

    if (-not $Force -and (Test-DependenciesAt -Directory $libsDirectory)) {
        return
    }

    if (-not (Test-ExpectedHash -Path $packagePath -ExpectedHash $packageSha256)) {
        if (Test-Path -LiteralPath $packagePath) {
            Remove-Item -LiteralPath $packagePath -Force
        }

        $downloadPath = $packagePath + '.download-' + [Guid]::NewGuid().ToString('N')
        Write-Host "Downloading verified WebView2 SDK $packageVersion..."
        [Net.ServicePointManager]::SecurityProtocol = `
            [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $packageUrl -OutFile $downloadPath -UseBasicParsing

        if (-not (Test-ExpectedHash -Path $downloadPath -ExpectedHash $packageSha256)) {
            throw "WebView2 package SHA-256 verification failed: $packageUrl"
        }

        Move-Item -LiteralPath $downloadPath -Destination $packagePath
        $downloadPath = $null
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $suffix = [Guid]::NewGuid().ToString('N')
    $stagingDirectory = Join-Path $projectDirectory ('.webview2-restore-' + $suffix)
    New-Item -ItemType Directory -Path $stagingDirectory | Out-Null

    $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        foreach ($dependency in $dependencies) {
            $entry = $archive.GetEntry($dependency.Entry)
            if ($null -eq $entry) {
                throw "WebView2 package entry is missing: $($dependency.Entry)"
            }

            $destination = Join-Path $stagingDirectory $dependency.Name
            $input = $entry.Open()
            $output = [IO.File]::Open(
                $destination,
                [IO.FileMode]::CreateNew,
                [IO.FileAccess]::Write,
                [IO.FileShare]::None)
            try {
                $input.CopyTo($output)
            } finally {
                $output.Dispose()
                $input.Dispose()
            }

            if (-not (Test-ExpectedHash -Path $destination -ExpectedHash $dependency.Sha256)) {
                throw "Extracted WebView2 dependency SHA-256 verification failed: $($dependency.Name)"
            }
        }
    } finally {
        $archive.Dispose()
    }

    $hadExistingDependencies = Test-Path -LiteralPath $libsDirectory
    if ($hadExistingDependencies) {
        [IO.File]::WriteAllText(
            (Join-Path $libsDirectory $backupMarkerName),
            $backupMarkerValue)
        Move-Item -LiteralPath $libsDirectory -Destination $backupDirectory
    }

    try {
        Move-Item -LiteralPath $stagingDirectory -Destination $libsDirectory
        $stagingDirectory = $null

        if (-not (Test-DependenciesAt -Directory $libsDirectory)) {
            throw 'Installed WebView2 dependencies failed final verification.'
        }
    } catch {
        $failedDirectory = Join-Path $projectDirectory `
            ('.webview2-failed-' + [Guid]::NewGuid().ToString('N'))
        if (Test-Path -LiteralPath $libsDirectory) {
            Move-Item -LiteralPath $libsDirectory -Destination $failedDirectory
        }
        if (Test-Path -LiteralPath $backupDirectory) {
            Move-Item -LiteralPath $backupDirectory -Destination $libsDirectory
            Remove-Item -LiteralPath (Join-Path $libsDirectory $backupMarkerName) -Force
        }
        if (Test-Path -LiteralPath $failedDirectory) {
            Remove-Item -LiteralPath $failedDirectory -Recurse -Force
        }
        throw
    }

    if (Test-Path -LiteralPath $backupDirectory) {
        if (-not (Test-OwnedBackup -Directory $backupDirectory)) {
            throw "Refusing to remove an unowned WebView2 backup: $backupDirectory"
        }
        try {
            Remove-Item -LiteralPath $backupDirectory -Recurse -Force
        } catch {
            Write-Warning "Could not remove the completed WebView2 backup: $($_.Exception.Message)"
        }
    }

    Write-Host "WebView2 SDK $packageVersion restored and verified."
} finally {
    if ($null -ne $downloadPath -and (Test-Path -LiteralPath $downloadPath)) {
        Remove-Item -LiteralPath $downloadPath -Force
    }
    if ($null -ne $stagingDirectory -and (Test-Path -LiteralPath $stagingDirectory)) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
    if ($null -ne $lockStream) {
        $lockStream.Dispose()
    }
}
