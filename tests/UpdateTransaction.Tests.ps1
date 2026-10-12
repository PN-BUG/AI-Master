$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$engine = Join-Path $repository 'Resources\ApplyUpdate.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('AIMaster-tests-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root
$completed = $false

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    Write-Host "PASS: $Message"
}

function New-Case([string]$Name) {
    $path = Join-Path $root $Name
    $install = Join-Path $path "installation with spaces and 'quote"
    $stage = Join-Path $path 'staged'
    $workspace = Join-Path $path 'workspace'
    foreach ($directory in @($install, $stage, $workspace)) { $null = New-Item -ItemType Directory -Path $directory -Force }
    [IO.File]::WriteAllText((Join-Path $install 'AIMaster.exe'), 'old executable')
    [IO.File]::WriteAllText((Join-Path $install 'settings.json'), 'user configuration')
    [IO.File]::WriteAllText((Join-Path $stage 'AIMaster.exe'), 'new executable')
    return @{ Path = $path; Install = $install; Stage = $stage; Workspace = $workspace }
}

function Invoke-Case($Case, [string]$MockBody = '') {
    $script = Join-Path $Case.Path 'test-runner.ps1'
    $quote = { param($value) "'" + $value.Replace("'", "''") + "'" }
    $mock = @'
function Start-Process {
    param($FilePath, $WorkingDirectory, $ArgumentList, [switch]$PassThru)
    [IO.File]::WriteAllText($env:AIMASTER_TEST_LAUNCH, $FilePath + "`n" + $WorkingDirectory)
    MOCK_BODY
    if ($PassThru) {
        $process = New-Object PSObject -Property @{ HasExited = $false; ExitCode = 1 }
        $process | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { return $false }
        $process | Add-Member -MemberType ScriptMethod -Name Kill -Value { $this.HasExited = $true }
        return $process
    }
}
'@
    $mock = $mock.Replace('MOCK_BODY', $MockBody)
    $invoke = "& $(& $quote $engine) -ApplicationProcessId 2147483647 -ApplicationStartTicks 0 -ExecutablePath $(& $quote (Join-Path $Case.Install 'AIMaster.exe')) -StagedDirectory $(& $quote $Case.Stage) -Workspace $(& $quote $Case.Workspace) -LogDirectory $(& $quote (Join-Path $Case.Path 'logs')) -StopApplication"
    [IO.File]::WriteAllText($script, $mock + "`n" + $invoke + "`nexit `$LASTEXITCODE", (New-Object Text.UTF8Encoding($true)))
    $env:AIMASTER_TEST_LAUNCH = Join-Path $Case.Path 'launch.txt'
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $script
    return $LASTEXITCODE
}

