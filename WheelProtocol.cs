using System;
using System.IO;

namespace FFBeast.SimHubPlugin
{
    public enum WheelSetting
    {
        Strength = 4, RotationRange = 5, SoftStopStrength = 6, SoftStopRange = 7,
        Damping = 8, SoftStopDamping = 9, ForceEnabled = 11, Spring = 43
    }

    public sealed class WheelSettings
    {
        public int Strength { get; set; } = 50;
        public int RotationRange { get; set; } = 900;
        public int Damping { get; set; }
        public int Spring { get; set; }
        public int SoftStopStrength { get; set; }
        public int SoftStopRange { get; set; }
        public int SoftStopDamping { get; set; }
        public bool ForceEnabled { get; set; } = true;

        public WheelSettings Clone() => (WheelSettings)MemberwiseClone();
        public int Get(WheelSetting field)
        {
            switch (field)
            {
                case WheelSetting.Strength: return Strength;
                case WheelSetting.RotationRange: return RotationRange;
                case WheelSetting.Damping: return Damping;
                case WheelSetting.Spring: return Spring;
                case WheelSetting.SoftStopStrength: return SoftStopStrength;
                case WheelSetting.SoftStopRange: return SoftStopRange;
                case WheelSetting.SoftStopDamping: return SoftStopDamping;
                case WheelSetting.ForceEnabled: return ForceEnabled ? 1 : 0;
                default: throw new ArgumentOutOfRangeException(nameof(field));
            }
        }
        public void Set(WheelSetting field, int value)
        {
            WheelProtocol.Validate(field, value);
            switch (field)
            {
                case WheelSetting.Strength: Strength = value; break;
                case WheelSetting.RotationRange: RotationRange = value; break;
                case WheelSetting.Damping: Damping = value; break;
                case WheelSetting.Spring: Spring = value; break;
                case WheelSetting.SoftStopStrength: SoftStopStrength = value; break;
                case WheelSetting.SoftStopRange: SoftStopRange = value; break;
                case WheelSetting.SoftStopDamping: SoftStopDamping = value; break;
                case WheelSetting.ForceEnabled: ForceEnabled = value != 0; break;
            }
        }
        public void Validate()
        {
            foreach (WheelSetting field in Enum.GetValues(typeof(WheelSetting))) WheelProtocol.Validate(field, Get(field));
        }
        public bool Matches(WheelSettings other)
        {
            foreach (WheelSetting field in Enum.GetValues(typeof(WheelSetting)))
                if (Get(field) != other.Get(field)) return false;
            return true;
        }
    }

    public sealed class WheelTelemetry
    {
        public double Position { get; }
        public double Torque { get; }
        public string FirmwareVersion { get; }
        public byte FirmwareReleaseType { get; }
        public DateTime SampledUtc { get; }
        public WheelTelemetry(double position, double torque, string firmwareVersion, byte firmwareReleaseType = 0)
        {
            Position = position; Torque = torque; FirmwareVersion = firmwareVersion;
            FirmwareReleaseType = firmwareReleaseType; SampledUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Only settings and controller commands are supported; no direct-force override reports.</summary>
    public static class WheelProtocol
    {
        public static void Validate(WheelSetting field, int value)
        {
            if (!Enum.IsDefined(typeof(WheelSetting), field)) throw new ArgumentOutOfRangeException(nameof(field));
            int min = field == WheelSetting.RotationRange ? 1 : 0;
            int max = field == WheelSetting.RotationRange ? ushort.MaxValue :
                field == WheelSetting.SoftStopRange ? byte.MaxValue : field == WheelSetting.ForceEnabled ? 1 : 100;
            if (value < min || value > max)
                throw new ArgumentOutOfRangeException(nameof(value), $"{field} must be between {min} and {max}.");
        }
        public static byte[] SettingReport(WheelSetting field, int value)
        {
            Validate(field, value);
            var report = new byte[65];
            report[0] = 0xA3; report[1] = 0x14; report[2] = (byte)field;
            // Byte 3 is the non-indexed field index (0); values start at byte 4.
            report[4] = (byte)value;
            if (field == WheelSetting.RotationRange || field == WheelSetting.Damping || field == WheelSetting.SoftStopDamping)
                report[5] = (byte)(value >> 8);
            return report;
        }
        public static WheelTelemetry ParseStatus(byte[] report, int count)
        {
            CheckReport(report, count, 0xA3);
            return new WheelTelemetry(Int16(report, 6) / 10000.0, Int16(report, 8) / 10000.0,
                $"{report[2]}.{report[3]}.{report[4]}", report[1]);
        }
        public static WheelSettings ParseSettings(byte[] effects, byte[] hardware)
        {
            CheckReport(effects, effects.Length, 0x22); CheckReport(hardware, hardware.Length, 0x21);
            if (hardware[6] > 1) throw new IOException("Invalid force-enable setting.");
            var settings = new WheelSettings
            {
                RotationRange = UInt16(effects, 1), Damping = UInt16(effects, 3),
                SoftStopDamping = UInt16(effects, 5), Strength = effects[7], Spring = effects[8],
                SoftStopRange = effects[9], SoftStopStrength = effects[10], ForceEnabled = hardware[6] == 1
            };
            try { settings.Validate(); }
            catch (ArgumentException ex) { throw new IOException("Wheel returned invalid settings.", ex); }
            return settings;
        }
        private static void CheckReport(byte[] report, int count, byte id)
        {
            if (count != 65 || report.Length < count || report[0] != id)
                throw new IOException("Wheelbase returned an invalid report.");
        }
        private static int UInt16(byte[] data, int offset) => data[offset] | (data[offset + 1] << 8);
        private static short Int16(byte[] data, int offset) => unchecked((short)UInt16(data, offset));
    }
}
