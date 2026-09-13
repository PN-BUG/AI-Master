[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [Alias('FrameworkDependent')][switch]$Lightweight,
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'AIMaster.csproj'
$releaseRoot = Join-Path $PSScriptRoot 'release'
$mode = if ($Lightweight) { 'lightweight' } else { 'standalone' }
$packageName = "AIMaster-$Runtime-$mode"
$outputDir = Join-Path $releaseRoot $packageName
$archivePath = "$outputDir.zip"

$releaseFullPath = [IO.Path]::GetFullPath($releaseRoot)
$outputFullPath = [IO.Path]::GetFullPath($outputDir)
if ([IO.Path]::GetDirectoryName($outputFullPath) -ne $releaseFullPath) {
    throw "Unsafe output path: $outputFullPath"
}

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
if (Test-Path -LiteralPath $outputDir) {
    Remove-Item -LiteralPath $outputDir -Recurse -Force
}
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

$selfContained = (-not $Lightweight).ToString().ToLowerInvariant()
$singleFile = (-not $Lightweight).ToString().ToLowerInvariant()
& dotnet publish $projectPath -c $Configuration -r $Runtime -o $outputDir --nologo -v minimal `
    "--self-contained=$selfContained" `
    "-p:PublishSingleFile=$singleFile" `
    "-p:IncludeNativeLibrariesForSelfExtract=$singleFile"
if ($LASTEXITCODE -ne 0) {
    throw "AIMaster publish failed with exit code $LASTEXITCODE"
}

$exe = Join-Path $outputDir 'AIMaster.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Missing published executable: $exe"
}

$runtimeNote = if ($Lightweight) {
    'The lightweight build requires .NET 8 Desktop Runtime (x64).'
} else {
    'The standalone build includes the .NET runtime.'
}

Set-Content -LiteralPath (Join-Path $outputDir 'START-HERE.txt') -Encoding UTF8 -Value @"
AIMaster

1. Install and sign in to Codex.
2. Run AIMaster.exe.
$runtimeNote

1. 安装并登录 Codex。
2. 运行 AIMaster.exe。

Documentation: https://github.com/PN-BUG/AI-Master
"@

if (-not $NoZip) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $outputDir,
        $archivePath,
        [IO.Compression.CompressionLevel]::Optimal,
        $true)
    Write-Host "Archive: $archivePath"
}

$bytes = (Get-ChildItem -LiteralPath $outputDir -File -Recurse | Measure-Object Length -Sum).Sum
$hash = if (Test-Path -LiteralPath $archivePath) {
    (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
} else {
    (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
}

Write-Host ("SUCCESS: {0:N2} MB" -f ($bytes / 1MB)) -ForegroundColor Green
Write-Host "Output: $outputDir"
Write-Host "SHA256: $hash"
