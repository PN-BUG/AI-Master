[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [Alias('FrameworkDependent')][switch]$Lightweight,
    [switch]$All,
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'AIMaster.csproj'
$releaseRoot = Join-Path $PSScriptRoot 'release'

if ($All) {
    if ($Lightweight) {
        throw '-All cannot be combined with -Lightweight or -FrameworkDependent.'
    }

    $commonArguments = @{
        Configuration = $Configuration
        Runtime = $Runtime
    }
    if ($NoZip) { $commonArguments.NoZip = $true }

    Write-Host "Packaging AIMaster standalone and lightweight builds..." -ForegroundColor Cyan
    & $PSCommandPath @commonArguments
    & $PSCommandPath @commonArguments -Lightweight

    $expectedPaths = @(
        (Join-Path $releaseRoot "AIMaster-$Runtime-standalone"),
        (Join-Path $releaseRoot "AIMaster-$Runtime-lightweight")
    )
    if (-not $NoZip) {
        $expectedPaths += @(
            (Join-Path $releaseRoot "AIMaster-$Runtime-standalone.zip"),
            (Join-Path $releaseRoot "AIMaster-$Runtime-lightweight.zip")
        )
    }
    foreach ($path in $expectedPaths) {
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Expected package output was not created: $path"
        }
    }

    Write-Host ''
    Write-Host 'ALL PACKAGES CREATED' -ForegroundColor Green
    foreach ($path in $expectedPaths) { Write-Host "  $path" }
    return
}

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

$startHereTemplate = Join-Path $PSScriptRoot 'Resources\START-HERE.template.txt'
if (-not (Test-Path -LiteralPath $startHereTemplate -PathType Leaf)) {
    throw "Missing START-HERE template: $startHereTemplate"
}
$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
$utf8WithBom = New-Object System.Text.UTF8Encoding($true)
$startHereText = [IO.File]::ReadAllText($startHereTemplate, $utf8WithoutBom)
$startHereText = $startHereText.Replace('{{RUNTIME_NOTE}}', $runtimeNote)
[IO.File]::WriteAllText(
    (Join-Path $outputDir 'START-HERE.txt'),
    $startHereText,
    $utf8WithBom)

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
