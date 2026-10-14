$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root = Join-Path ([IO.Path]::GetTempPath()) ('AIMaster-packaging-tests-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root
$completed = $false
$utf8 = New-Object Text.UTF8Encoding($true)

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    Write-Host "PASS: $Message"
}

# These orchestration tests mock publishing, processes, and the update engine.
# UpdateTransaction.Tests.ps1 separately exercises the actual transaction engine.
$buildStub = @'
param($Configuration, $Runtime, [switch]$All, [switch]$NoZip, $OutputRoot)
if ($env:AIMASTER_PACKAGE_TEST_INSTALL -and (Get-Process -Name AIMaster)) { throw 'Application is still running at the start of publishing' }
Add-Content -LiteralPath $env:AIMASTER_PACKAGE_TEST_EVENTS -Value 'build'
foreach ($mode in @('standalone', 'lightweight')) {
    if ($mode -eq 'lightweight' -and $env:AIMASTER_PACKAGE_TEST_FAILURE -eq 'build') { throw 'Simulated second package failure' }
    $name = "AIMaster-$Runtime-$mode"
    $dir = Join-Path $OutputRoot $name
    $null = New-Item -ItemType Directory -Path $dir -Force
    $binary = New-Object byte[] 80
    $binary[60] = 64
    $binary[68] = 0x64
    $binary[69] = 0x86
    $binary[70] = 2
    [IO.File]::WriteAllBytes((Join-Path $dir 'AIMaster.exe'), $binary)
    [IO.File]::WriteAllText((Join-Path $dir 'library.dll'), 'new library')
    if ($mode -eq 'lightweight') { [IO.File]::WriteAllText((Join-Path $dir 'AIMaster.runtimeconfig.json'), '{}') }
    if (-not $NoZip) {
        [IO.File]::WriteAllText((Join-Path $OutputRoot "$name.zip"), 'new archive')
        [IO.File]::WriteAllText((Join-Path $OutputRoot "$name.zip.sha256"), 'new hash')
    }
}
'@
$updateStub = @'
param($ApplicationProcessId, $ApplicationStartTicks, $ExecutablePath, $StagedDirectory, $Workspace, $LogDirectory, [switch]$StopApplication)
$ErrorActionPreference = 'Stop'
if (-not $StopApplication -or $ApplicationProcessId -ne 12345 -or $ApplicationStartTicks -le 0) { throw 'Incorrect process identity or stop flag' }
if ([IO.File]::ReadAllBytes($ExecutablePath)[70] -ne 1) { throw 'The old executable was replaced before the update engine ran' }
if (-not (Test-Path -LiteralPath (Join-Path $env:AIMASTER_PACKAGE_TEST_RELEASE 'AIMaster-win-x64-lightweight.zip')) -and $env:AIMASTER_PACKAGE_TEST_NOZIP -ne '1') { throw 'Packages not ready before update' }
if ($env:AIMASTER_PACKAGE_TEST_FAILURE -eq 'update') {
    [IO.File]::WriteAllText((Join-Path $Workspace 'error.txt'), 'Simulated update failure')
    exit 1
}
$install = [IO.Path]::GetDirectoryName($ExecutablePath)
Get-ChildItem -LiteralPath $StagedDirectory -File | Copy-Item -Destination $install -Force
Add-Content -LiteralPath $env:AIMASTER_PACKAGE_TEST_EVENTS -Value ("restart:$ExecutablePath")
exit 0
'@
$runner = @'
$global:aimasterTestStopped = $false
$global:aimasterTestProcess = [pscustomobject]@{ Id = 12345; Path = (Join-Path $env:AIMASTER_PACKAGE_TEST_RELEASE 'unused.exe'); StartTime = [DateTime]::UtcNow }
if ($env:AIMASTER_PACKAGE_TEST_INSTALL) { $global:aimasterTestProcess.Path = Join-Path $env:AIMASTER_PACKAGE_TEST_INSTALL 'AIMaster.exe' }
$global:aimasterTestProcess | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { return $true }
function Get-Process {
    param($Name, $Id, $ErrorAction)
    if ($env:AIMASTER_PACKAGE_TEST_INSTALL -and -not $global:aimasterTestStopped) {
        $global:aimasterTestProcess
        if ($env:AIMASTER_PACKAGE_TEST_FAILURE -eq 'multiple') { $global:aimasterTestProcess }
    }
}
function Stop-Process {
    param($Id, $ErrorAction)
    if ($Id -ne 12345) { throw 'Wrong process was stopped' }
    if ($env:AIMASTER_PACKAGE_TEST_FAILURE -eq 'stop') { throw 'Simulated stop failure' }
    Add-Content -LiteralPath $env:AIMASTER_PACKAGE_TEST_EVENTS -Value 'stop'
    $global:aimasterTestStopped = $true
}
function Start-Process {
    param($FilePath, $WorkingDirectory, [switch]$PassThru)
    if ($WorkingDirectory -ne [IO.Path]::GetDirectoryName($FilePath)) { throw 'Incorrect launch directory' }
    Add-Content -LiteralPath $env:AIMASTER_PACKAGE_TEST_EVENTS -Value ("launch:$FilePath")
    $process = New-Object PSObject -Property @{ HasExited = $true }
    $process | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { return $env:AIMASTER_PACKAGE_TEST_FAILURE -eq 'launch' }
    return $process
}
try {
    & (Join-Path $PSScriptRoot 'package-and-restart.ps1') -NoZip:($env:AIMASTER_PACKAGE_TEST_NOZIP -eq '1')
    exit 0
} catch { Write-Host $_; exit 1 }
'@

