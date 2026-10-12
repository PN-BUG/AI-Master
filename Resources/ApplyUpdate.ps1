param(
    [Parameter(Mandatory = $true)][int]$ApplicationProcessId,
    [Parameter(Mandatory = $true)][long]$ApplicationStartTicks,
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [Parameter(Mandatory = $true)][string]$StagedDirectory,
    [Parameter(Mandatory = $true)][string]$Workspace,
    [Parameter(Mandatory = $true)][string]$LogDirectory,
    [bool]$PreserveToolkitFiles = $false,
    [bool]$English = $false,
    [switch]$StopApplication
)

$ErrorActionPreference = 'Stop'
$lockStream = $null
$journal = @()
$transactionDir = $null
$mutating = $false
$restart = $null
$exitCode = 1
$logPath = $null
$applicationExited = $false
$recovering = $false

function Assert-ContainedPath([string]$Root, [string]$Path) {
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path outside update directory: $Path"
    }
    return $fullPath
}

function Assert-NoLinks([string]$Path) {
    $itemPath = [IO.Path]::GetFullPath($Path)
    while ($itemPath) {
        if (Test-Path -LiteralPath $itemPath) {
            if ((Get-Item -LiteralPath $itemPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Update path contains a symbolic link or junction: $itemPath"
            }
        }
        $itemPath = [IO.Path]::GetDirectoryName($itemPath)
    }
}

function Write-UpdateLog([string]$Message) {
    if ($logPath) { Add-Content -LiteralPath $logPath -Value ("{0:o} {1}" -f [DateTime]::UtcNow, $Message) -Encoding UTF8 }
}

function Get-Sha256([string]$Path) {
    $inputStream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($inputStream)).Replace('-', '') }
    finally { $algorithm.Dispose(); $inputStream.Dispose() }
}

