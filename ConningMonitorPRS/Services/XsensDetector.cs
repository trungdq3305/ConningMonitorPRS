using System;
using Microsoft.Win32;

namespace ConningMonitorPRS.Services
{
    public static class XsensDetector
    {
        private const string UsbEnumPath = @"SYSTEM\CurrentControlSet\Enum\USB";
        private const string XsensVid    = "VID_2639";

        public static string? FindPort()
        {
            try
            {
                using var usbKey = Registry.LocalMachine.OpenSubKey(UsbEnumPath);
                if (usbKey == null) return null;

                foreach (string deviceIdName in usbKey.GetSubKeyNames())
                {
                    if (!deviceIdName.StartsWith(XsensVid, StringComparison.OrdinalIgnoreCase)) continue;

                    using var deviceKey = usbKey.OpenSubKey(deviceIdName);
                    if (deviceKey == null) continue;

                    foreach (string instanceName in deviceKey.GetSubKeyNames())
                    {
                        using var instanceKey  = deviceKey.OpenSubKey(instanceName);
                        using var paramsKey    = instanceKey?.OpenSubKey("Device Parameters");
                        string?   portName     = paramsKey?.GetValue("PortName") as string;
                        if (!string.IsNullOrEmpty(portName))
                            return portName;
                    }
                }
            }
            catch (Exception ex) { SystemLogger.LogError("XsensDetector.FindPort", ex); }
            return null;
        }
    }
}
