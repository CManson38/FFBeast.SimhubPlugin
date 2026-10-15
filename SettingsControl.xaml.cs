using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FFBeast.SimHubPlugin
{
    public partial class SettingsControl : UserControl
    {
        public FFBeastPlugin? Plugin { get; }
        private readonly DispatcherTimer _updateTimer;
        private bool _busy;
        private bool _editorLoaded;
        private bool _updatingProfiles;
        private string _profileSignature = "";

        public SettingsControl()
        {
            InitializeComponent();
            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _updateTimer.Tick += (sender, e) => UpdateStatus();
            Loaded += (sender, e) => { if (Plugin != null) { RefreshProfiles(); UpdateStatus(); _updateTimer.Start(); } };
            Unloaded += (sender, e) => _updateTimer.Stop();
        }
        public SettingsControl(FFBeastPlugin plugin) : this() { Plugin = plugin; }

        private void UpdateStatus()
        {
            var status = Plugin?.Status;
            StatusText.Text = status?.State.ToString() ?? "Not initialized";
            StatusText.Foreground = status?.State == WheelState.Connected ? Brushes.Green : Brushes.DarkOrange;
            DeviceText.Text = status?.DeviceName ?? "Not connected";
            ConnectionMessageText.Text = status?.Message ?? "";
            var telemetry = Plugin?.Telemetry;
            TelemetryText.Text = telemetry == null ? "Wheel telemetry unavailable." :
                $"Firmware {telemetry.FirmwareVersion}   Position {telemetry.Position * 100:F1}%   Torque {telemetry.Torque * 100:F1}%";
            var settings = Plugin?.WheelSettings;
            CurrentSettingsText.Text = settings == null ? Plugin?.SettingsMessage ?? "" :
                $"Current: {settings.Strength}% strength, {settings.RotationRange}° rotation, FFB {(settings.ForceEnabled ? "enabled" : "disabled")}";
            ActiveProfileText.Text = "Active profile: " + (string.IsNullOrEmpty(Plugin?.ActiveProfileName) ? "Custom / none" : Plugin!.ActiveProfileName);
            if (!_editorLoaded && settings != null) LoadEditor(settings);
            bool available = Plugin != null && status?.State != WheelState.Stopped;
            ReconnectButton.IsEnabled = !_busy && available;
            WheelCommands.IsEnabled = !_busy && available && status?.State != WheelState.Restarting;
            ProfileControls.IsEnabled = !_busy && available;
            if (Plugin != null)
            {
                var config = Plugin.Profiles;
                string signature = config.SelectedProfileId + "|" + config.AutoApply + "|" + string.Join("|", config.Profiles.Select(p => p.Id + p.Name));
                if (signature != _profileSignature) RefreshProfiles();
            }
        }
        private void LoadEditor(WheelSettings settings)
        {
            StrengthInput.Text = settings.Strength.ToString(CultureInfo.InvariantCulture);
            RotationInput.Text = settings.RotationRange.ToString(CultureInfo.InvariantCulture);
            DampingInput.Text = settings.Damping.ToString(CultureInfo.InvariantCulture);
            SpringInput.Text = settings.Spring.ToString(CultureInfo.InvariantCulture);
            SoftStopStrengthInput.Text = settings.SoftStopStrength.ToString(CultureInfo.InvariantCulture);
            SoftStopRangeInput.Text = settings.SoftStopRange.ToString(CultureInfo.InvariantCulture);
            SoftStopDampingInput.Text = settings.SoftStopDamping.ToString(CultureInfo.InvariantCulture);
            ForceEnabledInput.IsChecked = settings.ForceEnabled;
            _editorLoaded = true;
        }
        private static int Number(TextBox input, string name)
        {
            if (!int.TryParse(input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                throw new ArgumentException(name + " must be a whole number.");
            return value;
        }
        private WheelSettings ReadEditor()
        {
            var settings = new WheelSettings
            {
                Strength = Number(StrengthInput, "Strength"), RotationRange = Number(RotationInput, "Rotation range"),
                Damping = Number(DampingInput, "Damping"), Spring = Number(SpringInput, "Spring"),
                SoftStopStrength = Number(SoftStopStrengthInput, "Soft-stop strength"),
                SoftStopRange = Number(SoftStopRangeInput, "Soft-stop range"),
                SoftStopDamping = Number(SoftStopDampingInput, "Soft-stop damping"), ForceEnabled = ForceEnabledInput.IsChecked == true
            };
            settings.Validate(); return settings;
        }
        private async Task ExecuteAsync(Func<Task<CommandResult>> action, bool reload = false)
        {
            if (_busy) return;
            _busy = true; ResultText.Text = "Working..."; UpdateStatus();
            try
            {
                var result = await action();
                ShowResult(result.Message, result.Outcome == CommandOutcome.Sent);
                if (reload && result.Outcome == CommandOutcome.Sent && Plugin?.WheelSettings != null) LoadEditor(Plugin.WheelSettings);
            }
            catch (Exception ex)
            {
                ShowResult(ex.Message, false);
                SimHub.Logging.Current.Error("[FFBeast Plugin] Settings command failed: " + ex);
            }
            finally { _busy = false; UpdateStatus(); }
        }
        private void ShowResult(string message, bool success)
        { ResultText.Text = message; ResultText.Foreground = success ? Brushes.Green : Brushes.DarkOrange; }

        private async void ReconnectButton_Click(object sender, RoutedEventArgs e)
        { if (Plugin != null) await ExecuteAsync(Plugin.ReconnectAsync, true); }
        private async void TestRecenterButton_Click(object sender, RoutedEventArgs e)
        { if (Plugin != null) await ExecuteAsync(Plugin.ResetCenterAsync); }
        private async void TestRebootButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_busy && Plugin != null && MessageBox.Show("Reboot the wheelbase controller?", "Confirm Reboot",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                await ExecuteAsync(Plugin.RebootAsync);
        }
        private async void SaveAndReboot_Click(object sender, RoutedEventArgs e)
        {
            if (!_busy && Plugin != null && MessageBox.Show("Save the wheel's current settings permanently and reboot? Unapplied editor values will not be saved.",
                "Save & Reboot", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                await ExecuteAsync(Plugin.SaveAndRebootAsync);
        }
        private async void ReadSettings_Click(object sender, RoutedEventArgs e)
        { if (Plugin != null) await ExecuteAsync(Plugin.RefreshSettingsAsync, true); }
        private async void ApplySettings_Click(object sender, RoutedEventArgs e)
        { if (Plugin != null) await ExecuteAsync(() => Plugin.ApplySettingsAsync(ReadEditor()), true); }
        private async void AdjustStrength_Click(object sender, RoutedEventArgs e)
        { if (Plugin != null) await ExecuteAsync(() => Plugin.AdjustStrengthAsync(int.Parse((string)((Button)sender).Tag)), true); }
        private async void StrengthPreset_Click(object sender, RoutedEventArgs e)
        { if (Plugin != null) await ExecuteAsync(() => Plugin.SetSettingAsync(WheelSetting.Strength, int.Parse((string)((Button)sender).Tag)), true); }
        private async void RotationPreset_Click(object sender, RoutedEventArgs e)
        { if (Plugin != null) await ExecuteAsync(() => Plugin.SetSettingAsync(WheelSetting.RotationRange, int.Parse((string)((Button)sender).Tag)), true); }
        private async void ReducedStrength_Click(object sender, RoutedEventArgs e)
        { if (Plugin != null) await ExecuteAsync(Plugin.ToggleReducedStrengthAsync, true); }
        private async void ToggleForce_Click(object sender, RoutedEventArgs e)
        { if (Plugin != null) await ExecuteAsync(Plugin.ToggleForceAsync, true); }

        private void RefreshProfiles()
        {
            if (Plugin == null) return;
            var config = Plugin.Profiles;
            string? previousId = (ProfileCombo.SelectedItem as WheelProfile)?.Id;
            _updatingProfiles = true;
            try
            {
                ProfileCombo.ItemsSource = config.Profiles;
                ProfileCombo.SelectedItem = config.Profiles.FirstOrDefault(p => p.Id == config.SelectedProfileId);
                if (config.Selected != null && previousId != config.SelectedProfileId)
                {
                    var selected = config.Selected;
                    ProfileNameInput.Text = selected.Name; ProfileGameInput.Text = selected.Game; ProfileCarInput.Text = selected.Car;
                    ProfileSummaryText.Text = $"{selected.Settings.Strength}% strength, {selected.Settings.RotationRange}° rotation. Values loaded into the Wheel settings editor.";
                    LoadEditor(selected.Settings);
                }
                AutoApplyInput.IsChecked = config.AutoApply;
                ActionsText.Text = string.Join(Environment.NewLine, Plugin.ActionNames);
                _profileSignature = config.SelectedProfileId + "|" + config.AutoApply + "|" + string.Join("|", config.Profiles.Select(p => p.Id + p.Name));
            }
            finally { _updatingProfiles = false; }
        }
        private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingProfiles || Plugin == null || !(ProfileCombo.SelectedItem is WheelProfile profile)) return;
            try
            {
                Plugin.SelectProfile(profile.Id);
                ProfileNameInput.Text = profile.Name; ProfileGameInput.Text = profile.Game; ProfileCarInput.Text = profile.Car;
                ProfileSummaryText.Text = $"{profile.Settings.Strength}% strength, {profile.Settings.RotationRange}° rotation. Values loaded into the Wheel settings editor.";
                LoadEditor(profile.Settings);
            }
            catch (Exception ex) { ShowResult(ex.Message, false); RefreshProfiles(); }
        }
        private void StoreProfile(bool replace)
        {
            if (Plugin == null) return;
            try
            {
                var selected = ProfileCombo.SelectedItem as WheelProfile;
                if (replace && selected == null) throw new ArgumentException("Select a profile to update.");
                Plugin.SaveProfile(new WheelProfile
                {
                    Id = selected?.Id ?? Guid.NewGuid().ToString("N"), Name = ProfileNameInput.Text,
                    Game = ProfileGameInput.Text, Car = ProfileCarInput.Text, Settings = ReadEditor()
                }, replace);
                RefreshProfiles(); ShowResult("Profile saved in SimHub. Wheel settings were not changed.", true);
            }
            catch (Exception ex) { ShowResult(ex.Message, false); }
        }
        private void SaveNewProfile_Click(object sender, RoutedEventArgs e) => StoreProfile(false);
        private void UpdateProfile_Click(object sender, RoutedEventArgs e) => StoreProfile(true);
        private void DeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            if (Plugin == null || !(ProfileCombo.SelectedItem is WheelProfile selected)) return;
            try { Plugin.DeleteProfile(selected.Id); RefreshProfiles(); ShowResult("Profile deleted.", true); }
            catch (Exception ex) { ShowResult(ex.Message, false); }
        }
        private async void ApplyProfile_Click(object sender, RoutedEventArgs e)
        { if (Plugin != null) await ExecuteAsync(Plugin.ApplySelectedProfileAsync, true); }
        private async void CycleProfile_Click(object sender, RoutedEventArgs e)
        { if (Plugin != null) await ExecuteAsync(() => Plugin.CycleProfileAsync(int.Parse((string)((Button)sender).Tag)), true); }
        private void AutoApply_Changed(object sender, RoutedEventArgs e)
        {
            if (_updatingProfiles || Plugin == null) return;
            try { Plugin.SetAutoApply(AutoApplyInput.IsChecked == true); RefreshProfiles(); }
            catch (Exception ex) { ShowResult(ex.Message, false); RefreshProfiles(); }
        }
        private void UseCurrentContext_Click(object sender, RoutedEventArgs e)
        {
            if (Plugin == null) return;
            ProfileGameInput.Text = Plugin.CurrentGame; ProfileCarInput.Text = Plugin.CurrentCar;
            ShowResult(string.IsNullOrWhiteSpace(Plugin.CurrentCar) ? "No current car is available yet." : "Game and car copied. Save the profile to store this match.", true);
        }
    }
}
