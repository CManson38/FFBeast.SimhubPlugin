# Build normally; deployment is explicit: .\build.ps1 -Test -Deploy
[CmdletBinding()]
param(
    [switch]$Clean,
    [switch]$Test,
    [switch]$Deploy,
    [switch]$Offline,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$SimHubPath = $env:SIMHUB_INSTALL_PATH
)
$ErrorActionPreference = 'Stop'
if (-not $SimHubPath) { $SimHubPath = 'C:\Program Files (x86)\SimHub' }
$SimHubPath = (Resolve-Path -LiteralPath $SimHubPath).Path.TrimEnd('\', '/')
$projectPath = Join-Path $PSScriptRoot 'FFBeast.SimHubPlugin.csproj'
$testProject = Join-Path $PSScriptRoot 'tests\FFBeast.RegressionTests.csproj'
$pathProperty = '-p:SIMHUB_INSTALL_PATH=' + $SimHubPath
$restoreOptions = @()
if ($Offline) {
    $packageCache = (& dotnet nuget locals global-packages --list --force-english-output) -replace '^global-packages:\s*', ''
    if ($LASTEXITCODE -ne 0) { throw 'Unable to locate the NuGet package cache.' }
    $restoreOptions = @('--ignore-failed-sources', '-p:NuGetAudit=false', '--packages', $packageCache.Trim().TrimEnd('\', '/'))
}

if ($Clean) {
    & dotnet clean $projectPath -c $Configuration --nologo $pathProperty
    if ($LASTEXITCODE -ne 0) { throw 'Plugin clean failed.' }
    if ($Test) {
        & dotnet clean $testProject -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Test clean failed.' }
    }
}
& dotnet build $projectPath -c $Configuration --nologo $pathProperty @restoreOptions
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
$pluginOutput = Join-Path $PSScriptRoot "bin\$Configuration\net48\FFBeast.SimHubPlugin.dll"
if (-not (Test-Path -LiteralPath $pluginOutput)) { throw "Build output missing: $pluginOutput" }
Write-Host "Built: $pluginOutput"

if ($Test) {
    & dotnet build $testProject -c $Configuration --nologo @restoreOptions
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & (Join-Path $PSScriptRoot "tests\bin\$Configuration\net48\FFBeast.RegressionTests.exe")
    if ($LASTEXITCODE -ne 0) { throw '.NET Framework regression tests failed.' }
    & dotnet (Join-Path $PSScriptRoot "tests\bin\$Configuration\net9.0\FFBeast.RegressionTests.dll")
    if ($LASTEXITCODE -ne 0) { throw '.NET 9 regression tests failed.' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'tests\smoke.ps1') -SimHubPath $SimHubPath -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'WPF smoke tests failed.' }
}
if ($Deploy) {
    & (Join-Path $PSScriptRoot 'deploy.ps1') -SimHubPath $SimHubPath -Configuration $Configuration
}