try {
    $success = New-Case 'success'
    [IO.File]::WriteAllText((Join-Path $success.Stage 'new-library.dll'), 'new library')
    Check ((Invoke-Case $success) -eq 0) 'transaction updates successfully'
    Check ([IO.File]::ReadAllText((Join-Path $success.Install 'AIMaster.exe')) -eq 'new executable') 'existing executable is overwritten in its original directory'
    Check ([IO.File]::ReadAllText((Join-Path $success.Install 'settings.json')) -eq 'user configuration') 'local settings are preserved'
    Check ([IO.File]::ReadAllText((Join-Path $success.Path 'launch.txt')) -eq ((Join-Path $success.Install 'AIMaster.exe') + "`n" + $success.Install)) 'restart uses the same executable path and working directory including quotes and spaces'
    Check (!(Test-Path -LiteralPath $success.Workspace) -and !(Test-Path -LiteralPath (Join-Path $success.Install '.aimaster-update'))) 'successful transaction removes staging and backups'

    $locked = New-Case 'locked-file'
    [IO.File]::WriteAllText((Join-Path $locked.Install 'z-locked.dll'), 'old library')
    [IO.File]::WriteAllText((Join-Path $locked.Stage 'z-locked.dll'), 'new library')
    [IO.File]::WriteAllText((Join-Path $locked.Stage 'b-new.dll'), 'new library')
    $held = [IO.File]::Open((Join-Path $locked.Install 'z-locked.dll'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { $result = Invoke-Case $locked } finally { $held.Dispose() }
    Check ($result -ne 0) 'locked files produce an update failure'
    Check ([IO.File]::ReadAllText((Join-Path $locked.Install 'AIMaster.exe')) -eq 'old executable') 'a failure after replacing the executable rolls it back'
    Check (!(Test-Path -LiteralPath (Join-Path $locked.Install 'b-new.dll'))) 'rollback removes newly added files'
    Check ([IO.File]::ReadAllText((Join-Path $locked.Install 'z-locked.dll')) -eq 'old library') 'the failed replacement retains the original file'
    Check (Test-Path -LiteralPath (Join-Path $locked.Workspace 'error.txt')) 'failure diagnostics survive cleanup'

    $restart = New-Case 'restart-failure'
    $body = 'if ($PassThru) { $process = New-Object PSObject -Property @{ HasExited = $true; ExitCode = 42 }; $process | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { return $true }; return $process }'
    Check ((Invoke-Case $restart $body) -ne 0) 'an immediately exiting updated application triggers rollback'
    Check ([IO.File]::ReadAllText((Join-Path $restart.Install 'AIMaster.exe')) -eq 'old executable') 'startup failure restores the old executable'

    $reserved = New-Case 'reserved-user-data'
    [IO.File]::WriteAllText((Join-Path $reserved.Stage 'settings.json'), 'package settings')
    Check ((Invoke-Case $reserved) -ne 0) 'a package cannot overwrite reserved user configuration'
    Check (!(Test-Path -LiteralPath (Join-Path $reserved.Workspace 'ready'))) 'invalid packages are rejected before the parent exits'
    Check ([IO.File]::ReadAllText((Join-Path $reserved.Install 'AIMaster.exe')) -eq 'old executable') 'preparation failure leaves the executable unchanged'

    $recovery = New-Case 'interrupted-transaction'
    $transaction = Join-Path $recovery.Install '.aimaster-update'
    $null = New-Item -ItemType Directory -Path (Join-Path $transaction 'backup') -Force
    [IO.File]::WriteAllText((Join-Path $transaction 'backup\AIMaster.exe'), 'old executable')
    [IO.File]::WriteAllText((Join-Path $recovery.Install 'AIMaster.exe'), 'partial update')
    [IO.File]::WriteAllText((Join-Path $recovery.Install 'abandoned.dll'), 'partial update')
    $previous = @{ State = 'Applying'; Files = @(@{ Relative = 'AIMaster.exe'; Existed = $true }, @{ Relative = 'abandoned.dll'; Existed = $false }) }
    [IO.File]::WriteAllText((Join-Path $transaction 'journal.json'), ($previous | ConvertTo-Json -Depth 5))
    Check ((Invoke-Case $recovery) -eq 0) 'a subsequent update recovers an interrupted transaction before applying the new package'
    Check (!(Test-Path -LiteralPath (Join-Path $recovery.Install 'abandoned.dll'))) 'interruption recovery removes partial additions'

    $rollbackFailure = New-Case 'rollback-failure'
    $body = 'if ($PassThru) { $global:held = [IO.File]::Open($FilePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read); throw "Simulated startup failure with a locked target" }'
    Check ((Invoke-Case $rollbackFailure $body) -ne 0) 'rollback errors are reported'
    Check ([IO.File]::ReadAllText((Join-Path $rollbackFailure.Install '.aimaster-update\backup\AIMaster.exe')) -eq 'old executable') 'failed rollback preserves the old executable backup'
    Check ((Get-Content -LiteralPath (Join-Path $rollbackFailure.Workspace 'error.txt') -Raw) -match 'Backup retained') 'the error identifies retained recovery files'
    $completed = $true
} finally {
    Remove-Item Env:\AIMASTER_TEST_LAUNCH -ErrorAction SilentlyContinue
    $fullRoot = [IO.Path]::GetFullPath($root)
    if (-not $fullRoot.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path.' }
    if ($completed) { Remove-Item -LiteralPath $fullRoot -Recurse -Force }
    else { Write-Host "Test diagnostics: $fullRoot" }
}
