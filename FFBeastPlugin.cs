using GameReaderCommon;
using SimHub.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace FFBeast.SimHubPlugin
{
    [PluginDescription("FFBeast wheelbase control, settings, profiles and read-only wheel telemetry")]
    [PluginAuthor("FFBeast Community")]
    [PluginName("FFBeast Wheelbase Control")]
    public class FFBeastPlugin : IPlugin, IDataPlugin, IWPFSettings
    {
        private readonly FFBeastWheelApiClient _wheelClient = new FFBeastWheelApiClient(
            new HidWheelTransport(), message => SimHub.Logging.Current.Info("[FFBeast Plugin] " + message));
        private readonly object _profilesGate = new object();
        private readonly object _autoGate = new object();
        private readonly AutoProfileSelector _autoSelector = new AutoProfileSelector();
        private volatile ProfileConfiguration _profiles = new ProfileConfiguration();
        private DateTime _nextAutoCheck;
        private bool _initialized;
        private volatile string _currentGame = "";
        private volatile string _currentCar = "";
        private readonly List<string> _actionNames = new List<string>();

        public PluginManager PluginManager { get; set; } = null!;
        public WheelStatus Status => _wheelClient.Status;
        public WheelTelemetry? Telemetry => _wheelClient.Telemetry;
        public WheelSettings? WheelSettings => _wheelClient.Settings;
        public string SettingsMessage => _wheelClient.SettingsMessage;
        public string ActiveProfileName => _wheelClient.ActiveProfileName;
        public ProfileConfiguration Profiles => _profiles.Clone();
        public string CurrentGame => _currentGame;
        public string CurrentCar => _currentCar;
        public string[] ActionNames => _actionNames.ToArray();
        public Task<CommandResult> ReconnectAsync() => _wheelClient.ReconnectAsync();
        public Task<CommandResult> ResetCenterAsync() => _wheelClient.ResetCenterAsync();
        public Task<CommandResult> RebootAsync() => _wheelClient.RebootAsync();
        public Task<CommandResult> SaveAndRebootAsync() => _wheelClient.SaveAndRebootAsync();
        public Task<CommandResult> RefreshSettingsAsync() => _wheelClient.RefreshSettingsAsync();
        public Task<CommandResult> ApplySettingsAsync(WheelSettings settings) => _wheelClient.ApplySettingsAsync(settings);
        public Task<CommandResult> SetSettingAsync(WheelSetting setting, int value) => _wheelClient.SetSettingAsync(setting, value);
        public Task<CommandResult> AdjustStrengthAsync(int delta) => _wheelClient.AdjustStrengthAsync(delta);
        public Task<CommandResult> ToggleReducedStrengthAsync() => _wheelClient.ToggleReducedStrengthAsync();
        public Task<CommandResult> ToggleForceAsync() => _wheelClient.ToggleForceAsync();
        public Control GetWPFSettingsControl(PluginManager pluginManager) => new SettingsControl(this);

        public void Init(PluginManager pluginManager)
        {
            PluginManager = pluginManager;
            SimHub.Logging.Current.Info("[FFBeast Plugin] Starting version " + GetType().Assembly.GetName().Version);
            try
            {
                var saved = this.ReadCommonSettings<ProfileConfiguration>("Profiles", () => new ProfileConfiguration());
                saved.Validate(); _profiles = saved.Clone();
            }
            catch (Exception ex) { SimHub.Logging.Current.Error("[FFBeast Plugin] Unable to load profiles: " + ex); }
            _initialized = true;
            RegisterAction("ResetCenter", ResetCenterAsync);
            RegisterAction("Reboot", RebootAsync);
            RegisterAction("SaveAndReboot", SaveAndRebootAsync);
            RegisterAction("Reconnect", ReconnectAsync);
            RegisterAction("ReadSettings", RefreshSettingsAsync);
            RegisterAction("StrengthIncrease", () => AdjustStrengthAsync(5));
            RegisterAction("StrengthDecrease", () => AdjustStrengthAsync(-5));
            RegisterAction("ToggleReducedStrength", ToggleReducedStrengthAsync);
            foreach (int percent in new[] { 25, 50, 75, 100 })
            {
                int value = percent;
                RegisterAction("Strength" + value, () => SetSettingAsync(WheelSetting.Strength, value));
            }
            foreach (int degrees in new[] { 360, 540, 900, 1080 })
            {
                int value = degrees;
                RegisterAction("Rotation" + value, () => SetSettingAsync(WheelSetting.RotationRange, value));
            }
            foreach (var setting in new[] { WheelSetting.Damping, WheelSetting.Spring, WheelSetting.SoftStopStrength, WheelSetting.SoftStopDamping, WheelSetting.SoftStopRange })
            {
                var field = setting;
                RegisterAction(field + "Increase", () => _wheelClient.AdjustSettingAsync(field, 5));
                RegisterAction(field + "Decrease", () => _wheelClient.AdjustSettingAsync(field, -5));
            }
            RegisterAction("EnableFFB", () => SetSettingAsync(WheelSetting.ForceEnabled, 1));
            RegisterAction("DisableFFB", () => SetSettingAsync(WheelSetting.ForceEnabled, 0));
            RegisterAction("ToggleFFB", ToggleForceAsync);
            RegisterAction("ApplySelectedProfile", ApplySelectedProfileAsync);
            RegisterAction("NextProfile", () => CycleProfileAsync(1));
            RegisterAction("PreviousProfile", () => CycleProfileAsync(-1));

            this.AttachDelegate("FFBeast.IsConnected", () => _wheelClient.IsConnected);
            this.AttachDelegate("FFBeast.DeviceName", () => Status.DeviceName);
            this.AttachDelegate("FFBeast.Status", () => Status.State.ToString());
            this.AttachDelegate("FFBeast.ConnectionMessage", () => Status.Message);
            this.AttachDelegate("FFBeast.TelemetryValid", () => Telemetry != null);
            this.AttachDelegate("FFBeast.Position", () => Telemetry?.Position);
            this.AttachDelegate("FFBeast.PositionPercent", () => Telemetry?.Position * 100);
            this.AttachDelegate("FFBeast.Torque", () => Telemetry?.Torque);
            this.AttachDelegate("FFBeast.TorquePercent", () => Telemetry?.Torque * 100);
            this.AttachDelegate("FFBeast.FirmwareVersion", () => Telemetry?.FirmwareVersion ?? "");
            this.AttachDelegate("FFBeast.FirmwareReleaseType", () => Telemetry?.FirmwareReleaseType);
            this.AttachDelegate("FFBeast.TelemetryTimestampUtc", () => Telemetry?.SampledUtc);
            this.AttachDelegate("FFBeast.SettingsValid", () => WheelSettings != null);
            this.AttachDelegate("FFBeast.SettingsMessage", () => SettingsMessage);
            this.AttachDelegate("FFBeast.Strength", () => WheelSettings?.Strength);
            this.AttachDelegate("FFBeast.RotationRange", () => WheelSettings?.RotationRange);
            this.AttachDelegate("FFBeast.Damping", () => WheelSettings?.Damping);
            this.AttachDelegate("FFBeast.Spring", () => WheelSettings?.Spring);
            this.AttachDelegate("FFBeast.SoftStopStrength", () => WheelSettings?.SoftStopStrength);
            this.AttachDelegate("FFBeast.SoftStopRange", () => WheelSettings?.SoftStopRange);
            this.AttachDelegate("FFBeast.SoftStopDamping", () => WheelSettings?.SoftStopDamping);
            this.AttachDelegate("FFBeast.ForceEnabled", () => WheelSettings?.ForceEnabled);
            this.AttachDelegate("FFBeast.ReducedStrengthActive", () => _wheelClient.ReducedStrengthActive);
            this.AttachDelegate("FFBeast.ActiveProfile", () => ActiveProfileName);
            this.AttachDelegate("FFBeast.SelectedProfile", () => _profiles.Selected?.Name ?? "");
            // Independent of game telemetry. This never sends direct-force reports.
            _wheelClient.StartMonitoring();
        }

        private void RegisterAction(string name, Func<Task<CommandResult>> action)
        {
            _actionNames.Add("FFBeast." + name);
            this.AddAction("FFBeast." + name, (a, b) => { _ = RunMappedActionAsync(action); });
        }
        private async Task RunMappedActionAsync(Func<Task<CommandResult>> action)
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception ex) { SimHub.Logging.Current.Error("[FFBeast Plugin] Action failed: " + ex); }
        }

        public void SaveProfile(WheelProfile profile, bool replace)
        {
            var copy = profile.Clone(); copy.Name = copy.Name.Trim(); copy.Game = copy.Game.Trim(); copy.Car = copy.Car.Trim(); copy.Validate();
            UpdateProfiles(config =>
            {
                int index = config.Profiles.FindIndex(p => p.Id == copy.Id);
                if (replace && index < 0) throw new ArgumentException("Select an existing profile to update.");
                if (replace) config.Profiles[index] = copy;
                else { copy.Id = Guid.NewGuid().ToString("N"); config.Profiles.Add(copy); }
                config.SelectedProfileId = copy.Id;
            });
        }
        public void DeleteProfile(string id) => UpdateProfiles(config =>
        {
            config.Profiles.RemoveAll(p => p.Id == id);
            if (config.SelectedProfileId == id) config.SelectedProfileId = config.Profiles.FirstOrDefault()?.Id ?? "";
        });
        public void SelectProfile(string id) => UpdateProfiles(config => config.SelectedProfileId = id);
        public void SetAutoApply(bool enabled) => UpdateProfiles(config => config.AutoApply = enabled);

        private void UpdateProfiles(Action<ProfileConfiguration> update)
        {
            lock (_profilesGate)
            {
                var next = _profiles.Clone(); update(next); next.Validate();
                // Persist before publishing so a failed save doesn't silently change the selection.
                if (_initialized) this.SaveCommonSettings("Profiles", next);
                _profiles = next;
            }
        }
        public Task<CommandResult> ApplySelectedProfileAsync()
        {
            var selected = _profiles.Selected;
            return selected == null ? Task.FromResult(new CommandResult(CommandOutcome.Failed, "Select a profile first."))
                : _wheelClient.ApplySettingsAsync(selected.Settings, selected.Name);
        }
        public Task<CommandResult> CycleProfileAsync(int direction)
        {
            UpdateProfiles(config => config.Cycle(direction));
            return ApplySelectedProfileAsync();
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            // Only context selection happens on the game thread. USB work stays on the worker.
            if (DateTime.UtcNow < _nextAutoCheck) return;
            _nextAutoCheck = DateTime.UtcNow.AddSeconds(1);
            _currentGame = data.GameName ?? "";
            _currentCar = data.GameRunning ? data.NewData?.CarModel ?? "" : "";
            if (!_wheelClient.IsConnected) return;
            WheelProfile? profile;
            lock (_autoGate) profile = _autoSelector.Select(_profiles, _currentGame, _currentCar,
                data.GameRunning && data.NewData != null, data.GameReplay, data.NewData?.SpeedKmh ?? double.NaN);
            if (profile != null) _ = ApplyAutomaticProfileAsync(profile);
        }
        private async Task ApplyAutomaticProfileAsync(WheelProfile profile)
        {
            try
            {
                var result = await _wheelClient.ApplySettingsAsync(profile.Settings, profile.Name).ConfigureAwait(false);
                if (result.Outcome == CommandOutcome.Busy) lock (_autoGate) _autoSelector.RetryBusyRequest();
            }
            catch (Exception ex) { SimHub.Logging.Current.Error("[FFBeast Plugin] Automatic profile failed: " + ex); }
        }
        public void End(PluginManager pluginManager)
        {
            _wheelClient.Dispose();
            _initialized = false;
        }
    }
}
