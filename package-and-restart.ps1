[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'release'))
$packageWorkspace = Join-Path $releaseRoot ('.package-' + [Guid]::NewGuid().ToString('N'))
$stagingRoot = Join-Path $packageWorkspace 'staged'
$backupRoot = Join-Path $packageWorkspace 'backup'
$published = @()
$completed = $false
$launched = $null
$applicationStopped = $false

function Get-PackageSha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}

function Assert-ReleasePath([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($releaseRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path outside the release directory: $Path"
    }
    $ancestor = $fullPath
    while ($ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and
            ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Package path contains a symbolic link or junction: $ancestor"
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    return $fullPath
}

# Capture the actual installation before building. Never infer it from shortcuts.
$applications = @(Get-Process -Name AIMaster -ErrorAction SilentlyContinue)
if ($applications.Count -gt 1) {
    throw 'Multiple AIMaster processes are running. Exit the extra instances before packaging.'
}
$application = if ($applications.Count -eq 1) { $applications[0] } else { $null }
$installationDir = $null
$runningMode = 'standalone'
if ($application) {
    if (-not $application.Path) { throw 'Cannot identify the running AIMaster executable. No files were replaced.' }
    $executablePath = [IO.Path]::GetFullPath($application.Path)
    $installationDir = [IO.Path]::GetDirectoryName($executablePath)
    $startedAt = $application.StartTime.ToUniversalTime().Ticks
    if (Test-Path -LiteralPath (Join-Path $installationDir 'AIMaster.runtimeconfig.json') -PathType Leaf) {
        $runningMode = 'lightweight'
    }
    $stream = [IO.File]::OpenRead($executablePath)
    try {
        $reader = New-Object IO.BinaryReader($stream)
        $stream.Position = 0x3c
        $stream.Position = $reader.ReadInt32() + 4
        $machine = $reader.ReadUInt16()
        $runningRuntime = switch ($machine) {
            0x8664 { 'win-x64' }
            0xaa64 { 'win-arm64' }
            default { throw 'Unsupported running executable architecture.' }
        }
    } finally { $stream.Dispose() }
    if ($runningRuntime -ne $Runtime) {
        throw "The running application uses $runningRuntime. Run package-all.cmd -Runtime $runningRuntime."
    }
    Write-Host "Running installation to update and restart: $executablePath" -ForegroundColor Cyan
}

$packageNames = @("AIMaster-$Runtime-standalone", "AIMaster-$Runtime-lightweight")
foreach ($name in $packageNames) {
    $destination = Assert-ReleasePath (Join-Path $releaseRoot $name)
    if ($installationDir -and $installationDir.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "The running installation is nested inside a package output directory: $installationDir"
    }
}

try {
    $null = Assert-ReleasePath $stagingRoot
    $null = New-Item -ItemType Directory -Path $stagingRoot -Force
    if ($application) {
        $original = Get-Process -Id $application.Id -ErrorAction SilentlyContinue
        if ($original) {
            if ($original.StartTime.ToUniversalTime().Ticks -ne $startedAt -or $original.Path -ine $executablePath) {
                throw 'The running process identity changed. No application was stopped.'
            }
            Write-Host "Stopping AIMaster before packaging (PID $($original.Id))..." -ForegroundColor Yellow
            Stop-Process -Id $original.Id -ErrorAction Stop
            $applicationStopped = $true
            if (-not $original.WaitForExit(10000)) { throw 'AIMaster did not exit before packaging.' }
            Write-Host 'AIMaster has exited. Starting packaging.' -ForegroundColor Green
        } else { $applicationStopped = $true }
    }
    Write-Host 'Building standalone and lightweight packages...'
    & (Join-Path $PSScriptRoot 'build.ps1') -All -Configuration $Configuration -Runtime $Runtime -NoZip:$NoZip -OutputRoot $stagingRoot

    # Check every artifact before touching the previous outputs or application.
    $artifacts = @()
    foreach ($name in $packageNames) {
        $package = Join-Path $stagingRoot $name
        if (-not (Test-Path -LiteralPath (Join-Path $package 'AIMaster.exe') -PathType Leaf)) {
            throw "Missing staged executable: $package"
        }
        $artifacts += $name
        if (-not $NoZip) { $artifacts += "$name.zip", "$name.zip.sha256" }
    }
    foreach ($name in $artifacts) {
        if (-not (Test-Path -LiteralPath (Join-Path $stagingRoot $name))) {
            throw "Missing staged artifact: $name"
        }
    }

    foreach ($name in $artifacts) {
        $destination = Assert-ReleasePath (Join-Path $releaseRoot $name)
        # The transaction engine updates this directory in place after all other
        # artifacts are ready; deleting it would destroy the running installation.
        if ($installationDir -and $destination -ieq $installationDir) { continue }
        $source = Assert-ReleasePath (Join-Path $stagingRoot $name)
        $backup = Assert-ReleasePath (Join-Path $backupRoot $name)
        $entry = [pscustomobject]@{ Destination = $destination; Backup = $backup; Installed = $false; BackedUp = $false }
        $published += $entry
        if (Test-Path -LiteralPath $destination) {
            $null = New-Item -ItemType Directory -Path $backupRoot -Force
            Move-Item -LiteralPath $destination -Destination $backup
            $entry.BackedUp = $true
        }
        Move-Item -LiteralPath $source -Destination $destination
        $entry.Installed = $true
    }

    if ($application) {
        $stage = Join-Path $stagingRoot "AIMaster-$Runtime-$runningMode"
        # When the installation is outside the package outputs, its package folder
        # has already moved into release. Use that same build, without republishing.
        if (-not (Test-Path -LiteralPath $stage)) { $stage = Join-Path $releaseRoot "AIMaster-$Runtime-$runningMode" }
        $expectedHash = Get-PackageSha256 (Join-Path $stage 'AIMaster.exe')
        $updateWorkspace = Join-Path ([IO.Path]::GetTempPath()) ('AIMaster-package-update-' + [Guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path $updateWorkspace
        & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Resources\ApplyUpdate.ps1') `
            -ApplicationProcessId $application.Id -ApplicationStartTicks $startedAt -ExecutablePath $executablePath `
            -StagedDirectory $stage -Workspace $updateWorkspace -LogDirectory (Join-Path $env:LOCALAPPDATA 'AIMaster\UpdateLogs') -StopApplication
        if ($LASTEXITCODE -ne 0) {
            $errorFile = Join-Path $updateWorkspace 'error.txt'
            if (Test-Path -LiteralPath $errorFile) { throw [IO.File]::ReadAllText($errorFile) }
            throw "Update failed. Diagnostics: $updateWorkspace"
        }
        # The updater has committed and restarted; do not roll back package outputs
        # independently after this point.
        $completed = $true
        if ((Get-PackageSha256 $executablePath) -ne $expectedHash) {
            throw 'The installed executable does not match the new package.'
        }
    } else {
        $executablePath = Join-Path $releaseRoot "AIMaster-$Runtime-standalone\AIMaster.exe"
        $launched = Start-Process -FilePath $executablePath -WorkingDirectory ([IO.Path]::GetDirectoryName($executablePath)) -PassThru
        if ($launched.WaitForExit(3000)) { throw 'The packaged application exited during startup.' }
        $completed = $true
    }
    Write-Host "Packaging complete. New version opened: $executablePath" -ForegroundColor Green
} catch {
    if (-not $completed) {
        if ($launched -and -not $launched.HasExited) { $launched.Kill(); $launched.WaitForExit() }
        for ($i = $published.Count - 1; $i -ge 0; $i--) {
            $entry = $published[$i]
            $destination = Assert-ReleasePath $entry.Destination
            $backup = Assert-ReleasePath $entry.Backup
            if ($entry.Installed) { Remove-Item -LiteralPath $destination -Recurse -Force }
            if ($entry.BackedUp) { Move-Item -LiteralPath $backup -Destination $destination }
        }
        if ($applicationStopped) {
            # The update engine may already have restarted the restored version.
            $running = @(Get-Process -Name AIMaster -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $executablePath })
            $journalPath = Join-Path $installationDir '.aimaster-update\journal.json'
            $needsRecovery = $false
            if (Test-Path -LiteralPath $journalPath) {
                $journal = Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
                $needsRecovery = $journal.State -notin @('Committed', 'RolledBack')
            }
            if ($running.Count -eq 0 -and -not $needsRecovery) {
                Write-Host "Reopening the previous version: $executablePath" -ForegroundColor Yellow
                Start-Process -FilePath $executablePath -WorkingDirectory $installationDir | Out-Null
            }
        }
    }
    Write-Host "Packaging failed. Diagnostics retained at: $packageWorkspace" -ForegroundColor Red
    throw
} finally {
    if ($completed) {
        $workspacePath = Assert-ReleasePath $packageWorkspace
        Remove-Item -LiteralPath $workspacePath -Recurse -Force -ErrorAction SilentlyContinue
    }
}
