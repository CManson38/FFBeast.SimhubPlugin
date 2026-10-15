# Testing

Run from the repository root with the .NET 9 SDK and SimHub installed:

```powershell
.\build.ps1 -Test
```

This builds the plugin, runs 62 regression cases on both .NET Framework 4.8 and .NET 9, and checks the actual WPF view's load/unload lifecycle in Windows PowerShell. The WPF smoke also covers profile CRUD, JSON serialization with SimHub's installed Newtonsoft.Json, profile selection in the editor, and numeric validation. Any failed case makes the script fail. The regression runner uses fake USB connections and an injected monotonic clock; it requires no test-framework packages and sends no commands to hardware. `dotnet test` is not the runner for this project.

For a network-restricted machine with the build packages already cached, use `.\build.ps1 -Test -Offline`. This ignores unavailable package sources and skips NuGet's online audit for that invocation.

The cases cover exact report bytes and zero padding, connecting from both commands, interface selection, absent hardware, descriptor/open/status failures, stale handles after a quick replug, write timeouts, uncertain reboot outcomes, cooldown boundaries, slow restarts, recovery backoff, overlapping requests, responsive status reads, shutdown during a write, repeated shutdown, and disposal failures.

Feature cases cover packed status/feature offsets, signed telemetry, settings value limits and widths, readback verification, strength clamps and restoration, FFB toggles, profile write ordering, captured asynchronous inputs, partial writes, unsupported feature reports, cache isolation, explicit-only persistence, profile matching/cycling/cloning, stationary/replay rules, and the absence of direct-force override commands.

An optional smoke check opens the real vendor interface, reads live state and both settings feature reports, and exercises controller reconnection without sending any command writes:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\smoke.ps1 -Hardware
```

Both smoke checks load SimHub's installed HidSharp library, rather than substituting the NuGet copy. Add `-SimHubPath 'C:\Your\SimHub'` if needed.

## Installation for interactive verification

Close SimHub, then run:

```powershell
.\build.ps1 -Test -Deploy
```

The script installs only the plugin DLL and symbols. It stores the previous files and a deployment manifest under `artifacts/backups/`, verifies the installed DLL hash, and rolls back changed files if deployment fails. SimHub's shared libraries are preserved. Start SimHub afterward and find `[FFBeast Plugin] Starting version 1.2.0.0` in its log.

## Manual cases in SimHub

| Case | Steps | Expected result |
| --- | --- | --- |
| Idle startup | Start SimHub with the wheel connected and no game running. | Status becomes Connected without opening a game. |
| Recenter | With the wheel at a known off-center position, press Test Recenter once. | One command is sent; the wheel adopts the requested center. The UI remains responsive. |
| Reboot recovery | Press Test Reboot and confirm. Wait for reconnection, then test recenter. | Restarting appears, followed by Connected after the wheel returns. Recenter works without manual Reconnect. A write interrupted by reboot is reported as unconfirmed. |
| Rapid presses | Trigger the mapped reboot action repeatedly while the first request is pending/restarting. | Pending requests are rejected and no second reboot is queued. |
| Unplug/replug | Unplug USB while idle, wait for Disconnected, and reconnect it. | Disconnected is detected on the next health check; Connected returns after recovery. |
| Start unplugged | Start SimHub without the wheel, then connect it. | The plugin retries while idle and connects automatically. |
| Mapped actions | Exercise both mapped actions and both test buttons. | Both routes use the same recovery and result handling. |
| Settings navigation | Open and close plugin settings repeatedly, then reopen. | Status polling resumes on load and stops on unload. |
| Shutdown | Close SimHub while the plugin is connected. | No further opens or commands occur after plugin shutdown. |
| Strength controls | Exercise +/- and presets; toggle reduced strength twice. | Values are read back; the second toggle restores the previous strength. Nothing is permanently saved. |
| Rotation and tuning | Change rotation, damping, integrated spring and soft stops, then read current settings. | The wheel's readback matches the requested values. |
| FFB enable | Disable, read settings, then enable. | The force-enable flag matches the action. |
| Telemetry | Move the wheel and inspect properties/dashboard values. | Position updates with fresh samples; torque is normalized, not Nm. Disconnect clears availability. |
| Profiles | Save an editor preset, change the wheel, and apply the preset. Cycle profiles using actions. | Profiles persist in SimHub; selected/active names are distinct; only Apply/cycle changes the wheel. |
| Automatic profiles | Enable matching with a game/car rule, enter the car, and stop. Repeat while driving and in replay. | At most one match is applied per context, only while stationary outside a replay. Default behavior is off. |
| Persistent save | Apply settings, invoke Save & Reboot, and confirm. | Only this explicit action saves to the wheel; restart recovery proceeds as for Reboot. |

Automated fakes verify writes, failures, and concurrency. The optional hardware smoke verifies reads, not physical setting changes or controller commands. Settings commands additionally read back feature reports before reporting success. No test generates direct-force effects.

## Restore the previous plugin

Close SimHub and copy `FFBeast.SimHubPlugin.dll` and its PDB from the desired backup folder into the SimHub installation directory. The corresponding `deployment.json` identifies the installation destination and installed build hash. Start SimHub again.
