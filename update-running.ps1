[CmdletBinding()]
param([int]$ApplicationProcessId)

$ErrorActionPreference = 'Stop'
$applications = @(Get-Process -Name AIMaster -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and (!$ApplicationProcessId -or $_.Id -eq $ApplicationProcessId)
})
if ($applications.Count -ne 1) {
    throw 'Exactly one running AIMaster is required. Use -ApplicationProcessId to select it when necessary.'
}
$application = $applications[0]
$executablePath = [IO.Path]::GetFullPath($application.Path)
$installationDir = [IO.Path]::GetDirectoryName($executablePath)
$startedAt = $application.StartTime.ToUniversalTime().Ticks
$lightweight = Test-Path -LiteralPath (Join-Path $installationDir 'AIMaster.runtimeconfig.json') -PathType Leaf
$stream = [IO.File]::OpenRead($executablePath)
try {
    $reader = New-Object IO.BinaryReader($stream)
    $stream.Position = 0x3c
    $stream.Position = $reader.ReadInt32() + 4
    $machine = $reader.ReadUInt16()
    $runtime = switch ($machine) {
        0x8664 { 'win-x64' }
        0xaa64 { 'win-arm64' }
        default { throw 'Unsupported executable architecture.' }
    }
} finally { $stream.Dispose() }

$workspace = Join-Path ([IO.Path]::GetTempPath()) ('AIMaster-update-' + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $workspace 'staged'
$null = New-Item -ItemType Directory -Path $stage -Force
Write-Host "Update target (running process): $executablePath"
Write-Host 'Building in a temporary directory. The running application remains untouched until this succeeds.'
$selfContained = (!$lightweight).ToString().ToLowerInvariant()
& dotnet publish (Join-Path $PSScriptRoot 'AIMaster.csproj') -c Release -r $runtime -o $stage --nologo -v minimal `
    "--self-contained=$selfContained" "-p:PublishSingleFile=$selfContained" `
    "-p:IncludeNativeLibrariesForSelfExtract=$selfContained"
if ($LASTEXITCODE -ne 0) { throw "Publish failed. The running installation is unchanged. Build workspace: $workspace" }
$stagedExe = Join-Path $stage 'AIMaster.exe'
if (-not (Test-Path -LiteralPath $stagedExe -PathType Leaf)) { throw 'The staged build has no executable.' }
$expectedVersion = ([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'AIMaster.csproj') -Raw)).Project.PropertyGroup.Version
$stagedVersion = (Get-Item -LiteralPath $stagedExe).VersionInfo.ProductVersion.Split('+')[0]
if ($stagedVersion -ne $expectedVersion) { throw 'The staged executable version is incorrect.' }
$startHere = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Resources\START-HERE.template.txt'))
$note = if ($lightweight) { 'The lightweight build requires .NET 8 Desktop Runtime.' } else { 'The standalone build includes the .NET runtime.' }
[IO.File]::WriteAllText((Join-Path $stage 'START-HERE.txt'), $startHere.Replace('{{RUNTIME_NOTE}}', $note), (New-Object Text.UTF8Encoding($true)))
$expectedHash = (Get-FileHash -LiteralPath $stagedExe -Algorithm SHA256).Hash
& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Resources\ApplyUpdate.ps1') `
    -ApplicationProcessId $application.Id -ApplicationStartTicks $startedAt -ExecutablePath $executablePath `
    -StagedDirectory $stage -Workspace $workspace -LogDirectory (Join-Path $env:LOCALAPPDATA 'AIMaster\UpdateLogs') -StopApplication
if ($LASTEXITCODE -ne 0) {
    if (Test-Path -LiteralPath (Join-Path $workspace 'error.txt')) {
        throw [IO.File]::ReadAllText((Join-Path $workspace 'error.txt'))
    }
    throw "Update failed. Diagnostics retained at: $workspace"
}
if ((Get-FileHash -LiteralPath $executablePath -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'Installed executable verification failed.'
}
Write-Host "Updated in place to $stagedVersion and restarted: $executablePath" -ForegroundColor Green