function New-Case([string]$Name, [string]$Install = 'standalone', [string]$Failure = '', [bool]$NoZip = $false) {
    $path = Join-Path $root $Name
    $release = Join-Path $path 'release'
    $null = New-Item -ItemType Directory -Path (Join-Path $path 'Resources') -Force
    Copy-Item -LiteralPath (Join-Path $repository 'package-and-restart.ps1') -Destination $path
    [IO.File]::WriteAllText((Join-Path $path 'build.ps1'), $buildStub, $utf8)
    [IO.File]::WriteAllText((Join-Path $path 'Resources\ApplyUpdate.ps1'), $updateStub, $utf8)
    [IO.File]::WriteAllText((Join-Path $path 'runner.ps1'), $runner, $utf8)
    foreach ($mode in @('standalone', 'lightweight')) {
        $dir = Join-Path $release "AIMaster-win-x64-$mode"
        $null = New-Item -ItemType Directory -Path $dir -Force
        [IO.File]::WriteAllText((Join-Path $dir 'library.dll'), 'old library')
        [IO.File]::WriteAllText((Join-Path $release "AIMaster-win-x64-$mode.zip"), 'old archive')
        [IO.File]::WriteAllText((Join-Path $release "AIMaster-win-x64-$mode.zip.sha256"), 'old hash')
    }
    $installation = switch ($Install) {
        'none' { '' }
        'outside' { Join-Path $path "old installation with spaces and 'quote" }
        default { Join-Path $release "AIMaster-win-x64-$Install" }
    }
    if ($installation) {
        $null = New-Item -ItemType Directory -Path $installation -Force
        $binary = New-Object byte[] 80
        $binary[60] = 64; $binary[68] = 0x64; $binary[69] = 0x86; $binary[70] = 1
        [IO.File]::WriteAllBytes((Join-Path $installation 'AIMaster.exe'), $binary)
        [IO.File]::WriteAllText((Join-Path $installation 'settings.json'), 'personal settings')
        if ($Install -eq 'lightweight') { [IO.File]::WriteAllText((Join-Path $installation 'AIMaster.runtimeconfig.json'), '{}') }
    }
    $env:AIMASTER_PACKAGE_TEST_INSTALL = $installation
    $env:AIMASTER_PACKAGE_TEST_RELEASE = $release
    $env:AIMASTER_PACKAGE_TEST_EVENTS = Join-Path $path 'events.txt'
    $env:AIMASTER_PACKAGE_TEST_FAILURE = $Failure
    $env:AIMASTER_PACKAGE_TEST_NOZIP = if ($NoZip) { '1' } else { '0' }
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $path 'runner.ps1') | Out-Host
    return @{ Path = $path; Release = $release; Install = $installation; ExitCode = $LASTEXITCODE; Events = @(Get-Content -LiteralPath $env:AIMASTER_PACKAGE_TEST_EVENTS -ErrorAction SilentlyContinue) }
}

