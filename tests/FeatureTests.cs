using FFBeast.SimHubPlugin;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
    private static IEnumerable<(string Name, Func<Task> Run)> FeatureTests() => new (string, Func<Task>)[]
    {
        ("All settings use the reference field report layout", SettingFrames),
        ("Settings reject invalid fields and values", SettingLimits),
        ("Status decoder reads signed normalized position and torque", StatusDecode),
        ("Status decoder rejects truncated and wrong reports", BadStatusDecode),
        ("Feature decoder reads packed settings offsets", FeatureDecode),
        ("Feature decoder rejects malformed settings", BadFeatureDecode),
        ("Settings write is verified and cached", SetAndVerify),
        ("Strength increase and decrease clamp at the limits", StrengthClamping),
        ("Setting adjustments clamp by their protocol width", SettingClamping),
        ("Reduced strength restores the previous strength", ReducedRestore),
        ("Reduced strength never raises a lower strength", ReducedLowStrength),
        ("External strength changes cancel the restore value", ReducedExternalChange),
        ("Manual strength changes cancel reduced mode", ReducedManualChange),
        ("Force toggle reads actual wheel state", ForceToggle),
        ("Applying a profile disables force before other changes", DisableFirst),
        ("Applying a profile enables force after other changes", EnableLast),
        ("Invalid profiles cannot partially write", InvalidProfile),
        ("Profile input is captured before asynchronous execution", ProfileSnapshot),
        ("Partial settings failure stops without resending", PartialSettingsFailure),
        ("Unconfirmed readback never retries settings writes", ReadbackMismatch),
        ("Failed readback clears cached settings", ReadbackFailure),
        ("No-op settings do not write", NoOpSettings),
        ("Settings refresh is read only", ReadOnlyRefresh),
        ("Unsupported feature reads do not break telemetry or recenter", UnsupportedFeatures),
        ("Cached settings are isolated from callers", CachedIsolation),
        ("Telemetry is published and cleared on disconnect", TelemetryCache),
        ("Save and reboot uses explicit persistence command", SaveFrame),
        ("Interrupted save and reboot is unconfirmed", SaveTimeout),
        ("Profile match prefers car-specific rules", ProfileMatching),
        ("Profiles reject incomplete mappings and duplicate IDs", InvalidProfileConfiguration),
        ("Profile cycling wraps both ways", ProfileCycling),
        ("Profile snapshots do not share mutable settings", ProfileClone),
        ("Automatic selection is disabled by default", AutoOff),
        ("Automatic selection waits for stationary valid telemetry", AutoStationary),
        ("Automatic selection runs once per game and car", AutoOnce),
        ("Busy automatic selection can be deferred", AutoBusy),
        ("Settings and profiles never send direct-force or implicit-save commands", NoForceOverrides)
    };

    private static void Throws(Action action)
    {
        try { action(); } catch (ArgumentException) { return; } catch (IOException) { return; }
        throw new Exception("Expected invalid input to be rejected.");
    }
    private static void Put16(byte[] bytes, int offset, int value)
    { bytes[offset] = (byte)value; bytes[offset + 1] = (byte)(value >> 8); }
    private static Task SettingFrames()
    {
        foreach (WheelSetting field in Enum.GetValues(typeof(WheelSetting)))
        {
            int value = field == WheelSetting.RotationRange ? 1080 : field == WheelSetting.ForceEnabled ? 1 : 75;
            var report = WheelProtocol.SettingReport(field, value);
            Equal(65, report.Length); Equal((byte)0xA3, report[0]); Equal((byte)0x14, report[1]);
            Equal((byte)field, report[2]); Equal((byte)0, report[3]); Equal((byte)value, report[4]);
            Equal((byte)(value >> 8), report[5]); Check(report.Skip(6).All(b => b == 0), "Nonzero padding.");
        }
        return Done();
    }
    private static Task SettingLimits()
    {
        Throws(() => WheelProtocol.SettingReport(WheelSetting.Strength, -1));
        Throws(() => WheelProtocol.SettingReport(WheelSetting.Strength, 101));
        Throws(() => WheelProtocol.SettingReport(WheelSetting.RotationRange, 0));
        Throws(() => WheelProtocol.SettingReport(WheelSetting.RotationRange, 65536));
        Throws(() => WheelProtocol.SettingReport(WheelSetting.SoftStopRange, 256));
        Throws(() => WheelProtocol.SettingReport(WheelSetting.ForceEnabled, 2));
        Throws(() => WheelProtocol.SettingReport((WheelSetting)16, 0));
        WheelProtocol.SettingReport(WheelSetting.RotationRange, 65535);
        WheelProtocol.SettingReport(WheelSetting.SoftStopRange, 255);
        return Done();
    }
    private static Task StatusDecode()
    {
        var report = new byte[65]; report[0] = 0xA3; report[1] = 2; report[2] = 26; report[3] = 1; report[4] = 1;
        report[5] = 1; Put16(report, 6, -10000); Put16(report, 8, 5000);
        var state = WheelProtocol.ParseStatus(report, 65);
        Equal(-1.0, state.Position); Equal(0.5, state.Torque); Equal("26.1.1", state.FirmwareVersion);
        Equal((byte)2, state.FirmwareReleaseType); Check(state.SampledUtc.Kind == DateTimeKind.Utc, "Timestamp must be UTC.");
        return Done();
    }
    private static Task BadStatusDecode()
    {
        Throws(() => WheelProtocol.ParseStatus(new byte[64], 64));
        Throws(() => WheelProtocol.ParseStatus(new byte[65], 65));
        Throws(() => WheelProtocol.ParseStatus(new byte[1], 65)); return Done();
    }
    private static (byte[] Effects, byte[] Hardware) Features()
    {
        var effects = new byte[65]; effects[0] = 0x22;
        Put16(effects, 1, 1080); Put16(effects, 3, 23); Put16(effects, 5, 45);
        effects[7] = 67; effects[8] = 12; effects[9] = 130; effects[10] = 89;
        var hardware = new byte[65]; hardware[0] = 0x21; hardware[6] = 1;
        return (effects, hardware);
    }
    private static Task FeatureDecode()
    {
        var reports = Features(); var settings = WheelProtocol.ParseSettings(reports.Effects, reports.Hardware);
        Equal(1080, settings.RotationRange); Equal(23, settings.Damping); Equal(45, settings.SoftStopDamping);
        Equal(67, settings.Strength); Equal(12, settings.Spring); Equal(130, settings.SoftStopRange);
        Equal(89, settings.SoftStopStrength); Check(settings.ForceEnabled, "Wrong hardware force flag offset."); return Done();
    }
    private static Task BadFeatureDecode()
    {
        var reports = Features(); reports.Effects[7] = 101;
        Throws(() => WheelProtocol.ParseSettings(reports.Effects, reports.Hardware));
        reports = Features(); reports.Hardware[6] = 2;
        Throws(() => WheelProtocol.ParseSettings(reports.Effects, reports.Hardware));
        Throws(() => WheelProtocol.ParseSettings(new byte[1], reports.Hardware)); return Done();
    }
    private static async Task SetAndVerify()
    {
        using var f = new Fixture();
        Equal(CommandOutcome.Sent, (await f.Client.SetSettingAsync(WheelSetting.RotationRange, 540)).Outcome);
        Equal(540, f.Client.Settings!.RotationRange); var report = f.Device.Connections.Single().Writes.Single();
        Equal((byte)0x1C, report[4]); Equal((byte)2, report[5]);
    }
    private static async Task StrengthClamping()
    {
        using var f = new Fixture(); await f.Client.AdjustStrengthAsync(int.MaxValue); Equal(100, f.Client.Settings!.Strength);
        await f.Client.AdjustStrengthAsync(int.MinValue); Equal(0, f.Client.Settings!.Strength);
    }
    private static async Task SettingClamping()
    {
        using var f = new Fixture(); await f.Client.AdjustSettingAsync(WheelSetting.SoftStopRange, int.MaxValue);
        Equal(255, f.Client.Settings!.SoftStopRange);
        await f.Client.AdjustSettingAsync(WheelSetting.Damping, -500); Equal(0, f.Client.Settings!.Damping);
        Equal(CommandOutcome.Failed, (await f.Client.SetSettingAsync((WheelSetting)99, 1)).Outcome);
    }
    private static async Task ReducedRestore()
    {
        using var f = new Fixture(); await f.Client.SetSettingAsync(WheelSetting.Strength, 80);
        await f.Client.ToggleReducedStrengthAsync(); Equal(25, f.Client.Settings!.Strength); Check(f.Client.ReducedStrengthActive, "Reduced mode not active.");
        await f.Client.ToggleReducedStrengthAsync(); Equal(80, f.Client.Settings!.Strength); Check(!f.Client.ReducedStrengthActive, "Restore mode not cleared.");
    }
    private static async Task ReducedLowStrength()
    {
        using var f = new Fixture(); await f.Client.SetSettingAsync(WheelSetting.Strength, 10);
        await f.Client.ToggleReducedStrengthAsync(); Equal(10, f.Client.Settings!.Strength);
    }
    private static async Task ReducedExternalChange()
    {
        using var f = new Fixture(); await f.Client.ToggleReducedStrengthAsync();
        f.Device.Connections.Single().DeviceSettings.Strength = 40;
        await f.Client.RefreshSettingsAsync(); Check(!f.Client.ReducedStrengthActive, "External change retained obsolete restore value.");
    }
    private static async Task ReducedManualChange()
    {
        using var f = new Fixture(); await f.Client.ToggleReducedStrengthAsync();
        await f.Client.AdjustStrengthAsync(5); Equal(30, f.Client.Settings!.Strength); Check(!f.Client.ReducedStrengthActive, "Manual tuning retained restore value.");
    }
    private static async Task ForceToggle()
    {
        using var f = new Fixture(); await f.Client.ToggleForceAsync(); Check(!f.Client.Settings!.ForceEnabled, "Did not disable.");
        await f.Client.ToggleForceAsync(); Check(f.Client.Settings!.ForceEnabled, "Did not enable.");
        Check(f.Device.Connections.Single().Writes.All(r => r[2] == 11 && r[5] == 0), "Wrong force field/type.");
    }
    private static async Task DisableFirst()
    {
        using var f = new Fixture(); var profile = new WheelSettings { Strength = 60, RotationRange = 540, ForceEnabled = false };
        Equal(CommandOutcome.Sent, (await f.Client.ApplySettingsAsync(profile, "GT")).Outcome);
        Equal((byte)11, f.Device.Connections.Single().Writes.First()[2]); Equal("GT", f.Client.ActiveProfileName);
    }
    private static async Task EnableLast()
    {
        using var f = new Fixture(); f.Device.Configure = c => c.DeviceSettings.ForceEnabled = false;
        var profile = new WheelSettings { Strength = 60, RotationRange = 540, ForceEnabled = true };
        await f.Client.ApplySettingsAsync(profile); Equal((byte)11, f.Device.Connections.Single().Writes.Last()[2]);
    }
    private static async Task InvalidProfile()
    {
        using var f = new Fixture();
        Equal(CommandOutcome.Failed, (await f.Client.ApplySettingsAsync(new WheelSettings { Spring = 101 })).Outcome);
        Equal(0, f.Device.OpenCount);
    }
    private static async Task ProfileSnapshot()
    {
        using var f = new Fixture(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        f.Device.Configure = c => c.OnWrite = () => { entered.Set(); Check(release.Wait(5000), "Write block timeout."); };
        var profile = new WheelSettings { Strength = 70 };
        var apply = f.Client.ApplySettingsAsync(profile);
        try { Check(entered.Wait(2000), "Write not started."); profile.Strength = 90; }
        finally { release.Set(); }
        Equal(CommandOutcome.Sent, (await apply).Outcome); Equal(70, f.Client.Settings!.Strength);
    }
    private static async Task PartialSettingsFailure()
    {
        using var f = new Fixture(); f.Device.Configure = c => { c.WriteError = new IOException("lost"); c.WriteErrorOn = 2; };
        var desired = new WheelSettings { Strength = 65, RotationRange = 540, Spring = 15 };
        Equal(CommandOutcome.Unconfirmed, (await f.Client.ApplySettingsAsync(desired, "partial")).Outcome);
        Equal(2, f.Device.Connections.Single().Writes.Count); Check(f.Client.Settings == null && !f.Client.IsConnected, "Partial update marked valid.");
        Equal("", f.Client.ActiveProfileName);
    }
    private static async Task ReadbackMismatch()
    {
        using var f = new Fixture(); f.Device.Configure = c => c.IgnoreSettingWrites = true;
        Equal(CommandOutcome.Unconfirmed, (await f.Client.SetSettingAsync(WheelSetting.Strength, 65)).Outcome);
        Equal(1, f.Device.Connections.Single().Writes.Count); Check(f.Client.Settings == null, "Unverified settings cached.");
    }
    private static async Task ReadbackFailure()
    {
        using var f = new Fixture(); f.Device.Configure = c => c.OnWrite = () => c.SettingsReadError = new IOException("feature failed");
        Equal(CommandOutcome.Unconfirmed, (await f.Client.SetSettingAsync(WheelSetting.Strength, 65)).Outcome);
        Check(f.Client.Settings == null, "Readback failure retained settings.");
    }
    private static async Task NoOpSettings()
    {
        using var f = new Fixture();
        Equal(CommandOutcome.Sent, (await f.Client.ApplySettingsAsync(new WheelSettings())).Outcome);
        Equal(0, f.Device.Connections.Single().Writes.Count);
    }
    private static async Task ReadOnlyRefresh()
    {
        using var f = new Fixture(); await f.Client.RefreshSettingsAsync();
        Equal(0, f.Device.Connections.Single().Writes.Count); Equal(50, f.Client.Settings!.Strength);
    }
    private static async Task UnsupportedFeatures()
    {
        using var f = new Fixture(); f.Device.Configure = c => c.SettingsReadError = new IOException("unsupported");
        f.Client.PollConnection(); Check(f.Client.IsConnected && f.Client.Telemetry != null, "Feature failure broke health.");
        Check(f.Client.Settings == null, "Unavailable settings reported valid.");
        Equal(CommandOutcome.Sent, (await f.Client.ResetCenterAsync()).Outcome);
    }
    private static async Task CachedIsolation()
    {
        using var f = new Fixture(); await f.Client.RefreshSettingsAsync(); f.Client.Settings!.Strength = 100;
        Equal(50, f.Client.Settings!.Strength);
    }
    private static Task TelemetryCache()
    {
        using var f = new Fixture(); f.Client.PollConnection(); Equal(-0.5, f.Client.Telemetry!.Torque);
        f.Transport.Devices.Clear(); f.Client.PollConnection(); Check(f.Client.Telemetry == null, "Disconnected sample retained."); return Done();
    }
    private static async Task SaveFrame()
    {
        using var f = new Fixture(); Equal(CommandOutcome.Sent, (await f.Client.SaveAndRebootAsync()).Outcome);
        Frame(f.Device.Connections.Single(), 0x02); Equal(WheelState.Restarting, f.Client.Status.State);
    }
    private static async Task SaveTimeout()
    {
        using var f = new Fixture(); f.Device.Configure = c => c.WriteError = new TimeoutException();
        var result = await f.Client.SaveAndRebootAsync(); Equal(CommandOutcome.Unconfirmed, result.Outcome);
        Check(result.Message.Contains("Save & Reboot"), "Save outcome must identify persistence command.");
        Equal(1, f.Device.Connections.Single().Writes.Count);
    }
    private static ProfileConfiguration Config()
    {
        var config = new ProfileConfiguration();
        config.Profiles.Add(new WheelProfile { Id = "manual", Name = "Manual" });
        config.Profiles.Add(new WheelProfile { Id = "game", Name = "Game", Game = "IRacing" });
        config.Profiles.Add(new WheelProfile { Id = "car", Name = "GT", Game = "IRacing", Car = "GT3" });
        return config;
    }
    private static Task ProfileMatching()
    {
        var config = Config(); Equal("car", config.Match("iracing", "gt3")!.Id);
        Equal("game", config.Match("IRacing", "Formula")!.Id); Check(config.Match("Other", "GT3") == null, "Manual profile automatically matched."); return Done();
    }
    private static Task InvalidProfileConfiguration()
    {
        Throws(() => new WheelProfile { Name = "invalid", Car = "GT3" }.Validate());
        var config = Config(); config.Profiles.Add(config.Profiles[0].Clone()); Throws(config.Validate);
        Throws(() => new WheelProfile { Name = "" }.Validate()); return Done();
    }
    private static Task ProfileCycling()
    {
        var config = Config(); Equal("manual", config.Cycle(1)!.Id); Equal("car", config.Cycle(-1)!.Id);
        Equal("manual", config.Cycle(1)!.Id); Equal("game", config.Cycle(1)!.Id); return Done();
    }
    private static Task ProfileClone()
    {
        var config = Config(); var copy = config.Clone(); copy.Profiles[0].Settings.Strength = 99;
        Equal(50, config.Profiles[0].Settings.Strength); return Done();
    }
    private static Task AutoOff()
    {
        var selector = new AutoProfileSelector(); Check(selector.Select(Config(), "IRacing", "GT3", true, false, 0) == null, "Auto apply enabled by default."); return Done();
    }
    private static Task AutoStationary()
    {
        var config = Config(); config.AutoApply = true; var selector = new AutoProfileSelector();
        Check(selector.Select(config, "IRacing", "GT3", true, false, 50) == null, "Applied while driving.");
        Check(selector.Select(config, "IRacing", "GT3", true, false, double.NaN) == null, "Applied with invalid speed.");
        Check(selector.Select(config, "IRacing", "GT3", true, true, 0) == null, "Applied in replay.");
        Equal("car", selector.Select(config, "IRacing", "GT3", true, false, 1)!.Id); return Done();
    }
    private static Task AutoOnce()
    {
        var config = Config(); config.AutoApply = true; var selector = new AutoProfileSelector();
        Check(selector.Select(config, "IRacing", "GT3", true, false, 0) != null, "First context not applied.");
        Check(selector.Select(config, "IRacing", "GT3", true, false, 0) == null, "Reapplied same context.");
        Equal("game", selector.Select(config, "IRacing", "Other", true, false, 0)!.Id);
        selector.Select(config, "", "", false, false, 0);
        Check(selector.Select(config, "IRacing", "Other", true, false, 0) != null, "New session context not reset."); return Done();
    }
    private static Task AutoBusy()
    {
        var config = Config(); config.AutoApply = true; var selector = new AutoProfileSelector();
        selector.Select(config, "IRacing", "GT3", true, false, 0); selector.RetryBusyRequest();
        Check(selector.Select(config, "IRacing", "GT3", true, false, 0) != null, "Busy request cannot retry."); return Done();
    }
    private static async Task NoForceOverrides()
    {
        using var f = new Fixture(); await f.Client.ApplySettingsAsync(new WheelSettings { Strength = 55, Spring = 10, Damping = 15 });
        await f.Client.ToggleForceAsync(); await f.Client.ToggleReducedStrengthAsync();
        Check(f.Device.Connections.SelectMany(c => c.Writes).All(r => r[1] == 0x14), "Settings used a direct-force or persistence command.");
    }
}
