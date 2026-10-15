using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FFBeast.SimHubPlugin
{
    public enum WheelState { Disconnected, Connecting, Connected, Restarting, Stopped }
    public enum CommandOutcome { Sent, Failed, Unconfirmed, Busy }

    public sealed class WheelStatus
    {
        public WheelState State { get; }
        public string DeviceName { get; }
        public string Message { get; }
        public WheelStatus(WheelState state, string deviceName, string message)
        { State = state; DeviceName = deviceName; Message = message; }
    }

    public sealed class CommandResult
    {
        public CommandOutcome Outcome { get; }
        public string Message { get; }
        public CommandResult(CommandOutcome outcome, string message)
        { Outcome = outcome; Message = message; }
    }

    /// <summary>Serializes all USB access; status reads never wait for USB.</summary>
    public sealed partial class FFBeastWheelApiClient : IDisposable
    {
        private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(5);
        private readonly object _gate = new object();
        private readonly IWheelTransport _transport;
        private readonly Action<string> _log;
        private readonly Func<TimeSpan> _now;
        private readonly Timer _monitor;
        private IWheelConnection? _connection;
        private string? _devicePath;
        private TimeSpan _nextConnectionAttempt;
        private TimeSpan _restartUntil;
        private int _commandPending;
        private int _stopping;
        private volatile WheelStatus _status = new WheelStatus(WheelState.Disconnected, "Not connected", "Waiting for wheelbase.");

        public FFBeastWheelApiClient(IWheelTransport transport, Action<string> log, Func<TimeSpan>? now = null)
        {
            _transport = transport;
            _log = log;
            var clock = Stopwatch.StartNew();
            _now = now ?? (() => clock.Elapsed);
            _monitor = new Timer(_ => PollConnection(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public WheelStatus Status => _status;
        public bool IsConnected => Status.State == WheelState.Connected;
        public string DeviceName => Status.DeviceName;
        public WheelTelemetry? Telemetry
        {
            get
            {
                var sample = _telemetry;
                return sample != null && DateTime.UtcNow - sample.SampledUtc <= TimeSpan.FromSeconds(1) ? sample : null;
            }
        }
        public WheelSettings? Settings => _settings?.Clone();
        public string SettingsMessage => _settingsMessage;
        public string ActiveProfileName => _activeProfileName;
        public bool ReducedStrengthActive => Volatile.Read(ref _restoreStrength) >= 0;

        public void StartMonitoring()
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _stopping) == 0) _monitor.Change(0, 50);
            }
        }

        public Task<CommandResult> ReconnectAsync() => RunCommandAsync(() =>
        {
            if (_now() < _restartUntil) return RestartingResult();
            CloseConnection();
            if (!EnsureConnected(true)) return Result(CommandOutcome.Failed, Status.Message);
            RefreshSettingsSafely();
            return Result(CommandOutcome.Sent, "Connected to wheelbase.");
        });

        public Task<CommandResult> ResetCenterAsync() => RunCommandAsync(() => SendCommand(0x04, false));
        public Task<CommandResult> RebootAsync() => RunCommandAsync(() => SendCommand(0x01, true));
        public Task<CommandResult> SaveAndRebootAsync() => RunCommandAsync(() => SendCommand(0x02, true));

        private async Task<CommandResult> RunCommandAsync(Func<CommandResult> command)
        {
            // Drop overlapping presses instead of queuing delayed commands or another reboot.
            if (Interlocked.CompareExchange(ref _commandPending, 1, 0) != 0)
                return Result(CommandOutcome.Busy, "Another wheel command is in progress.");
            try
            {
                return await Task.Run(() =>
                {
                    lock (_gate)
                    {
                        if (Volatile.Read(ref _stopping) != 0)
                            return Result(CommandOutcome.Failed, "Plugin is stopped.");
                        try { return command(); }
                        catch (Exception ex)
                        {
                            Disconnect("Wheel command failed: " + ex.Message);
                            _log("Unexpected command error: " + ex);
                            return Result(CommandOutcome.Failed, Status.Message);
                        }
                    }
                }).ConfigureAwait(false);
            }
            finally { Interlocked.Exchange(ref _commandPending, 0); }
        }

        private CommandResult SendCommand(byte command, bool reboot)
        {
            if (_now() < _restartUntil) return RestartingResult();
            // Replace stale handles before writing. Writes themselves are never retried.
            if (!EnsureConnected(true)) return Result(CommandOutcome.Failed, Status.Message);
            var report = new byte[65];
            string operation = command == 0x02 ? "Save & Reboot" : "Reboot";
            report[0] = 0xA3;
            report[1] = command;
            try
            {
                _connection!.Write(report);
                return Result(CommandOutcome.Sent, reboot
                    ? operation + " command sent. Waiting for the wheelbase to reconnect."
                    : "Recenter command sent.");
            }
            catch (Exception ex) when (reboot && (ex is IOException || ex is TimeoutException))
            {
                _log(operation + " write interrupted; the wheel may already be restarting: " + ex);
                return Result(CommandOutcome.Unconfirmed,
                    operation + " could not be confirmed. The wheel may be restarting; waiting to reconnect. The command will not be retried.");
            }
            catch (Exception ex)
            {
                Disconnect("Command failed: " + ex.Message + " The command was not retried.");
                _log("USB write failed: " + ex);
                return Result(CommandOutcome.Failed, Status.Message);
            }
            finally
            {
                if (reboot)
                {
                    CloseConnection();
                    _restartUntil = _now() + ReconnectInterval;
                    _nextConnectionAttempt = _restartUntil;
                    SetStatus(WheelState.Restarting, "Not connected", "Waiting for wheelbase restart; reconnecting automatically.");
                }
            }
        }

        internal void PollConnection()
        {
            // Timer callbacks never queue behind a command or shutdown.
            if (!Monitor.TryEnter(_gate)) return;
            try
            {
                if (Volatile.Read(ref _stopping) == 0 && EnsureConnected(false) && _now() >= _nextSettingsRead)
                    RefreshSettingsSafely();
            }
            catch (Exception ex)
            {
                Disconnect("Connection check failed: " + ex.Message);
                _log("Connection check error: " + ex);
            }
            finally { Monitor.Exit(_gate); }
        }

        private bool EnsureConnected(bool immediate)
        {
            if (_now() < _restartUntil) return false;
            if (_connection != null)
            {
                try
                {
                    // Enumeration catches removal; bounded reads catch stale reused paths.
                    if (!_transport.Enumerate().Any(d => d.DevicePath == _devicePath))
                        throw new IOException("Wheelbase was unplugged.");
                    _telemetry = _connection.ReadStatus();
                    return true;
                }
                catch (Exception ex) { Disconnect("Connection lost: " + ex.Message); }
            }
            if (!immediate && _now() < _nextConnectionAttempt) return false;
            _nextConnectionAttempt = _now() + ReconnectInterval;
            SetStatus(WheelState.Connecting, "Not connected", "Looking for wheelbase.");
            string failure = "Wheelbase not found. Retrying automatically.";
            try
            {
                foreach (var device in _transport.Enumerate())
                {
                    IWheelConnection? candidate = null;
                    try
                    {
                        if (!IsVendorInterface(device)) continue;
                        var name = device.DeviceName;
                        var path = device.DevicePath;
                        candidate = device.Open();
                        var telemetry = candidate.ReadStatus();
                        _connection = candidate;
                        candidate = null;
                        _devicePath = path;
                        _telemetry = telemetry;
                        _nextSettingsRead = TimeSpan.Zero;
                        SetStatus(WheelState.Connected, name, "Wheelbase is responding.");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        failure = "Unable to connect: " + ex.Message + " Retrying automatically.";
                        _log("Could not open vendor interface: " + ex);
                    }
                    finally
                    {
                        try { candidate?.Dispose(); }
                        catch (Exception ex) { _log("Failed to close candidate: " + ex); }
                    }
                }
            }
            catch (Exception ex)
            {
                failure = "Device enumeration failed: " + ex.Message + " Retrying automatically.";
                _log(failure);
            }
            SetStatus(WheelState.Disconnected, "Not connected", failure);
            return false;
        }

        internal static bool IsVendorInterface(IWheelDevice device) =>
            device.DevicePath.IndexOf("&mi_00", StringComparison.OrdinalIgnoreCase) >= 0 &&
            device.InputReportLength == 65 && device.OutputReportLength == 65;

        private CommandResult RestartingResult() => Result(CommandOutcome.Busy, "Wheelbase is restarting. Please wait for reconnection.");
        private CommandResult Result(CommandOutcome outcome, string message)
        {
            _log(outcome + ": " + message);
            return new CommandResult(outcome, message);
        }
        private void SetStatus(WheelState state, string name, string message)
        {
            if (_status.State != state || _status.Message != message) _log(state + ": " + message);
            _status = new WheelStatus(state, name, message);
        }
        private void Disconnect(string message)
        {
            CloseConnection();
            _nextConnectionAttempt = _now() + ReconnectInterval;
            SetStatus(WheelState.Disconnected, "Not connected", message);
        }
        private void CloseConnection()
        {
            var old = _connection;
            _connection = null;
            _devicePath = null;
            _telemetry = null;
            _settings = null;
            _settingsMessage = "Wheel settings are unavailable while disconnected.";
            _restoreStrength = -1;
            _activeProfileName = "";
            try { old?.Dispose(); }
            catch (Exception ex) { _log("Failed to close HID connection: " + ex); }
        }
        public void Dispose()
        {
            Interlocked.Exchange(ref _stopping, 1);
            _monitor.Dispose();
            lock (_gate)
            {
                CloseConnection();
                SetStatus(WheelState.Stopped, "Not connected", "Plugin is stopped.");
            }
        }
    }
}