function Restore-Files([object[]]$Entries) {
    for ($i = $Entries.Count - 1; $i -ge 0; $i--) {
        $entry = $Entries[$i]
        $target = Assert-ContainedPath $installDir (Join-Path $installDir $entry.Relative)
        $backup = Assert-ContainedPath $transactionDir (Join-Path $transactionDir ('backup\' + $entry.Relative))
        Assert-NoLinks $target
        Assert-NoLinks $backup
        if ($entry.Existed) {
            # File.Replace creates the backup as part of the replacement. No backup means
            # this entry was journaled but never replaced.
            if (Test-Path -LiteralPath $backup -PathType Leaf) {
                if (Test-Path -LiteralPath $target -PathType Leaf) {
                    [IO.File]::Replace($backup, $target, [NullString]::Value)
                } else { [IO.File]::Move($backup, $target) }
            }
        } elseif (Test-Path -LiteralPath $target -PathType Leaf) {
            Remove-Item -LiteralPath $target -Force
        }
    }
}

function Get-OriginalProcess {
    $candidate = Get-Process -Id $ApplicationProcessId -ErrorAction SilentlyContinue
    if (-not $candidate) { return $null }
    if ($candidate.StartTime.ToUniversalTime().Ticks -ne $ApplicationStartTicks) { return $null }
    if (-not [string]::Equals($candidate.Path, $ExecutablePath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The running application does not match the update target.'
    }
    return $candidate
}

try {
    $ExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
    $installDir = [IO.Path]::GetDirectoryName($ExecutablePath)
    $StagedDirectory = [IO.Path]::GetFullPath($StagedDirectory).TrimEnd('\')
    $Workspace = [IO.Path]::GetFullPath($Workspace)
    if (-not (Test-Path -LiteralPath $Workspace -PathType Container)) { throw 'Missing update workspace.' }
    $executableName = [IO.Path]::GetFileName($ExecutablePath)
    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) { throw 'Missing installed executable.' }
    Assert-NoLinks $ExecutablePath
    Assert-NoLinks $StagedDirectory
    if ([string]::Equals($installDir, $StagedDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The update must be staged separately from the installation.'
    }
    $null = New-Item -ItemType Directory -Path $LogDirectory -Force
    $logPath = Join-Path $LogDirectory ('update-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N') + '.log')
    Write-UpdateLog "Preparing update: $ExecutablePath"
    $lockPath = Join-Path $installDir '.aimaster-update.lock'
    Assert-NoLinks $lockPath
    $lockStream = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $transactionDir = Join-Path $installDir '.aimaster-update'
    Assert-NoLinks $transactionDir
    $journalPath = Join-Path $transactionDir 'journal.json'

    # Read a previous transaction now, but only restore it once the application exits.
    $previous = $null
    if (Test-Path -LiteralPath $journalPath -PathType Leaf) {
        $previous = Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
    }
    $preparedDir = Join-Path $Workspace 'prepared'
    $backupDir = Join-Path $transactionDir 'backup'
    $files = @(Get-ChildItem -LiteralPath $StagedDirectory -File -Recurse -Force | Sort-Object FullName)
    if (-not ($files | Where-Object { $_.FullName -ieq (Join-Path $StagedDirectory $executableName) })) {
        throw 'The staged package has no application executable at its root.'
    }
    $plan = @()
    foreach ($file in $files) {
        Assert-NoLinks $file.FullName
        $relative = $file.FullName.Substring($StagedDirectory.Length).TrimStart('\')
        if ($relative -imatch '(^|\\)(settings\.json|ai-manager\.json|\.env(?:\..*)?|\.aimaster-update(?:\.lock)?)(\\|$)') {
            throw "The package contains a reserved user-data or updater path: $relative"
        }
        if ($PreserveToolkitFiles -and ($relative -ieq 'tools.json' -or $relative -like 'tools\*')) { continue }
        $destination = Assert-ContainedPath $installDir (Join-Path $installDir $relative)
        $incoming = Assert-ContainedPath $preparedDir (Join-Path $preparedDir $relative)
        Assert-NoLinks $destination
        if (Test-Path -LiteralPath $destination -PathType Container) { throw "A directory conflicts with a package file: $relative" }
        $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($incoming)) -Force
        Copy-Item -LiteralPath $file.FullName -Destination $incoming
        if ((Get-Sha256 $incoming) -ne (Get-Sha256 $file.FullName)) {
            throw "The staged copy failed verification: $relative"
        }
        $plan += [pscustomobject]@{ Relative = $relative; Existed = $false }
    }

    # The parent only exits after this acknowledgement. Permission/path failures above
    # therefore leave the running application untouched.
    [IO.File]::WriteAllText((Join-Path $Workspace 'ready'), $ExecutablePath)
    if ($StopApplication) {
        $original = Get-OriginalProcess
        if ($original) { Stop-Process -Id $original.Id -ErrorAction Stop }
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while (Get-OriginalProcess) {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'The application did not exit in time.' }
        Start-Sleep -Milliseconds 250
    }
    $applicationExited = $true
    if ($previous -and $previous.State -notin @('Committed', 'RolledBack')) {
        $recovering = $true
        Restore-Files -Entries @($previous.Files)
        Write-UpdateLog 'Recovered the previous interrupted update.'
        $recovering = $false
    }
    if (Test-Path -LiteralPath $transactionDir) {
        $null = Assert-ContainedPath $installDir $transactionDir
        Remove-Item -LiteralPath $transactionDir -Recurse -Force
    }
    $null = New-Item -ItemType Directory -Path $transactionDir
    $incomingDir = Join-Path $transactionDir 'incoming'
    foreach ($entry in $plan) {
        $destination = Assert-ContainedPath $installDir (Join-Path $installDir $entry.Relative)
        Assert-NoLinks $destination
        $incoming = Join-Path $incomingDir $entry.Relative
        $backup = Join-Path $backupDir $entry.Relative
        $entry.Existed = Test-Path -LiteralPath $destination -PathType Leaf
        $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force
        $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($backup)) -Force
        $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($incoming)) -Force
        Copy-Item -LiteralPath (Join-Path $preparedDir $entry.Relative) -Destination $incoming
        $journal += $entry
        # Write the journal BEFORE replacing a file; a partially completed operation
        # must also be considered by rollback and interruption recovery.
        [IO.File]::WriteAllText(($journalPath + '.tmp'), (@{ State = 'Applying'; Files = $journal } | ConvertTo-Json -Depth 5))
        if (Test-Path -LiteralPath $journalPath) { [IO.File]::Replace(($journalPath + '.tmp'), $journalPath, [NullString]::Value) }
        else { [IO.File]::Move(($journalPath + '.tmp'), $journalPath) }
        $mutating = $true
        $attempt = 0
        while ($true) {
            try {
                if ($entry.Existed) { [IO.File]::Replace($incoming, $destination, $backup) }
                else { [IO.File]::Move($incoming, $destination) }
                break
            } catch [IO.IOException] {
                $attempt++
                if ($attempt -ge 12) { throw }
                Start-Sleep -Milliseconds 250
            }
        }
        Write-UpdateLog "Replaced $($entry.Relative)"
    }
    $restart = Start-Process -FilePath $ExecutablePath -WorkingDirectory $installDir -ArgumentList '--update-restart' -PassThru
    if ($restart.WaitForExit(3000)) { throw "The updated application exited during startup (exit code $($restart.ExitCode))." }
    [IO.File]::WriteAllText(($journalPath + '.tmp'), (@{ State = 'Committed'; Files = $journal } | ConvertTo-Json -Depth 5))
    [IO.File]::Replace(($journalPath + '.tmp'), $journalPath, [NullString]::Value)
    Write-UpdateLog 'Update committed and application restarted from its original path.'
    $null = Assert-ContainedPath $installDir $transactionDir
    Remove-Item -LiteralPath $transactionDir -Recurse -Force -ErrorAction SilentlyContinue
    $exitCode = 0
} catch {
    $failure = $_.Exception.Message
    $restored = -not $mutating
    try { Write-UpdateLog "FAILED: $failure" } catch { }
    if ($mutating) {
        try {
            if ($restart -and -not $restart.HasExited) { $restart.Kill(); $restart.WaitForExit() }
            Restore-Files -Entries $journal
            $restored = $true
            [IO.File]::WriteAllText($journalPath, (@{ State = 'RolledBack'; Files = $journal } | ConvertTo-Json -Depth 5))
            Write-UpdateLog 'Rollback completed.'
            Start-Process -FilePath $ExecutablePath -WorkingDirectory $installDir -ArgumentList '--update-restart' | Out-Null
        } catch {
            $failure += "`nRollback/restart error: $($_.Exception.Message)"
            try { Write-UpdateLog $failure } catch { }
        }
    } elseif ($applicationExited -and -not $recovering) {
        try { Start-Process -FilePath $ExecutablePath -WorkingDirectory $installDir -ArgumentList '--update-restart' | Out-Null } catch { $failure += "`nRestart error: $($_.Exception.Message)" }
    }
    $message = if ($English) { "Update failed. $failure" } else { "更新失败。$failure" }
    if (($mutating -and -not $restored) -or $recovering) {
        $message += "`nBackup retained at: $transactionDir"
    }
    if ($logPath) { $message += "`nLog: $logPath" }
    [IO.File]::WriteAllText((Join-Path $Workspace 'error.txt'), $message)
    # Only show a dialog if the parent has already exited; otherwise it displays the error.
    if ($mutating -and -not $StopApplication) {
        Add-Type -AssemblyName PresentationFramework
        $title = if ($English) { 'Update failed' } else { '更新失败' }
        [System.Windows.MessageBox]::Show($message, $title) | Out-Null
    }
} finally {
    if ($lockStream) { $lockStream.Dispose() }
    # Keep the workspace, journal and backups on failure for diagnosis and recovery.
    if ($exitCode -eq 0) {
        $null = Assert-ContainedPath ([IO.Path]::GetTempPath()) $Workspace
        Remove-Item -LiteralPath $Workspace -Recurse -Force -ErrorAction SilentlyContinue
    }
}
exit $exitCode
