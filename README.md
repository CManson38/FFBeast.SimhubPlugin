# FFBeast Wheelbase Control for SimHub

A SimHub plugin that allows you to control your FFBeast wheelbase directly from SimHub, enabling you to bind controller buttons or keyboard keys to wheelbase functions like recenter.

> **Note**: This plugin is designed specifically for the FFBeast direct drive wheelbase and requires the device to be connected via USB.

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-Framework%204.8-purple.svg)](https://dotnet.microsoft.com/)
[![SimHub](https://img.shields.io/badge/SimHub-Compatible-green.svg)](https://www.simhubdash.com/)

## Features

- **Reset Center**: Recalibrate the wheelbase center position with a button press
- **Reboot Controller**: Restart the wheelbase controller
- **Wheel settings**: Strength presets and +/- controls, reduced-strength toggle, rotation presets, damping, integrated spring, soft stops, and FFB enable/disable
- **Profiles**: Save named presets in SimHub, apply/cycle them from buttons, and optionally match games/cars while stationary
- **Read-only telemetry**: Live steering position, normalized torque output, firmware version, and current wheel settings for dashboards and formulas
- **Save & Reboot**: Explicitly save the wheel's current settings permanently and restart it
- **Auto-reconnect**: Detects lost connections and reconnects even when no game is running
- **SimHub Integration**: Full integration with SimHub's control mapping system
- **Settings UI**: Test actions and view connection status directly in SimHub settings

## Prerequisites

- Windows PC with SimHub installed
- FFBeast wheelbase connected via USB
- .NET 9 SDK or later (for building from source)
- Visual Studio 2022 or VS Code (optional, can build with dotnet CLI)

## Installation

### Option 1: Using Pre-built DLL (Recommended)

1. Go to the [Releases](https://github.com/CoryManson/FFBeast.SimhubPlugin/releases) page
2. Download the latest `FFBeast.SimHubPlugin-vX.X.X.zip` file
3. Close SimHub and extract the ZIP archive
4. Copy `FFBeast.SimHubPlugin.dll` (and optional PDB) to your SimHub installation directory. Preserve SimHub's existing `HidSharp.dll` and other shared dependencies:
   ```
   C:\Program Files (x86)\SimHub\
   ```
5. Restart SimHub
6. The plugin should appear in the SimHub settings under **Additional Plugins**

### Option 2: Build from Source

1. **Prerequisites**:
   - .NET 9 SDK or later
   - SimHub installed (default: `C:\Program Files (x86)\SimHub\`)

2. **Clone the Repository**:
   ```powershell
   git clone https://github.com/CoryManson/FFBeast.SimhubPlugin.git
   cd FFBeast.SimhubPlugin
   ```

3. **Set Environment Variable** (Optional):
   - If SimHub is installed in a non-default location, set `SIMHUB_INSTALL_PATH`:
     ```powershell
     [System.Environment]::SetEnvironmentVariable('SIMHUB_INSTALL_PATH', 'C:\Your\SimHub\Path\', 'User')
     ```

4. **Build the Project**:
   ```powershell
   dotnet build FFBeast.SimHubPlugin.csproj -c Release
   ```

5. **Test and deploy to SimHub** (close SimHub first; deployment backs up the installed plugin):
   ```powershell
   .\build.ps1 -Test -Deploy
   ```

   Building alone never changes the SimHub installation. Use `-SimHubPath 'C:\Your\SimHub'` for a non-default installation. Existing build output can be installed with `.\deploy.ps1`.

6. **Restart SimHub**:
   - Close and restart SimHub to load the plugin

## Usage

### Setting up Button Bindings in SimHub

1. **Open SimHub** and navigate to **Controls and Events**

2. **Go to Custom Serial Controls** (or Controls tab)

3. **Add a New Action**:
   - Click "Add" or "+" button
   - Look for actions starting with "FFBeast."

4. **Available Actions**:
   - `FFBeast.ResetCenter` - Reset wheelbase center position
   - `FFBeast.Reboot` - Reboot the wheelbase controller
   - See the action reference below or the plugin's **Button mappings** tab for the complete list

5. **Map to Your Controller**:
   - Select the action (e.g., `FFBeast.ResetCenter`)
   - Click "Map" or "Bind"
   - Press the button on your controller or keyboard key you want to use
   - Save the mapping

### Example: Binding Recenter to a Button

1. In SimHub, go to **Controls and Events** → **Custom Serial controls**
2. Click **Add action**
3. Find and select **FFBeast.ResetCenter**
4. Click the **Map** button
5. Press your desired button (e.g., a button on your wheel or controller)
6. The button is now bound - press it anytime to recenter your wheelbase!

### Plugin Settings

Access the plugin settings in SimHub:
1. Go to **Additional plugins** or **Plugins** tab
2. Find **FFBeast Wheelbase Control**
3. View connection status and device name
4. Use **Quick Test** buttons to test actions:
   - **Test Recenter** - Immediately recenter the wheelbase
   - **Test Reboot** - Reboot the wheelbase (with confirmation dialog)

Test buttons and mapped actions share the same background command path. Overlapping requests are rejected instead of queued. Results appear beneath the test buttons and in the SimHub log.

Reboot always closes the old USB handle and waits at least five seconds before reconnecting. If Windows reports a timeout or disconnect during reboot, the result is **unconfirmed**: the wheel may already be restarting. The plugin never automatically resends a command. `Connected` means the wheel has returned a valid status report; it does not confirm that a previous command executed.

### Wheel settings and persistence

Open **Wheel settings** and use **Read current settings** to load the editor from the wheel. Preset buttons apply immediately. **Apply editor settings** validates all values, writes only changed fields, and reads the wheel settings back before reporting success. The editor stays unchanged during ordinary polling so unsaved edits are preserved.

| Setting | Values |
| --- | --- |
| Strength, static damping, integrated spring, soft-stop strength/damping | 0–100% |
| Rotation range | 1–65535° (protocol range); quick presets: 360°, 540°, 900°, 1080° |
| Soft-stop range | 0–255 extra degrees beyond rotation range |
| FFB enabled | On/off |

The reduced-strength toggle caps the current strength at 25% and remembers the previous strength for the next toggle. It never increases a strength already below 25%. Manual strength changes, external strength changes, and disconnects clear the remembered restore value.

Changes remain temporary until **Save & Reboot** is explicitly invoked. That action saves the settings currently on the wheel, including changes made by other applications; it does not apply unsaved editor values. Saving a SimHub profile only writes a preset to SimHub's common plugin settings.

Profile application uses multiple USB writes. Force disable is sent first when requested, and force enable is sent last when needed. If a write or readback fails, the result is unconfirmed and some changes may already have applied. The plugin stops, releases the handle, and never resends those writes. Reconnect/read current settings before deciding what to apply next.

### Profiles

1. Read the wheel settings or edit the values on the **Wheel settings** tab.
2. On **Profiles**, enter a name and choose **Save editor as new profile**. Use **Update selected from editor** to replace an existing preset.
3. Select a profile to load its values into the editor; choose **Apply selected profile** to change the wheel.
4. Map **ApplySelectedProfile**, **NextProfile**, or **PreviousProfile** to wheel buttons. Next/Previous selects and applies the adjacent profile.

Automatic profile matching is **off by default**. To opt in, give a profile an exact game name and optionally a car model (or use **Use current game and car**), save it, and enable **Automatically apply matching profiles**. Matches ignore case, and car-specific rules take priority over game-only rules. A match is attempted once per game/car context when speed is at most 1 km/h and the game is outside a replay. A failed partial application is not automatically retried; a busy request can be deferred. Reconnecting the wheel does not reapply an already attempted profile. Profiles with no game name are manual only.

### Action reference

All names below have the `FFBeast.` prefix.

| Actions | Behavior |
| --- | --- |
| `ResetCenter`, `Reboot`, `SaveAndReboot` | Controller commands; SaveAndReboot is the only persistent-save action |
| `Reconnect`, `ReadSettings` | Refresh connection or current wheel settings |
| `StrengthIncrease`, `StrengthDecrease` | Adjust strength by 5 percentage points, clamped to 0–100% |
| `Strength25`, `Strength50`, `Strength75`, `Strength100` | Apply a strength preset |
| `ToggleReducedStrength` | Cap strength at 25% / restore previous strength |
| `Rotation360`, `Rotation540`, `Rotation900`, `Rotation1080` | Apply rotation range in degrees |
| `DampingIncrease` / `DampingDecrease` | Adjust static damping by 5 percentage points |
| `SpringIncrease` / `SpringDecrease` | Adjust integrated spring by 5 percentage points |
| `SoftStopStrengthIncrease` / `SoftStopStrengthDecrease` | Adjust soft-stop strength by 5 percentage points |
| `SoftStopDampingIncrease` / `SoftStopDampingDecrease` | Adjust soft-stop damping by 5 percentage points |
| `SoftStopRangeIncrease` / `SoftStopRangeDecrease` | Adjust extra soft-stop range by 5 degrees |
| `EnableFFB`, `DisableFFB`, `ToggleFFB` | Change the wheel's force-enable setting |
| `ApplySelectedProfile`, `NextProfile`, `PreviousProfile` | Apply the selected/adjacent SimHub preset |

Mapped actions execute directly; the Save & Reboot confirmation dialog applies to the settings UI button.

### Dashboard properties

Properties are declared under the plugin with the `FFBeast.` prefix (SimHub may display the plugin class prefix as well).

| Properties | Meaning |
| --- | --- |
| `IsConnected`, `DeviceName`, `Status`, `ConnectionMessage` | Connection status |
| `TelemetryValid`, `TelemetryTimestampUtc` | Whether a recent status sample is available, and its UTC timestamp |
| `Position`, `PositionPercent` | Signed normalized steering position (-1 to +1), or percent |
| `Torque`, `TorquePercent` | Signed normalized reported torque output, or percent; not calibrated Nm |
| `FirmwareVersion`, `FirmwareReleaseType` | Firmware version and numeric firmware release-type code |
| `SettingsValid`, `SettingsMessage` | Whether current wheel settings have been read successfully, with a diagnostic message |
| `Strength`, `RotationRange`, `Damping`, `Spring` | Current settings read from the wheel |
| `SoftStopStrength`, `SoftStopRange`, `SoftStopDamping`, `ForceEnabled` | Current soft-stop and force-enable settings |
| `ReducedStrengthActive`, `ActiveProfile`, `SelectedProfile` | Session reduction state and profile names |

Telemetry snapshots refresh at up to 20 Hz; the input reader continuously drains USB reports to avoid a stale backlog. Settings refresh every five seconds and after commands. Numeric telemetry/settings properties become null when unavailable. `ActiveProfile` is cleared on disconnect, manual tuning, or a detected external settings change.

The plugin does not generate telemetry-driven effects or send direct-force override reports. iRacing continues to generate the native FFB; the controls above adjust the wheelbase's own settings only.

## Troubleshooting

### Plugin Not Showing Up

- Verify all DLLs are copied to the SimHub directory
- Check SimHub logs for any errors
- Ensure .NET Framework 4.8 is installed
- Try restarting SimHub

### Cannot Connect to Wheelbase

- Make sure the FFBeast wheelbase is connected via USB
- Check Windows Device Manager for the device (VID: 1115, PID: 22999)
- Try unplugging and reconnecting the wheelbase
- The plugin monitors live status and retries failed connections at least 5 seconds apart, including while no game is running

### Button Mapping Not Working

- Verify the action is properly mapped in SimHub Controls
- Check that the plugin shows "Connected" status in green
- Test the action using the Quick Test buttons first
- Try remapping the button
- Check SimHub logs for any error messages

## Development

### Project Structure

```
FFBeast.SimhubPlugin/
├── FFBeast.SimHubPlugin.csproj   # Modern SDK-style project file
├── FFBeastPlugin.cs              # SimHub actions, properties and lifecycle
├── FFBeastWheelApiClient.cs      # Serialized commands and connection recovery
├── FFBeastWheelSettingsClient.cs # Settings changes and readback verification
├── WheelProtocol.cs              # Packed reports, settings limits and telemetry decoding
├── WheelProfiles.cs              # Profile models, matching and cycling
├── WheelTransport.cs            # Testable USB transport contracts
├── HidWheelTransport.cs         # HidSharp adapter with bounded reads/writes
├── SettingsControl.xaml          # WPF settings UI
├── SettingsControl.xaml.cs       # Settings UI code-behind
├── build.ps1                     # PowerShell build script
├── deploy.ps1                    # Explicit deployment with backup and rollback on failure
├── tests/                        # Regression runner and WPF/hardware smoke checks
├── Properties/                   # Assembly metadata and resources
│   ├── AssemblyInfo.cs
│   ├── Resources.resx
│   └── Resources.Designer.cs
├── README.md                     # This file
└── TESTING.md                    # Automated and manual verification
```

### HID Communication Protocol

The plugin communicates with the FFBeast wheelbase using 65-byte HID reports:

```csharp
// Report structure (65 bytes: Report ID + 64 bytes data)
byte[] buffer = new byte[65];
buffer[0] = REPORT_GENERIC_INPUT_OUTPUT;  // 0xA3 (Report ID)
buffer[1] = COMMAND;                      // Command byte
// Remaining bytes are padding (zeros)
```

**Supported Commands**:
- `0x04` - Reset center position (recenter the wheelbase)
- `0x01` - Reboot controller (restart the wheelbase)
- `0x02` - Save current wheel settings and reboot
- `0x14` - Change one settings field (field ID at byte 2, index 0 at byte 3, value at byte 4)

Effect settings are read from feature report `0x22`, hardware settings from `0x21`, and live state from input report `0xA3`. All reports are 65 bytes. No direct-control/force-override report (`0x10`) is implemented.

**Device Selection**:
- VID: 1115 (0x045B), PID: 22999 (0x59D7)
- Requires MI_00 and exactly 65-byte input/output reports for the vendor interface
- Avoids the joystick interface (MI_01) which has 18-byte reports

### Building for Distribution

1. Build in Release mode:
   ```powershell
   dotnet build FFBeast.SimHubPlugin.csproj -c Release
   ```

2. Files to include in distribution (from `bin/Release/net48/`):
   - `FFBeast.SimHubPlugin.dll` - Main plugin assembly
   - `FFBeast.SimHubPlugin.pdb` - Debug symbols (optional)

   SimHub supplies `HidSharp.dll`; do not overwrite it with the build's package copy. The plugin compiles against HidSharp 2.1.0 and the smoke checks exercise the installed SimHub version.

### Creating a Release

To create a release for distribution:

1. **Build Release configuration**:
   ```powershell
   dotnet build FFBeast.SimHubPlugin.csproj -c Release
   ```

2. **Package the files**:
   ```powershell
   # Create a release folder
   New-Item -ItemType Directory -Force -Path release
   
   # Copy required files
   Copy-Item "bin\Release\net48\FFBeast.SimHubPlugin.dll" release\
   Copy-Item "README.md" release\
   
   # Create ZIP archive
   Compress-Archive -Path release\* -DestinationPath FFBeast.SimHubPlugin-v1.2.0-test.zip
   ```

3. **Create GitHub Release**:
   - Go to your repository on GitHub
   - Click "Releases" → "Create a new release"
   - Tag version (e.g., `v1.0.0`)
   - Upload the ZIP file
   - Add release notes

## Technical Details

### Dependencies

- **HidSharp 2.1.0** - Compile-time HID package; SimHub's installed library is used at runtime
- **SimHub SDK DLLs** - GameReaderCommon, SimHub.Plugins, SimHub.Logging, log4net
- **Target Framework**: .NET Framework 4.8 (SimHub compatibility)
- **Build SDK**: .NET 9 SDK (modern SDK-style project format)

### FFBeast Wheelbase Specifications

- **Vendor ID**: 1115 (0x045B)
- **Product ID**: 22999 (0x59D7)
- **HID Interface**: Vendor-specific interface (MI_00)
- **Report Length**: 65 bytes (Report ID + 64 bytes data)
- **Connection**: USB HID device

## Contributing

Contributions are welcome! Please feel free to submit issues or pull requests.

### Development Setup

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Make your changes
4. Test with your FFBeast wheelbase and SimHub
5. Commit your changes (`git commit -m 'Add amazing feature'`)
6. Push to the branch (`git push origin feature/amazing-feature`)
7. Open a Pull Request

### References

- [FFBeast Wheel API Library](https://github.com/pelukkk/pelukkk.github.io/tree/main/ffbeast-wheel-api-lib) - Original C/C++ API implementation
- [SimHub](https://www.simhubdash.com/) - Racing dashboard and control platform

## License

This project is provided as-is for the FFBeast community.

## Support

For issues or questions:
- Check SimHub logs for error messages
- Refer to FFBeast documentation
- Open an issue on the project repository

## Version History

### 1.2.0-test
- Add verified wheel-setting changes, strength and rotation presets, reduced-strength toggle, and FFB enable/disable
- Add damping, integrated spring, and soft-stop controls
- Expose current wheel settings and live position/torque/firmware properties
- Add named SimHub profiles, button-driven cycling, and optional stationary game/car matching
- Add explicit Save & Reboot; keep routine settings/profile changes temporary
- Expand regression coverage and WPF/profile persistence checks; no direct-force effects

### 1.1.0-test
- Recover stale or unplugged connections independently of game telemetry
- Serialize connection access and run commands outside the UI thread
- Release reboot handles, distinguish uncertain results, and prevent overlapping commands
- Stop settings polling when the view unloads and remove private-field reflection
- Add regression tests on .NET Framework 4.8 and .NET 9, WPF lifecycle checks, and optional read-only hardware checks
- Make deployment explicit, preserve shared libraries, and back up the installed plugin

### 1.0.0 (2025-11-03)
- Initial release
- SimHub plugin with action mapping support
- Reset Center and Reboot controller actions
- Auto-reconnect functionality (5-second interval)
- WPF settings UI with connection status and test buttons
- Modern SDK-style project compatible with .NET 9 SDK
