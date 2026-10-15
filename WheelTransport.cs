using System;
using System.Collections.Generic;

namespace FFBeast.SimHubPlugin
{
    public interface IWheelTransport { IEnumerable<IWheelDevice> Enumerate(); }
    public interface IWheelDevice
    {
        string DevicePath { get; }
        string DeviceName { get; }
        int InputReportLength { get; }
        int OutputReportLength { get; }
        IWheelConnection Open();
    }
    public interface IWheelConnection : IDisposable
    {
        void Write(byte[] report);
        // Throw unless a valid status report arrives within a bounded timeout.
        WheelTelemetry ReadStatus();
        WheelSettings ReadSettings();
    }
}
