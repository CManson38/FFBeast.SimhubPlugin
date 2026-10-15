using HidSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace FFBeast.SimHubPlugin
{
    public sealed class HidWheelTransport : IWheelTransport
    {
        public IEnumerable<IWheelDevice> Enumerate() => DeviceList.Local
            .GetHidDevices(1115, 22999).Select(device => new Device(device));

        private sealed class Device : IWheelDevice
        {
            private readonly HidDevice _device;
            public Device(HidDevice device) { _device = device; }
            public string DevicePath => _device.DevicePath;
            public string DeviceName => _device.GetProductName();
            public int InputReportLength => _device.GetMaxInputReportLength();
            public int OutputReportLength => _device.GetMaxOutputReportLength();
            public IWheelConnection Open()
            {
                var stream = _device.Open();
                try
                {
                    stream.ReadTimeout = 500;
                    stream.WriteTimeout = 1500;
                    return new Connection(stream);
                }
                catch { stream.Dispose(); throw; }
            }
        }
        private sealed class Connection : IWheelConnection
        {
            private readonly HidStream _stream;
            private readonly Thread _reader;
            private readonly ManualResetEventSlim _ready = new ManualResetEventSlim();
            private volatile WheelTelemetry? _latest;
            private volatile Exception? _readError;
            private long _lastRead;
            private int _stopping;

            public Connection(HidStream stream)
            {
                _stream = stream;
                _reader = new Thread(ReadLoop) { IsBackground = true, Name = "FFBeast status reader" };
                _reader.Start();
            }
            // One continuous reader drains the input stream so dashboards receive current
            // telemetry rather than old packets accumulated between polling intervals.
            private void ReadLoop()
            {
                var buffer = new byte[65];
                try
                {
                    while (Volatile.Read(ref _stopping) == 0)
                    {
                        int count = _stream.Read(buffer, 0, buffer.Length);
                        _latest = WheelProtocol.ParseStatus(buffer, count);
                        Interlocked.Exchange(ref _lastRead, Stopwatch.GetTimestamp());
                        _ready.Set();
                    }
                }
                catch (Exception ex) { _readError = ex; }
                finally { _ready.Set(); }
            }
            public void Write(byte[] report) => _stream.Write(report, 0, report.Length);
            public WheelTelemetry ReadStatus()
            {
                if (!_ready.Wait(500)) throw new TimeoutException("No wheel status received.");
                if (_readError != null) throw new IOException("Wheel status stream stopped.", _readError);
                var sample = _latest;
                if (sample == null || (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastRead)) / (double)Stopwatch.Frequency > 0.5)
                    throw new TimeoutException("Wheel status is stale.");
                return sample;
            }
            public WheelSettings ReadSettings()
            {
                var effects = new byte[65]; effects[0] = 0x22;
                var hardware = new byte[65]; hardware[0] = 0x21;
                _stream.GetFeature(effects); _stream.GetFeature(hardware);
                return WheelProtocol.ParseSettings(effects, hardware);
            }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
                try { _stream.Dispose(); }
                finally
                {
                    // Closing the stream cancels its pending read. Don't dispose the signal
                    // until the worker has exited, even if a driver is slow to cancel.
                    if (_reader.Join(1000)) _ready.Dispose();
                }
            }
        }
    }
}
