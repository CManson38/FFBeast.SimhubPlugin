using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FFBeast.SimHubPlugin
{
    public sealed partial class FFBeastWheelApiClient
    {
        private volatile WheelTelemetry? _telemetry;
        private volatile WheelSettings? _settings;
        private volatile string _settingsMessage = "Waiting to read wheel settings.";
        private volatile string _activeProfileName = "";
        private TimeSpan _nextSettingsRead;
        private int _restoreStrength = -1;
        private int _reducedStrength;

        public Task<CommandResult> RefreshSettingsAsync() => RunCommandAsync(() =>
        {
            if (_now() < _restartUntil) return RestartingResult();
            if (!EnsureConnected(true)) return Result(CommandOutcome.Failed, Status.Message);
            ReadSettingsCore();
            return Result(CommandOutcome.Sent, "Current wheel settings loaded.");
        });

        public Task<CommandResult> SetSettingAsync(WheelSetting field, int value)
        {
            try { WheelProtocol.Validate(field, value); }
            catch (ArgumentException ex) { return Task.FromResult(Result(CommandOutcome.Failed, ex.Message)); }
            return RunCommandAsync(() => WithSettings(current =>
            {
                var desired = current.Clone(); desired.Set(field, value);
                return WriteSettings(current, desired);
            }));
        }

        public Task<CommandResult> AdjustStrengthAsync(int delta) => RunCommandAsync(() => WithSettings(current =>
        {
            var desired = current.Clone();
            desired.Strength = (int)Math.Max(0L, Math.Min(100L, (long)current.Strength + delta));
            return WriteSettings(current, desired);
        }));

        public Task<CommandResult> AdjustSettingAsync(WheelSetting field, int delta)
        {
            try { WheelProtocol.Validate(field, field == WheelSetting.RotationRange ? 1 : 0); }
            catch (ArgumentException ex) { return Task.FromResult(Result(CommandOutcome.Failed, ex.Message)); }
            return RunCommandAsync(() => WithSettings(current =>
            {
                int min = field == WheelSetting.RotationRange ? 1 : 0;
                int max = field == WheelSetting.RotationRange ? 65535 : field == WheelSetting.SoftStopRange ? 255 : field == WheelSetting.ForceEnabled ? 1 : 100;
                var desired = current.Clone();
                desired.Set(field, (int)Math.Max(min, Math.Min(max, (long)current.Get(field) + delta)));
                return WriteSettings(current, desired);
            }));
        }

        public Task<CommandResult> ToggleReducedStrengthAsync() => RunCommandAsync(() => WithSettings(current =>
        {
            int restore = _restoreStrength;
            var desired = current.Clone();
            desired.Strength = restore >= 0 ? restore : Math.Min(current.Strength, 25);
            var result = WriteSettings(current, desired);
            if (result.Outcome == CommandOutcome.Sent && restore < 0)
            {
                _reducedStrength = desired.Strength;
                _restoreStrength = current.Strength;
            }
            return result;
        }));

        public Task<CommandResult> ToggleForceAsync() => RunCommandAsync(() => WithSettings(current =>
        {
            var desired = current.Clone(); desired.ForceEnabled = !current.ForceEnabled;
            return WriteSettings(current, desired);
        }));

        public Task<CommandResult> ApplySettingsAsync(WheelSettings settings, string profileName = "")
        {
            WheelSettings desired;
            try
            {
                if (settings == null) throw new ArgumentException("Settings are missing.");
                desired = settings.Clone(); desired.Validate();
            }
            catch (ArgumentException ex) { return Task.FromResult(Result(CommandOutcome.Failed, ex.Message)); }
            return RunCommandAsync(() => WithSettings(current =>
            {
                var result = WriteSettings(current, desired);
                if (result.Outcome == CommandOutcome.Sent) _activeProfileName = profileName;
                return result;
            }));
        }

        private CommandResult WithSettings(Func<WheelSettings, CommandResult> action)
        {
            if (_now() < _restartUntil) return RestartingResult();
            if (!EnsureConnected(true)) return Result(CommandOutcome.Failed, Status.Message);
            return action(ReadSettingsCore());
        }

        private WheelSettings ReadSettingsCore()
        {
            var actual = _connection!.ReadSettings(); actual.Validate();
            if (_settings != null && !_settings.Matches(actual)) _activeProfileName = "";
            if (_restoreStrength >= 0 && actual.Strength != _reducedStrength) _restoreStrength = -1;
            _settings = actual.Clone();
            _settingsMessage = "Current settings read from wheel.";
            _nextSettingsRead = _now() + TimeSpan.FromSeconds(5);
            return actual;
        }

        private void RefreshSettingsSafely()
        {
            _nextSettingsRead = _now() + TimeSpan.FromSeconds(5);
            try { ReadSettingsCore(); }
            catch (Exception ex)
            {
                string message = "Unable to read wheel settings: " + ex.Message;
                if (_settingsMessage != message) _log(message);
                _settings = null; _settingsMessage = message;
                _activeProfileName = ""; _restoreStrength = -1;
            }
        }

        private CommandResult WriteSettings(WheelSettings current, WheelSettings desired)
        {
            desired.Validate();
            _activeProfileName = ""; _restoreStrength = -1;
            // Disable first when requested; enabling is deliberately the last write.
            var fields = Enum.GetValues(typeof(WheelSetting)).Cast<WheelSetting>()
                .OrderBy(field => field == WheelSetting.ForceEnabled ? (desired.ForceEnabled ? 2 : 0) : 1)
                .Where(field => current.Get(field) != desired.Get(field)).ToArray();
            if (fields.Length == 0) return Result(CommandOutcome.Sent, "Wheel settings already match.");
            try
            {
                foreach (var field in fields) _connection!.Write(WheelProtocol.SettingReport(field, desired.Get(field)));
                // USB completion isn't firmware acknowledgement. Read back, allowing a short
                // processing interval; retry only the reads, never a settings write.
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    if (ReadSettingsCore().Matches(desired))
                        return Result(CommandOutcome.Sent, "Wheel settings applied and verified. Use Save & Reboot to make them persistent.");
                    if (attempt < 4) Thread.Sleep(20);
                }
                throw new IOException("Wheel did not confirm the requested settings.");
            }
            catch (Exception ex)
            {
                Disconnect("Settings could not be confirmed. Some changes may have applied. Refresh before trying again.");
                _log("Settings operation interrupted (no writes retried): " + ex);
                return Result(CommandOutcome.Unconfirmed, Status.Message);
            }
        }
    }
}
