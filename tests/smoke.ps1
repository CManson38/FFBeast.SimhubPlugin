# Run in Windows PowerShell (STA) to exercise the actual net48 WPF assembly.
[CmdletBinding()]
param(
    [string]$SimHubPath = $env:SIMHUB_INSTALL_PATH,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Hardware
)
$ErrorActionPreference = 'Stop'
if (-not $SimHubPath) { $SimHubPath = 'C:\Program Files (x86)\SimHub' }
$pluginPath = Join-Path (Split-Path $PSScriptRoot -Parent) "bin\$Configuration\net48\FFBeast.SimHubPlugin.dll"
foreach ($name in @('HidSharp.dll', 'Newtonsoft.Json.dll', 'log4net.dll', 'SimHub.Logging.dll', 'GameReaderCommon.dll', 'SimHub.Plugins.dll')) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $SimHubPath $name))
}
Add-Type -AssemblyName PresentationFramework
[void][Reflection.Assembly]::LoadFrom($pluginPath)
function Assert-True($condition, $message) { if (-not $condition) { throw $message } }
$plugin = New-Object FFBeast.SimHubPlugin.FFBeastPlugin
$control = New-Object FFBeast.SimHubPlugin.SettingsControl -ArgumentList $plugin
$timerField = $control.GetType().GetField('_updateTimer', [Reflection.BindingFlags]'NonPublic,Instance')
$timer = $timerField.GetValue($control)
Assert-True (-not $timer.IsEnabled) 'Timer started before view loaded.'
$control.RaiseEvent((New-Object Windows.RoutedEventArgs -ArgumentList ([Windows.FrameworkElement]::LoadedEvent)))
Assert-True $timer.IsEnabled 'Timer did not start on Loaded.'
$control.RaiseEvent((New-Object Windows.RoutedEventArgs -ArgumentList ([Windows.FrameworkElement]::UnloadedEvent)))
Assert-True (-not $timer.IsEnabled) 'Timer retained unloaded view.'
$control.RaiseEvent((New-Object Windows.RoutedEventArgs -ArgumentList ([Windows.FrameworkElement]::LoadedEvent)))
Assert-True $timer.IsEnabled 'Timer did not restart on second Loaded.'
$control.RaiseEvent((New-Object Windows.RoutedEventArgs -ArgumentList ([Windows.FrameworkElement]::UnloadedEvent)))

# Exercise profile CRUD and the same JSON serializer used by SimHub, in memory only.
$profile = New-Object FFBeast.SimHubPlugin.WheelProfile
$profile.Name = 'Smoke profile'
$profile.Game = 'IRacing'
$profile.Car = 'GT3'
$profile.Settings.Strength = 65
$profile.Settings.RotationRange = 540
$plugin.SaveProfile($profile, $false)
$config = $plugin.Profiles
Assert-True ($config.Profiles.Count -eq 1) 'Profile was not created.'
$profile = $config.Selected
$profile.Name = 'Updated profile'
$plugin.SaveProfile($profile, $true)
Assert-True ($plugin.Profiles.Selected.Name -eq 'Updated profile') 'Profile was not updated.'
$json = [Newtonsoft.Json.JsonConvert]::SerializeObject($plugin.Profiles)
$roundTrip = [Newtonsoft.Json.JsonConvert]::DeserializeObject($json, [FFBeast.SimHubPlugin.ProfileConfiguration])
$roundTrip.Validate()
Assert-True ($roundTrip.Selected.Settings.Strength -eq 65 -and $roundTrip.Selected.Settings.RotationRange -eq 540) 'Profile settings did not survive JSON serialization.'
$control.RaiseEvent((New-Object Windows.RoutedEventArgs -ArgumentList ([Windows.FrameworkElement]::LoadedEvent)))
Assert-True ($control.FindName('StrengthInput').Text -eq '65') 'Selected profile was not loaded into editor.'
$control.FindName('StrengthInput').Text = 'invalid'
$applyHandler = $control.GetType().GetMethod('ApplySettings_Click', [Reflection.BindingFlags]'NonPublic,Instance')
[void]$applyHandler.Invoke($control, @($null, $null))
Assert-True ($control.FindName('ResultText').Text -match 'whole number') 'Editor failed to reject invalid numeric input.'
$control.RaiseEvent((New-Object Windows.RoutedEventArgs -ArgumentList ([Windows.FrameworkElement]::UnloadedEvent)))
$plugin.DeleteProfile($plugin.Profiles.SelectedProfileId)
Assert-True ($plugin.Profiles.Profiles.Count -eq 0) 'Profile was not deleted.'
Write-Host 'PASS profile CRUD, JSON persistence, selected-profile editor and numeric validation'
$plugin.End($null)
$designControl = New-Object FFBeast.SimHubPlugin.SettingsControl
$designControl.RaiseEvent((New-Object Windows.RoutedEventArgs -ArgumentList ([Windows.FrameworkElement]::LoadedEvent)))
Assert-True (-not $timerField.GetValue($designControl).IsEnabled) 'Design view started polling without a plugin.'
Write-Host 'PASS WPF load/unload/reload and design constructor'

if ($Hardware) {
    $transport = New-Object FFBeast.SimHubPlugin.HidWheelTransport
    $device = @($transport.Enumerate() | Where-Object { $_.DevicePath -match '&mi_00' })[0]
    Assert-True ($null -ne $device) 'FFBeast vendor interface not found.'
    $connection = $device.Open()
    try {
        for ($probe = 0; $probe -lt 5; $probe++) { [void]$connection.ReadStatus() }
        $settings = $connection.ReadSettings()
        $settings.Validate()
        Write-Host "Read wheel settings: strength $($settings.Strength)%, rotation $($settings.RotationRange) degrees, FFB $($settings.ForceEnabled)"
    }
    finally { $connection.Dispose() }
    # Test the actual controller's timer/reconnect path, still without sending a command.
    $hardwarePlugin = New-Object FFBeast.SimHubPlugin.FFBeastPlugin
    try {
        $result = $hardwarePlugin.ReconnectAsync().GetAwaiter().GetResult()
        Assert-True ($result.Outcome.ToString() -eq 'Sent') $result.Message
        Assert-True ($hardwarePlugin.Status.State.ToString() -eq 'Connected') 'Controller did not mark responding wheel connected.'
        Assert-True ($null -ne $hardwarePlugin.WheelSettings) $hardwarePlugin.SettingsMessage
        Write-Host 'PASS live vendor status reads and controller reconnection (no command writes)'
    }
    finally { $hardwarePlugin.End($null) }
}
