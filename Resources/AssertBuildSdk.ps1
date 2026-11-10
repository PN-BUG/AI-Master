function Test-BuildSdk {
    if (-not (Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue)) {
        return $false
    }

    # --version checks SDK resolution for this repository, including global.json.
    Push-Location $PSScriptRoot
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $sdkVersion = & dotnet --version 2>&1
        $sdkExitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
        Pop-Location
    }
    return ($sdkExitCode -eq 0 -and "$sdkVersion" -match '^(\d+)\.' -and [int]$Matches[1] -ge 8)
}

function Assert-BuildSdk {
    if (Test-BuildSdk) { return }

    $installationHelp = 'Install the .NET 8 SDK: winget install --id Microsoft.DotNet.SDK.8 --exact . Download: https://dotnet.microsoft.com/download/dotnet/8.0'
    Write-Host 'A usable .NET SDK 8 or newer was not found. The Desktop Runtime alone cannot build AIMaster.' -ForegroundColor Yellow
    try {
        $answer = Read-Host 'Install .NET 8 SDK now and continue packaging? [y/N]'
    } catch {
        throw "Interactive confirmation is unavailable. $installationHelp"
    }
    if ($answer -notmatch '^(?i:y|yes)$') {
        throw "SDK installation cancelled. No application was stopped or package replaced. $installationHelp"
    }
    if (-not (Get-Command winget -CommandType Application -ErrorAction SilentlyContinue)) {
        throw "WinGet was not found. $installationHelp"
    }

    & winget install --id Microsoft.DotNet.SDK.8 --exact --source winget --accept-source-agreements --accept-package-agreements
    if ($LASTEXITCODE -ne 0) {
        throw "SDK installation failed with exit code $LASTEXITCODE. $installationHelp"
    }

    # Pick up PATH changes made by the installer in this same packaging session.
    $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = "$env:Path;$machinePath;$userPath"
    if (-not (Test-BuildSdk)) {
        throw "SDK installation completed, but no compatible SDK can be resolved. Reopen the terminal and retry; check global.json if this persists. $installationHelp"
    }
    Write-Host 'SDK check passed. Continuing packaging...' -ForegroundColor Green
}