try {
    foreach ($mode in @('standalone', 'lightweight', 'outside')) {
        $case = New-Case "success-$mode" $mode
        Check ($case.ExitCode -eq 0) "$mode packaging succeeds"
        Check ([IO.File]::ReadAllBytes((Join-Path $case.Install 'AIMaster.exe'))[70] -eq 2) "$mode updates the actual running installation"
        Check ([IO.File]::ReadAllText((Join-Path $case.Install 'settings.json')) -eq 'personal settings') "$mode preserves personal settings"
        Check (($case.Events -join '|') -eq ('stop|build|restart:' + (Join-Path $case.Install 'AIMaster.exe'))) "$mode exits before building and restarts from the original path"
        Check (@(Get-ChildItem -LiteralPath $case.Release -Directory -Filter '.package-*').Count -eq 0) "$mode cleans successful staging and backups"
    }
    $case = New-Case 'no-running-instance' 'none'
    Check ($case.ExitCode -eq 0 -and $case.Events[-1] -eq ('launch:' + (Join-Path $case.Release 'AIMaster-win-x64-standalone\AIMaster.exe'))) 'no running instance launches the new standalone build'
    $case = New-Case 'no-zip' 'lightweight' '' $true
    Check ($case.ExitCode -eq 0 -and [IO.File]::ReadAllText((Join-Path $case.Release 'AIMaster-win-x64-standalone.zip')) -eq 'old archive') 'NoZip updates and restarts without replacing archives'
    foreach ($failure in @('build', 'update')) {
        $case = New-Case "failure-$failure" 'standalone' $failure
        Check ($case.ExitCode -ne 0) "$failure failure reports a nonzero exit code"
        Check ([IO.File]::ReadAllBytes((Join-Path $case.Install 'AIMaster.exe'))[70] -eq 1) "$failure failure preserves the old executable"
        Check ([IO.File]::ReadAllText((Join-Path $case.Release 'AIMaster-win-x64-lightweight\library.dll')) -eq 'old library') "$failure failure restores old package directories"
        Check ([IO.File]::ReadAllText((Join-Path $case.Release 'AIMaster-win-x64-standalone.zip')) -eq 'old archive') "$failure failure restores old archives"
        Check (($case.Events -join '|') -eq ('stop|build|launch:' + (Join-Path $case.Install 'AIMaster.exe'))) "$failure failure reopens the old application after stopping it before the build"
    }
    $case = New-Case 'startup-failure' 'none' 'launch'
    Check ($case.ExitCode -ne 0 -and [IO.File]::ReadAllText((Join-Path $case.Release 'AIMaster-win-x64-standalone\library.dll')) -eq 'old library') 'startup failure restores the previous outputs'
    $case = New-Case 'multiple-processes' 'standalone' 'multiple'
    Check ($case.ExitCode -ne 0 -and $case.Events.Count -eq 0) 'multiple processes are rejected before building'
    $case = New-Case 'stop-failure' 'standalone' 'stop'
    Check ($case.ExitCode -ne 0 -and $case.Events.Count -eq 0) 'failure to stop the application prevents packaging'
    $completed = $true
} finally {
    Get-ChildItem Env:\AIMASTER_PACKAGE_TEST_* | Remove-Item
    $fullRoot = [IO.Path]::GetFullPath($root)
    if (-not $fullRoot.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path.' }
    if ($completed) { Remove-Item -LiteralPath $fullRoot -Recurse -Force }
    else { Write-Host "Test diagnostics: $fullRoot" }
}
