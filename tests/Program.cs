using FFBeast.SimHubPlugin;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
    private static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("Recenter sends the reference protocol frame", RecenterFrame),
            ("Reboot sends once, closes its handle and waits", RebootFrame),
            ("Timeout invalidates recenter handle without retrying", FailedRecenter),
            ("Reboot timeout is unconfirmed and never resent", RebootTimeout),
            ("Reboot disconnect is unconfirmed", RebootDisconnect),
            ("Unexpected reboot error is failed, not successful", UnexpectedRebootError),
            ("Disconnected recenter connects before writing", ConnectOnCommand),
            ("Disconnected reboot connects before writing", ConnectOnReboot),
            ("No device fails both commands without writing", MissingDevice),
            ("Only the vendor interface with exact report sizes is used", InterfaceSelection),
            ("Open failure falls through to another candidate", OpenFailure),
            ("Unresponsive candidate is disposed", BadCandidate),
            ("Unreadable product metadata does not abort enumeration", BadMetadata),
            ("Stale reused path is replaced before sending", StaleHandle),
            ("Idle polling detects unplug and reconnects after backoff", UnplugAndReplug),
            ("Enumeration failure is recoverable", EnumerationFailure),
            ("Reconnect releases the previous handle", ManualReconnect),
            ("Repeated commands are dropped while a write is in progress", ConcurrentCommands),
            ("Status and polling remain responsive during blocked writes", NonBlockingStatus),
            ("Shutdown waits for active I/O and prevents reopening", ShutdownDuringWrite),
            ("Disposal failures still clear connection state", DisposeFailure),
            ("Monitoring starts without game callbacks", IdleMonitoring),
            ("Repeated shutdown is safe", RepeatedDispose),
            ("Restart absent at first scan continues recovering", SlowRestart),
            ("Failed command gate is released for the next command", RecoverAfterFailure)
        }.Concat(FeatureTests()).ToArray();
        int failed = 0;
        foreach (var test in tests)
        {
            try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + test.Name + "\n" + ex); }
        }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed ({System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}).");
        return failed == 0 ? 0 : 1;
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual)
    { Check(Equals(expected, actual), $"Expected {expected}, got {actual}."); }
    private static void Frame(FakeConnection connection, byte command)
    {
        Equal(1, connection.Writes.Count);
        var bytes = connection.Writes.Single();
        Equal(65, bytes.Length); Equal((byte)0xA3, bytes[0]); Equal(command, bytes[1]);
        Check(bytes.Skip(2).All(b => b == 0), "Padding must be zero.");
    }
    private static Task Done() => Task.CompletedTask;

    private static async Task RecenterFrame()
    {
        using var f = new Fixture();
        Equal(CommandOutcome.Sent, (await f.Client.ResetCenterAsync()).Outcome);
        Frame(f.Device.Connections.Single(), 0x04); Check(f.Client.IsConnected, "Must remain connected.");
    }
    private static async Task RebootFrame()
    {
        using var f = new Fixture();
        Equal(CommandOutcome.Sent, (await f.Client.RebootAsync()).Outcome);
        var old = f.Device.Connections.Single(); Frame(old, 0x01);
        Check(old.Disposed, "Old handle retained after reboot."); Equal(WheelState.Restarting, f.Client.Status.State);
        Equal(CommandOutcome.Busy, (await f.Client.RebootAsync()).Outcome);
        Equal(CommandOutcome.Busy, (await f.Client.ResetCenterAsync()).Outcome);
        Equal(CommandOutcome.Busy, (await f.Client.ReconnectAsync()).Outcome);
        f.Now = TimeSpan.FromSeconds(4.999); f.Client.PollConnection(); Equal(1, f.Device.Connections.Count);
        f.Now = TimeSpan.FromSeconds(5); f.Client.PollConnection();
        Check(f.Client.IsConnected, "Must recover at cooldown boundary."); Equal(2, f.Device.Connections.Count);
        Equal(0, f.Device.Connections[1].Writes.Count);
    }
    private static async Task FailedRecenter()
    {
        using var f = new Fixture(); f.Client.PollConnection();
        var old = f.Device.Connections.Single(); old.WriteError = new TimeoutException("write timeout");
        Equal(CommandOutcome.Failed, (await f.Client.ResetCenterAsync()).Outcome);
        Check(old.Disposed && !f.Client.IsConnected, "Write failure retained connected handle.");
        Equal(1, old.Writes.Count); Equal(1, f.Device.Connections.Count);
    }
    private static async Task RebootTimeout()
    {
        using var f = new Fixture(); f.Device.Configure = c => c.WriteError = new TimeoutException();
        Equal(CommandOutcome.Unconfirmed, (await f.Client.RebootAsync()).Outcome);
        Check(!f.Client.IsConnected && f.Device.Connections.Single().Disposed, "Reboot must clear connection.");
        f.Now = TimeSpan.FromSeconds(5); f.Client.PollConnection();
        Equal(1, f.Device.Connections.Sum(c => c.Writes.Count));
        Equal(WheelState.Connected, f.Client.Status.State);
    }
    private static async Task RebootDisconnect()
    {
        using var f = new Fixture(); f.Device.Configure = c => c.WriteError = new IOException("disconnected");
        Equal(CommandOutcome.Unconfirmed, (await f.Client.RebootAsync()).Outcome);
        Equal(WheelState.Restarting, f.Client.Status.State); Equal(1, f.Device.Connections.Single().Writes.Count);
    }
    private static async Task UnexpectedRebootError()
    {
        using var f = new Fixture(); f.Device.Configure = c => c.WriteError = new InvalidOperationException();
        Equal(CommandOutcome.Failed, (await f.Client.RebootAsync()).Outcome);
        Check(f.Device.Connections.Single().Disposed, "Reboot must release handle on any error.");
    }
    private static async Task ConnectOnCommand()
    {
        using var f = new Fixture(); Equal(CommandOutcome.Sent, (await f.Client.ResetCenterAsync()).Outcome);
        Equal(1, f.Device.Connections.Count);
    }
    private static async Task ConnectOnReboot()
    {
        using var f = new Fixture(); Equal(CommandOutcome.Sent, (await f.Client.RebootAsync()).Outcome);
        Equal(1, f.Device.Connections.Count);
    }
    private static async Task MissingDevice()
    {
        using var f = new Fixture(); f.Transport.Devices.Clear();
        Equal(CommandOutcome.Failed, (await f.Client.ResetCenterAsync()).Outcome);
        Equal(CommandOutcome.Failed, (await f.Client.RebootAsync()).Outcome);
        Equal(WheelState.Disconnected, f.Client.Status.State); Equal(0, f.Device.Connections.Count);
    }
    private static Task InterfaceSelection()
    {
        using var f = new Fixture(); f.Transport.Devices.Clear();
        var joystick = new FakeDevice { DevicePath = "hid#vid_045b&pid_59d7&mi_01", OutputReportLength = 18, InputReportLength = 25 };
        var oversized = new FakeDevice { OutputReportLength = 66 };
        var shortInput = new FakeDevice { InputReportLength = 18 };
        var wrongInterface = new FakeDevice { DevicePath = "hid#vid_045b&pid_59d7&mi_01" };
        f.Device.DevicePath = "hid#VID_045B&PID_59D7&MI_00";
        f.Transport.Devices.AddRange(new[] { joystick, oversized, shortInput, wrongInterface, f.Device });
        f.Client.PollConnection(); Check(f.Client.IsConnected, "Valid vendor interface skipped.");
        Equal(0, joystick.OpenCount + oversized.OpenCount + shortInput.OpenCount + wrongInterface.OpenCount);
        Equal(1, f.Device.OpenCount); return Done();
    }
    private static Task OpenFailure()
    {
        using var f = new Fixture(); var bad = new FakeDevice { OpenError = new IOException("busy") };
        f.Transport.Devices.Insert(0, bad); f.Client.PollConnection();
        Equal(1, bad.OpenCount); Check(f.Client.IsConnected, "Did not try next candidate."); return Done();
    }
    private static Task BadCandidate()
    {
        using var f = new Fixture(); f.Device.Configure = c => c.ReadError = new TimeoutException();
        f.Client.PollConnection(); Check(!f.Client.IsConnected, "Unresponsive device marked connected.");
        Check(f.Device.Connections.Single().Disposed, "Failed candidate leaked."); return Done();
    }
    private static Task BadMetadata()
    {
        using var f = new Fixture(); var bad = new FakeDevice { NameError = new IOException("descriptor") };
        f.Transport.Devices.Insert(0, bad); f.Client.PollConnection();
        Check(f.Client.IsConnected, "One bad descriptor aborted whole scan."); Equal(0, bad.OpenCount); return Done();
    }
    private static async Task StaleHandle()
    {
        using var f = new Fixture(); f.Client.PollConnection();
        var old = f.Device.Connections.Single(); old.ReadError = new IOException("stale handle");
        Equal(CommandOutcome.Sent, (await f.Client.ResetCenterAsync()).Outcome);
        Check(old.Disposed, "Stale connection leaked."); Equal(0, old.Writes.Count);
        Frame(f.Device.Connections[1], 0x04);
    }
    private static Task UnplugAndReplug()
    {
        using var f = new Fixture(); f.Client.PollConnection(); var old = f.Device.Connections.Single();
        f.Transport.Devices.Clear(); f.Client.PollConnection();
        Check(old.Disposed && !f.Client.IsConnected, "Unplug not detected.");
        f.Transport.Devices.Add(f.Device); f.Now = TimeSpan.FromSeconds(4); f.Client.PollConnection();
        Equal(1, f.Device.Connections.Count);
        f.Now = TimeSpan.FromSeconds(5); f.Client.PollConnection(); Check(f.Client.IsConnected, "Replug did not recover."); return Done();
    }
    private static Task EnumerationFailure()
    {
        using var f = new Fixture(); f.Transport.Error = new IOException("enumeration failed"); f.Client.PollConnection();
        Equal(WheelState.Disconnected, f.Client.Status.State); f.Transport.Error = null;
        f.Now = TimeSpan.FromSeconds(5); f.Client.PollConnection(); Check(f.Client.IsConnected, "Failed to recover scan."); return Done();
    }
    private static async Task ManualReconnect()
    {
        using var f = new Fixture(); f.Client.PollConnection(); var old = f.Device.Connections.Single();
        Equal(CommandOutcome.Sent, (await f.Client.ReconnectAsync()).Outcome);
        Check(old.Disposed, "Reconnect retained old stream."); Equal(2, f.Device.Connections.Count);
    }
    private static async Task ConcurrentCommands()
    {
        using var f = new Fixture(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        f.Device.Configure = c => c.OnWrite = () => { entered.Set(); Check(release.Wait(5000), "Test write timed out."); };
        var first = f.Client.RebootAsync(); Check(entered.Wait(2000), "Write did not start.");
        try
        {
            Equal(CommandOutcome.Busy, (await f.Client.RebootAsync()).Outcome);
            Equal(CommandOutcome.Busy, (await f.Client.ResetCenterAsync()).Outcome);
            Equal(CommandOutcome.Busy, (await f.Client.ReconnectAsync()).Outcome);
        }
        finally { release.Set(); }
        Equal(CommandOutcome.Sent, (await first).Outcome); Equal(1, f.Device.Connections.Single().Writes.Count);
    }
    private static async Task NonBlockingStatus()
    {
        using var f = new Fixture(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        f.Device.Configure = c => c.OnWrite = () => { entered.Set(); Check(release.Wait(5000), "Test write timed out."); };
        var command = f.Client.ResetCenterAsync(); Check(entered.Wait(2000), "Write did not start.");
        try
        {
            var read = Task.Run(() => { var status = f.Client.Status; f.Client.PollConnection(); return status; });
            Check(await Task.WhenAny(read, Task.Delay(1000)) == read, "Status/poll blocked on USB.");
            Check(!f.Device.Connections.Single().Disposed, "Poll disposed active stream.");
        }
        finally { release.Set(); }
        await command;
    }
    private static async Task ShutdownDuringWrite()
    {
        using var f = new Fixture(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        f.Device.Configure = c => c.OnWrite = () => { entered.Set(); Check(release.Wait(5000), "Test write timed out."); };
        var command = f.Client.ResetCenterAsync(); Check(entered.Wait(2000), "Write did not start.");
        var shutdown = Task.Run(() => f.Client.Dispose());
        try { Check(!f.Device.Connections.Single().Disposed, "Shutdown disposed stream during I/O."); }
        finally { release.Set(); }
        await command; await shutdown; f.Client.PollConnection(); f.Client.StartMonitoring();
        Equal(CommandOutcome.Failed, (await f.Client.ResetCenterAsync()).Outcome);
        Equal(WheelState.Stopped, f.Client.Status.State); Equal(1, f.Device.Connections.Count);
    }
    private static async Task DisposeFailure()
    {
        using var f = new Fixture(); f.Client.PollConnection();
        var old = f.Device.Connections.Single(); old.DisposeError = new IOException("close"); old.WriteError = new IOException("lost");
        Equal(CommandOutcome.Failed, (await f.Client.ResetCenterAsync()).Outcome);
        Check(!f.Client.IsConnected, "Dispose exception retained connected state.");
        f.Now = TimeSpan.FromSeconds(5); f.Client.PollConnection(); Check(f.Client.IsConnected, "Dispose error prevented recovery.");
    }
    private static async Task IdleMonitoring()
    {
        using var f = new Fixture(); using var opened = new ManualResetEventSlim();
        f.Device.Configure = c => opened.Set(); f.Client.StartMonitoring();
        Check(await Task.Run(() => opened.Wait(2000)), "No idle connection attempt.");
        // Synchronize with callback before asserting the published state.
        await f.Client.ReconnectAsync(); Check(f.Client.IsConnected, "Idle monitor did not connect.");
    }
    private static async Task RepeatedDispose()
    {
        using var f = new Fixture(); f.Client.PollConnection(); f.Client.Dispose(); f.Client.Dispose();
        f.Client.StartMonitoring(); f.Client.PollConnection();
        Equal(CommandOutcome.Failed, (await f.Client.RebootAsync()).Outcome);
        Equal(WheelState.Stopped, f.Client.Status.State); Equal(1, f.Device.Connections.Single().DisposeCount);
    }
    private static async Task SlowRestart()
    {
        using var f = new Fixture(); await f.Client.RebootAsync(); f.Transport.Devices.Clear();
        f.Now = TimeSpan.FromSeconds(5); f.Client.PollConnection(); Equal(WheelState.Disconnected, f.Client.Status.State);
        f.Transport.Devices.Add(f.Device); f.Now = TimeSpan.FromSeconds(10); f.Client.PollConnection();
        Check(f.Client.IsConnected, "Slow restart did not recover."); Equal(1, f.Device.Connections.Sum(c => c.Writes.Count));
    }
    private static async Task RecoverAfterFailure()
    {
        using var f = new Fixture(); f.Client.PollConnection();
        f.Device.Connections.Single().WriteError = new IOException("lost"); await f.Client.ResetCenterAsync();
        Equal(CommandOutcome.Sent, (await f.Client.ResetCenterAsync()).Outcome); Equal(2, f.Device.Connections.Count);
    }

    private sealed class Fixture : IDisposable
    {
        public TimeSpan Now;
        public FakeDevice Device = new FakeDevice();
        public FakeTransport Transport = new FakeTransport();
        public FFBeastWheelApiClient Client;
        public Fixture()
        {
            Transport.Devices.Add(Device);
            Client = new FFBeastWheelApiClient(Transport, _ => { }, () => Now);
        }
        public void Dispose() => Client.Dispose();
    }
    private sealed class FakeTransport : IWheelTransport
    {
        public List<IWheelDevice> Devices = new List<IWheelDevice>();
        public Exception? Error;
        public IEnumerable<IWheelDevice> Enumerate()
        { if (Error != null) throw Error; return Devices.ToArray(); }
    }
    private sealed class FakeDevice : IWheelDevice
    {
        public string DevicePath { get; set; } = "hid#vid_045b&pid_59d7&mi_00";
        public string DeviceName { get { if (NameError != null) throw NameError; return "Test wheel"; } }
        public int InputReportLength { get; set; } = 65;
        public int OutputReportLength { get; set; } = 65;
        public Exception? OpenError, NameError;
        public Action<FakeConnection>? Configure;
        public int OpenCount;
        public List<FakeConnection> Connections = new List<FakeConnection>();
        public IWheelConnection Open()
        {
            OpenCount++; if (OpenError != null) throw OpenError;
            var connection = new FakeConnection(); Configure?.Invoke(connection); Connections.Add(connection); return connection;
        }
    }
    private sealed class FakeConnection : IWheelConnection
    {
        public List<byte[]> Writes = new List<byte[]>();
        public Exception? WriteError, ReadError, DisposeError;
        public Action? OnWrite;
        public bool Disposed;
        public int DisposeCount;
        public void Write(byte[] report)
        {
            Check(!Disposed, "Write after disposal."); Writes.Add((byte[])report.Clone()); OnWrite?.Invoke();
            Check(!Disposed, "Disposed during write.");
            if (WriteError != null && (WriteErrorOn == 0 || Writes.Count == WriteErrorOn)) throw WriteError;
            if (report[1] == 0x14 && !IgnoreSettingWrites)
            {
                var field = (WheelSetting)report[2];
                int value = report[4] | (report[5] << 8);
                DeviceSettings.Set(field, value);
            }
        }
        public WheelTelemetry ReadStatus()
        {
            Check(!Disposed, "Read after disposal."); if (ReadError != null) throw ReadError;
            return new WheelTelemetry(0.25, -0.5, "26.1.1");
        }
        public WheelSettings ReadSettings()
        {
            Check(!Disposed, "Read settings after disposal.");
            if (SettingsReadError != null) throw SettingsReadError;
            return DeviceSettings.Clone();
        }
        public WheelSettings DeviceSettings = new WheelSettings();
        public Exception? SettingsReadError;
        public bool IgnoreSettingWrites;
        public int WriteErrorOn;
        public void Dispose()
        { DisposeCount++; Disposed = true; if (DisposeError != null) throw DisposeError; }
    }
}
