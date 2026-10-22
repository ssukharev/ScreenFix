[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string[]] $Runtime = @('win-x64'),

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $OutputDirectory,

    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

if ($env:OS -ne 'Windows_NT') {
    throw 'Run this script on Windows with the .NET 10 SDK installed.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK was not found. Install the .NET 10 SDK and open a new terminal.'
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts'
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$applicationProject = Join-Path $repositoryRoot 'src/ScreenFix.Windows/ScreenFix.Windows.csproj'
$testProjects = @(
    (Join-Path $repositoryRoot 'tests/ScreenFix.Core.Tests/ScreenFix.Core.Tests.csproj'),
    (Join-Path $repositoryRoot 'tests/ScreenFix.Display.Tests/ScreenFix.Display.Tests.csproj')
)

if (-not $SkipTests) {
    foreach ($testProject in $testProjects) {
        & dotnet run --project $testProject --configuration $Configuration --no-launch-profile
        if ($LASTEXITCODE -ne 0) {
            throw "Checks failed for $testProject (exit code $LASTEXITCODE)."
        }
    }
}

[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
foreach ($targetRuntime in ($Runtime | Select-Object -Unique)) {
    $publishDirectory = Join-Path $OutputDirectory "publish/$targetRuntime"
    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }

    $publishArguments = @(
        'publish', $applicationProject,
        '--configuration', $Configuration,
        '--runtime', $targetRuntime,
        '--self-contained', 'true',
        '--output', $publishDirectory,
        '--nologo',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:PublishTrimmed=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false'
    )
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Publish for $targetRuntime failed (exit code $LASTEXITCODE)."
    }

    $applicationFile = Join-Path $publishDirectory 'ScreenFix.exe'
    if (-not (Test-Path -LiteralPath $applicationFile)) {
        throw "The published application was not found: $applicationFile"
    }
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.md') -Destination (Join-Path $publishDirectory 'README.ru.md')
    $archivePath = Join-Path $OutputDirectory "ScreenFix-$targetRuntime.zip"
    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archivePath -Force
    Write-Host "Portable package: $archivePath"
}
