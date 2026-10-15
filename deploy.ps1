# Deploy only this plugin; never replace SimHub's shared dependencies.
[CmdletBinding()]
param(
    [string]$SimHubPath = $env:SIMHUB_INSTALL_PATH,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
if (-not $SimHubPath) { $SimHubPath = 'C:\Program Files (x86)\SimHub' }
$SimHubPath = (Resolve-Path -LiteralPath $SimHubPath).Path.TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath (Join-Path $SimHubPath 'SimHubWPF.exe'))) { throw 'SimHub installation not found.' }
if (Get-Process -Name SimHubWPF -ErrorAction SilentlyContinue) {
    throw 'Close SimHub before deployment, then run .\deploy.ps1 again. No running process will be stopped automatically.'
}
if (-not (Test-Path -LiteralPath (Join-Path $SimHubPath 'HidSharp.dll'))) { throw 'SimHub HidSharp.dll is missing.' }
$outputPath = Join-Path $PSScriptRoot "bin\$Configuration\net48"
$pluginName = 'FFBeast.SimHubPlugin.dll'
$sourceDll = Join-Path $outputPath $pluginName
if (-not (Test-Path -LiteralPath $sourceDll)) { throw 'Build the plugin before deploying.' }
$backupPath = Join-Path $PSScriptRoot ('artifacts\backups\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $backupPath -Force | Out-Null
$files = @($pluginName, 'FFBeast.SimHubPlugin.pdb')
foreach ($name in $files) {
    $installed = Join-Path $SimHubPath $name
    if (Test-Path -LiteralPath $installed) { Copy-Item -LiteralPath $installed -Destination (Join-Path $backupPath $name) }
}
$changed = @()
try {
    foreach ($name in $files) {
        $source = Join-Path $outputPath $name
        if (Test-Path -LiteralPath $source) {
            # Record before copying so a partially written destination is also rolled back.
            $changed += $name
            Copy-Item -LiteralPath $source -Destination (Join-Path $SimHubPath $name) -Force
        }
    }
    $expectedHash = (Get-FileHash -LiteralPath $sourceDll).Hash
    $installedHash = (Get-FileHash -LiteralPath (Join-Path $SimHubPath $pluginName)).Hash
    if ($expectedHash -ne $installedHash) { throw 'Installed DLL hash does not match build output.' }
    [ordered]@{
        InstalledUtc = [DateTime]::UtcNow.ToString('o')
        Destination = $SimHubPath
        Version = [Diagnostics.FileVersionInfo]::GetVersionInfo($sourceDll).FileVersion
        SHA256 = $installedHash
        BackupPath = $backupPath
        SharedHidSharpSHA256 = (Get-FileHash -LiteralPath (Join-Path $SimHubPath 'HidSharp.dll')).Hash
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backupPath 'deployment.json')
}
catch {
    foreach ($name in $changed) {
        $backup = Join-Path $backupPath $name
        $destination = Join-Path $SimHubPath $name
        if (Test-Path -LiteralPath $backup) { Copy-Item -LiteralPath $backup -Destination $destination -Force }
        elseif (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Force }
    }
    throw
}
Write-Host "Installed version $([Diagnostics.FileVersionInfo]::GetVersionInfo($sourceDll).FileVersion) into $SimHubPath"
Write-Host "Backup: $backupPath"
Write-Host 'Start SimHub to load the new plugin.'
