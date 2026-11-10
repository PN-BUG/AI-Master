$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\Resources\AssertBuildSdk.ps1')

# Exercise the confirmation and installer without installing software on the host.
function Test-BuildSdk { return $script:sdkAvailable }
function Read-Host { param($Prompt); $script:promptCount++; return $script:answer }
function Get-Command { param($Name, $CommandType, $ErrorAction); if ($script:hasWinget) { return @{ Name = $Name } } }
function winget {
    $script:installCount++
    $global:LASTEXITCODE = $script:installerExit
    $script:sdkAvailable = $script:installResolvesSdk
}
function Check($Condition, $Message) {
    if (-not $Condition) { throw $Message }
    Write-Host "PASS: $Message"
}
function Run-Scenario($Available, $Answer, $HasWinget, $InstallerExit, $ResolvesSdk) {
    $script:sdkAvailable = $Available
    $script:answer = $Answer
    $script:hasWinget = $HasWinget
    $script:installerExit = $InstallerExit
    $script:installResolvesSdk = $ResolvesSdk
    $script:promptCount = 0
    $script:installCount = 0
    $script:failure = $null
    try { Assert-BuildSdk } catch { $script:failure = $_.ToString() }
}
$originalPath = $env:Path
try {
    Run-Scenario $true 'y' $true 0 $true
    Check ($null -eq $failure -and $promptCount -eq 0 -and $installCount -eq 0) 'Existing SDK proceeds without a prompt or install'
    Run-Scenario $false '' $true 0 $true
    Check ($failure -like '*cancelled*' -and $installCount -eq 0) 'Default answer cancels without installing'
    Run-Scenario $false 'yes' $true 0 $true
    Check ($null -eq $failure -and $promptCount -eq 1 -and $installCount -eq 1) 'Consent installs once and continues after successful recheck'
    Run-Scenario $false 'y' $false 0 $true
    Check ($failure -like '*WinGet was not found*' -and $installCount -eq 0) 'Missing WinGet gives manual installation guidance'
    Run-Scenario $false 'y' $true 5 $false
    Check ($failure -like '*exit code 5*') 'Installer failure stops packaging'
    Run-Scenario $false 'y' $true 0 $false
    Check ($failure -like '*no compatible SDK*') 'Failed SDK recheck stops packaging'
} finally { $env:Path = $originalPath }
