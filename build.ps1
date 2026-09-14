<#
.SYNOPSIS
    Builds, tests, and publishes Whisper.

.DESCRIPTION
    The same script CI runs, so a broken publish surfaces locally rather than on release
    day. Publishing is where self-contained builds usually break: a missing native asset
    or a reflection-only type only fails once the single-file bundle is produced.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER SkipTests
    Skips the test run. Useful when iterating on the publish output itself.

.PARAMETER SkipIntegrationTests
    Runs only the fast unit tests.

.PARAMETER Runtime
    Target runtime identifier for the published binaries. Defaults to win-x64.

.EXAMPLE
    ./build.ps1
.EXAMPLE
    ./build.ps1 -SkipIntegrationTests
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$SkipIntegrationTests,
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'

function Invoke-Step {
    param([string]$Name, [scriptblock]$Action)

    Write-Host ''
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action

    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE."
    }
}

Invoke-Step 'Restore' { dotnet restore (Join-Path $root 'Whisper.sln') }

Invoke-Step 'Build' {
    dotnet build (Join-Path $root 'Whisper.sln') --configuration $Configuration --no-restore
}

if (-not $SkipTests) {
    $filter = if ($SkipIntegrationTests) { 'Category!=Integration' } else { $null }

    Invoke-Step 'Unit tests' {
        $arguments = @(
            'test'
            (Join-Path $root 'tests/Whisper.Tests')
            '--configuration', $Configuration
            '--no-build'
        )

        if ($filter) { $arguments += @('--filter', $filter) }

        dotnet @arguments
    }

    if (-not $SkipIntegrationTests) {
        Invoke-Step 'Integration tests' {
            dotnet test (Join-Path $root 'tests/Whisper.IntegrationTests') `
                --configuration $Configuration --no-build
        }
    }
}

if (Test-Path $artifacts) {
    Remove-Item $artifacts -Recurse -Force
}

# Self-contained so neither machine needs the .NET runtime installed, which is the whole
# point of "download it and run it" for a self-hosted server.
Invoke-Step "Publish server ($Runtime)" {
    dotnet publish (Join-Path $root 'src/Whisper.Server') `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        --output (Join-Path $artifacts 'server')
}

Invoke-Step "Publish client ($Runtime)" {
    dotnet publish (Join-Path $root 'src/Whisper.Client') `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        --output (Join-Path $artifacts 'client')
}

Invoke-Step 'Package' {
    Compress-Archive -Path (Join-Path $artifacts 'server/*') `
        -DestinationPath (Join-Path $artifacts "whisper-server-$Runtime.zip") -Force
    Compress-Archive -Path (Join-Path $artifacts 'client/*') `
        -DestinationPath (Join-Path $artifacts "whisper-client-$Runtime.zip") -Force
    $global:LASTEXITCODE = 0
}

Write-Host ''
Write-Host 'Done. Artifacts:' -ForegroundColor Green
Get-ChildItem $artifacts -Filter '*.zip' | ForEach-Object {
    Write-Host ("  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB))
}
